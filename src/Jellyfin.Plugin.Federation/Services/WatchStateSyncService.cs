using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

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

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableWatchStateSync || config.RemoteServers.Count == 0) return;
        if (e.Item is null) return;

        var tmdb = e.Item.GetProviderId(MetadataProvider.Tmdb);
        var imdb = e.Item.GetProviderId(MetadataProvider.Imdb);
        if (string.IsNullOrEmpty(tmdb) && string.IsNullOrEmpty(imdb)) return;

        var played = e.UserData?.Played ?? false;
        var position = e.UserData?.PlaybackPositionTicks ?? 0L;

        _ = Task.Run(async () =>
        {
            foreach (var server in config.RemoteServers.Where(s => s.Enabled))
            {
                try
                {
                    var remoteId = await _client.ResolveRemoteItemIdAsync(server, tmdb, imdb, CancellationToken.None).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(remoteId)) continue;

                    if (position > 0)
                        await _client.UpdateProgressAsync(server, remoteId, position, CancellationToken.None).ConfigureAwait(false);
                    if (played)
                        await _client.MarkPlayedAsync(server, remoteId, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Watch sync to {Server} failed", server.Name);
                }
            }
        });
    }
}
