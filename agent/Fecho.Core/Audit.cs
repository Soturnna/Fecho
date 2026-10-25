using System.Text.Json.Serialization;

namespace Fecho.Core;

public sealed class AuditEntry
{
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    /// <summary>Motivo interno (só fica no PC; o app recebe apenas "recusado").</summary>
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public interface IAuditSink
{
    void Append(AuditEntry entry);
}

/// <summary>Registro em memória dos últimos eventos, com gravação opcional em disco.</summary>
public sealed class AuditLog
{
    private const int Capacity = 200;
    private readonly LinkedList<AuditEntry> _entries = new();
    private readonly IClock _clock;
    private readonly IAuditSink? _sink;

    public AuditLog(IClock clock, IAuditSink? sink = null)
    {
        _clock = clock;
        _sink = sink;
    }

    public void Record(string? device, string cmd, bool ok, string? detail = null)
    {
        var e = new AuditEntry { Ts = _clock.NowMs, Device = device, Cmd = cmd, Ok = ok, Detail = detail };
        lock (_entries)
        {
            _entries.AddFirst(e);
            if (_entries.Count > Capacity) _entries.RemoveLast();
        }
        try { _sink?.Append(e); } catch (IOException) { /* o log em disco é melhor esforço */ }
    }

    /// <summary>Eventos bem-sucedidos e relevantes para mostrar no app.</summary>
    public IReadOnlyList<AuditEntry> RecentForApp(int max)
    {
        lock (_entries)
        {
            return _entries
                .Where(e => e.Ok && e.Cmd is "unlock" or "lock" or "sleep" or "shutdown" or "restart" or "pair" or "autolock")
                .Take(max)
                .Select(e => new AuditEntry { Ts = e.Ts, Device = e.Device, Cmd = e.Cmd, Ok = e.Ok })
                .ToList();
        }
    }

    public IReadOnlyList<AuditEntry> All()
    {
        lock (_entries) return _entries.ToList();
    }
}
