using System;
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
/// </summary>
public class FederationInterceptMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FederationInterceptMiddleware> _logger;

    public FederationInterceptMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory, ILogger<FederationInterceptMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value;
        if (path is null || ctx.Request.Method != HttpMethods.Get)
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // Match /Items/{fed_X}/Images/{type} OR /Users/{uid}/Items/{fed_X}/Images/{type}.
        var imageMatch = System.Text.RegularExpressions.Regex.Match(path,
            @"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)/Images/([^/]+)$");
        if (imageMatch.Success)
        {
            await ProxyImage(ctx, imageMatch.Groups[1].Value, imageMatch.Groups[2].Value).ConfigureAwait(false);
            return;
        }

        // Match /Items/{fed_X} OR /Users/{uid}/Items/{fed_X} (item details probe).
        var itemMatch = System.Text.RegularExpressions.Regex.Match(path,
            @"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)$");
        if (itemMatch.Success)
        {
            await ProxyItemDetail(ctx, itemMatch.Groups[1].Value).ConfigureAwait(false);
            return;
        }

        // Match /Items/{fedlib_X} OR /Users/{uid}/Items/{fedlib_X} (collection folder probe).
        var libMatch = System.Text.RegularExpressions.Regex.Match(path,
            @"^(?:/Users/[^/]+)?/Items/(fedlib_[0-9a-fA-F]+_[^/]+)$");
        if (libMatch.Success)
        {
            await WriteStubFolder(ctx, libMatch.Groups[1].Value).ConfigureAwait(false);
            return;
        }

        // Similar / Themes / etc. - return empty list so the SPA's secondary fetches stop 400ing.
        var subPath = System.Text.RegularExpressions.Regex.Match(path,
            @"^(?:/Users/[^/]+)?/Items/(fed_[0-9a-fA-F]+_[^/]+)/(Similar|ThemeMedia|ThemeSongs|ThemeVideos|InstantMix|SpecialFeatures|Trailers|RemoteImages|RemoteSearch)$");
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
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(remoteId)}/Images/{Uri.EscapeDataString(imageType)}{qs}";
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
            dict["ServerId"] = string.Empty;
            dict["ImageTags"] = new System.Collections.Generic.Dictionary<string, string> { ["Primary"] = "fed" };
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

    private static async Task WriteStubItem(HttpContext ctx, string fedId)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            Id = fedId,
            Name = "(federated)",
            Type = "Movie",
            MediaType = "Video",
            ServerId = string.Empty,
            ImageTags = new { Primary = "fed" },
            IsFolder = false,
            UserData = new { Played = false, IsFavorite = false, PlaybackPositionTicks = 0L, PlayCount = 0 }
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }

    private static async Task WriteStubFolder(HttpContext ctx, string fedLibId)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            Id = fedLibId,
            Name = "Library",
            Type = "CollectionFolder",
            CollectionType = "movies",
            ServerId = string.Empty,
            ImageTags = new { },
            IsFolder = true
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }

    private static async Task WriteEmptyList(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"Items\":[],\"TotalRecordCount\":0,\"StartIndex\":0}").ConfigureAwait(false);
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
