using System.Collections.Concurrent;
using MachineCommons.Config;
using Microsoft.Extensions.Options;

namespace MachineCommons.Security;

public sealed class RateLimiter
{
    private readonly BoardOptions _options;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _globalWrites;

    public RateLimiter(IOptions<BoardOptions> options)
    {
        _options = options.Value;
    }

    public bool TryAcquireWrite(string clientId, string ip, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!TryAcquire($"c:{clientId}", _options.WriteRatePerClientPerMinute, out retryAfterSeconds))
            return false;
        if (!TryAcquire($"ipw:{ip}", _options.WriteRatePerIpPerMinute, out retryAfterSeconds))
            return false;
        if (!TryAcquire("global", _options.GlobalWriteRatePerMinute, out retryAfterSeconds))
            return false;
        Interlocked.Increment(ref _globalWrites);
        return true;
    }

    public bool TryAcquireRead(string ip, out int retryAfterSeconds)
        => TryAcquire($"ipr:{ip}", _options.ReadRatePerIpPerMinute, out retryAfterSeconds);

    public int EstimateDifficulty()
    {
        // Adaptive: raise difficulty when global write bucket is hot.
        if (!_buckets.TryGetValue("global", out var bucket))
            return _options.PowBaseDifficulty;

        var usedRatio = 1.0 - (double)bucket.Tokens / Math.Max(1, _options.GlobalWriteRatePerMinute);
        var extra = (int)Math.Floor(usedRatio * (_options.PowMaxDifficulty - _options.PowBaseDifficulty));
        return Math.Clamp(_options.PowBaseDifficulty + extra, _options.PowBaseDifficulty, _options.PowMaxDifficulty);
    }

    public int DifficultyForClient(string clientId)
    {
        var baseDiff = EstimateDifficulty();
        if (_buckets.TryGetValue($"c:{clientId}", out var b))
        {
            var used = 1.0 - (double)b.Tokens / Math.Max(1, _options.WriteRatePerClientPerMinute);
            if (used > 0.5)
                baseDiff = Math.Min(_options.PowMaxDifficulty, baseDiff + 2);
        }
        return baseDiff;
    }

    private bool TryAcquire(string key, int perMinute, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        TrimIfNeeded();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(perMinute));
        lock (bucket)
        {
            bucket.Refill(perMinute);
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return true;
            }
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1 - bucket.Tokens) / (perMinute / 60.0)));
            return false;
        }
    }

    private void TrimIfNeeded()
    {
        if (_buckets.Count <= _options.RateLimiterMaxKeys) return;
        foreach (var key in _buckets.Keys.Take(_buckets.Count - _options.RateLimiterMaxKeys / 2))
            _buckets.TryRemove(key, out _);
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastTicks;

        public Bucket(int capacity)
        {
            Tokens = capacity;
            LastTicks = Environment.TickCount64;
        }

        public void Refill(int perMinute)
        {
            var now = Environment.TickCount64;
            var elapsed = (now - LastTicks) / 1000.0;
            if (elapsed <= 0) return;
            Tokens = Math.Min(perMinute, Tokens + elapsed * (perMinute / 60.0));
            LastTicks = now;
        }
    }
}
