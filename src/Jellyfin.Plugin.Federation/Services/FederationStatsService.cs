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

        // Snapshot the live list once — concurrent admin saves can mutate RemoteServers via
        // /System/Configuration/Plugins endpoint while we iterate, otherwise.
        foreach (var server in config.RemoteServers.ToArray())
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
        // Only TMDB-bearing rows can contribute to dedup detection at all. Comparing distinct
        // TMDB to all rows (including items lacking a TMDB id — episodes, anime, home media,
        // not-yet-matched items) wildly inflates the ratio. Use TMDB-bearing rows on both sides.
        var (tmdbRowCount, distinctTmdb) = _store.CountTmdbRowsAndDistinct();
        var dedupRatio = ComputeDedupRatio(tmdbRowCount, distinctTmdb);

        var serversSnapshot = config.RemoteServers.ToArray();

        return new FederationStats
        {
            GeneratedUtc = DateTime.UtcNow,
            PeerCount = serversSnapshot.Length,
            EnabledPeerCount = serversSnapshot.Count(s => s.Enabled),
            OnlinePeerCount = serversSnapshot.Count(s => _health.Get(s.Id).Online),
            TotalCachedItems = totalCachedItems,
            DistinctItems = distinctTmdb,
            DedupRatio = dedupRatio,
            TotalStreamCount = totalStreams,
            TotalBytesServed = totalBytes,
            Peers = perPeer,
            TopStreamed = _store.TopStreamedItems(10)
                .Select(t => new TopStreamedEntry { PeerId = t.PeerId, ItemId = t.ItemId, PlayCount = t.PlayCount, Bytes = t.Bytes })
                .ToList()
        };
    }

    /// <summary>
    /// Dedup ratio for the federation dashboard. 0 = every TMDB-bearing item is unique
    /// across peers. 1 = pure duplication (theoretically — distinct=0). 0.5 = on average,
    /// every item is on two peers. Rounded to 4 decimal places for display.
    /// </summary>
    /// <param name="tmdbRowCount">Total rows in remote_items with non-empty tmdb.</param>
    /// <param name="distinctTmdb">DISTINCT tmdb values across those rows.</param>
    public static double ComputeDedupRatio(int tmdbRowCount, int distinctTmdb)
    {
        if (tmdbRowCount <= 0) return 0.0;
        if (distinctTmdb <= 0) return 1.0; // every row is a dup, distinct count is bogus → clamp
        if (distinctTmdb > tmdbRowCount) return 0.0; // can't have more distinct values than rows
        return Math.Round(1.0 - ((double)distinctTmdb / tmdbRowCount), 4);
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
