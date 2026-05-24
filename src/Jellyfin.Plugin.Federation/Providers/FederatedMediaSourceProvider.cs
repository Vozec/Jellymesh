using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    private readonly ILogger<FederatedMediaSourceProvider> _logger;

    public FederatedMediaSourceProvider(RemoteItemStore store, ILogger<FederatedMediaSourceProvider> logger)
    {
        _store = store;
        _logger = logger;
    }

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

            try
            {
                var remoteSources = JsonSerializer.Deserialize<List<MediaSourceInfo>>(match.MediaSourceJson ?? "[]");
                if (remoteSources is null) continue;

                foreach (var rs in remoteSources)
                {
                    rs.Id = $"fed_{server.Id:N}_{rs.Id}";
                    rs.Protocol = MediaProtocol.Http;
                    rs.IsRemote = true;
                    rs.Path = BuildProxyUrl(server.Id, match.RemoteItemId, rs.Id);
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
