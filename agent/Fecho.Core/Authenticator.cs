using System.Text.Json;

namespace Fecho.Core;

/// <summary>Estado de segurança de uma conexão WebSocket.</summary>
public sealed class ConnectionState
{
    /// <summary>Aparelho que já se autenticou nesta conexão (recebe eventos).</summary>
    public string? DeviceId { get; set; }
    public byte[]? SessionKey { get; set; }
    public string? SessionDevice { get; set; }
    public long LastSeq { get; set; }

    /// <summary>Até quando (ms) este aparelho pode navegar nas pastas do PC.</summary>
    public long FilesGrantUntil { get; set; }
    public string? FilesGrantDevice { get; set; }
    public Dictionary<string, UploadSession> Uploads { get; } = new(StringComparer.Ordinal);

    /// <summary>Se este aparelho está pedindo o streaming da tela agora.</summary>
    private volatile bool _wantsScreen;
    public bool WantsScreen { get => _wantsScreen; set => _wantsScreen = value; }
    public int ScreenMaxWidth { get; set; } = 900;
    public int ScreenQuality { get; set; } = 50;

    /// <summary>Chamado quando a conexão fecha: encerra sessão, permissão, envios e streaming.</summary>
    public void Release()
    {
        EndSession();
        FilesGrantUntil = 0;
        FilesGrantDevice = null;
        WantsScreen = false;
        foreach (var u in Uploads.Values) u.Dispose();
        Uploads.Clear();
    }

    public void EndSession()
    {
        if (SessionKey is not null) Array.Clear(SessionKey);
        SessionKey = null;
        SessionDevice = null;
        LastSeq = 0;
    }
}

public sealed record AuthResult(bool Ok, CommandPayload? Payload, DeviceRecord? Device, string? Reason, bool RateLimited = false)
{
    public static AuthResult Fail(string reason, CommandPayload? p = null) => new(false, p, null, reason);
}

/// <summary>
/// Aplica, em ordem, todas as verificações da seção 5 de docs/SEGURANCA.md.
/// Nenhum comando chega ao Windows sem passar por aqui.
/// </summary>
public sealed class Authenticator
{
    public const int MaxPayloadBytes = 96 * 1024;
    public static readonly TimeSpan ClockWindow = TimeSpan.FromSeconds(30);

    private readonly string _agentId;
    private readonly DeviceRegistry _registry;
    private readonly PairingManager _pairing;
    private readonly IClock _clock;
    private readonly NonceCache _nonces;
    private readonly RateLimiter _signedLimiter;
    private readonly RateLimiter _sessionLimiter;
    private readonly RateLimiter _pairLimiter;

    public Authenticator(string agentId, DeviceRegistry registry, PairingManager pairing, IClock clock)
    {
        _agentId = agentId;
        _registry = registry;
        _pairing = pairing;
        _clock = clock;
        // O nonce precisa ficar guardado por mais tempo que a janela de horário inteira.
        _nonces = new NonceCache(clock, ClockWindow * 4);
        _signedLimiter = new RateLimiter(clock, capacity: 20, perSecond: 10);
        _sessionLimiter = new RateLimiter(clock, capacity: 240, perSecond: 120);
        _pairLimiter = new RateLimiter(clock, capacity: 5, perSecond: 1);
    }

    public AuthResult Authenticate(Envelope env, ConnectionState conn)
    {
        var bytes = Crypto.FromBase64(env.Payload, MaxPayloadBytes);
        if (bytes is null) return AuthResult.Fail("payload ilegível");

        CommandPayload? p;
        try { p = JsonSerializer.Deserialize<CommandPayload>(bytes); }
        catch (JsonException) { return AuthResult.Fail("json inválido"); }
        if (p is null || p.V != 1) return AuthResult.Fail("versão inválida");
        if (!string.Equals(p.Agent, _agentId, StringComparison.Ordinal)) return AuthResult.Fail("agente diferente", p);
        if (!DeviceRegistry.IsValidDeviceId(p.Device)) return AuthResult.Fail("id de aparelho inválido", p);

        if (p.Cmd == CommandPolicy.Pair) return AuthenticatePairing(env, p, bytes);

        var rule = CommandPolicy.Get(p.Cmd);
        if (rule is null) return AuthResult.Fail("comando desconhecido", p);

        var device = _registry.Get(p.Device);
        if (device is null) return AuthResult.Fail("aparelho não pareado", p);

        if (!WithinClockWindow(p.Ts)) return AuthResult.Fail("horário fora da janela", p);

        if (env.Mac is not null)
        {
            if (env.Sig is not null) return AuthResult.Fail("sig e mac juntos", p);
            if (!rule.AllowSession) return AuthResult.Fail("comando exige assinatura", p);
            if (conn.SessionKey is null || conn.SessionDevice != p.Device) return AuthResult.Fail("sem sessão", p);
            if (p.Seq is null || p.Seq <= conn.LastSeq) return AuthResult.Fail("seq repetido", p);
            var mac = Crypto.FromBase64(env.Mac, 64);
            if (mac is null || !Crypto.FixedTimeEquals(mac, Crypto.HmacSha256(conn.SessionKey, bytes)))
                return AuthResult.Fail("mac inválido", p);
            if (!_sessionLimiter.TryTake(p.Device)) return new AuthResult(false, p, device, "limite", RateLimited: true);
            conn.LastSeq = p.Seq.Value;
            conn.DeviceId = device.Id;
            return new AuthResult(true, p, device, null);
        }

        var sig = Crypto.FromBase64(env.Sig, 256);
        if (sig is null) return AuthResult.Fail("sem assinatura", p);
        var key = rule.Level == AuthLevel.Unlock ? device.UnlockKeyBytes : device.ControlKeyBytes;
        if (!Crypto.VerifyP256(key, bytes, sig)) return AuthResult.Fail("assinatura inválida", p);
        if (!IsValidNonce(p.Nonce) || !_nonces.TryUse(p.Device, p.Nonce)) return AuthResult.Fail("nonce repetido", p);
        if (!_signedLimiter.TryTake(p.Device)) return new AuthResult(false, p, device, "limite", RateLimited: true);

        conn.DeviceId = device.Id;
        return new AuthResult(true, p, device, null);
    }

    private AuthResult AuthenticatePairing(Envelope env, CommandPayload p, byte[] bytes)
    {
        if (!_pairLimiter.TryTake("pair")) return new AuthResult(false, p, null, "limite", RateLimited: true);
        if (!_pairing.IsActive) return AuthResult.Fail("pareamento não iniciado", p);
        if (!WithinClockWindow(p.Ts)) return AuthResult.Fail("horário fora da janela", p);
        if (p.Args.ValueKind != JsonValueKind.Object) return AuthResult.Fail("args ausentes", p);

        var controlKey = Crypto.FromBase64(Str(p.Args, "controlKey"), 128);
        var unlockKey = Crypto.FromBase64(Str(p.Args, "unlockKey"), 128);
        if (!Crypto.IsValidP256PublicKey(controlKey) || !Crypto.IsValidP256PublicKey(unlockKey))
            return AuthResult.Fail("chaves inválidas", p);
        if (Crypto.FixedTimeEquals(controlKey!, unlockKey!)) return AuthResult.Fail("chaves iguais", p);

        // Prova de posse: quem pede o pareamento precisa ter a chave de controle.
        var sig = Crypto.FromBase64(env.Sig, 256);
        if (sig is null || !Crypto.VerifyP256(controlKey!, bytes, sig)) return AuthResult.Fail("assinatura inválida", p);
        if (!IsValidNonce(p.Nonce) || !_nonces.TryUse(p.Device, p.Nonce)) return AuthResult.Fail("nonce repetido", p);

        // O token é conferido por último: só gasta tentativa quem já provou ter a chave.
        if (!_pairing.TryConsume(Str(p.Args, "token"))) return AuthResult.Fail("token inválido", p);

        var device = new DeviceRecord
        {
            Id = p.Device,
            Name = DeviceRegistry.SanitizeName(Str(p.Args, "name")),
            ControlKey = Convert.ToBase64String(controlKey!),
            UnlockKey = Convert.ToBase64String(unlockKey!),
            PairedAt = DateTimeOffset.FromUnixTimeMilliseconds(_clock.NowMs),
        };
        if (!_registry.Add(device)) return AuthResult.Fail("limite de aparelhos", p);
        _pairing.NotifyPaired(device);
        return new AuthResult(true, p, device, null);
    }

    private bool WithinClockWindow(long ts) =>
        Math.Abs(_clock.NowMs - ts) <= (long)ClockWindow.TotalMilliseconds;

    private static bool IsValidNonce(string? nonce)
    {
        var raw = Crypto.FromBase64(nonce, 64);
        return raw is { Length: >= 16 };
    }

    internal static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
