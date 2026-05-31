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
    // A single LOCAL item detail fetch (the details page). We append peer copies of the same
    // film as alternate MediaSources so the version picker can switch to them - Jellyfin builds
    // that picker from the static DTO, which doesn't run IMediaSourceProvider.
    private static readonly System.Text.RegularExpressions.Regex RxLocalItemDetail =
        new(@"^(?:/Users/[^/]+)?/Items/([0-9a-fA-F]{32})$", RxOpt);
    // Actual media byte streams. The web player builds /Videos/{itemId}/stream(.ext),
    // /Videos/{itemId}/master.m3u8 + /Videos/{itemId}/hls1/.../seg.ts (transcode), and
    // /Audio/{itemId}/universal. With a federated itemId the LOCAL server 404s (no such
    // item), so playback dies even after PlaybackInfo returns sources. We transparently
    // reverse-proxy the whole subtree to the source peer. Group 1 = Videos|Audio,
    // 2 = fed id, 3 = remaining path (/stream.mp4, /hls1/..., empty).
    private static readonly System.Text.RegularExpressions.Regex RxMediaStream =
        new(@"^/(Videos|Audio)/(fed_[0-9a-fA-F]+_[^/]+)(/[^?]*)?$", RxOpt | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

        // Our own controller + the loopback merge calls (ProxyFedLibList / AppendPeerItems fetch
        // http://localhost/Federation/Peers/...) live under /Federation. Never intercept them, so
        // the merge fan-out can't recursively re-enter this middleware.
        if (path.StartsWith("/Federation/", StringComparison.OrdinalIgnoreCase))
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

        if (ctx.Request.Method != HttpMethods.Get && ctx.Request.Method != HttpMethods.Head)
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // Media byte streams (/Videos/fed_X/..., /Audio/fed_X/...): proxy to the source peer
        // so the backend fetches the bytes and serves them to the client. Matched first so the
        // local server never sees the federated id and 404s.
        var mediaMatch = RxMediaStream.Match(path);
        if (mediaMatch.Success)
        {
            await ProxyMediaStream(ctx, mediaMatch.Groups[1].Value, mediaMatch.Groups[2].Value, mediaMatch.Groups[3].Value).ConfigureAwait(false);
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

        // Single LOCAL item detail: when dedup is on and a peer holds the same film, append the
        // peer copy as an alternate MediaSource so the details-page version picker can switch to
        // it (the picker is built from this DTO, which otherwise lacks IMediaSourceProvider sources).
        var localDetail = RxLocalItemDetail.Match(path);
        if (localDetail.Success
            && (Plugin.Instance?.Configuration?.EnableDedup ?? false)
            && (Plugin.Instance?.Configuration?.RemoteServers.Count ?? 0) > 0)
        {
            await AppendLocalItemVersions(ctx, localDetail.Groups[1].Value).ConfigureAwait(false);
            return;
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
            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = TimeSpan.FromSeconds(15);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            // Rebuild the upstream query (fillHeight/fillWidth/quality) from the parsed
            // collection, dropping our injected tag/api_key sentinels. Rebuilding via
            // QueryString.Create avoids the dangling '&&' a regex-strip leaves behind when
            // a removed key sits in the middle of the string.
            var qb = new Microsoft.AspNetCore.Http.QueryString();
            foreach (var kv in ctx.Request.Query)
            {
                if (kv.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Equals("tag", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in kv.Value) qb = qb.Add(kv.Key, v ?? string.Empty);
            }
            var qs = qb.Value ?? string.Empty;
            // imageType may include an index suffix (e.g. 'Backdrop/0'); escape per segment
            // so the slash stays a path separator rather than %2F which peers reject.
            var imageTypePath = string.Join('/', imageType.Split('/').Select(Uri.EscapeDataString));
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}/Images/{imageTypePath}{qs}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentType?.MediaType is { } ct) ctx.Response.ContentType = ct;
            // Only cache successful responses, and mark private: the bytes were fetched with the
            // peer's credentials so a shared proxy cache must not serve them to other users.
            if (resp.IsSuccessStatusCode)
                ctx.Response.Headers["Cache-Control"] = "private, max-age=600";
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
            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = TimeSpan.FromSeconds(10);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            // With a configured RemoteUserId we can hit /Users/{uid}/Items/{id} (carries
            // UserData). Without one, the bare /Items/{id} path 400s on Jellyfin, so use the
            // list form /Items?Ids={id} (no user context needed) and unwrap Items[0].
            const string fields = "Overview,Genres,Studios,People,Tags,ProviderIds,MediaStreams,MediaSources,RunTimeTicks,ProductionYear,PremiereDate,OfficialRating,CommunityRating,Taglines,ExternalUrls";
            string url;
            var byList = string.IsNullOrEmpty(peer.RemoteUserId);
            if (byList)
                url = $"{peer.BaseUrl.TrimEnd('/')}/Items?Ids={Uri.EscapeDataString(remoteId)}&Recursive=true&Fields={fields}";
            else
                url = $"{peer.BaseUrl.TrimEnd('/')}/Users/{Uri.EscapeDataString(peer.RemoteUserId!)}/Items/{Uri.EscapeDataString(remoteId)}?Fields={fields}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { await WriteStubItem(ctx, fedId).ConfigureAwait(false); return; }
            using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            // The list form wraps the item in { Items:[...] }; unwrap to the single DTO.
            var itemRoot = doc.RootElement;
            if (itemRoot.ValueKind == JsonValueKind.Object && itemRoot.TryGetProperty("Items", out var wrapArr) && wrapArr.ValueKind == JsonValueKind.Array)
            {
                var first = wrapArr.EnumerateArray().Cast<JsonElement?>().FirstOrDefault();
                if (first is null || first.Value.ValueKind != JsonValueKind.Object) { await WriteStubItem(ctx, fedId).ConfigureAwait(false); return; }
                itemRoot = first.Value;
            }
            // Clone the entire DTO into a mutable dictionary so we can rewrite the few
            // identity fields without losing any of the rich metadata (overview, genres,
            // tags, mediastreams, runtime, studios, people, etc.).
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var p in itemRoot.EnumerateObject()) dict[p.Name] = JsonElementToObject(p.Value);
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
            // apiClient.getItem(userId, mediaSourceId || item.Id) which 404s when the bare peer
            // id is queried locally. Use the SAME rewrite as PlaybackInfo so the detail page and
            // the playback negotiation agree on shape (id namespacing, play-method classification,
            // /Videos/fed_X routing).
            if (dict.TryGetValue("MediaSources", out var msObj) && msObj is System.Collections.Generic.List<object?> sources)
                RewriteFederatedMediaSources(sources, peerN, remoteId, fedId);
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

            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = TimeSpan.FromSeconds(15);
            RemoteJellyfinClient.AddBasicAuth(http, peer);

            // Resolve a peer-side user id. PlaybackInfo POST with a DeviceProfile REQUIRES a
            // user context: with our local UserId (unknown to the peer) or with no user at all
            // the peer returns 400 -> NoCompatibleStream. Swap in the peer's own user id.
            var peerUid = await ResolvePeerUserId(http, peer, ctx.RequestAborted).ConfigureAwait(false);

            // Rebuild the forwarded query from scratch.
            //   - api_key  : our sentinel, peer uses its own API key.
            //   - UserId   : replaced with the peer's user id (set below); drop the inbound one.
            //   - LiveStreamId / PlaySessionId / DeviceProfileId : local session state the peer
            //     can't resolve.
            // MediaSourceId may carry our fed_<peerN>_<remoteId> id on a follow-up call; unwrap
            // it to the bare remote id the peer knows.
            var fedSrcPrefix = "fed_" + peerN + "_";
            var drop = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "api_key", "UserId", "LiveStreamId", "PlaySessionId", "DeviceProfileId" };
            var kept = new System.Collections.Generic.List<string>();
            foreach (var pair in (ctx.Request.QueryString.Value ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair.Substring(0, eq);
                if (drop.Contains(Uri.UnescapeDataString(key))) continue;
                var val = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                if (key.Equals("MediaSourceId", StringComparison.OrdinalIgnoreCase) && val.StartsWith(fedSrcPrefix, StringComparison.Ordinal))
                    val = val.Substring(fedSrcPrefix.Length);
                kept.Add(eq < 0 ? key : key + "=" + val);
            }
            if (!string.IsNullOrEmpty(peerUid)) kept.Insert(0, "UserId=" + Uri.EscapeDataString(peerUid));
            var qs = kept.Count > 0 ? "?" + string.Join("&", kept) : string.Empty;
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}/PlaybackInfo{qs}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            // Rewrite any fed_ ids in the body (MediaSourceId/ItemId) and replace the local
            // UserId with the peer's, then forward the device profile.
            if (!string.IsNullOrEmpty(body))
                body = SetBodyUserId(RewriteFedIdsForPeer(body, peerN, remoteId), peerUid);
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { ctx.Response.StatusCode = (int)resp.StatusCode; return; }

            using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = JsonElementToObject(p.Value);

            // Rewrite MediaSources so the SPA classifies the play method itself and routes bytes
            // through our /Videos/fed_X proxy. Shared with the item-detail path (see helper).
            if (dict.TryGetValue("MediaSources", out var msObj) && msObj is System.Collections.Generic.List<object?> sources)
            {
                RewriteFederatedMediaSources(sources, peerN, remoteId, fedId);
                // Multi-version: stack the same film's copies from other peers (registered during
                // a merged-library browse) as additional MediaSources -> version picker.
                if (_versionAlternates.TryGetValue(fedId, out var alts) && alts.Count > 0)
                    foreach (var (altPeerN, altRemoteId) in alts.ToArray())
                    {
                        var extra = await FetchAltPeerSources(altPeerN, altRemoteId, body, ctx.RequestAborted).ConfigureAwait(false);
                        if (extra is not null) sources.AddRange(extra);
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

    // Transparent reverse-proxy for /Videos/fed_X/... and /Audio/fed_X/... -> the source peer.
    // This is the path the browser <video> element / hls.js actually hits to pull bytes:
    // direct play (/stream.mp4), transcode playlist (/master.m3u8) and its segments
    // (/hls1/.../seg.ts) all funnel through here. The peer does any transcoding; we stream
    // the bytes back, swapping our api_key for the peer's so the client never needs peer
    // credentials. Range is forwarded so seeking works.
    private async Task ProxyMediaStream(HttpContext ctx, string kind, string fedId, string rest)
    {
        var inner = fedId.Substring("fed_".Length);
        var sep = inner.IndexOf('_');
        if (sep <= 0) { ctx.Response.StatusCode = 400; return; }
        var peerN = inner.Substring(0, sep);
        var remoteId = inner.Substring(sep + 1);
        if (!Guid.TryParseExact(peerN, "N", out var peerId)) { ctx.Response.StatusCode = 400; return; }

        var config = Plugin.Instance?.Configuration;
        var peer = config?.RemoteServers.FirstOrDefault(p => p.Id == peerId);
        if (peer is null || !peer.Enabled) { ctx.Response.StatusCode = 404; return; }

        // Access control. Bytes are served before Jellyfin's auth middleware (HLS segments
        // carry the peer's api_key, not a local token), so we gate access ourselves:
        //   1. a valid 'fst' HMAC token (used by direct-play URLs and rewritten HLS segments), OR
        //   2. a valid local Jellyfin session/api-key (the player stamps api_key on the first
        //      /Videos request it builds itself, which we can't pre-sign).
        var fst = ctx.Request.Query[MediaTokenParam].ToString();
        var authed = VerifyMediaToken(fst, peerN, remoteId) || await IsLocallyAuthenticated(ctx).ConfigureAwait(false);
        if (!authed) { ctx.Response.StatusCode = 401; return; }

        // Multi-version: for a merged card with several peers' copies, the player keeps the
        // CARD's item id in the path but selects a version via MediaSourceId=fed_<otherPeer>_<id>.
        // Honour that peer so the chosen copy streams from the right server (otherwise every
        // version would stream from the card's own peer).
        var msid = ctx.Request.Query["MediaSourceId"].ToString();
        if (msid.StartsWith("fed_", StringComparison.Ordinal))
        {
            var mr = msid.Substring("fed_".Length);
            var msep = mr.IndexOf('_');
            if (msep > 0 && Guid.TryParseExact(mr.Substring(0, msep), "N", out var altPid) && altPid != peerId)
            {
                var altPeer = config?.RemoteServers.FirstOrDefault(p => p.Id == altPid);
                if (altPeer is not null && altPeer.Enabled)
                {
                    peerN = mr.Substring(0, msep);
                    remoteId = mr.Substring(msep + 1);
                    peerId = altPid;
                    peer = altPeer;
                }
            }
        }

        // The remaining path may itself embed a fed_<peerN>_ media-source id segment (e.g.
        // /Videos/fed_X/fed_X/Subtitles/...). Unwrap to the bare remote id.
        var fedSrcPrefix = "fed_" + peerN + "_";
        rest = (rest ?? string.Empty).Replace(fedSrcPrefix, string.Empty, StringComparison.Ordinal);

        // Rebuild the query: drop our api_key + fst (peer knows neither), unwrap fed_ ids.
        var kept = new System.Collections.Generic.List<string>();
        foreach (var pair in (ctx.Request.QueryString.Value ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair.Substring(0, eq);
            var bareKey = Uri.UnescapeDataString(key);
            if (bareKey.Equals("api_key", StringComparison.OrdinalIgnoreCase) || bareKey.Equals(MediaTokenParam, StringComparison.OrdinalIgnoreCase)) continue;
            var val = eq < 0 ? string.Empty : pair.Substring(eq + 1);
            if (val.Contains(fedSrcPrefix, StringComparison.Ordinal))
                val = val.Replace(fedSrcPrefix, string.Empty, StringComparison.Ordinal);
            kept.Add(eq < 0 ? key : key + "=" + val);
        }
        var qs = kept.Count > 0 ? "?" + string.Join("&", kept) : string.Empty;
        var upstream = $"{peer.BaseUrl.TrimEnd('/')}/{kind}/{Uri.EscapeDataString(remoteId)}{rest}{qs}";

        try
        {
            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // long-lived media stream
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            var method = ctx.Request.Method == HttpMethods.Head ? HttpMethod.Head : HttpMethod.Get;
            using var req = new HttpRequestMessage(method, upstream);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            if (ctx.Request.Headers.TryGetValue("Range", out var range))
                req.Headers.TryAddWithoutValidation("Range", (string)range!);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);

            // HLS playlists reference sub-playlists/segments under the same item, with the
            // peer's api_key baked in. Rewrite them: strip the peer key, inject OUR fst token so
            // the browser's follow-up segment requests authenticate against us.
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isPlaylist = ctype.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || rest.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (isPlaylist && method != HttpMethod.Head)
            {
                var raw = await resp.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);
                var token = NewMediaToken(peerN, remoteId);
                var rewritten = RewriteHlsPlaylist(raw, peer.BaseUrl, kind, remoteId, fedId, token);
                var bytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = string.IsNullOrEmpty(ctype) ? "application/vnd.apple.mpegurl" : ctype;
                ctx.Response.ContentLength = bytes.Length;
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            var cap = config?.OutboundBitrateCapBps ?? 0;
            ctx.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers)
            {
                if (HopByHop.Contains(h.Key)) continue;
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            }
            foreach (var h in resp.Content.Headers)
            {
                if (HopByHop.Contains(h.Key)) continue;
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            }
            // Throttling re-paces the body but doesn't change byte count, so Content-Length stays
            // valid; leave it. HEAD has no body.
            if (method == HttpMethod.Head) return;

            // Audit byte-bearing responses for the per-peer quota/usage view. Skip HLS segments:
            // a transcoded movie is thousands of segment requests and would flood the audit table
            // with one row each. Direct-play (the common case) and the universal audio stream are
            // single requests, so one row each. Throttling still applies to everything.
            var store = ctx.RequestServices.GetService(typeof(RemoteItemStore)) as RemoteItemStore;
            var isSegment = rest.Contains("/hls", StringComparison.OrdinalIgnoreCase)
                || rest.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || rest.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase);
            long auditId = -1;
            if (store is not null && !isSegment)
            {
                try { auditId = store.BeginAudit(peerId, remoteId, null); }
                catch (Exception ex) { _logger.LogDebug(ex, "BeginAudit failed for {Peer} {Item}", peerId, remoteId); }
            }
            var served = 0L;
            try
            {
                var raw = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
                // ThrottledStream owns disposing the inner stream; don't double-dispose raw.
                await using Stream src = cap > 0 ? new ThrottledStream(raw, cap / 8) : raw;
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(), ctx.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted).ConfigureAwait(false);
                    served += n;
                }
            }
            finally
            {
                if (auditId > 0 && store is not null)
                {
                    try { store.CompleteAudit(auditId, served); }
                    catch (Exception ex) { _logger.LogDebug(ex, "CompleteAudit failed for {AuditId}", auditId); }
                }
            }
        }
        catch (OperationCanceledException) { /* client seeked / closed the player */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media stream proxy failed for {Kind}/{Item}", kind, fedId);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
        }
    }

    private async Task<bool> IsLocallyAuthenticated(HttpContext ctx)
    {
        try
        {
            if (ctx.RequestServices.GetService(typeof(MediaBrowser.Controller.Net.IAuthorizationContext))
                is not MediaBrowser.Controller.Net.IAuthorizationContext authCtx) return false;
            var info = await authCtx.GetAuthorizationInfo(ctx).ConfigureAwait(false);
            return info is not null && (info.User is not null || info.IsApiKey);
        }
        catch
        {
            return false;
        }
    }

    // Rewrite an HLS playlist so every relative URI it references routes back through us with a
    // fresh fst token instead of the peer's api_key. Lines are either bare URIs (segments,
    // variant playlists) or tag lines carrying URI="..." attributes (#EXT-X-MEDIA, #EXT-X-MAP,
    // #EXT-X-KEY, #EXT-X-I-FRAME-STREAM-INF). Absolute URLs pointing at the peer are folded
    // back to our /{kind}/fed_X/ space.
    internal static string RewriteHlsPlaylist(string body, string peerBaseUrl, string kind, string remoteId, string fedId, string token)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "URI=\"([^\"]*)\"");
                if (m.Success)
                    lines[i] = line.Substring(0, m.Groups[1].Index) + RewriteHlsUri(m.Groups[1].Value, peerBaseUrl, kind, remoteId, fedId, token) + line.Substring(m.Groups[1].Index + m.Groups[1].Length);
                continue;
            }
            lines[i] = RewriteHlsUri(line, peerBaseUrl, kind, remoteId, fedId, token);
        }
        return string.Join("\n", lines);
    }

    private static string RewriteHlsUri(string uri, string peerBaseUrl, string kind, string remoteId, string fedId, string token)
    {
        if (string.IsNullOrWhiteSpace(uri)) return uri;
        var u = uri.Trim();
        // Fold an absolute peer URL down to the path the player should hit on us.
        var basePrefix = peerBaseUrl.TrimEnd('/');
        if (u.StartsWith(basePrefix + $"/{kind}/{remoteId}", StringComparison.OrdinalIgnoreCase))
            u = u.Substring((basePrefix + $"/{kind}/{remoteId}").Length).TrimStart('/');
        else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return StripPeerCredentials(uri); // foreign absolute URL: pass through but never leak the peer's api_key/fst
        // Split path?query, drop the peer's api_key, append our fst.
        var qi = u.IndexOf('?');
        var path = qi < 0 ? u : u.Substring(0, qi);
        var query = qi < 0 ? string.Empty : u.Substring(qi + 1);
        var parts = new System.Collections.Generic.List<string>();
        foreach (var p in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = p.IndexOf('=');
            var k = eq < 0 ? p : p.Substring(0, eq);
            if (Uri.UnescapeDataString(k).Equals("api_key", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.UnescapeDataString(k).Equals(MediaTokenParam, StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add(p);
        }
        parts.Add(MediaTokenParam + "=" + Uri.EscapeDataString(token));
        return path + "?" + string.Join("&", parts);
    }

    // Drop api_key / fst from a URL we don't rewrite, so a peer can't bake its own credential
    // into a foreign-host playlist URI that we'd then serve to an anonymous client.
    private static string StripPeerCredentials(string uri)
    {
        var qi = uri.IndexOf('?');
        if (qi < 0) return uri;
        var path = uri.Substring(0, qi);
        var kept = new System.Collections.Generic.List<string>();
        foreach (var p in uri.Substring(qi + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = p.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? p : p.Substring(0, eq));
            if (k.Equals("api_key", StringComparison.OrdinalIgnoreCase) || k.Equals(MediaTokenParam, StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(p);
        }
        return kept.Count > 0 ? path + "?" + string.Join("&", kept) : path;
    }

    // Single source of truth for turning a peer's MediaSources[] (as a list of mutable dicts)
    // into ones the local SPA can play: namespace the id, make it mirror a normal local item so
    // jellyfin-web classifies the play method on its own (no Protocol=Http / custom Path /
    // DirectStreamUrl), drop the peer's filesystem Path, and fold any transcode url back onto
    // our /Videos/fed_X space with an fst token. Used by BOTH PlaybackInfo and item-detail so
    // the two never diverge (a past bug: the detail page kept the old /Federation/Stream shape).
    internal static void RewriteFederatedMediaSources(System.Collections.Generic.List<object?> sources, string peerN, string remoteId, string fedItemId)
    {
        // Drop sources the peer itself federated from a THIRD party (Id already fed_-prefixed).
        // Those are the peer's OWN transitive alternates, not its local copy; re-exporting them
        // double-prefixes the id (fed_X_fed_Y) and pulls A->B->C multi-version loops in. We only
        // want the peer's native source(s).
        sources.RemoveAll(s => s is System.Collections.Generic.Dictionary<string, object?> d
            && (d.TryGetValue("Id", out var idv) ? idv?.ToString() : null)?.StartsWith("fed_", StringComparison.Ordinal) == true);
        var token = NewMediaToken(peerN, remoteId);
        foreach (var s in sources)
        {
            if (s is not System.Collections.Generic.Dictionary<string, object?> ms) continue;
            var originalId = ms.TryGetValue("Id", out var idVal) ? idVal?.ToString() : remoteId;
            ms["Id"] = "fed_" + peerN + "_" + originalId;
            ms["IsRemote"] = true;
            ms["Path"] = null; // don't leak the peer's filesystem path
            ms["SupportsDirectPlay"] = true;
            ms["SupportsDirectStream"] = true;
            ms["SupportsTranscoding"] = true;
            ms.Remove("DirectStreamUrl"); // let the client build /Videos/fed_X/stream itself
            if (ms.TryGetValue("TranscodingUrl", out var tuObj) && tuObj is string tu && !string.IsNullOrEmpty(tu))
                ms["TranscodingUrl"] = RewriteTranscodingUrl(tu, peerN, remoteId, fedItemId, token);
        }
    }

    // Fold a peer transcode url (/Videos/{remoteId}/master.m3u8?...&api_key=PEER...) onto our
    // own /Videos/{fedId}/ space, drop the peer api_key, add an fst token. The follow-up
    // playlist + segment requests then route through our proxy and authenticate with fst.
    internal static string RewriteTranscodingUrl(string url, string peerN, string remoteId, string fedId, string token)
    {
        var qi = url.IndexOf('?');
        var path = qi < 0 ? url : url.Substring(0, qi);
        var query = qi < 0 ? string.Empty : url.Substring(qi + 1);
        // Swap the remote item id in the path for our fed id. The peer emits it as the path
        // segment right after /Videos|/Audio, in EITHER N (32 hex) or D (hyphenated) GUID form.
        path = System.Text.RegularExpressions.Regex.Replace(
            path, @"(/(?:Videos|Audio)/)[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}",
            "$1" + fedId, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var fedSrcPrefix = "fed_" + peerN + "_";
        var parts = new System.Collections.Generic.List<string>();
        foreach (var p in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = p.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? p : p.Substring(0, eq));
            if (k.Equals("api_key", StringComparison.OrdinalIgnoreCase) || k.Equals(MediaTokenParam, StringComparison.OrdinalIgnoreCase)) continue;
            var val = eq < 0 ? string.Empty : p.Substring(eq + 1);
            if (val.Contains(fedSrcPrefix, StringComparison.Ordinal)) val = val.Replace(fedSrcPrefix, string.Empty, StringComparison.Ordinal);
            parts.Add(eq < 0 ? p : p.Substring(0, eq + 1) + val);
        }
        parts.Add(MediaTokenParam + "=" + Uri.EscapeDataString(token));
        return path + "?" + string.Join("&", parts);
    }

    private static readonly System.Collections.Generic.HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "trailers", "transfer-encoding", "upgrade", "server",
    };

    // === Federated media-stream tokens (fst) ===
    // Short-lived HMAC token authorising anonymous access to ONE peer item's bytes. Bytes are
    // proxied before Jellyfin's auth runs (HLS segments carry the peer's api_key, not a local
    // token), so we sign access ourselves. The token binds (peerN, remoteId, exp); peerN and
    // remoteId come from the URL path, so a token for item A can't be replayed for item B.
    internal const string MediaTokenParam = "fst";
    private static readonly TimeSpan MediaTokenTtl = TimeSpan.FromHours(8);

    private static byte[] GetSigningKey()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return Array.Empty<byte>();
        if (string.IsNullOrEmpty(cfg.MediaSigningKey))
        {
            lock (Plugin.ConfigWriteLock)
            {
                if (string.IsNullOrEmpty(cfg.MediaSigningKey))
                {
                    var raw = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                    cfg.MediaSigningKey = Convert.ToBase64String(raw);
                    Plugin.Instance?.SaveConfiguration();
                }
            }
        }
        try { return Convert.FromBase64String(cfg.MediaSigningKey); }
        catch { return Array.Empty<byte>(); }
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string SignMediaToken(string peerN, string remoteId, long expUnix)
    {
        var key = GetSigningKey();
        using var h = new System.Security.Cryptography.HMACSHA256(key);
        var sig = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{peerN}.{remoteId}.{expUnix}"));
        return expUnix.ToString(System.Globalization.CultureInfo.InvariantCulture) + "~" + Base64Url(sig);
    }

    internal static string NewMediaToken(string peerN, string remoteId)
        => SignMediaToken(peerN, remoteId, DateTimeOffset.UtcNow.Add(MediaTokenTtl).ToUnixTimeSeconds());

    internal static bool VerifyMediaToken(string? token, string peerN, string remoteId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('~');
        if (dot <= 0) return false;
        if (!long.TryParse(token.AsSpan(0, dot), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exp)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
        // Fail closed: if the signing key is unavailable (null plugin / corrupt config), refuse
        // rather than validating against an empty HMAC key that any caller could forge.
        if (GetSigningKey().Length == 0) return false;
        var expected = SignMediaToken(peerN, remoteId, exp);
        // Constant-time compare of the whole token string.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected));
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
            var http = _httpClientFactory.CreateClient("federation");
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

    // Replace a top-level UserId in a PlaybackInfo body with the peer's user id (or remove it
    // when none could be resolved). The SPA stamps OUR local user guid there; the peer can't
    // resolve it and returns empty MediaSources / NoCompatibleStream.
    private static string SetBodyUserId(string body, string? peerUid)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            if (node is not System.Text.Json.Nodes.JsonObject obj) return body;
            if (!obj.ContainsKey("UserId") && string.IsNullOrEmpty(peerUid)) return body;
            if (string.IsNullOrEmpty(peerUid)) obj.Remove("UserId");
            else obj["UserId"] = peerUid;
            return obj.ToJsonString();
        }
        catch
        {
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

    // Resolve a user id valid ON THE PEER for an API-key (no-user) context. Prefer the
    // configured RemoteUserId, fall back to the cached admin id, else probe /Users and cache
    // the first account. PlaybackInfo POST with a DeviceProfile requires a user context; with
    // none the peer returns 400 -> NoCompatibleStream. Returns null if it can't be resolved.
    private async Task<string?> ResolvePeerUserId(HttpClient http, Configuration.RemoteServer peer, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(peer.RemoteUserId)) return peer.RemoteUserId;
        if (_peerAdminUserIdCache.TryGetValue(peer.Id, out var cached) && !string.IsNullOrEmpty(cached)) return cached;
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, $"{peer.BaseUrl.TrimEnd('/')}/Users");
            probe.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(probe, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Id", out var idv))
                {
                    var uid = idv.GetString();
                    if (!string.IsNullOrEmpty(uid)) { _peerAdminUserIdCache[peer.Id] = uid!; return uid; }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResolvePeerUserId probe failed for {Peer}", peer.Name);
        }
        return null;
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
            var http = _httpClientFactory.CreateClient("federation");
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
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { Items = items, TotalRecordCount = items.Count, StartIndex = 0 })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProxyFedLibList failed for {Lib}", fedLibId);
            ctx.Response.StatusCode = 502;
        }
    }

    // Append peer copies of a LOCAL item as alternate MediaSources so the details-page version
    // picker can offer them (Jellyfin's static DTO doesn't run IMediaSourceProvider). The source
    // Ids match the provider's, so selecting one -> native PlaybackInfo returns the same source
    // (playable Http via /Federation/Stream).
    private async Task AppendLocalItemVersions(HttpContext ctx, string localGuid)
    {
        ctx.Request.Headers.Remove("Accept-Encoding");
        var bodyStream = ctx.Response.Body;
        using var buffer = new System.IO.MemoryStream();
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                buffer.Position = 0; await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false); return;
            }
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = JsonElementToObject(p.Value);
            // Only act on items that already expose MediaSources (i.e. a playable video the picker
            // is built from). Folders / people / list responses pass straight through.
            if (!dict.TryGetValue("MediaSources", out var msObj) || msObj is not System.Collections.Generic.List<object?> sources || sources.Count == 0)
            {
                buffer.Position = 0; await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false); return;
            }

            string? tmdb = null, imdb = null;
            if (dict.TryGetValue("ProviderIds", out var pidObj) && pidObj is System.Collections.Generic.Dictionary<string, object?> pids)
                foreach (var kv in pids)
                {
                    if (string.Equals(kv.Key, "Tmdb", StringComparison.OrdinalIgnoreCase)) tmdb = kv.Value?.ToString();
                    else if (string.Equals(kv.Key, "Imdb", StringComparison.OrdinalIgnoreCase)) imdb = kv.Value?.ToString();
                }
            var name = dict.TryGetValue("Name", out var nm) ? nm as string : null;
            int? year = (dict.TryGetValue("ProductionYear", out var yv) && yv is not null && int.TryParse(yv.ToString(), out var yy)) ? yy : null;

            var store = ctx.RequestServices.GetService(typeof(RemoteItemStore)) as RemoteItemStore;
            var config = Plugin.Instance?.Configuration;
            var added = 0;
            if (store is not null && config is not null)
            {
                var seenServers = new System.Collections.Generic.HashSet<Guid>();
                foreach (var match in store.FindMatches(tmdb, imdb, name, year))
                {
                    var server = config.RemoteServers.FirstOrDefault(s => s.Id == match.ServerId);
                    if (server is null || !server.Enabled) continue;
                    if (!seenServers.Add(server.Id)) continue; // one alternate per peer
                    var srcId = match.RemoteItemId;
                    string? container = null; object? streams = null;
                    try
                    {
                        using var msd = JsonDocument.Parse(match.MediaSourceJson ?? "[]");
                        if (msd.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var first = msd.RootElement.EnumerateArray().FirstOrDefault();
                            if (first.ValueKind == JsonValueKind.Object)
                            {
                                if (first.TryGetProperty("Id", out var sid) && sid.ValueKind == JsonValueKind.String) srcId = sid.GetString() ?? srcId;
                                if (first.TryGetProperty("Container", out var c2)) container = c2.GetString();
                                if (first.TryGetProperty("MediaStreams", out var st)) streams = JsonElementToObject(st);
                            }
                        }
                    }
                    catch { /* malformed peer json -> minimal source */ }

                    var serverN = server.Id.ToString("N");
                    var token = NewMediaToken(serverN, match.RemoteItemId);
                    var path = $"/Federation/Stream/{serverN}/{match.RemoteItemId}?sourceId={Uri.EscapeDataString(srcId)}&{MediaTokenParam}={Uri.EscapeDataString(token)}";
                    sources.Add(new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["Id"] = "fed_" + serverN + "_" + srcId,
                        ["Name"] = "[" + server.Name + "] " + (name ?? "version"),
                        ["Protocol"] = "Http",
                        ["Path"] = path,
                        ["IsRemote"] = true,
                        ["SupportsDirectPlay"] = true,
                        ["SupportsDirectStream"] = true,
                        ["SupportsTranscoding"] = false,
                        ["Container"] = container,
                        ["MediaStreams"] = streams,
                    });
                    added++;
                }
            }
            if (added > 0) dict["MediaSourceCount"] = sources.Count;

            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentType = "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict));
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppendLocalItemVersions failed for {Id}", localGuid);
            buffer.Position = 0;
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
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

            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = TimeSpan.FromSeconds(15);
            var localPort = ctx.Connection.LocalPort > 0 ? ctx.Connection.LocalPort : 8096;
            var jellyfinHost = $"http://localhost:{localPort}";
            var limit = int.TryParse(ctx.Request.Query["Limit"].ToString(), out var l) ? l : 100;
            limit = Math.Clamp(limit, 1, 200);
            // Forward the in-library search term so peers filter their own side instead of us
            // only matching within the first `limit` rows they happen to return.
            var searchTerm = ctx.Request.Query["SearchTerm"].ToString();
            var searchQs = string.IsNullOrEmpty(searchTerm) ? string.Empty : $"&searchTerm={Uri.EscapeDataString(searchTerm)}";

            // Fan out to every merged peer in parallel (was sequential: N peers = N round trips
            // back to back, the dominant cost of a merged library / home load). HttpClient is
            // thread-safe; ForwardAuth only reads request headers.
            var fetches = merges.Select(async m =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{jellyfinHost}/Federation/Peers/{m.PeerId}/Libraries/{Uri.EscapeDataString(m.LibraryId)}/Items?limit={limit}{searchQs}");
                    ForwardAuth(ctx, req);
                    using var r = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode) return (m, (string?)null);
                    return (m, await r.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppendPeerItems skipping peer {Peer} lib {Lib}", m.PeerId, m.LibraryId);
                    return (m, (string?)null);
                }
            }).ToList();
            var results = await Task.WhenAll(fetches).ConfigureAwait(false);

            // Dedup the same film coming from several merged peers (and from our local library):
            // seed seen-keys from the local items already in the response, then skip any peer
            // item whose title+year already appeared. Applied in merges order for stability.
            var seen = SeedDedupKeys(items);
            var peerAdded = 0;
            // dedupKey -> (kept card's fed id, the card dict) so duplicates from other peers can
            // be stacked as alternate versions on the kept card.
            var keptByKey = new System.Collections.Generic.Dictionary<string, (string FedId, System.Collections.Generic.Dictionary<string, object?> Card)>();
            foreach (var (m, json) in results)
            {
                if (json is null) continue;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    var peerN = m.PeerId.ToString("N");
                    if (d.RootElement.TryGetProperty("items", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            var rawId = el.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                            if (string.IsNullOrEmpty(rawId)) continue;
                            var dk = PeerElementDedupKey(el);
                            if (!seen.Add(dk))
                            {
                                // Same title+year already shown. If WE kept a peer card for it,
                                // stack this peer's copy as an alternate version on that card.
                                if (keptByKey.TryGetValue(dk, out var kept))
                                    RegisterAlternate(kept.FedId, peerN, rawId!);
                                continue;
                            }
                            var fedId = "fed_" + peerN + "_" + rawId;
                            ResetAlternates(fedId);
                            var card = MapPeerItemDto(el, peerN);
                            keptByKey[dk] = (fedId, card);
                            items.Add(card);
                            peerAdded++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppendPeerItems parse failed for peer {Peer}", m.PeerId);
                }
            }
            // Flag cards that gained alternate versions so the SPA shows a version count.
            foreach (var kv in keptByKey)
                if (_versionAlternates.TryGetValue(kv.Value.FedId, out var alts) && alts.Count > 0)
                    kv.Value.Card["MediaSourceCount"] = alts.Count + 1;
            dict["Items"] = items;
            // Keep the local total meaningful: original count + the peer items we actually added,
            // instead of clobbering it with this single page's length.
            var localTotal = (dict.TryGetValue("TotalRecordCount", out var tObj) && tObj is not null)
                ? Convert.ToInt64(tObj, System.Globalization.CultureInfo.InvariantCulture) : items.Count - peerAdded;
            dict["TotalRecordCount"] = localTotal + peerAdded;

            // Merged content changes with peer availability + merge settings; never let the
            // browser serve a stale copy, so a plain reload (not a hard refresh) shows fresh data.
            ctx.Response.Headers["Cache-Control"] = "no-store";
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

            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = TimeSpan.FromSeconds(15);
            var localPort = ctx.Connection.LocalPort > 0 ? ctx.Connection.LocalPort : 8096;
            var jellyfinHost = $"http://localhost:{localPort}";
            var limit = int.TryParse(ctx.Request.Query["Limit"].ToString(), out var l) ? l : 16;
            limit = Math.Clamp(limit, 1, 50);

            // Fan out to merged peers in parallel (was sequential).
            var fetches = merges.Select(async m =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{jellyfinHost}/Federation/Peers/{m.PeerId}/Libraries/{Uri.EscapeDataString(m.LibraryId)}/Items?limit={limit}");
                    ForwardAuth(ctx, req);
                    using var r = await http.SendAsync(req, ctx.RequestAborted).ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode) return (m, (string?)null);
                    return (m, await r.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppendPeerLatest skipping peer {Peer} lib {Lib}", m.PeerId, m.LibraryId);
                    return (m, (string?)null);
                }
            }).ToList();
            var results = await Task.WhenAll(fetches).ConfigureAwait(false);

            var seen = SeedDedupKeys(items);
            foreach (var (m, json) in results)
            {
                if (json is null) continue;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    var peerN = m.PeerId.ToString("N");
                    if (d.RootElement.TryGetProperty("items", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (!seen.Add(PeerElementDedupKey(el))) continue;
                            items.Add(MapPeerItemDto(el, peerN));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppendPeerLatest parse failed for peer {Peer}", m.PeerId);
                }
            }

            ctx.Response.Headers["Cache-Control"] = "no-store";
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

    // Title+year dedup key so the same film merged from several peers (or already present
    // locally) collapses to one card. Lower-cased, trimmed; year optional.
    private static string DedupKey(string? name, string? year)
        => (name ?? string.Empty).Trim().ToLowerInvariant() + "|" + (year ?? string.Empty);

    private static System.Collections.Generic.HashSet<string> SeedDedupKeys(System.Collections.Generic.List<object?> items)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (it is not System.Collections.Generic.Dictionary<string, object?> d) continue;
            var name = d.TryGetValue("Name", out var n) ? n as string : null;
            string? year = null;
            if (d.TryGetValue("ProductionYear", out var y) && y is not null) year = Convert.ToString(y, System.Globalization.CultureInfo.InvariantCulture);
            seen.Add(DedupKey(name, year));
        }
        return seen;
    }

    private static string PeerElementDedupKey(JsonElement el)
    {
        var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
        string? year = el.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return DedupKey(name, year);
    }

    // === Multi-version (same film on several peers) ===
    // When a merged library lists the same title+year from more than one peer we keep ONE card
    // (dedup) and remember the OTHER peers' copies here, keyed by the kept card's fed id. On
    // PlaybackInfo for that card we fetch each alternate peer's MediaSources too and stack them,
    // so the player shows a version picker instead of silently hiding the duplicates. Rebuilt on
    // every library browse; capped to avoid unbounded growth.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<(string PeerN, string RemoteId)>> _versionAlternates = new();

    private static void ResetAlternates(string fedId)
    {
        _versionAlternates.TryRemove(fedId, out _);
        if (_versionAlternates.Count > 5000) _versionAlternates.Clear(); // crude leak guard
    }

    private static void RegisterAlternate(string keptFedId, string peerN, string remoteId)
    {
        var list = _versionAlternates.GetOrAdd(keptFedId, _ => new System.Collections.Generic.List<(string, string)>());
        lock (list)
        {
            if (!list.Exists(a => a.PeerN == peerN && a.RemoteId == remoteId))
                list.Add((peerN, remoteId));
        }
    }

    // Fetch + fed-rewrite a peer's MediaSources for an alternate version, labelled with the peer
    // name so the version picker can tell copies apart. Returns null on any failure.
    private async Task<System.Collections.Generic.List<object?>?> FetchAltPeerSources(string peerN, string remoteId, string deviceProfileBody, CancellationToken ct)
    {
        if (!Guid.TryParseExact(peerN, "N", out var pid)) return null;
        var peer = Plugin.Instance?.Configuration?.RemoteServers.FirstOrDefault(p => p.Id == pid);
        if (peer is null || !peer.Enabled) return null;
        try
        {
            var http = _httpClientFactory.CreateClient("federation");
            http.Timeout = TimeSpan.FromSeconds(15);
            RemoteJellyfinClient.AddBasicAuth(http, peer);
            var peerUid = await ResolvePeerUserId(http, peer, ct).ConfigureAwait(false);
            var qs = string.IsNullOrEmpty(peerUid) ? string.Empty : "?UserId=" + Uri.EscapeDataString(peerUid);
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}/PlaybackInfo{qs}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            var body = SetBodyUserId(RewriteFedIdsForPeer(deviceProfileBody, peerN, remoteId), peerUid);
            if (!string.IsNullOrEmpty(body)) req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("MediaSources", out var msEl) || msEl.ValueKind != JsonValueKind.Array) return null;
            var list = new System.Collections.Generic.List<object?>();
            foreach (var s in msEl.EnumerateArray()) list.Add(JsonElementToObject(s));
            RewriteFederatedMediaSources(list, peerN, remoteId, "fed_" + peerN + "_" + remoteId);
            foreach (var o in list)
                if (o is System.Collections.Generic.Dictionary<string, object?> ms)
                    ms["Name"] = "[" + peer.Name + "] " + (ms.TryGetValue("Name", out var nm) ? nm : string.Empty);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FetchAltPeerSources failed for {Peer} {Item}", peerN, remoteId);
            return null;
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
