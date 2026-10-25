using System.Text.Json;

namespace Fecho.Core;

/// <summary>Erro com código que pode ser devolvido ao app.</summary>
public sealed class CommandException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}

/// <summary>
/// Executa um comando JÁ autenticado. Aqui só se valida o formato dos
/// argumentos; a segurança ficou no Authenticator.
/// </summary>
public sealed class CommandDispatcher
{
    public const int MaxTextLength = 1_000;
    public const int MaxClipboardLength = 64 * 1024;
    public const int MaxMouseDelta = 2_000;

    private static readonly HashSet<string> AllowedModifiers = new(StringComparer.Ordinal) { "ctrl", "alt", "shift", "win" };

    private static readonly HashSet<string> AllowedKeys = BuildAllowedKeys();

    public static readonly TimeSpan FilesGrantDuration = TimeSpan.FromMinutes(10);

    private readonly IPlatformActions _actions;
    private readonly AuditLog _audit;
    private readonly FileService? _files;
    private readonly IClock _clock;

    public CommandDispatcher(IPlatformActions actions, AuditLog audit, FileService? files = null, IClock? clock = null)
    {
        _actions = actions;
        _audit = audit;
        _files = files;
        _clock = clock ?? new SystemClock();
    }

    public object? Execute(CommandPayload p, DeviceRecord device, ConnectionState conn)
    {
        var a = p.Args;
        switch (p.Cmd)
        {
            case "hello":
                return _actions.GetStatus();

            case "session.open":
                conn.EndSession();
                conn.SessionKey = Crypto.RandomBytes(32);
                conn.SessionDevice = device.Id;
                conn.LastSeq = 0;
                return new { key = Convert.ToBase64String(conn.SessionKey) };

            case "activity":
                return _audit.RecentForApp(20);

            case "lock": _actions.Lock(); return null;
            case "sleep": _actions.Sleep(); return null;
            case "restart": _actions.Restart(); return null;
            case "shutdown": _actions.Shutdown(); return null;

            case "unlock":
                return _actions.Unlock() switch
                {
                    UnlockOutcome.Done => null,
                    UnlockOutcome.NotLocked => new { alreadyUnlocked = true },
                    UnlockOutcome.NotInstalled => throw new CommandException(ErrorCodes.Unavailable, "credential provider ausente"),
                    _ => throw new CommandException(ErrorCodes.Failed, "falha no desbloqueio"),
                };

            case "volume.get": return _actions.GetVolume();
            case "volume.set": _actions.SetMasterVolume(Percent(a, "value")); return null;
            case "volume.mute": _actions.SetMute(Bool(a, "muted")); return null;
            case "volume.app":
                _actions.SetAppVolume(Text(a, "id", 32), Percent(a, "value"));
                return null;

            case "media":
                _actions.Media(Text(a, "action", 16) switch
                {
                    "playpause" => MediaAction.PlayPause,
                    "next" => MediaAction.Next,
                    "prev" => MediaAction.Previous,
                    _ => throw Invalid("ação de mídia"),
                });
                return null;

            case "mouse.move":
                _actions.MouseMove(Int(a, "dx", -MaxMouseDelta, MaxMouseDelta), Int(a, "dy", -MaxMouseDelta, MaxMouseDelta));
                return null;
            case "mouse.click":
                _actions.MouseClick(Text(a, "button", 8) switch
                {
                    "left" => MouseButton.Left,
                    "right" => MouseButton.Right,
                    _ => throw Invalid("botão"),
                });
                return null;
            case "mouse.scroll":
                _actions.MouseScroll(Int(a, "amount", -50, 50));
                return null;

            case "key":
                var key = Text(a, "key", 12);
                if (!AllowedKeys.Contains(key)) throw Invalid("tecla");
                var mods = Mods(a);
                _actions.Key(key, mods);
                return null;
            case "text":
                var text = Text(a, "text", MaxTextLength);
                if (text.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) throw Invalid("caractere de controle");
                _actions.Text(text);
                return null;

            case "clipboard.get":
                var clip = _actions.GetClipboardText() ?? "";
                return new { text = clip.Length > MaxClipboardLength ? clip[..MaxClipboardLength] : clip };
            case "clipboard.set":
                _actions.SetClipboardText(Text(a, "text", MaxClipboardLength));
                return null;
        }
        if (p.Cmd.StartsWith("files.", StringComparison.Ordinal)) return ExecuteFiles(p, device, conn);
        throw Invalid("comando");
    }

    private object? ExecuteFiles(CommandPayload p, DeviceRecord device, ConnectionState conn)
    {
        var files = _files ?? throw new CommandException(ErrorCodes.Unavailable, "arquivos desativados");
        var a = p.Args;
        switch (p.Cmd)
        {
            // Face ID uma vez libera a navegação nas pastas por 10 minutos, só nesta conexão.
            case "files.grant":
                conn.FilesGrantDevice = device.Id;
                conn.FilesGrantUntil = _clock.NowMs + (long)FilesGrantDuration.TotalMilliseconds;
                return new { expiresIn = (int)FilesGrantDuration.TotalSeconds };

            case "files.roots":
                RequireGrant(device, conn);
                return files.Roots();

            case "files.list":
                RequireGrant(device, conn);
                return files.List(Text(a, "root", 32), OptionalText(a, "path", 1024));

            case "files.read":
            {
                RequireGrant(device, conn);
                var (data, size, eof) = files.Read(Text(a, "root", 32), Text(a, "path", 1024),
                    Long(a, "offset", 0, long.MaxValue), Int(a, "length", 1, FileService.MaxChunkBytes));
                return new { data = Convert.ToBase64String(data), size, eof };
            }

            // Enviar ao PC não exige Face ID: o arquivo só pode cair em Downloads\Fecho.
            case "files.upload.begin":
            {
                var upload = files.BeginUpload(Text(a, "name", 255), Long(a, "size", 0, FileService.MaxUploadBytes),
                    conn.Uploads.Count);
                conn.Uploads[upload.Id] = upload;
                return new { id = upload.Id, chunk = FileService.MaxChunkBytes };
            }

            case "files.upload.chunk":
            {
                var upload = Upload(a, conn);
                var data = Crypto.FromBase64(Text(a, "data", FileService.MaxChunkBytes * 2), FileService.MaxChunkBytes)
                    ?? throw Invalid("dados");
                try
                {
                    files.WriteChunk(upload, Long(a, "offset", 0, FileService.MaxUploadBytes), data);
                }
                catch (CommandException)
                {
                    conn.Uploads.Remove(upload.Id);
                    upload.Dispose();
                    throw;
                }
                return new { written = upload.Written };
            }

            case "files.upload.end":
            {
                var upload = Upload(a, conn);
                conn.Uploads.Remove(upload.Id);
                try
                {
                    var hash = Crypto.FromBase64(Text(a, "sha256", 64), 32) ?? throw Invalid("hash");
                    var name = files.FinishUpload(upload, hash);
                    _audit.Record(device.Id, "file.received", true, name);
                    return new { name };
                }
                finally
                {
                    upload.Dispose();
                }
            }
        }
        throw Invalid("comando");
    }

    private void RequireGrant(DeviceRecord device, ConnectionState conn)
    {
        if (conn.FilesGrantDevice != device.Id || conn.FilesGrantUntil < _clock.NowMs)
            throw new CommandException(ErrorCodes.NeedsGrant, "sem permissão de arquivos");
    }

    private static UploadSession Upload(JsonElement a, ConnectionState conn) =>
        conn.Uploads.TryGetValue(Text(a, "id", 32), out var u) ? u : throw Invalid("envio");

    private static string OptionalText(JsonElement a, string name, int max)
    {
        var s = Authenticator.Str(a, name) ?? "";
        if (s.Length > max) throw Invalid(name);
        return s;
    }

    private static long Long(JsonElement a, string name, long min, long max)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)
            || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
            throw Invalid(name);
        if (n < min || n > max) throw Invalid(name);
        return n;
    }

    private static CommandException Invalid(string what) => new(ErrorCodes.Invalid, what);

    private static string Text(JsonElement a, string name, int max)
    {
        var s = Authenticator.Str(a, name) ?? throw Invalid(name);
        if (s.Length == 0 || s.Length > max) throw Invalid(name);
        return s;
    }

    private static int Int(JsonElement a, string name, int min, int max)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)
            || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
            throw Invalid(name);
        if (n < min || n > max) throw Invalid(name);
        return n;
    }

    private static int Percent(JsonElement a, string name) => Int(a, name, 0, 100);

    private static bool Bool(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) throw Invalid(name);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(name),
        };
    }

    private static IReadOnlyList<string> Mods(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("mods", out var m)) return Array.Empty<string>();
        if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() > 4) throw Invalid("mods");
        var list = new List<string>();
        foreach (var item in m.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (s is null || !AllowedModifiers.Contains(s) || list.Contains(s)) throw Invalid("mods");
            list.Add(s);
        }
        return list;
    }

    private static HashSet<string> BuildAllowedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "esc", "tab", "enter", "backspace", "delete", "space",
            "up", "down", "left", "right", "home", "end", "pageup", "pagedown", "win",
        };
        for (var i = 1; i <= 12; i++) keys.Add("f" + i);
        for (var c = 'a'; c <= 'z'; c++) keys.Add(c.ToString());
        for (var c = '0'; c <= '9'; c++) keys.Add(c.ToString());
        return keys;
    }
}
