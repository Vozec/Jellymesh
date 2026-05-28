using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Pipeline middleware that intercepts /Items/fed_X/... and /Items/fedlib_X/... requests
/// BEFORE Jellyfin's MVC routing tries to bind itemId as a Guid (which rejects our
/// federated ids with 400). For images we proxy to the peer; for metadata requests we
/// return a stub DTO so the SPA's movies.html + details pages stop erroring.
///
/// Design rationale: Jellyfin's plugin model has no "virtual item provider" extension
/// point for items that live outside the local library — IItemRepository is sealed
/// to ItemsManager and IPostScanTask runs offline. Middleware is the only stable
/// integration surface that lets us synthesise BaseItemDto payloads for ids the SPA
/// asks about. Same applies to /Sessions/Playing/* — ISessionManager events fire only
/// for items present in the local DB, so for fed_X writeback we intercept the POST
/// and forward to the peer ourselves.
///
/// Response-body buffering for /Items?ParentId list responses is required because the
/// upstream MVC controller returns the list shape we want to append to; we strip
/// Accept-Encoding before _next so the response-compression middleware doesn't gzip
/// our buffer.
/// </summary>
public class FederationInterceptMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MediaBrowser.Controller.IServerApplicationHost _appHost;
    private readonly ILogger<FederationInterceptMiddleware> _logger;

    // Hot-path patterns: middleware fires for every HTTP request, so recompiling these on each
    // call was 10 fresh regex parses per request. Lifted to RegexOptions.Compiled statics.
    private const System.Text.RegularExpressions.RegexOptions RxOpt =
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
    private static readonly System.Text.RegularExpressions.Regex RxPlaybackInfo =
        new(@"^/Items/(fed_[0-9a-fA-F]+_[^/]+)/PlaybackInfo$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxSessionsPlaying =
        new(@"^/Sessions/Playing(?:/(?:Progress|Stopped))?$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxItemsList =
        new(@"^(?:/Users/[^/]+)?/Items$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxItemsLatest =
        new(@"^/Users/[^/]+/Items/Latest$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxImage =
        new(@"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)/Images/([^/]+)(?:/(\d+))?$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxItem =
        new(@"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxLib =
        new(@"^(?:/Users/[^/]+)?/Items/(fedlib_[0-9a-fA-F]+_[^/]+)$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxTheme =
        new(@"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)/(ThemeMedia|ThemeSongs|ThemeVideos)$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxSubPath =
        new(@"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)/(Similar|InstantMix|SpecialFeatures|Trailers|Intros|RemoteImages|RemoteSearch|AdditionalParts|Ancestors|ParentalRating)$", RxOpt);
    private static readonly System.Text.RegularExpressions.Regex RxLocalGuid =
        new(@"^[0-9a-fA-F]{32}$", RxOpt);

    public FederationInterceptMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory, MediaBrowser.Controller.IServerApplicationHost appHost, ILogger<FederationInterceptMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _appHost = appHost;
        _logger = logger;
    }

    private string LocalServerId => _appHost.SystemId ?? string.Empty;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value;
        if (path is null)
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // PlaybackInfo is a POST; route it through our peer proxy + path rewrite before any
        // other matcher so playback works end to end. Without this Jellyfin's stock route
        // returns 400 on the federated id.
        if (ctx.Request.Method == HttpMethods.Post)
        {
            var pbMatch = RxPlaybackInfo.Match(path);
            if (pbMatch.Success)
            {
                await ProxyPlaybackInfo(ctx, pbMatch.Groups[1].Value).ConfigureAwait(false);
                return;
            }
            // /Sessions/Playing, /Sessions/Playing/Progress, /Sessions/Playing/Stopped:
            // the SPA reports playback state with itemId=fed_X. Jellyfin's stock handler drops
            // it because the item isn't in the local library, so peer never sees progress.
            // Rewrite + forward to the source peer with our outbound API key.
            if (RxSessionsPlaying.IsMatch(path))
            {
                if (await TryForwardPlaybackSession(ctx).ConfigureAwait(false)) return;
            }
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        if (ctx.Request.Method != HttpMethods.Get)
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // /Items?ParentId=fedlib_X — list query for a federated library. The native
        // movies.html SPA controller issues this; we synthesise an Items[] envelope from
        // /Federation/Peers/{peer}/Libraries/{lib}/Items. Single-flight: any /Items request
        // (with or without /Users/{uid} prefix) qualifies.
        var pathIsItemsList = RxItemsList.IsMatch(path);
        if (pathIsItemsList)
        {
            var parentId = ctx.Request.Query["ParentId"].ToString();
            if (parentId.StartsWith("fedlib_", StringComparison.Ordinal))
            {
                await ProxyFedLibList(ctx, parentId).ConfigureAwait(false);
                return;
            }
            // /Items?ParentId=<localLib> with merge mapping → forward to Jellyfin, then
            // append peer items into the response. ResponseWrap below handles it.
            if (IsLocalGuid(parentId) && HasMergeFor(parentId))
            {
                await AppendPeerItems(ctx, parentId).ConfigureAwait(false);
                return;
            }
        }

        // /Users/{uid}/Items/Latest?ParentId=<localLib>: Home page 'Recently Added' carousel
        // calls this for each library. Returns a flat array (not wrapped). Append peer items
        // when the local lib has merge mappings so the carousel shows federated 'recents' too.
        var pathIsLatest = RxItemsLatest.IsMatch(path);
        if (pathIsLatest)
        {
            var parentId = ctx.Request.Query["ParentId"].ToString();
            if (IsLocalGuid(parentId) && HasMergeFor(parentId))
            {
                await AppendPeerLatest(ctx, parentId).ConfigureAwait(false);
                return;
            }
        }

        // Match /Items/{fed_X}/Images/{type} OR /Items/{fed_X}/Images/{type}/{index}.
        // Backdrop URLs include an /0 suffix to pick the Nth artwork.
        var imageMatch = RxImage.Match(path);
        if (imageMatch.Success)
        {
            var indexPart = imageMatch.Groups[3].Success ? "/" + imageMatch.Groups[3].Value : string.Empty;
            await ProxyImage(ctx, imageMatch.Groups[1].Value, imageMatch.Groups[2].Value + indexPart).ConfigureAwait(false);
            return;
        }

        // Match /Items/{fed_X} OR /Users/{uid}/Items/{fed_X} (item details probe).
        var itemMatch = RxItem.Match(path);
        if (itemMatch.Success)
        {
            await ProxyItemDetail(ctx, itemMatch.Groups[1].Value).ConfigureAwait(false);
            return;
        }

        // Match /Items/{fedlib_X} OR /Users/{uid}/Items/{fedlib_X} (collection folder probe).
        var libMatch = RxLib.Match(path);
        if (libMatch.Success)
        {
            await WriteStubFolder(ctx, libMatch.Groups[1].Value).ConfigureAwait(false);
            return;
        }

        // ThemeMedia / ThemeSongs / ThemeVideos need the wrapped shape with OwnerId, or the
        // SPA throws 'TypeError: cannot read OwnerId of undefined' (themeMediaPlayer.js:111).
        var themeMatch = RxTheme.Match(path);
        if (themeMatch.Success)
        {
            await WriteThemeMediaShape(ctx, themeMatch.Groups[1].Value, themeMatch.Groups[2].Value).ConfigureAwait(false);
            return;
        }

        // Similar / Intros / etc. - return empty list so the SPA's secondary
        // fetches stop 400ing.
        var subPath = RxSubPath.Match(path);
        if (subPath.Success)
        {
            await WriteEmptyList(ctx).ConfigureAwait(false);
            return;
        }

        await _next(ctx).ConfigureAwait(false);
    }

    private async Task ProxyImage(HttpContext ctx, string fedId, string imageType)
    {
        var rest = fedId.Substring("fed_".Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0) { ctx.Response.StatusCode = 400; return; }
        var peerN = rest.Substring(0, sep);
        var remoteId = rest.Substring(sep + 1);
        if (!Guid.TryParseExact(peerN, "N", out var peerId)) { ctx.Response.StatusCode = 400; return; }

        var config = Plugin.Instance?.Configuration;
        var peer = config?.RemoteServers.FirstOrDefault(p => p.Id == peerId);
        if (peer is null || !peer.Enabled) { ctx.Response.StatusCode = 404; return; }

        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            // Pass through the upstream Jellyfin's query string (fillHeight/fillWidth/quality)
            // minus our injected tag/api_key sentinels.
            var qs = ctx.Request.QueryString.Value ?? string.Empty;
            qs = System.Text.RegularExpressions.Regex.Replace(qs, @"([?&])(api_key|tag)=[^&]*", "$1");
            qs = qs.Replace("?&", "?").TrimEnd('?', '&');
            // imageType may include an index suffix (e.g. 'Backdrop/0'); escape per segment
            // so the slash stays a path separator rather than %2F which peers reject.
            var imageTypePath = string.Join('/', imageType.Split('/').Select(Uri.EscapeDataString));
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}/Images/{imageTypePath}{qs}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentType?.MediaType is { } ct) ctx.Response.ContentType = ct;
            ctx.Response.Headers["Cache-Control"] = "public, max-age=600";
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Federated image proxy failed for {Item}", fedId);
            ctx.Response.StatusCode = 502;
        }
    }

    private async Task ProxyItemDetail(HttpContext ctx, string fedId)
    {
        // Proxy the peer's full BaseItemDto so the details page shows real title, overview,
        // year, genres etc. instead of the bare 'federated' stub. Id stays as fed_X +
        // ImageTags is forced to {Primary:'fed'} so /Items/fed_X/Images/Primary still routes
        // through our image proxy.
        var rest = fedId.Substring("fed_".Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0) { ctx.Response.StatusCode = 400; return; }
        var peerN = rest.Substring(0, sep);
        var remoteId = rest.Substring(sep + 1);
        if (!Guid.TryParseExact(peerN, "N", out var peerId)) { ctx.Response.StatusCode = 400; return; }

        var config = Plugin.Instance?.Configuration;
        var peer = config?.RemoteServers.FirstOrDefault(p => p.Id == peerId);
        if (peer is null || !peer.Enabled) { await WriteStubItem(ctx, fedId).ConfigureAwait(false); return; }

        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Users/{Uri.EscapeDataString(peer.RemoteUserId ?? string.Empty)}/Items/{Uri.EscapeDataString(remoteId)}";
            if (string.IsNullOrEmpty(peer.RemoteUserId))
                url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { await WriteStubItem(ctx, fedId).ConfigureAwait(false); return; }
            using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            // Clone the entire DTO into a mutable dictionary so we can rewrite the few
            // identity fields without losing any of the rich metadata (overview, genres,
            // tags, mediastreams, runtime, studios, people, etc.).
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = JsonElementToObject(p.Value);
            dict["Id"] = fedId;
            dict["ServerId"] = LocalServerId;
            dict["ImageTags"] = new System.Collections.Generic.Dictionary<string, string> { ["Primary"] = "fed" };
            // People[].Id are person item ids on the peer; the SPA renders cast images via
            // /Items/{personId}/Images/Primary. Without the fed_ prefix the local server
            // 404s on each one. Rewrite so the request flows back through our image proxy.
            if (dict.TryGetValue("People", out var peopleObj) && peopleObj is System.Collections.Generic.List<object?> people)
            {
                foreach (var entry in people)
                {
                    if (entry is not System.Collections.Generic.Dictionary<string, object?> person) continue;
                    if (person.TryGetValue("Id", out var pid) && pid is string pidStr && !string.IsNullOrEmpty(pidStr))
                    {
                        person["Id"] = "fed_" + peerN + "_" + pidStr;
                    }
                }
            }
            // MediaSources[].Id are peer-side ids; playbackmanager.js does
            // apiClient.getItem(userId, mediaSourceId || item.Id) at L2654 which 404s when
            // the bare peer id is queried locally. Rewrite + point Path at our /Federation/Stream
            // proxy so direct-play works without ever asking the local server to resolve the
            // peer id.
            if (dict.TryGetValue("MediaSources", out var msObj) && msObj is System.Collections.Generic.List<object?> sources)
            {
                foreach (var s in sources)
                {
                    if (s is not System.Collections.Generic.Dictionary<string, object?> ms) continue;
                    var origId = ms.TryGetValue("Id", out var idVal) ? idVal?.ToString() : remoteId;
                    var newId = "fed_" + peerN + "_" + origId;
                    ms["Id"] = newId;
                    ms["IsRemote"] = true;
                    ms["Protocol"] = "Http";
                    ms["Path"] = $"/Federation/Stream/{peerN}/{remoteId}?sourceId={Uri.EscapeDataString(origId ?? string.Empty)}";
                    ms["DirectStreamUrl"] = ms["Path"];
                    ms["SupportsDirectPlay"] = true;
                    ms["SupportsDirectStream"] = true;
                    ms["SupportsTranscoding"] = true;
                }
            }
            // SPA helpers like ThemeMediaPlayer + RatingHelper assume these fields exist on
            // every BaseItemDto; surface defaults so they don't blow up when the peer omitted
            // them (live TV / certain content types).
            if (!dict.ContainsKey("OwnerId") || dict["OwnerId"] is null) dict["OwnerId"] = string.Empty;
            if (!dict.ContainsKey("ChannelId") || dict["ChannelId"] is null) dict["ChannelId"] = string.Empty;
            if (!dict.ContainsKey("ParentLogoItemId") || dict["ParentLogoItemId"] is null) dict["ParentLogoItemId"] = string.Empty;
            if (!dict.ContainsKey("ParentBackdropItemId") || dict["ParentBackdropItemId"] is null) dict["ParentBackdropItemId"] = string.Empty;
            if (!dict.ContainsKey("ParentBackdropImageTags") || dict["ParentBackdropImageTags"] is null) dict["ParentBackdropImageTags"] = new System.Collections.Generic.List<string>();
            if (!dict.ContainsKey("BackdropImageTags") || dict["BackdropImageTags"] is null) dict["BackdropImageTags"] = new System.Collections.Generic.List<string>();
            if (!dict.ContainsKey("UserData") || dict["UserData"] is null)
                dict["UserData"] = new System.Collections.Generic.Dictionary<string, object?> { ["Played"] = false, ["IsFavorite"] = false, ["PlaybackPositionTicks"] = 0L, ["PlayCount"] = 0 };
            // Strip server-specific fields that would conflict with our id space.
            dict.Remove("PlaylistItemId");
            dict.Remove("AncestorIds");
            dict.Remove("ParentId");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(dict)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ProxyItemDetail failed for {Item}", fedId);
            await WriteStubItem(ctx, fedId).ConfigureAwait(false);
        }
    }

    private static object? JsonElementToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number: return el.TryGetInt64(out var l) ? (object)l : el.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Array:
                var list = new System.Collections.Generic.List<object?>();
                foreach (var x in el.EnumerateArray()) list.Add(JsonElementToObject(x));
                return list;
            case JsonValueKind.Object:
                var map = new System.Collections.Generic.Dictionary<string, object?>();
                foreach (var p in el.EnumerateObject()) map[p.Name] = JsonElementToObject(p.Value);
                return map;
            default: return null;
        }
    }

    private async Task ProxyPlaybackInfo(HttpContext ctx, string fedId)
    {
        // The SPA POSTs profile-based capabilities; we forward them unchanged to the peer
        // which decides direct-play / transcode and returns MediaSources. We then rewrite
        // each MediaSource.Path + Id so the player hits OUR /Federation/Stream proxy
        // instead of trying to reach the peer directly (the player only has session creds
        // for THIS server).
        var rest = fedId.Substring("fed_".Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0) { ctx.Response.StatusCode = 400; return; }
        var peerN = rest.Substring(0, sep);
        var remoteId = rest.Substring(sep + 1);
        if (!Guid.TryParseExact(peerN, "N", out var peerId)) { ctx.Response.StatusCode = 400; return; }

        var config = Plugin.Instance?.Configuration;
        var peer = config?.RemoteServers.FirstOrDefault(p => p.Id == peerId);
        if (peer is null || !peer.Enabled) { ctx.Response.StatusCode = 404; return; }

        try
        {
            // Buffer the incoming request body (capabilities + device profile).
            string body;
            using (var reader = new System.IO.StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            var qs = ctx.Request.QueryString.Value ?? string.Empty;
            // Strip our api_key sentinel; the peer wouldn't honour it.
            qs = System.Text.RegularExpressions.Regex.Replace(qs, @"([?&])api_key=[^&]*", "$1");
            qs = qs.Replace("?&", "?").TrimEnd('?', '&');
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}/PlaybackInfo{qs}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { ctx.Response.StatusCode = (int)resp.StatusCode; return; }

            using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = JsonElementToObject(p.Value);

            // Rewrite MediaSources: each source's Id gets a fed_ prefix so subsequent
            // /Videos/{sourceId}/stream requests route through us. Path points at our
            // /Federation/Stream proxy which fetches the raw bytes from the peer with
            // ?static=true (peer never transcodes; we do the transcoding locally).
            if (dict.TryGetValue("MediaSources", out var msObj) && msObj is System.Collections.Generic.List<object?> sources)
            {
                foreach (var s in sources)
                {
                    if (s is not System.Collections.Generic.Dictionary<string, object?> ms) continue;
                    var originalId = ms.TryGetValue("Id", out var idVal) ? idVal?.ToString() : remoteId;
                    var newSourceId = "fed_" + peerN + "_" + originalId;
                    ms["Id"] = newSourceId;
                    ms["IsRemote"] = true;
                    // Play the file directly from our /Federation/Stream proxy. Setting
                    // DirectPlay=true tells the browser to use the URL as-is (works whenever
                    // the container + codec is browser-native). When the codec is not
                    // playable, the browser falls back to asking us for a transcoded stream;
                    // we keep DirectStream/Transcoding on so that path is available too.
                    ms["Protocol"] = "Http";
                    ms["Path"] = $"/Federation/Stream/{peerN}/{remoteId}?sourceId={Uri.EscapeDataString(originalId ?? string.Empty)}";
                    ms["SupportsDirectPlay"] = true;
                    ms["SupportsDirectStream"] = true;
                    ms["SupportsTranscoding"] = true;
                    // Expose a DirectStreamUrl so the player can pick it up without going
                    // through /Videos/{sourceId}/stream resolution.
                    ms["DirectStreamUrl"] = ms["Path"];
                }
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(dict)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlaybackInfo proxy failed for {Item}", fedId);
            ctx.Response.StatusCode = 502;
        }
    }

    private async Task WriteStubItem(HttpContext ctx, string fedId)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            Id = fedId,
            Name = "(federated)",
            Type = "Movie",
            MediaType = "Video",
            ServerId = LocalServerId,
            ImageTags = new { Primary = "fed" },
            IsFolder = false,
            UserData = new { Played = false, IsFavorite = false, PlaybackPositionTicks = 0L, PlayCount = 0 }
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }

    private async Task WriteStubFolder(HttpContext ctx, string fedLibId)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            Id = fedLibId,
            Name = "Library",
            Type = "CollectionFolder",
            CollectionType = "movies",
            ServerId = LocalServerId,
            ImageTags = new { },
            IsFolder = true
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }

    private static bool IsLocalGuid(string s) => s != null && RxLocalGuid.IsMatch(s);

    private async Task<bool> TryForwardPlaybackSession(HttpContext ctx)
    {
        // Sniff ItemId / NowPlayingItemId in the JSON body. If fed_X, forward this event
        // to the source peer with its API key + the unwrapped remote id, then return 204 so
        // the local server doesn't log 'PlaybackStart reported with null media info'.
        ctx.Request.EnableBuffering();
        string body;
        using (var sr = new StreamReader(ctx.Request.Body, leaveOpen: true))
        {
            body = await sr.ReadToEndAsync().ConfigureAwait(false);
            ctx.Request.Body.Position = 0;
        }
        if (string.IsNullOrEmpty(body)) return false;

        string? fedId = null;
        long? positionTicks = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "ItemId", "NowPlayingItemId", "Id" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s) && s!.StartsWith("fed_", StringComparison.Ordinal)) { fedId = s; break; }
                }
            }
            if (doc.RootElement.TryGetProperty("PositionTicks", out var pt) && pt.ValueKind == JsonValueKind.Number)
                positionTicks = pt.GetInt64();
        }
        catch (Exception ex)
        {
            // Non-JSON or shape-unexpected: not a fed_X session, fall through to local handler.
            _logger.LogDebug(ex, "TryForwardPlaybackSession body parse skip");
            return false;
        }
        if (fedId is null) return false;

        var rest = fedId.Substring("fed_".Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0) return false;
        var peerN = rest.Substring(0, sep);
        var remoteId = rest.Substring(sep + 1);
        if (!Guid.TryParseExact(peerN, "N", out var peerId)) return false;

        var peer = Plugin.Instance?.Configuration?.RemoteServers.FirstOrDefault(p => p.Id == peerId);
        if (peer is null || !peer.Enabled) { ctx.Response.StatusCode = 204; return true; }

        // Rewrite ItemId + MediaSourceId fields (some events carry both) to the bare remote id.
        // Walk the JSON tree explicitly rather than regex-substituting on the raw body — a title
        // or path containing 'fed_…' as a substring would otherwise corrupt the payload.
        var rewritten = RewriteFedIdsForPeer(body, peerN, remoteId);
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            var url = $"{peer.BaseUrl.TrimEnd('/')}{ctx.Request.Path}{ctx.Request.QueryString}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            req.Content = new System.Net.Http.StringContent(rewritten, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);

            // Forwarding /Sessions/Playing/* without a real PlaySessionId can produce a 500 on
            // the peer (no matching session). That's fine; the play-count + last-played event
            // still registered. Always respond 204 to our local client so the SPA's progress
            // ping doesn't surface as a network failure.
            ctx.Response.StatusCode = 204;

            // Position only lands in UserData when we write it explicitly: the /Sessions/Playing
            // path needs a session id we don't have. Do this for Progress + Stopped so resume
            // position on the peer matches what the federated viewer last saw.
            if (positionTicks is { } pos && pos > 0
                && (ctx.Request.Path.Value!.EndsWith("/Progress", StringComparison.Ordinal)
                    || ctx.Request.Path.Value!.EndsWith("/Stopped", StringComparison.Ordinal)))
            {
                await WritePeerUserData(http, peer, remoteId, pos, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Forward playback session for {FedId} failed", fedId);
            ctx.Response.StatusCode = 204;
        }
        return true;
    }

    // Cache: peer id → resolved RemoteUserId. Avoids burning a /Users round trip on every
    // playback Progress tick. Lives for the process lifetime; an admin id change requires
    // a Jellyfin restart, which clears this anyway.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _peerAdminUserIdCache = new();

    internal static string RewriteFedIdsForPeer(string body, string peerN, string remoteId)
    {
        var fedPrefix = "fed_" + peerN + "_";
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            if (node is null) return body;
            RewriteFedIdsInPlace(node, fedPrefix, remoteId);
            return node.ToJsonString();
        }
        catch
        {
            // Body wasn't valid JSON to begin with — pass through unchanged so the peer can
            // surface its own validation error rather than us silently dropping the call.
            return body;
        }
    }

    private static void RewriteFedIdsInPlace(System.Text.Json.Nodes.JsonNode node, string fedPrefix, string remoteId)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is null) continue;
                    if (kv.Value is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue(out string? s)
                        && s is not null && s.StartsWith(fedPrefix, StringComparison.Ordinal))
                    {
                        obj[kv.Key] = remoteId;
                    }
                    else
                    {
                        RewriteFedIdsInPlace(kv.Value, fedPrefix, remoteId);
                    }
                }
                break;
            case System.Text.Json.Nodes.JsonArray arr:
                foreach (var el in arr)
                {
                    if (el is not null) RewriteFedIdsInPlace(el, fedPrefix, remoteId);
                }
                break;
        }
    }

    private async Task WritePeerUserData(HttpClient http, Configuration.RemoteServer peer, string remoteItemId, long positionTicks, CancellationToken ct)
    {
        var uid = !string.IsNullOrEmpty(peer.RemoteUserId)
            ? peer.RemoteUserId
            : (_peerAdminUserIdCache.TryGetValue(peer.Id, out var cached) ? cached : null);
        if (string.IsNullOrEmpty(uid))
        {
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, $"{peer.BaseUrl.TrimEnd('/')}/Users");
                probe.Headers.Add("X-Emby-Token", peer.ApiKey);
                using var resp = await http.SendAsync(probe, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;
                using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Id", out var idv))
                    {
                        uid = idv.GetString();
                        if (!string.IsNullOrEmpty(uid)) _peerAdminUserIdCache[peer.Id] = uid!;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Probe /Users on {Peer} for admin id resolution failed", peer.Name);
                return;
            }
        }
        if (string.IsNullOrEmpty(uid)) return;

        try
        {
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Users/{uid}/Items/{Uri.EscapeDataString(remoteItemId)}/UserData";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            req.Content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(new { PlaybackPositionTicks = positionTicks }),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Peer {Peer} returned {Status} writing UserData for {Item}",
                    peer.Name, (int)resp.StatusCode, remoteItemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WritePeerUserData failed for {Peer} item {Id}", peer.Name, remoteItemId);
        }
    }

    private static void ForwardAuth(HttpContext ctx, HttpRequestMessage req)
    {
        // Mirror every auth-bearing header the browser sent so the loopback hits a real user
        // identity. jellyfin-web uses 'Authorization: MediaBrowser Token=...' for ApiClient
        // requests + 'X-Emby-Token' for some legacy code paths. Forward both.
        var auth = ctx.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
        var token = ctx.Request.Headers["X-Emby-Token"].ToString();
        if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["api_key"].ToString();
        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        var embyAuth = ctx.Request.Headers["X-Emby-Authorization"].ToString();
        if (!string.IsNullOrEmpty(embyAuth)) req.Headers.TryAddWithoutValidation("X-Emby-Authorization", embyAuth);
    }

    private static bool HasMergeFor(string localLibId)
    {
        var settings = Plugin.Instance?.Configuration?.PeerLibrarySettings;
        if (settings is null) return false;
        var target = localLibId.Replace("-", string.Empty).ToLowerInvariant();
        foreach (var s in settings)
        {
            if (s.Enabled && !string.IsNullOrEmpty(s.MergeWithLocalLibraryId)
                && s.MergeWithLocalLibraryId.Replace("-", string.Empty).ToLowerInvariant() == target)
                return true;
        }
        return false;
    }

    private async Task ProxyFedLibList(HttpContext ctx, string fedLibId)
    {
        // 'fedlib_<peerN>_<libId>' → peer items list. Synthesised as a flat BaseItemDto
        // envelope matching what /Users/{uid}/Items would normally return.
        var rest = fedLibId.Substring("fedlib_".Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0) { ctx.Response.StatusCode = 400; return; }
        var peerN = rest.Substring(0, sep);
        var libId = rest.Substring(sep + 1);
        if (!Guid.TryParseExact(peerN, "N", out var peerId)) { ctx.Response.StatusCode = 400; return; }
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            // Forward via our own /Federation/Peers/.../Items so we reuse the auth + cache.
            var limit = int.TryParse(ctx.Request.Query["Limit"].ToString(), out var l) ? l : 100;
            limit = Math.Clamp(limit, 1, 200);
            // Use the container-local listen port. ctx.Request.Host carries the browser-facing
            // host (eg localhost:8098 with docker port mapping) which is unreachable from inside.
            var localPort = ctx.Connection.LocalPort > 0 ? ctx.Connection.LocalPort : 8096;
            var jellyfinHost = $"http://localhost:{localPort}";
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{jellyfinHost}/Federation/Peers/{peerId}/Libraries/{Uri.EscapeDataString(libId)}/Items?limit={limit}");
            ForwardAuth(ctx, req);
            using var resp = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            var items = new System.Collections.Generic.List<object?>();
            if (doc.RootElement.TryGetProperty("items", out var arr))
            {
                foreach (var el in arr.EnumerateArray()) items.Add(MapPeerItemDto(el, peerN));
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { Items = items, TotalRecordCount = items.Count, StartIndex = 0 })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProxyFedLibList failed for {Lib}", fedLibId);
            ctx.Response.StatusCode = 502;
        }
    }

    private async Task AppendPeerItems(HttpContext ctx, string localLibId)
    {
        // Forward to Jellyfin, capture the JSON, then concat peer items per merge config.
        // Jellyfin's response-compression middleware would otherwise gzip the buffered body and
        // make JsonDocument.ParseAsync choke on '0x0B' — strip Accept-Encoding before _next so
        // the inner pipeline returns plain JSON we can re-serialise.
        ctx.Request.Headers.Remove("Accept-Encoding");
        var bodyStream = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await _next(ctx).ConfigureAwait(false); }
        finally { ctx.Response.Body = bodyStream; }

        buffer.Position = 0;
        if (ctx.Response.StatusCode < 200 || ctx.Response.StatusCode >= 300)
        {
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = JsonElementToObject(p.Value);
            var items = (dict.TryGetValue("Items", out var iObj) && iObj is System.Collections.Generic.List<object?> existing)
                ? existing
                : new System.Collections.Generic.List<object?>();

            var settings = Plugin.Instance?.Configuration?.PeerLibrarySettings ?? new System.Collections.Generic.List<Configuration.PeerLibrarySetting>();
            var targetNoHyphen = localLibId.Replace("-", string.Empty).ToLowerInvariant();
            var merges = settings.Where(s => s.Enabled
                && !string.IsNullOrEmpty(s.MergeWithLocalLibraryId)
                && s.MergeWithLocalLibraryId.Replace("-", string.Empty).ToLowerInvariant() == targetNoHyphen).ToList();

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var localPort = ctx.Connection.LocalPort > 0 ? ctx.Connection.LocalPort : 8096;
            var jellyfinHost = $"http://localhost:{localPort}";
            var limit = int.TryParse(ctx.Request.Query["Limit"].ToString(), out var l) ? l : 100;
            limit = Math.Clamp(limit, 1, 200);

            foreach (var m in merges)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{jellyfinHost}/Federation/Peers/{m.PeerId}/Libraries/{Uri.EscapeDataString(m.LibraryId)}/Items?limit={limit}");
                    ForwardAuth(ctx, req);
                    using var r = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode) continue;
                    using var s = await r.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
                    using var d = await JsonDocument.ParseAsync(s, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
                    var peerN = m.PeerId.ToString("N");
                    if (d.RootElement.TryGetProperty("items", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray()) items.Add(MapPeerItemDto(el, peerN));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppendPeerItems skipping peer {Peer} lib {Lib}", m.PeerId, m.LibraryId);
                }
            }
            dict["Items"] = items;
            dict["TotalRecordCount"] = items.Count;

            ctx.Response.ContentType = "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict));
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppendPeerItems failed for {Lib}", localLibId);
            buffer.Position = 0;
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task AppendPeerLatest(HttpContext ctx, string localLibId)
    {
        // Home carousel: /Users/{uid}/Items/Latest?ParentId=<lib>. Response is a JSON array
        // (not a wrapping object). Append a few peer items so the user sees federated 'recents'
        // alongside local ones. Limit defaults to 16; we trim our injection to the same.
        ctx.Request.Headers.Remove("Accept-Encoding");
        var bodyStream = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await _next(ctx).ConfigureAwait(false); }
        finally { ctx.Response.Body = bodyStream; }

        buffer.Position = 0;
        if (ctx.Response.StatusCode < 200 || ctx.Response.StatusCode >= 300)
        {
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            var items = new System.Collections.Generic.List<object?>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray()) items.Add(JsonElementToObject(el));
            }

            var settings = Plugin.Instance?.Configuration?.PeerLibrarySettings ?? new System.Collections.Generic.List<Configuration.PeerLibrarySetting>();
            var targetNoHyphen = localLibId.Replace("-", string.Empty).ToLowerInvariant();
            var merges = settings.Where(s => s.Enabled
                && !string.IsNullOrEmpty(s.MergeWithLocalLibraryId)
                && s.MergeWithLocalLibraryId.Replace("-", string.Empty).ToLowerInvariant() == targetNoHyphen).ToList();

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var localPort = ctx.Connection.LocalPort > 0 ? ctx.Connection.LocalPort : 8096;
            var jellyfinHost = $"http://localhost:{localPort}";
            var limit = int.TryParse(ctx.Request.Query["Limit"].ToString(), out var l) ? l : 16;
            limit = Math.Clamp(limit, 1, 50);

            foreach (var m in merges)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{jellyfinHost}/Federation/Peers/{m.PeerId}/Libraries/{Uri.EscapeDataString(m.LibraryId)}/Items?limit={limit}");
                    ForwardAuth(ctx, req);
                    using var r = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode) continue;
                    using var s = await r.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
                    using var d = await JsonDocument.ParseAsync(s, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
                    var peerN = m.PeerId.ToString("N");
                    if (d.RootElement.TryGetProperty("items", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray()) items.Add(MapPeerItemDto(el, peerN));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppendPeerLatest skipping peer {Peer} lib {Lib}", m.PeerId, m.LibraryId);
                }
            }

            ctx.Response.ContentType = "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items));
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppendPeerLatest failed for {Lib}", localLibId);
            buffer.Position = 0;
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
        }
    }

    private System.Collections.Generic.Dictionary<string, object?> MapPeerItemDto(JsonElement el, string peerN)
    {
        var rawId = el.TryGetProperty("id", out var idVal) ? idVal.GetString() : string.Empty;
        var fedId = "fed_" + peerN + "_" + rawId;
        return new System.Collections.Generic.Dictionary<string, object?>
        {
            ["Id"] = fedId,
            ["Name"] = el.TryGetProperty("name", out var n) ? n.GetString() : null,
            ["Type"] = el.TryGetProperty("type", out var t) ? t.GetString() : "Movie",
            ["MediaType"] = "Video",
            ["ServerId"] = LocalServerId,
            ["ProductionYear"] = el.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : (object?)null,
            ["ImageTags"] = new System.Collections.Generic.Dictionary<string, string> { ["Primary"] = "fed" },
            ["BackdropImageTags"] = new System.Collections.Generic.List<object?>(),
            ["IsFolder"] = false,
            ["UserData"] = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["Played"] = false,
                ["IsFavorite"] = false,
                ["PlaybackPositionTicks"] = 0L,
                ["PlayCount"] = 0
            }
        };
    }

    private static async Task WriteEmptyList(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"Items\":[],\"TotalRecordCount\":0,\"StartIndex\":0}").ConfigureAwait(false);
    }

    private static async Task WriteThemeMediaShape(HttpContext ctx, string fedId, string variant)
    {
        // themeMediaPlayer.js dereferences result.OwnerId on whichever branch is picked
        // (ThemeVideosResult or ThemeSongsResult). The full AllThemeMediaResult shape always
        // wins even when both are empty + the standalone /ThemeSongs and /ThemeVideos
        // endpoints expect a single ThemeMediaResult.
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var inner = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["Items"] = new System.Collections.Generic.List<object?>(),
            ["TotalRecordCount"] = 0,
            ["OwnerId"] = fedId
        };
        object payload = variant switch
        {
            "ThemeMedia" => new System.Collections.Generic.Dictionary<string, object?>
            {
                ["ThemeVideosResult"] = inner,
                ["ThemeSongsResult"] = inner,
                ["SoundtrackSongsResult"] = inner
            },
            _ => inner
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }
}

/// <summary>Registers FederationInterceptMiddleware at the front of the request pipeline.</summary>
public class FederationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<FederationInterceptMiddleware>();
            next(app);
        };
    }
}
