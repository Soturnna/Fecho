using System.Collections.Concurrent;

namespace Fecho.Core;

public interface IClock
{
    long NowMs { get; }
}

public sealed class SystemClock : IClock
{
    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Guarda os nonces já usados durante a janela de validade. Um comando com
/// nonce repetido é um replay e é recusado.
/// </summary>
public sealed class NonceCache
{
    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly long _ttlMs;
    private long _nextPrune;

    public NonceCache(IClock clock, TimeSpan ttl)
    {
        _clock = clock;
        _ttlMs = (long)ttl.TotalMilliseconds;
    }

    /// <returns>true se o nonce é novo; false se já foi visto.</returns>
    public bool TryUse(string deviceId, string nonce)
    {
        var now = _clock.NowMs;
        PruneIfDue(now);
        return _seen.TryAdd(deviceId + "|" + nonce, now + _ttlMs);
    }

    private void PruneIfDue(long now)
    {
        if (now < Interlocked.Read(ref _nextPrune)) return;
        Interlocked.Exchange(ref _nextPrune, now + 10_000);
        foreach (var kv in _seen)
        {
            if (kv.Value < now) _seen.TryRemove(kv.Key, out _);
        }
    }
}

/// <summary>Balde de fichas por chave: limita comandos por segundo.</summary>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long Last;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly double _capacity;
    private readonly double _perMs;

    public RateLimiter(IClock clock, int capacity, int perSecond)
    {
        _clock = clock;
        _capacity = capacity;
        _perMs = perSecond / 1000.0;
    }

    public bool TryTake(string key)
    {
        var now = _clock.NowMs;
        var b = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = _capacity, Last = now });
        lock (b)
        {
            b.Tokens = Math.Min(_capacity, b.Tokens + (now - b.Last) * _perMs);
            b.Last = now;
            if (b.Tokens < 1) return false;
            b.Tokens -= 1;
            return true;
        }
    }
}
