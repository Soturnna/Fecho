using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fecho.Core;

public sealed class DeviceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Chave pública de controle (X9.63, Base64).</summary>
    [JsonPropertyName("controlKey")] public string ControlKey { get; set; } = "";
    /// <summary>Chave pública de desbloqueio (X9.63, Base64). Exige Face ID no iPhone.</summary>
    [JsonPropertyName("unlockKey")] public string UnlockKey { get; set; } = "";
    [JsonPropertyName("pairedAt")] public DateTimeOffset PairedAt { get; set; }

    [JsonIgnore] public byte[] ControlKeyBytes => Convert.FromBase64String(ControlKey);
    [JsonIgnore] public byte[] UnlockKeyBytes => Convert.FromBase64String(UnlockKey);
}

/// <summary>
/// Onde a lista de aparelhos é gravada. No Windows a implementação cifra o
/// conteúdo com DPAPI antes de gravar no disco.
/// </summary>
public interface IDeviceStorage
{
    string? Read();
    void Write(string content);
}

public sealed class InMemoryDeviceStorage : IDeviceStorage
{
    private string? _content;
    public string? Read() => _content;
    public void Write(string content) => _content = content;
}

public sealed class DeviceRegistry
{
    public const int MaxDevices = 10;

    private readonly IDeviceStorage _storage;
    private readonly object _gate = new();
    private Dictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);

    /// <summary>Disparado quando um aparelho é removido, para derrubar conexões abertas.</summary>
    public event Action<string>? Revoked;
    public event Action? Changed;

    public DeviceRegistry(IDeviceStorage storage)
    {
        _storage = storage;
        var raw = storage.Read();
        if (!string.IsNullOrEmpty(raw))
        {
            var list = JsonSerializer.Deserialize<List<DeviceRecord>>(raw) ?? new();
            _devices = list.ToDictionary(d => d.Id, StringComparer.Ordinal);
        }
    }

    public DeviceRecord? Get(string id)
    {
        lock (_gate) return _devices.TryGetValue(id, out var d) ? d : null;
    }

    public IReadOnlyList<DeviceRecord> List()
    {
        lock (_gate) return _devices.Values.OrderBy(d => d.PairedAt).ToList();
    }

    public bool Add(DeviceRecord device)
    {
        lock (_gate)
        {
            if (!_devices.ContainsKey(device.Id) && _devices.Count >= MaxDevices) return false;
            _devices[device.Id] = device;
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.Remove(id);
            if (removed) Persist();
        }
        if (removed)
        {
            Revoked?.Invoke(id);
            Changed?.Invoke();
        }
        return removed;
    }

    private void Persist() =>
        _storage.Write(JsonSerializer.Serialize(_devices.Values.ToList()));

    /// <summary>Identificador de aparelho: 8 a 64 caracteres de [A-Za-z0-9-].</summary>
    public static bool IsValidDeviceId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public static string SanitizeName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "iPhone";
        return clean.Length > 40 ? clean[..40] : clean;
    }
}
