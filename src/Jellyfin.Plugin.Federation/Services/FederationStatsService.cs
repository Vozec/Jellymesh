using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services;

public class FederationStatsService
{
    private readonly RemoteItemStore _store;
    private readonly PeerHealthRegistry _health;

    public FederationStatsService(RemoteItemStore store, PeerHealthRegistry health)
    {
        _store = store;
        _health = health;
    }

    public FederationStats Build()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return new FederationStats();

        var perPeerCount = _store.CountItemsPerPeer().ToDictionary(t => t.PeerId, t => t.ItemCount);

        var perPeer = new List<PeerStat>();
        long totalBytes = 0;
        int totalStreams = 0;

        foreach (var server in config.RemoteServers)
        {
            var h = _health.Get(server.Id);
            var totals = _store.PeerStreamTotals(server.Id);
            totalBytes += totals.TotalBytes;
            totalStreams += totals.StreamCount;

            perPeer.Add(new PeerStat
            {
                Id = server.Id,
                Name = server.Name,
                Enabled = server.Enabled,
                Online = h.Online,
                LastCheckUtc = h.LastCheckUtc,
                LastRttMs = h.LastRttMs,
                CachedItemCount = perPeerCount.GetValueOrDefault(server.Id),
                StreamCount = totals.StreamCount,
                BytesServed = totals.TotalBytes
            });
        }

        var totalCachedItems = perPeerCount.Values.Sum();
        var distinctTmdb = _store.CountDistinctTmdbAcrossPeers();
        // Dedup ratio: of all cached entries, what fraction is duplicate (same TMDB on multiple peers).
        // 0 = every item unique, 1 = pure duplication. Useful to see how much your peers overlap.
        var dedupRatio = totalCachedItems > 0
            ? 1.0 - ((double)distinctTmdb / totalCachedItems)
            : 0.0;

        return new FederationStats
        {
            GeneratedUtc = DateTime.UtcNow,
            PeerCount = config.RemoteServers.Count,
            EnabledPeerCount = config.RemoteServers.Count(s => s.Enabled),
            OnlinePeerCount = config.RemoteServers.Count(s => _health.Get(s.Id).Online),
            TotalCachedItems = totalCachedItems,
            DistinctItems = distinctTmdb,
            DedupRatio = Math.Round(dedupRatio, 4),
            TotalStreamCount = totalStreams,
            TotalBytesServed = totalBytes,
            Peers = perPeer,
            TopStreamed = _store.TopStreamedItems(10)
                .Select(t => new TopStreamedEntry { PeerId = t.PeerId, ItemId = t.ItemId, PlayCount = t.PlayCount, Bytes = t.Bytes })
                .ToList()
        };
    }
}

public class FederationStats
{
    public DateTime GeneratedUtc { get; set; }
    public int PeerCount { get; set; }
    public int EnabledPeerCount { get; set; }
    public int OnlinePeerCount { get; set; }
    public int TotalCachedItems { get; set; }
    public int DistinctItems { get; set; }
    public double DedupRatio { get; set; }
    public int TotalStreamCount { get; set; }
    public long TotalBytesServed { get; set; }
    public List<PeerStat> Peers { get; set; } = new();
    public List<TopStreamedEntry> TopStreamed { get; set; } = new();
}

public class PeerStat
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Online { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public int LastRttMs { get; set; }
    public int CachedItemCount { get; set; }
    public int StreamCount { get; set; }
    public long BytesServed { get; set; }
}

public class TopStreamedEntry
{
    public Guid PeerId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public int PlayCount { get; set; }
    public long Bytes { get; set; }
}
