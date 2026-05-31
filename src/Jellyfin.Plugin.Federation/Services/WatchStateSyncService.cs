using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Watch-state writeback for LOCAL playback that resolves to a peer item via TMDB/IMDB.
///
/// Federated playback (playing a fed_X id directly) is handled separately by
/// FederationInterceptMiddleware.TryForwardPlaybackSession + WritePeerUserData, which
/// forwards /Sessions/Playing/* events to the source peer and explicitly writes
/// PlaybackPositionTicks via /Users/{uid}/Items/{remoteId}/UserData. The two paths
/// together cover both directions: a user can resume on either node.
/// </summary>
public class WatchStateSyncService : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly RemoteJellyfinClient _client;
    private readonly ILogger<WatchStateSyncService> _logger;

    public WatchStateSyncService(IUserDataManager userDataManager, RemoteJellyfinClient client, ILogger<WatchStateSyncService> logger)
    {
        _userDataManager = userDataManager;
        _client = client;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("Federation watch-state sync hook armed.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        GC.SuppressFinalize(this);
    }

    // Cache the peer item-id we resolve for a (peer, providerId) so progress ticks during one
    // playback don't re-issue a /Items lookup to every peer on every save. Negative results are
    // cached too (peer doesn't have the film) to avoid re-asking each tick.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Id, DateTime Exp)> _resolveCache = new();
    // Coalesce the flood of progress saves Jellyfin emits during playback: skip a writeback when
    // we pushed the same (peer, item) within the debounce window and the played-state is unchanged.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime When, bool Played, long Pos)> _lastPush = new();
    private static readonly TimeSpan ResolveTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PushDebounce = TimeSpan.FromSeconds(10);
    private static readonly long PosDeltaTicks = TimeSpan.FromSeconds(30).Ticks;

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            // Loop-break: a save with reason=Import was made by our own pull-direction sync.
            // Pushing it back to peers would ping-pong forever and overwrite peer's freshly-newer state.
            if (e.SaveReason == UserDataSaveReason.Import) return;

            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableWatchStateSync || config.RemoteServers.Count == 0) return;
            if (e.Item is null) return;

            var tmdb = e.Item.GetProviderId(MetadataProvider.Tmdb);
            var imdb = e.Item.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrEmpty(tmdb) && string.IsNullOrEmpty(imdb)) return;

            var played = e.UserData?.Played ?? false;
            var position = e.UserData?.PlaybackPositionTicks ?? 0L;

            // Snapshot the peer list on the synchronous handler thread; the background task
            // must not enumerate the live List<RemoteServer> while admin UI may be mutating it.
            var peerSnapshot = config.RemoteServers.Where(s => s.Enabled).ToArray();

            _ = Task.Run(async () =>
            {
                foreach (var server in peerSnapshot)
                {
                    try
                    {
                        var key = server.Id.ToString("N") + ":" + (tmdb ?? "") + ":" + (imdb ?? "");
                        // Debounce: always let a played-state flip through; otherwise skip near-duplicate
                        // progress saves inside the window.
                        if (_lastPush.TryGetValue(key, out var last)
                            && last.Played == played
                            && (DateTime.UtcNow - last.When) < PushDebounce
                            && Math.Abs(last.Pos - position) < PosDeltaTicks)
                            continue;

                        var remoteId = await ResolveCachedAsync(server, key, tmdb, imdb).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(remoteId)) continue;

                        if (position > 0)
                            await _client.UpdateProgressAsync(server, remoteId, position, CancellationToken.None).ConfigureAwait(false);
                        // Propagate played=false as well - un-marking watched should federate.
                        await _client.MarkPlayedAsync(server, remoteId, played, CancellationToken.None).ConfigureAwait(false);

                        if (_lastPush.Count > 5000) _lastPush.Clear();
                        _lastPush[key] = (DateTime.UtcNow, played, position);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Watch sync to {Server} failed", server.Name);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            // Runs on Jellyfin's synchronous UserDataSaved dispatcher - never throw back into it
            // or we'd break other subscribers.
            _logger.LogWarning(ex, "OnUserDataSaved handler failed");
        }
    }

    private async Task<string?> ResolveCachedAsync(Configuration.RemoteServer server, string key, string? tmdb, string? imdb)
    {
        if (_resolveCache.TryGetValue(key, out var c) && c.Exp > DateTime.UtcNow) return c.Id;
        var id = await _client.ResolveRemoteItemIdAsync(server, tmdb, imdb, CancellationToken.None).ConfigureAwait(false);
        if (_resolveCache.Count > 5000) _resolveCache.Clear();
        _resolveCache[key] = (id, DateTime.UtcNow + ResolveTtl);
        return id;
    }
}
