using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

public class FederationSyncTask : IScheduledTask
{
    private readonly RemoteJellyfinClient _client;
    private readonly RemoteItemStore _store;
    private readonly ILogger<FederationSyncTask> _logger;

    public FederationSyncTask(RemoteJellyfinClient client, RemoteItemStore store, ILogger<FederationSyncTask> logger)
    {
        _client = client;
        _store = store;
        _logger = logger;
    }

    public string Name => "Federation: sync peer libraries";

    public string Key => "FederationSync";

    public string Description => "Pull item catalogs from all configured peer Jellyfin servers and update the local federation cache.";

    public string Category => "Federation";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.RemoteServers.Count == 0)
        {
            _logger.LogInformation("No peer servers configured. Skipping.");
            return;
        }

        var syncStart = DateTime.UtcNow;
        var total = config.RemoteServers.Count;
        var done = 0;

        foreach (var server in config.RemoteServers)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!server.Enabled) { done++; continue; }

            try
            {
                if (!await _client.PingAsync(server, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("Peer {Name} offline, skipping", server.Name);
                    continue; // finally increments done + reports progress
                }

                // Gossip step: ask peer for catalog digest. If it matches our cached one,
                // skip the full pull (anti-spam — no point downloading 10k items every minute
                // when nothing changed). Peers without the plugin return null → fall back to full pull.
                var digest = await _client.FetchDigestAsync(server, cancellationToken).ConfigureAwait(false);
                if (digest is { } d)
                {
                    var cached = _store.GetCachedDigest(server.Id);
                    if (cached == d.Hash)
                    {
                        _logger.LogDebug("Peer {Name} digest unchanged ({Hash}), skipping pull", server.Name, d.Hash);
                        continue; // finally still increments done + reports progress
                    }
                }

                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                var count = 0;
                await foreach (var item in _client.FetchItemsAsync(server, cancellationToken))
                {
                    _store.Upsert(item);
                    seenIds.Add(item.RemoteItemId);
                    count++;
                }

                // Delete detection: anything we had cached for this peer but didn't see this round is gone.
                var previousIds = _store.GetItemIdsForPeer(server.Id);
                previousIds.ExceptWith(seenIds);
                if (previousIds.Count > 0)
                {
                    _store.DeleteItemsByIds(server.Id, previousIds);
                    _logger.LogInformation("Removed {Count} deleted items from {Name}", previousIds.Count, server.Name);
                }
                _store.PurgeStale(server.Id, syncStart);

                // Re-fetch the digest AFTER the pull so the cached hash reflects what we actually
                // stored, not the pre-pull peer state (which may have shifted during the pull).
                var postDigest = await _client.FetchDigestAsync(server, cancellationToken).ConfigureAwait(false);
                if (postDigest is { } pd)
                    _store.SaveDigest(server.Id, pd.Count, pd.Hash, null);

                _logger.LogInformation("Synced {Count} items from {Name}", count, server.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync failed for {Name}", server.Name);
            }
            finally
            {
                done++;
                progress.Report(100.0 * done / total);
            }
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 60;
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
            }
        };
    }
}
