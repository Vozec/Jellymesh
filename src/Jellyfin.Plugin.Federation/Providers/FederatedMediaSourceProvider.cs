using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers;

public class FederatedMediaSourceProvider : IMediaSourceProvider
{
    private readonly RemoteItemStore _store;
    private readonly PeerHealthRegistry _health;
    private readonly ILogger<FederatedMediaSourceProvider> _logger;

    public FederatedMediaSourceProvider(RemoteItemStore store, PeerHealthRegistry health, ILogger<FederatedMediaSourceProvider> logger)
    {
        _store = store;
        _health = health;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions _msJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableDedup || config.RemoteServers.Count == 0)
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

        var tmdb = item.GetProviderId("Tmdb");
        var imdb = item.GetProviderId("Imdb");
        var title = item.Name;
        var year = item.ProductionYear;

        var sources = new List<MediaSourceInfo>();

        foreach (var match in _store.FindMatches(tmdb, imdb, title, year))
        {
            var server = config.RemoteServers.FirstOrDefault(s => s.Id == match.ServerId);
            if (server is null || !server.Enabled) continue;
            if (!_health.IsOnline(server.Id)) continue;

            try
            {
                var remoteSources = JsonSerializer.Deserialize<List<MediaSourceInfo>>(match.MediaSourceJson ?? "[]", _msJson);
                if (remoteSources is null) continue;

                foreach (var rs in remoteSources)
                {
                    var originalSourceId = rs.Id;
                    rs.Id = $"fed_{server.Id:N}_{originalSourceId}";
                    rs.IsRemote = true;
                    rs.Path = BuildProxyUrl(server.Id, match.RemoteItemId, originalSourceId);
                    // Note: leave rs.Protocol as set by the peer (File/Hls/Http) - the player
                    // sees the proxy URL via Path; forcing Http here strips Hls semantics from
                    // MediaSourceInfo that the player relies on for direct-play decisions.
                    rs.Name = $"[{server.Name}] {rs.Name}";
                    sources.Add(rs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize remote source for {Item}", item.Name);
            }
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    private static string BuildProxyUrl(Guid serverId, string remoteItemId, string sourceId)
        => $"/Federation/Stream/{serverId:N}/{remoteItemId}?sourceId={sourceId}";
}
