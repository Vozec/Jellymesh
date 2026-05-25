using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

public class FederationSyncTask : IScheduledTask
{
    private readonly RemoteJellyfinClient _client;
    private readonly RemoteItemStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly SyncProgressTracker _progress;
    private readonly ILogger<FederationSyncTask> _logger;

    public FederationSyncTask(
        RemoteJellyfinClient client,
        RemoteItemStore store,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        SyncProgressTracker progress,
        ILogger<FederationSyncTask> logger)
    {
        _client = client;
        _store = store;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _progress = progress;
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

        _progress.BeginRound(config.RemoteServers.Select(s => (s.Id, s.Name)));

        foreach (var server in config.RemoteServers)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!server.Enabled) { done++; _progress.Update(server.Id, SyncProgressTracker.Phase.Skipped, 100, detail: "disabled"); continue; }

            try
            {
                _progress.Update(server.Id, SyncProgressTracker.Phase.Pinging, 5);
                if (!await _client.PingAsync(server, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("Peer {Name} offline, skipping", server.Name);
                    _progress.Update(server.Id, SyncProgressTracker.Phase.Skipped, 100, detail: "offline");
                    continue;
                }

                // Gossip step: ask peer for catalog digest. If it matches our cached one,
                // skip the full pull (anti-spam - no point downloading 10k items every minute
                // when nothing changed). Peers without the plugin return null → fall back to full pull.
                var digest = await _client.FetchDigestAsync(server, cancellationToken).ConfigureAwait(false);
                if (digest is { } d)
                {
                    var cached = _store.GetCachedDigest(server.Id);
                    if (cached == d.Hash)
                    {
                        _logger.LogDebug("Peer {Name} digest unchanged ({Hash}), skipping pull", server.Name, d.Hash);
                        _progress.Update(server.Id, SyncProgressTracker.Phase.Skipped, 100, detail: "cache hit (digest unchanged)");
                        continue;
                    }
                }

                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                var count = 0;
                var expected = digest?.Count ?? 0;
                _progress.Update(server.Id, SyncProgressTracker.Phase.Pulling, 10, total: expected, detail: "fetching items");
                await foreach (var item in _client.FetchItemsAsync(server, cancellationToken))
                {
                    _store.Upsert(item);
                    seenIds.Add(item.RemoteItemId);
                    count++;
                    // Report progress every 20 items to avoid hammering the tracker on huge libs.
                    if (count % 20 == 0)
                    {
                        var pct = expected > 0 ? 10 + (int)(80.0 * count / expected) : Math.Min(80, 10 + count);
                        _progress.Update(server.Id, SyncProgressTracker.Phase.Pulling, pct, seen: count, total: expected);
                    }
                }
                _progress.Update(server.Id, SyncProgressTracker.Phase.Saving, 92, seen: count, total: expected, detail: "saving");

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
                _progress.Update(server.Id, SyncProgressTracker.Phase.Done, 100, seen: count, total: expected);

                // Pull peer's user data into a configured local user (if any).
                if (!string.IsNullOrEmpty(server.LocalUserIdForSync) && Guid.TryParse(server.LocalUserIdForSync, out var localUid))
                {
                    var pulled = await PullWatchStateAsync(server, localUid, cancellationToken).ConfigureAwait(false);
                    if (pulled > 0)
                        _logger.LogInformation("Pulled {Count} watch-state entries from {Name} into user {Uid}",
                            pulled, server.Name, localUid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync failed for {Name}", server.Name);
                _progress.Update(server.Id, SyncProgressTracker.Phase.Failed, 100, detail: ex.Message);
            }
            finally
            {
                done++;
                progress.Report(100.0 * done / total);
            }
        }

        _progress.CompleteRound();
    }

    private async Task<int> PullWatchStateAsync(Configuration.RemoteServer server, Guid localUserId, CancellationToken ct)
    {
        var user = _userManager.GetUserById(localUserId);
        if (user is null)
        {
            _logger.LogWarning("LocalUserIdForSync {Uid} not found in user manager", localUserId);
            return 0;
        }

        var applied = 0;
        await foreach (var entry in _client.FetchUserDataAsync(server, ct))
        {
            if (ct.IsCancellationRequested) break;

            var tmdb = entry.ProviderIds.GetValueOrDefault("Tmdb");
            var imdb = entry.ProviderIds.GetValueOrDefault("Imdb");
            if (string.IsNullOrEmpty(tmdb) && string.IsNullOrEmpty(imdb)) continue;

            var localItem = FindLocalByProviderId(tmdb, imdb);
            if (localItem is null) continue;

            var existing = _userDataManager.GetUserData(user, localItem);
            // Merge rules:
            //   Played:   OR (never demote - peer reporting unplayed doesn't unwatch local)
            //   Position: only when peer's LastPlayedDate is strictly newer than ours
            //   Favorite: OR
            //   PlayCount: max
            // LastPlayedDate is updated to max(theirs, ours) - never moved backward.
            var incomingNewer = entry.LastPlayedDate.HasValue
                && (!existing.LastPlayedDate.HasValue || entry.LastPlayedDate > existing.LastPlayedDate);

            var changed = false;
            if (entry.Played && !existing.Played)
            {
                existing.Played = true;
                // Promote a played-flag carry-over with a timestamp so future merges can order.
                if (entry.LastPlayedDate.HasValue && (!existing.LastPlayedDate.HasValue || entry.LastPlayedDate > existing.LastPlayedDate))
                    existing.LastPlayedDate = entry.LastPlayedDate;
                else if (!existing.LastPlayedDate.HasValue)
                    existing.LastPlayedDate = DateTime.UtcNow;
                changed = true;
            }
            if (incomingNewer && entry.PlaybackPositionTicks != existing.PlaybackPositionTicks)
            {
                existing.PlaybackPositionTicks = entry.PlaybackPositionTicks;
                existing.LastPlayedDate = entry.LastPlayedDate; // already known non-null + newer
                changed = true;
            }
            if (entry.IsFavorite && !existing.IsFavorite)
            {
                existing.IsFavorite = true;
                changed = true;
            }
            if (entry.PlayCount > existing.PlayCount)
            {
                existing.PlayCount = entry.PlayCount;
                changed = true;
            }

            if (changed)
            {
                // Reason = Import so WatchStateSyncService's push handler skips this save and
                // doesn't bounce it back to the peer (loop break).
                _userDataManager.SaveUserData(user, localItem, existing, UserDataSaveReason.Import, ct);
                applied++;
            }
        }
        return applied;
    }

    private BaseItem? FindLocalByProviderId(string? tmdb, string? imdb)
    {
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            Limit = 1
        };
        if (!string.IsNullOrEmpty(tmdb))
            q.HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = tmdb };
        else if (!string.IsNullOrEmpty(imdb))
            q.HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Imdb.ToString()] = imdb };
        else
            return null;

        return _libraryManager.GetItemList(q).FirstOrDefault();
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
