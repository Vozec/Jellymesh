using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

public class RemoteJellyfinClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RemoteJellyfinClient> _logger;

    public RemoteJellyfinClient(IHttpClientFactory httpClientFactory, ILogger<RemoteJellyfinClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> PingAsync(RemoteServer server, CancellationToken ct)
    {
        try
        {
            var http = BuildClient(server);
            using var resp = await http.GetAsync("/System/Info/Public", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ping failed for {Server}", server.Name);
            return false;
        }
    }

    /// <summary>Fetches the peer's Federation plugin catalog digest. Returns null if the peer doesn't run the plugin.</summary>
    public async Task<(int Count, string Hash)?> FetchDigestAsync(RemoteServer server, CancellationToken ct)
    {
        try
        {
            var http = BuildClient(server);
            using var resp = await http.GetAsync("/Federation/Catalog/Digest", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var count = doc.RootElement.TryGetProperty("Count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var hash = doc.RootElement.TryGetProperty("Hash", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            return (count, hash);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Digest fetch failed for {Server} (peer may not run plugin)", server.Name);
            return null;
        }
    }

    public async IAsyncEnumerable<RemoteItem> FetchItemsAsync(
        RemoteServer server,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int pageSize = 500;
        var fields = "ProviderIds,MediaSources,MediaStreams,Path,Width,Height,Container,Bitrate,RunTimeTicks";
        var prefix = !string.IsNullOrEmpty(server.RemoteUserId) ? $"/Users/{server.RemoteUserId}/Items" : "/Items";

        var http = BuildClient(server);
        var start = 0;
        int total;
        do
        {
            var url = $"{prefix}?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields={fields}&StartIndex={start}&Limit={pageSize}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            total = doc.RootElement.TryGetProperty("TotalRecordCount", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt32() : 0;

            if (!doc.RootElement.TryGetProperty("Items", out var items)) yield break;

            var thisPage = 0;
            foreach (var el in items.EnumerateArray())
            {
                yield return MapItem(server.Id, el);
                thisPage++;
            }
            if (thisPage == 0) yield break;
            start += thisPage;
        } while (start < total);
    }

    private static RemoteItem MapItem(Guid serverId, JsonElement el)
    {
        var item = new RemoteItem
        {
            ServerId = serverId,
            RemoteItemId = el.TryGetProperty("Id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Type = el.TryGetProperty("Type", out var t) ? t.GetString() ?? string.Empty : string.Empty,
            Name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
            ProductionYear = el.TryGetProperty("ProductionYear", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
            RunTimeTicks = el.TryGetProperty("RunTimeTicks", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : null,
            Width = el.TryGetProperty("Width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null,
            Height = el.TryGetProperty("Height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null,
            Container = el.TryGetProperty("Container", out var c) ? c.GetString() : null,
            Bitrate = el.TryGetProperty("Bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null,
            LastSeenUtc = DateTime.UtcNow
        };

        if (el.TryGetProperty("ProviderIds", out var pids) && pids.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in pids.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    item.ProviderIds[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }

        if (el.TryGetProperty("MediaSources", out var ms))
            item.MediaSourceJson = ms.GetRawText();

        return item;
    }

    /// <summary>
    /// Fetches the peer's items that have non-default UserData (played, in-progress, or
    /// favorited). Each result carries ProviderIds for matching to local items + the
    /// UserData payload.
    /// </summary>
    public async IAsyncEnumerable<RemoteUserDataEntry> FetchUserDataAsync(
        RemoteServer server,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(server.RemoteUserId)) yield break;

        const int pageSize = 500;
        var http = BuildClient(server);

        // Two passes - peer's API doesn't have an "IsPlayed OR IsResumable" filter, so we
        // union the two queries client-side. Each is paginated against TotalRecordCount;
        // same defensive pagination as FetchItemsAsync to avoid silently truncating peers
        // with >pageSize watched items.
        foreach (var filter in new[] { "IsPlayed=true", "IsResumable=true" })
        {
            var start = 0;
            int total;
            do
            {
                var url = $"/Users/{server.RemoteUserId}/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=UserData,ProviderIds&{filter}&StartIndex={start}&Limit={pageSize}";
                int pageYielded;
                (total, pageYielded) = await EnumerateUserDataPageAsync(http, url, ct, this).ConfigureAwait(false);
                if (pageYielded == 0) break;
                start += pageYielded;

                // Yield happens by re-enumerating - done above via helper that puts entries
                // into a buffer, then we drain here. (yields are forbidden directly inside
                // catch blocks; the helper avoids that constraint.)
                foreach (var entry in _pendingBuffer) yield return entry;
                _pendingBuffer.Clear();
            } while (start < total);
        }
    }

    // Re-used scratch buffer per call. Safe because FetchUserDataAsync is invoked sequentially
    // by FederationSyncTask (one sync round at a time).
    private readonly List<RemoteUserDataEntry> _pendingBuffer = new();

    private static async Task<(int Total, int Yielded)> EnumerateUserDataPageAsync(
        HttpClient http, string url, CancellationToken ct, RemoteJellyfinClient self)
    {
        HttpResponseMessage? resp = null;
        JsonDocument? doc = null;
        try
        {
            resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (0, 0);
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var total = doc.RootElement.TryGetProperty("TotalRecordCount", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt32() : 0;
            if (!doc.RootElement.TryGetProperty("Items", out var items)) return (total, 0);

            var yielded = 0;
            foreach (var el in items.EnumerateArray())
            {
                var entry = MapUserData(el);
                if (entry is not null) { self._pendingBuffer.Add(entry); yielded++; }
            }
            return (total, yielded);
        }
        catch (Exception ex)
        {
            self._logger.LogDebug(ex, "FetchUserData page failed for {Url}", url);
            return (0, 0);
        }
        finally
        {
            doc?.Dispose();
            resp?.Dispose();
        }
    }

    private static RemoteUserDataEntry? MapUserData(JsonElement el)
    {
        if (!el.TryGetProperty("UserData", out var ud) || ud.ValueKind != JsonValueKind.Object) return null;
        var entry = new RemoteUserDataEntry
        {
            Played = ud.TryGetProperty("Played", out var p) && p.ValueKind == JsonValueKind.True,
            PlaybackPositionTicks = ud.TryGetProperty("PlaybackPositionTicks", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt64() : 0L,
            PlayCount = ud.TryGetProperty("PlayCount", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : 0,
            IsFavorite = ud.TryGetProperty("IsFavorite", out var f) && f.ValueKind == JsonValueKind.True,
            LastPlayedDate = ud.TryGetProperty("LastPlayedDate", out var lpd) && lpd.ValueKind == JsonValueKind.String && DateTime.TryParse(lpd.GetString(), out var d) ? d : null
        };
        if (el.TryGetProperty("ProviderIds", out var pids) && pids.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in pids.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    entry.ProviderIds[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }
        return entry;
    }

    /// <summary>Ask a peer to mint a federation share key on behalf of a third party.</summary>
    public async Task<IntroduceCallResult?> CallIntroduceAsync(RemoteServer peer,
        string forUrl, int hopCount, string? note, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            AddBasicAuth(http, peer);
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = $"{peer.BaseUrl.TrimEnd('/')}/Federation/Introduce";
            var body = JsonContent.Create(new { ForUrl = forUrl, HopCount = hopCount, Note = note });
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            req.Headers.Add("X-Federation-Share", peer.FederationShareKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Accepted)
            {
                _logger.LogWarning("CallIntroduce to {Peer} returned {Status}", peer.BaseUrl, (int)resp.StatusCode);
                return new IntroduceCallResult { Status = ((int)resp.StatusCode).ToString(), HttpStatus = (int)resp.StatusCode };
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            return new IntroduceCallResult
            {
                HttpStatus = (int)resp.StatusCode,
                Status = root.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "",
                ApiKey = root.TryGetProperty("ApiKey", out var k) ? k.GetString() : null,
                OurBaseUrl = root.TryGetProperty("OurBaseUrl", out var b) ? b.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CallIntroduce to {Peer} threw", peer.BaseUrl);
            return null;
        }
    }

    /// <summary>Forward a freshly-issued key to its target. Includes the introducer's
    /// HTTP Basic credentials for reaching the new peer, since the receiver will need the
    /// same credentials to talk to that peer.</summary>
    public async Task<ForwardResult> CallIntroducedAsync(RemoteServer receiver, string newPeerUrl,
        string newPeerKey, string introducedBy, int hopCount,
        string? newPeerBasicAuthUser, string? newPeerBasicAuthPass, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            AddBasicAuth(http, receiver);
            http.Timeout = TimeSpan.FromSeconds(10);

            // Probe for plugin presence. Any HTTP response counts; network failure rejects.
            using var probeReq = new HttpRequestMessage(HttpMethod.Get, $"{receiver.BaseUrl.TrimEnd('/')}/Federation/Catalog/Digest");
            try
            {
                using var probeResp = await http.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new ForwardResult { Reachable = false };
            }

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{receiver.BaseUrl.TrimEnd('/')}/Federation/Introduced");
            req.Headers.Add("X-Federation-Share", receiver.FederationShareKey);
            req.Content = JsonContent.Create(new
            {
                NewPeerUrl = newPeerUrl,
                NewPeerKey = newPeerKey,
                IntroducedBy = introducedBy,
                HopCount = hopCount,
                BasicAuthUser = newPeerBasicAuthUser,
                BasicAuthPass = newPeerBasicAuthPass
            });
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return new ForwardResult { Reachable = true, HttpStatus = (int)resp.StatusCode, Accepted = resp.IsSuccessStatusCode };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CallIntroduced to {Target} threw", receiver.BaseUrl);
            return new ForwardResult { Reachable = false };
        }
    }

    /// <summary>Send a "please add this" request to a peer using their FederationShareKey.</summary>
    public async Task<bool> SendRequestAsync(RemoteServer server, string ourPublicBaseUrl,
        string? tmdbId, string? imdbId, string? title, int? year, string? note, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(server.FederationShareKey)) return false;
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url = $"{server.BaseUrl.TrimEnd('/')}/Federation/Request";
            var body = JsonContent.Create(new
            {
                FromBaseUrl = ourPublicBaseUrl?.TrimEnd('/'),
                TmdbId = tmdbId,
                ImdbId = imdbId,
                Title = title,
                Year = year,
                Note = note
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            req.Headers.Add("X-Federation-Share", server.FederationShareKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendRequest to {Peer} failed", server.Name);
            return false;
        }
    }

    public async Task<bool> MarkPlayedAsync(RemoteServer server, string remoteItemId, bool played, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(server.RemoteUserId)) return false;
        try
        {
            var http = BuildClient(server);
            var url = $"/Users/{server.RemoteUserId}/PlayedItems/{remoteItemId}";
            using var resp = played
                ? await http.PostAsync(url, content: null, ct).ConfigureAwait(false)
                : await http.DeleteAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarkPlayed failed for {Server} item {Id}", server.Name, remoteItemId);
            return false;
        }
    }

    public async Task<bool> UpdateProgressAsync(RemoteServer server, string remoteItemId, long? positionTicks, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(server.RemoteUserId)) return false;
        try
        {
            var http = BuildClient(server);
            var url = $"/Users/{server.RemoteUserId}/Items/{remoteItemId}/UserData";
            var body = JsonContent.Create(new { PlaybackPositionTicks = positionTicks ?? 0 });
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdateProgress failed for {Server} item {Id}", server.Name, remoteItemId);
            return false;
        }
    }

    public async Task<string?> ResolveRemoteItemIdAsync(RemoteServer server, string? tmdbId, string? imdbId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(imdbId)) return null;
        try
        {
            var http = BuildClient(server);
            var qs = !string.IsNullOrEmpty(tmdbId)
                ? $"AnyProviderIdEquals=tmdb.{tmdbId}"
                : $"AnyProviderIdEquals=imdb.{imdbId}";
            // IncludeItemTypes filter is critical: without it, Limit=1 can match a Channel
            // or library folder that happens to surface for the user before the actual movie.
            var url = $"/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=ProviderIds&Limit=1&{qs}";
            if (!string.IsNullOrEmpty(server.RemoteUserId))
                url = $"/Users/{server.RemoteUserId}/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=ProviderIds&Limit=1&{qs}";

            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("Items", out var items)) return null;
            foreach (var el in items.EnumerateArray())
                if (el.TryGetProperty("Id", out var id)) return id.GetString();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResolveRemoteItemId failed for {Server}", server.Name);
            return null;
        }
    }

    private HttpClient BuildClient(RemoteServer server)
    {
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(server.BaseUrl.TrimEnd('/'));
        http.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
        AddBasicAuth(http, server);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    /// <summary>Adds Authorization: Basic header when the peer is behind a reverse proxy
    /// requiring HTTP Basic auth. Skipped when credentials are unset.</summary>
    internal static void AddBasicAuth(HttpClient http, RemoteServer server)
    {
        if (string.IsNullOrEmpty(server.BasicAuthUser) && string.IsNullOrEmpty(server.BasicAuthPass)) return;
        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{server.BasicAuthUser}:{server.BasicAuthPass}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
    }
}
