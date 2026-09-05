using System.Collections.Concurrent;
using System.Text;
using MachineCommons.Config;
using Microsoft.Extensions.Options;

namespace MachineCommons.Cache;

public sealed class BoundedMemoryCache
{
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, Entry> _map = new();
    private long _bytes;
    private int _entries;

    public BoundedMemoryCache(IOptions<BoardOptions> options)
    {
        _maxBytes = Math.Max(1, options.Value.CacheMaxMb) * 1024L * 1024L;
    }

    public int EntryCount => _entries;
    public long ApproxBytes => Interlocked.Read(ref _bytes);

    public bool TryGet(string key, out string value)
    {
        if (_map.TryGetValue(key, out var e))
        {
            e.LastAccess = Environment.TickCount64;
            value = e.Value;
            return true;
        }
        value = "";
        return false;
    }

    public void Set(string key, string value)
    {
        var size = Encoding.UTF8.GetByteCount(value) + key.Length * 2 + 64;
        if (size > _maxBytes) return;

        _map.AddOrUpdate(key,
            _ =>
            {
                Interlocked.Add(ref _bytes, size);
                Interlocked.Increment(ref _entries);
                return new Entry(value, size);
            },
            (_, old) =>
            {
                Interlocked.Add(ref _bytes, size - old.Size);
                return new Entry(value, size);
            });

        EvictIfNeeded();
    }

    public void RemoveByPrefix(string prefix)
    {
        foreach (var key in _map.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (_map.TryRemove(key, out var e))
            {
                Interlocked.Add(ref _bytes, -e.Size);
                Interlocked.Decrement(ref _entries);
            }
        }
    }

    public void InvalidateMessage(long id)
    {
        RemoveByPrefix($"post:{id}");
        RemoveByPrefix($"thread:{id}");
        RemoveByPrefix($"context:{id}");
        RemoveByPrefix($"p:{id}");
    }

    private void EvictIfNeeded()
    {
        while (Interlocked.Read(ref _bytes) > _maxBytes && !_map.IsEmpty)
        {
            var victim = _map.OrderBy(kv => kv.Value.LastAccess).Select(kv => kv.Key).FirstOrDefault();
            if (victim is null) break;
            if (_map.TryRemove(victim, out var e))
            {
                Interlocked.Add(ref _bytes, -e.Size);
                Interlocked.Decrement(ref _entries);
            }
        }
    }

    private sealed class Entry
    {
        public string Value;
        public int Size;
        public long LastAccess;

        public Entry(string value, int size)
        {
            Value = value;
            Size = size;
            LastAccess = Environment.TickCount64;
        }
    }
}
