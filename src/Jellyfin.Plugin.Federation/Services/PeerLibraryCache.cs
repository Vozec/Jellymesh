using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// In-memory cache for /Federation/Peers/{id}/Libraries and per-library item lists.
/// Avoids hammering peer servers every time the home page mounts. Invalidated by
/// PeerHealthRegistry health flips and by PushInvalidationService notifications.
/// </summary>
public class PeerLibraryCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private record CacheEntry(string Json, DateTime Stored);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    public bool TryGet(string key, out string json)
    {
        json = string.Empty;
        if (!_entries.TryGetValue(key, out var e)) return false;
        if (DateTime.UtcNow - e.Stored > DefaultTtl)
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        json = e.Json;
        return true;
    }

    public void Store(string key, string json)
    {
        _entries[key] = new CacheEntry(json, DateTime.UtcNow);
    }

    public void InvalidatePeer(Guid peerId)
    {
        var prefix = $"peer:{peerId:N}:";
        foreach (var k in _entries.Keys)
        {
            if (k.StartsWith(prefix, StringComparison.Ordinal)) _entries.TryRemove(k, out _);
        }
    }

    public void Clear() => _entries.Clear();

    public static string LibsKey(Guid peerId) => $"peer:{peerId:N}:libs";
    public static string LibItemsKey(Guid peerId, string libId, int limit) => $"peer:{peerId:N}:lib:{libId}:limit:{limit}";
}
