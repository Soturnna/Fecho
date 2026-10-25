namespace Fecho.Core;

public sealed record PairingOffer(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Controla o token de pareamento: aleatório, curto (2 min), uso único e
/// invalidado depois de 5 tentativas erradas.
/// </summary>
public sealed class PairingManager
{
    public const int MaxFailedAttempts = 5;

    private readonly IClock _clock;
    private readonly object _gate = new();
    private byte[]? _token;
    private long _expiresMs;
    private int _failures;

    public event Action<DeviceRecord>? Paired;

    public PairingManager(IClock clock) => _clock = clock;

    public PairingOffer Begin(TimeSpan ttl)
    {
        lock (_gate)
        {
            _token = Crypto.RandomBytes(32);
            _expiresMs = _clock.NowMs + (long)ttl.TotalMilliseconds;
            _failures = 0;
            return new PairingOffer(Crypto.Base64Url(_token),
                DateTimeOffset.FromUnixTimeMilliseconds(_expiresMs));
        }
    }

    public void Cancel()
    {
        lock (_gate) Clear();
    }

    public bool IsActive
    {
        get { lock (_gate) return _token is not null && _clock.NowMs <= _expiresMs; }
    }

    /// <summary>Consome o token se ele confere. Só funciona uma vez.</summary>
    public bool TryConsume(string? candidate)
    {
        var given = Crypto.FromBase64Url(candidate);
        lock (_gate)
        {
            if (_token is null) return false;
            if (_clock.NowMs > _expiresMs)
            {
                Clear();
                return false;
            }
            if (given is null || given.Length != _token.Length || !Crypto.FixedTimeEquals(given, _token))
            {
                if (++_failures >= MaxFailedAttempts) Clear();
                return false;
            }
            Clear();
            return true;
        }
    }

    internal void NotifyPaired(DeviceRecord device) => Paired?.Invoke(device);

    private void Clear()
    {
        if (_token is not null) Array.Clear(_token);
        _token = null;
        _failures = 0;
    }
}

public static class PairingUri
{
    /// <summary>
    /// Monta o conteúdo do QR code. A impressão digital do certificado viaja
    /// por este canal visual, que um invasor na rede não consegue alterar.
    /// </summary>
    public static string Build(string host, int port, string certFingerprintHex, string token, string agentId, string pcName)
    {
        static string E(string s) => Uri.EscapeDataString(s);
        return $"fecho://parear?h={E(host)}&p={port}&f={E(certFingerprintHex)}&t={E(token)}&id={E(agentId)}&n={E(pcName)}";
    }
}
