using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Channels;

public class FriendsLibraryChannel : IChannel, IHasCacheKey
{
    private readonly RemoteItemStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly PeerHealthRegistry _health;
    private readonly ILogger<FriendsLibraryChannel> _logger;

    public FriendsLibraryChannel(RemoteItemStore store, ILibraryManager libraryManager, PeerHealthRegistry health, ILogger<FriendsLibraryChannel> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _health = health;
        _logger = logger;
    }

    public string Name => "Friends Library";

    public string Description => "Media available on federated peer Jellyfin servers but not in your local library.";

    // DataVersion only changes when peer health flips - _health.Signature() embeds a
    // counter that increments on every state transition. No date prefix → the channel
    // cache file isn't thrown away every hour for nothing.
    public string DataVersion => _health.Signature();

    public string HomePageUrl => "https://github.com/vozec/JellyfinFederation";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie, ChannelMediaContentType.Episode },
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        MaxPageSize = 200,
        AutoRefreshLevels = 3
    };

    public bool IsEnabledFor(string userId) => Plugin.Instance?.Configuration.ShowRemoteOnlyItems ?? true;

    public string GetCacheKey(string? userId) => $"federation-{_health.Signature()}-{userId}";

    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary };

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.ShowRemoteOnlyItems)
            return Task.FromResult(new ChannelItemResult { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 });

        var localTmdbIds = CollectLocalTmdbIds();
        var items = new List<ChannelItemInfo>();

        foreach (var remote in _store.GetAllItems())
        {
            if (remote.Type != "Movie" && remote.Type != "Episode") continue;

            var tmdb = remote.ProviderIds.GetValueOrDefault("Tmdb");
            if (!string.IsNullOrEmpty(tmdb) && localTmdbIds.Contains(tmdb))
                continue;

            var server = config.RemoteServers.FirstOrDefault(s => s.Id == remote.ServerId);
            if (server is null || !server.Enabled) continue;
            if (!_health.IsOnline(server.Id)) continue;

            var info = new ChannelItemInfo
            {
                Id = $"fed_{remote.ServerId:N}_{remote.RemoteItemId}",
                Name = remote.Name,
                Type = ChannelItemType.Media,
                ContentType = remote.Type == "Episode" ? ChannelMediaContentType.Episode : ChannelMediaContentType.Movie,
                MediaType = ChannelMediaType.Video,
                ProductionYear = remote.ProductionYear,
                ProviderIds = new Dictionary<string, string>(remote.ProviderIds),
                // Image is fetched server-side by the plugin's reverse-proxy endpoint
                // (FederationController.ProxyImage) so the peer API key never reaches the client.
                ImageUrl = $"/Federation/Image/{server.Id:N}/{remote.RemoteItemId}/Primary",
                MediaSources = BuildMediaSources(server.Id, remote)
            };
            items.Add(info);
        }

        var pageStart = query.StartIndex ?? 0;
        var pageLimit = query.Limit ?? items.Count;
        var paged = items.Skip((int)pageStart).Take((int)pageLimit).ToList();

        return Task.FromResult(new ChannelItemResult
        {
            Items = paged,
            TotalRecordCount = items.Count
        });
    }

    private HashSet<string> CollectLocalTmdbIds()
    {
        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode },
                Recursive = true
            };
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _libraryManager.GetItemList(query))
            {
                var t = item.GetProviderId(MetadataProvider.Tmdb);
                if (!string.IsNullOrEmpty(t)) ids.Add(t);
            }
            return ids;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate local TMDB ids");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<MediaSourceInfo> BuildMediaSources(Guid serverId, Models.RemoteItem remote)
    {
        var list = new List<MediaSourceInfo>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<MediaSourceInfo>>(remote.MediaSourceJson ?? "[]");
            if (parsed is null) return list;
            foreach (var ms in parsed)
            {
                var originalSourceId = ms.Id;
                ms.Id = $"fed_{serverId:N}_{originalSourceId}";
                ms.IsRemote = true;
                // Pass the ORIGINAL source id in the proxy URL - the peer doesn't know
                // about the fed_<guid>_ prefix.
                ms.Path = $"/Federation/Stream/{serverId:N}/{remote.RemoteItemId}?sourceId={Uri.EscapeDataString(originalSourceId ?? string.Empty)}";
                list.Add(ms);
            }
        }
        catch
        {
            // ignore malformed source json
        }
        return list;
    }
}
