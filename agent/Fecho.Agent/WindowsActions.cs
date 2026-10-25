using System.Diagnostics;
using Fecho.Core;
using NAudio.CoreAudioApi;
using static Fecho.Agent.NativeMethods;

namespace Fecho.Agent;

/// <summary>Implementação real dos comandos no Windows.</summary>
public sealed class WindowsActions : IPlatformActions
{
    private static readonly Dictionary<string, (ushort Vk, bool Extended)> KeyMap = BuildKeys();
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.Ordinal)
    {
        ["ctrl"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B,
    };

    private volatile bool _locked;

    public bool IsLocked
    {
        get => _locked;
        set => _locked = value;
    }

    public StatusInfo GetStatus() => new()
    {
        Name = Environment.MachineName,
        Os = "Windows " + Environment.OSVersion.Version.Build,
        Locked = _locked,
        UnlockAvailable = CredProviderInstaller.IsRegistered() && UnlockSecrets.HasPassword(),
        Version = typeof(WindowsActions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
    };

    // ---------- Energia ----------

    public void Lock()
    {
        if (!LockWorkStation()) throw new InvalidOperationException("LockWorkStation falhou");
    }

    public void Sleep()
    {
        // Responde ao app antes de o PC dormir.
        Task.Run(async () =>
        {
            await Task.Delay(800);
            SetSuspendState(false, false, false);
        });
    }

    public void Restart() => RunShutdown("/r /t 3");

    public void Shutdown() => RunShutdown("/s /t 3");

    private static void RunShutdown(string args)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
    }

    /// <summary>
    /// Desbloqueia a tela de login entregando a senha (guardada com DPAPI) ao
    /// componente de desbloqueio por um pipe local. A validação do Face ID já
    /// foi feita no app antes de este comando chegar aqui.
    /// </summary>
    public UnlockOutcome Unlock()
    {
        if (!_locked) return UnlockOutcome.NotLocked;
        if (!CredProviderInstaller.IsRegistered() || !UnlockSecrets.HasPassword())
            return UnlockOutcome.NotInstalled;

        var creds = UnlockSecrets.Load();
        if (creds is null) return UnlockOutcome.Failed;

        var (domain, user, password) = creds.Value;
        try
        {
            return UnlockBridge.Send(domain, user, password) switch
            {
                BridgeResult.Sent => UnlockOutcome.Done,
                BridgeResult.NotRunning => UnlockOutcome.NotInstalled,
                _ => UnlockOutcome.Failed,
            };
        }
        finally
        {
            password = new string('\0', password.Length);
            GC.KeepAlive(password);
        }
    }

    // ---------- Áudio ----------

    public VolumeState GetVolume()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var state = new VolumeState
        {
            Volume = (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
            Muted = device.AudioEndpointVolume.Mute,
        };

        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.IsSystemSoundsSession) continue;
            var pid = (int)s.GetProcessID;
            string name;
            try
            {
                using var proc = Process.GetProcessById(pid);
                name = proc.ProcessName;
            }
            catch (ArgumentException) { continue; }
            catch (InvalidOperationException) { continue; }

            state.Apps.Add(new AppVolume
            {
                Id = pid.ToString(),
                Name = FriendlyName(name),
                Volume = (int)Math.Round(s.SimpleAudioVolume.Volume * 100),
                Muted = s.SimpleAudioVolume.Mute,
            });
        }
        return state;
    }

    public void SetMasterVolume(int percent)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
        if (percent > 0 && device.AudioEndpointVolume.Mute) device.AudioEndpointVolume.Mute = false;
    }

    public void SetMute(bool muted)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.Mute = muted;
    }

    public void SetAppVolume(string id, int percent)
    {
        if (!int.TryParse(id, out var pid)) throw new CommandException(ErrorCodes.Invalid, "id");
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            if ((int)sessions[i].GetProcessID == pid)
            {
                sessions[i].SimpleAudioVolume.Volume = percent / 100f;
                return;
            }
        }
        throw new CommandException(ErrorCodes.Invalid, "app não encontrado");
    }

    public void Media(MediaAction action)
    {
        ushort vk = action switch
        {
            MediaAction.PlayPause => 0xB3,
            MediaAction.Next => 0xB0,
            MediaAction.Previous => 0xB1,
            _ => throw new CommandException(ErrorCodes.Invalid, "mídia"),
        };
        Send(NativeMethods.Key(vk, up: false, extended: true), NativeMethods.Key(vk, up: true, extended: true));
    }

    // ---------- Mouse e teclado ----------

    public void MouseMove(int dx, int dy)
    {
        EnsureUnlocked();
        Send(Mouse(MOUSEEVENTF_MOVE, dx, dy));
    }

    public void MouseClick(MouseButton button)
    {
        EnsureUnlocked();
        var (down, up) = button == MouseButton.Left
            ? (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
            : (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
        Send(Mouse(down), Mouse(up));
    }

    public void MouseScroll(int amount)
    {
        EnsureUnlocked();
        Send(Mouse(MOUSEEVENTF_WHEEL, data: amount * 120));
    }

    public void MouseMoveTo(double x, double y)
    {
        EnsureUnlocked();
        Send(MouseAbs(x, y));
    }

    public void MouseClickAt(double x, double y, MouseButton button)
    {
        EnsureUnlocked();
        var (down, up) = ButtonFlags(button);
        Send(MouseAbs(x, y), Mouse(down), Mouse(up));
    }

    public void MouseButtonDown(double x, double y, MouseButton button)
    {
        EnsureUnlocked();
        var (down, _) = ButtonFlags(button);
        Send(MouseAbs(x, y), Mouse(down));
    }

    public void MouseButtonUp(double x, double y, MouseButton button)
    {
        EnsureUnlocked();
        var (_, up) = ButtonFlags(button);
        Send(MouseAbs(x, y), Mouse(up));
    }

    private static (uint Down, uint Up) ButtonFlags(MouseButton button) => button == MouseButton.Left
        ? (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        : (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);

    public void Key(string key, IReadOnlyList<string> modifiers)
    {
        EnsureUnlocked();
        if (!KeyMap.TryGetValue(key, out var k)) throw new CommandException(ErrorCodes.Invalid, "tecla");
        var inputs = new List<INPUT>();
        foreach (var m in modifiers) inputs.Add(NativeMethods.Key(Modifiers[m], up: false));
        inputs.Add(NativeMethods.Key(k.Vk, up: false, k.Extended));
        inputs.Add(NativeMethods.Key(k.Vk, up: true, k.Extended));
        for (var i = modifiers.Count - 1; i >= 0; i--) inputs.Add(NativeMethods.Key(Modifiers[modifiers[i]], up: true));
        Send(inputs.ToArray());
    }

    public void Text(string text)
    {
        EnsureUnlocked();
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\n')
            {
                inputs.Add(NativeMethods.Key(0x0D, up: false));
                inputs.Add(NativeMethods.Key(0x0D, up: true));
            }
            else
            {
                inputs.Add(Unicode(c, up: false));
                inputs.Add(Unicode(c, up: true));
            }
        }
        Send(inputs.ToArray());
    }

    // ---------- Área de transferência ----------

    public string? GetClipboardText()
    {
        EnsureUnlocked();
        return RunSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
    }

    public void SetClipboardText(string text)
    {
        EnsureUnlocked();
        RunSta<object?>(() => { Clipboard.SetText(text); return null; });
    }

    /// <summary>Com o PC bloqueado, nada de teclado, mouse ou área de transferência.</summary>
    private void EnsureUnlocked()
    {
        if (_locked) throw new CommandException(ErrorCodes.Unavailable, "pc bloqueado");
    }

    /// <summary>A área de transferência do Windows só funciona em thread STA.</summary>
    private static T RunSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("área de transferência ocupada");
        if (error is not null) throw error;
        return result;
    }

    private static string FriendlyName(string processName) =>
        processName.Length == 0 ? "App" : char.ToUpperInvariant(processName[0]) + processName[1..];

    private static Dictionary<string, (ushort, bool)> BuildKeys()
    {
        var map = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal)
        {
            ["esc"] = (0x1B, false), ["tab"] = (0x09, false), ["enter"] = (0x0D, false),
            ["backspace"] = (0x08, false), ["delete"] = (0x2E, true), ["space"] = (0x20, false),
            ["up"] = (0x26, true), ["down"] = (0x28, true), ["left"] = (0x25, true), ["right"] = (0x27, true),
            ["home"] = (0x24, true), ["end"] = (0x23, true), ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true),
            ["win"] = (0x5B, true),
        };
        for (var i = 1; i <= 12; i++) map["f" + i] = ((ushort)(0x70 + i - 1), false);
        for (var c = 'a'; c <= 'z'; c++) map[c.ToString()] = ((ushort)char.ToUpperInvariant(c), false);
        for (var c = '0'; c <= '9'; c++) map[c.ToString()] = ((ushort)c, false);
        return map;
    }
}
