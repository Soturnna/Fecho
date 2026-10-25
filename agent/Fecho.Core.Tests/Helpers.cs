using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fecho.Core;

namespace Fecho.Tests;

public sealed class FakeClock : IClock
{
    public long NowMs { get; set; } = 1_800_000_000_000;
}

/// <summary>Simula um iPhone: duas chaves P-256, como no Secure Enclave.</summary>
public sealed class TestDevice
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public ECDsa ControlKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ECDsa UnlockKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string AgentId { get; set; } = "agente-teste";
    public IClock Clock { get; set; } = new FakeClock();

    public static byte[] PublicX963(ECDsa key)
    {
        var p = key.ExportParameters(false);
        var b = new byte[65];
        b[0] = 0x04;
        p.Q.X!.CopyTo(b, 1);
        p.Q.Y!.CopyTo(b, 33);
        return b;
    }

    public DeviceRecord Record() => new()
    {
        Id = Id,
        Name = "iPhone de teste",
        ControlKey = Convert.ToBase64String(PublicX963(ControlKey)),
        UnlockKey = Convert.ToBase64String(PublicX963(UnlockKey)),
        PairedAt = DateTimeOffset.UnixEpoch,
    };

    public byte[] PayloadBytes(string cmd, object? args = null, long? ts = null, string? nonce = null, long? seq = null, string? agent = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["device"] = Id,
            ["agent"] = agent ?? AgentId,
            ["cmd"] = cmd,
            ["args"] = args ?? new { },
            ["ts"] = ts ?? Clock.NowMs,
            ["nonce"] = nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        };
        if (seq is not null) payload["seq"] = seq;
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    public Envelope Signed(string cmd, object? args = null, ECDsa? key = null, long? ts = null, string? nonce = null, string? agent = null)
    {
        var bytes = PayloadBytes(cmd, args, ts, nonce, agent: agent);
        var sig = (key ?? ControlKey).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new Envelope { Id = "1", Payload = Convert.ToBase64String(bytes), Sig = Convert.ToBase64String(sig) };
    }

    public Envelope Session(string cmd, object args, byte[] sessionKey, long seq)
    {
        var bytes = PayloadBytes(cmd, args, seq: seq);
        var mac = HMACSHA256.HashData(sessionKey, bytes);
        return new Envelope { Id = "1", Payload = Convert.ToBase64String(bytes), Mac = Convert.ToBase64String(mac) };
    }

    public static string ToJson(Envelope e) => JsonSerializer.Serialize(e);
    public static byte[] ToBytes(Envelope e) => Encoding.UTF8.GetBytes(ToJson(e));
}

public sealed class FakeActions : IPlatformActions
{
    public List<string> Calls { get; } = new();
    public UnlockOutcome UnlockResult { get; set; } = UnlockOutcome.NotInstalled;

    public StatusInfo GetStatus() { Calls.Add("status"); return new StatusInfo { Name = "PC", Os = "Windows", Version = "teste" }; }
    public void Lock() => Calls.Add("lock");
    public void Sleep() => Calls.Add("sleep");
    public void Restart() => Calls.Add("restart");
    public void Shutdown() => Calls.Add("shutdown");
    public UnlockOutcome Unlock() { Calls.Add("unlock"); return UnlockResult; }
    public VolumeState GetVolume() => new() { Volume = 50 };
    public void SetMasterVolume(int percent) => Calls.Add($"volume:{percent}");
    public void SetMute(bool muted) => Calls.Add($"mute:{muted}");
    public void SetAppVolume(string id, int percent) => Calls.Add($"app:{id}:{percent}");
    public void Media(MediaAction action) => Calls.Add($"media:{action}");
    public void MouseMove(int dx, int dy) => Calls.Add($"move:{dx},{dy}");
    public void MouseClick(MouseButton button) => Calls.Add($"click:{button}");
    public void MouseScroll(int amount) => Calls.Add($"scroll:{amount}");
    public void MouseMoveTo(double x, double y) => Calls.Add(System.FormattableString.Invariant($"moveto:{x},{y}"));
    public void MouseClickAt(double x, double y, MouseButton b) => Calls.Add(System.FormattableString.Invariant($"clickat:{b}:{x},{y}"));
    public void MouseButtonDown(double x, double y, MouseButton b) => Calls.Add(System.FormattableString.Invariant($"down:{b}:{x},{y}"));
    public void MouseButtonUp(double x, double y, MouseButton b) => Calls.Add(System.FormattableString.Invariant($"up:{b}:{x},{y}"));
    public void Key(string key, IReadOnlyList<string> modifiers) => Calls.Add($"key:{string.Join("+", modifiers)}+{key}");
    public void Text(string text) => Calls.Add($"text:{text}");
    public string? GetClipboardText() => "copiado";
    public void SetClipboardText(string text) => Calls.Add($"clip:{text}");
}

/// <summary>Tela falsa: devolve sempre um quadro de 4 bytes.</summary>
public sealed class FakeScreenSource : IScreenSource
{
    public int Captures;
    public ScreenFrame? Capture(int maxWidth, int quality)
    {
        Interlocked.Increment(ref Captures);
        return new ScreenFrame(new byte[] { 1, 2, 3, 4 }, 4, 4);
    }
}

/// <summary>Monta o agente inteiro em memória para os testes.</summary>
public sealed class Harness
{
    public FakeClock Clock { get; } = new();
    public DeviceRegistry Registry { get; } = new(new InMemoryDeviceStorage());
    public PairingManager Pairing { get; }
    public Authenticator Auth { get; }
    public FakeActions Actions { get; } = new();
    public AuditLog Audit { get; }
    public CommandDispatcher Dispatcher { get; }
    public TestDevice Device { get; }

    public Harness(bool paired = true)
    {
        Pairing = new PairingManager(Clock);
        Auth = new Authenticator("agente-teste", Registry, Pairing, Clock);
        Audit = new AuditLog(Clock);
        Dispatcher = new CommandDispatcher(Actions, Audit);
        Device = new TestDevice { Clock = Clock };
        if (paired) Registry.Add(Device.Record());
    }
}
