using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api;

[ApiController]
[Authorize]
[Route("Federation")]
public class FederationController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Services.RemoteItemStore _store;
    private readonly ILogger<FederationController> _logger;

    public FederationController(IHttpClientFactory httpClientFactory, Services.RemoteItemStore store, ILogger<FederationController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    [HttpGet("Stream/{serverId}/{itemId}")]
    public async Task<IActionResult> Stream(string serverId, string itemId, [FromQuery] string? sourceId, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return NotFound();
        // Item ids on Jellyfin are GUIDs. Reject anything else so `..` or `/` can't traverse
        // the peer's URL space under our auth context.
        if (!Guid.TryParseExact(itemId, "N", out _) && !Guid.TryParse(itemId, out _)) return BadRequest();

        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        var upstream = $"{server.BaseUrl.TrimEnd('/')}/Videos/{Uri.EscapeDataString(itemId)}/stream?static=true";
        if (!string.IsNullOrEmpty(sourceId))
            upstream += $"&MediaSourceId={Uri.EscapeDataString(sourceId)}";

        var req = new HttpRequestMessage(HttpMethod.Get, upstream);
        req.Headers.Add("X-Emby-Token", server.ApiKey);

        if (Request.Headers.TryGetValue("Range", out var range))
            req.Headers.TryAddWithoutValidation("Range", (string)range!);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        Response.StatusCode = (int)resp.StatusCode;
        CopySafeHeaders(resp.Headers, Response.Headers);
        CopySafeHeaders(resp.Content.Headers, Response.Headers);
        // When we'll throttle and re-meter the body, the upstream Content-Length no longer
        // matches what we'll write. Let Kestrel set Content-Length (or chunk) based on actual bytes.
        if (config.OutboundBitrateCapBps > 0) Response.Headers.Remove("content-length");

        // Audit row is best-effort — if SQLite is locked we still serve the stream.
        var auditId = -1L;
        try { auditId = _store.BeginAudit(server.Id, itemId, User?.Identity?.Name); }
        catch (Exception ex) { _logger.LogWarning(ex, "BeginAudit failed for {Peer} {Item}; serving stream without audit row", server.Id, itemId); }

        var bytesServed = 0L;
        try
        {
            var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // ThrottledStream owns disposing src (its Dispose forwards). Don't await using src
            // separately or src is disposed twice.
            var cap = config.OutboundBitrateCapBps;
            await using Stream throttled = cap > 0 ? new Services.ThrottledStream(src, cap / 8) : src;

            var buf = new byte[81920];
            int n;
            while ((n = await throttled.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await Response.Body.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                bytesServed += n;
            }
        }
        finally
        {
            if (auditId > 0)
            {
                try { _store.CompleteAudit(auditId, bytesServed); }
                catch (Exception ex) { _logger.LogWarning(ex, "CompleteAudit failed for {AuditId}", auditId); }
            }
        }
        return new EmptyResult();
    }

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "trailers", "transfer-encoding", "upgrade",
        "server", // we shouldn't impersonate upstream's server header
    };

    private static void CopySafeHeaders(System.Net.Http.Headers.HttpHeaders src, IHeaderDictionary dst)
    {
        foreach (var h in src)
        {
            if (HopByHopHeaders.Contains(h.Key)) continue;
            dst[h.Key] = h.Value.ToArray();
        }
    }

    // === Peer-to-peer "please add this" request system ===

    [AllowAnonymous]
    [HttpPost("Request")]
    public IActionResult ReceiveRequest([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] IncomingRequestPayload payload,
        [FromServices] Services.RequestStore requests)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (payload is null) return BadRequest("missing body");

        // Trim and reject whitespace-only — IsNullOrWhiteSpace catches "   " that IsNullOrEmpty
        // would let through, preventing a peer from consuming pending-uniq slots with garbage.
        var tmdb = string.IsNullOrWhiteSpace(payload.TmdbId) ? null : payload.TmdbId.Trim();
        var imdb = string.IsNullOrWhiteSpace(payload.ImdbId) ? null : payload.ImdbId.Trim();
        var title = string.IsNullOrWhiteSpace(payload.Title) ? null : payload.Title.Trim();
        var note = string.IsNullOrWhiteSpace(payload.Note) ? null : payload.Note.Trim();
        if (tmdb is null && imdb is null && title is null)
            return BadRequest("need at least one of TmdbId, ImdbId, Title");

        // Cap field lengths to prevent a (possibly compromised) share-key holder from filling
        // the requests DB with multi-MB strings.
        if (tmdb is { Length: > 64 } || imdb is { Length: > 64 }) return BadRequest("id too long");
        if (title is { Length: > 512 }) return BadRequest("title too long");
        if (note is { Length: > 2048 }) return BadRequest("note too long");

        var config = Plugin.Instance?.Configuration;
        Guid? peerId = null;
        if (config is not null && !string.IsNullOrEmpty(payload.FromBaseUrl))
        {
            var match = config.RemoteServers.FirstOrDefault(s => Services.PeerUrl.SameHost(s.BaseUrl, payload.FromBaseUrl));
            peerId = match?.Id;
        }

        var id = requests.Insert(new Services.FederationRequest
        {
            Direction = "in",
            PeerId = peerId,
            PeerUrl = payload.FromBaseUrl,
            TmdbId = tmdb,
            ImdbId = imdb,
            Title = title,
            Year = payload.Year,
            Note = note
        });
        // Null id = uniq_inbound_pending kicked in. Log for debugging but still respond 204
        // (idempotent semantics: "we got your request, already on our list").
        if (id is null) _logger.LogDebug("ReceiveRequest deduped from peer {Url} tmdb={Tmdb} imdb={Imdb} title={Title}", payload.FromBaseUrl, tmdb, imdb, title);
        return NoContent();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("SendRequest")]
    public async Task<IActionResult> SendRequest([FromBody] SendRequestPayload payload,
        [FromServices] Services.RequestStore requests,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        if (payload is null || !Guid.TryParse(payload.PeerId, out var peerId)) return BadRequest();
        var config = Plugin.Instance?.Configuration;
        var peer = config?.RemoteServers.FirstOrDefault(s => s.Id == peerId);
        if (peer is null) return NotFound("Unknown peer");
        if (string.IsNullOrEmpty(peer.FederationShareKey)) return BadRequest("Peer has no FederationShareKey configured for us");
        if (string.IsNullOrEmpty(config!.PublicBaseUrl)) return BadRequest("PublicBaseUrl not set — peer wouldn't be able to identify us");

        var ok = await client.SendRequestAsync(peer, config.PublicBaseUrl, payload.TmdbId, payload.ImdbId, payload.Title, payload.Year, payload.Note, ct).ConfigureAwait(false);

        requests.Insert(new Services.FederationRequest
        {
            Direction = "out",
            PeerId = peer.Id,
            PeerUrl = peer.BaseUrl,
            TmdbId = payload.TmdbId,
            ImdbId = payload.ImdbId,
            Title = payload.Title,
            Year = payload.Year,
            Note = payload.Note,
            Status = ok ? "pending" : "send-failed"
        });

        return ok ? Ok(new { sent = true }) : StatusCode(502, "Peer rejected the request");
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Requests/{direction}")]
    public IActionResult ListRequests(string direction, [FromQuery] string? status,
        [FromServices] Services.RequestStore requests)
    {
        if (direction != "in" && direction != "out") return BadRequest("direction must be 'in' or 'out'");
        if (status is not null && !Services.RequestStore.IsValidStatus(status))
            return BadRequest("unknown status");
        return Ok(requests.List(direction, status));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Requests/{id}/Status")]
    public IActionResult SetRequestStatus(long id, [FromQuery] string status,
        [FromServices] Services.RequestStore requests)
    {
        // 'send-failed' included so admin can dismiss / retry a failed outbound row.
        if (!Services.RequestStore.IsValidStatus(status))
            return BadRequest("status must be pending|accepted|declined|dismissed|send-failed");
        return requests.UpdateStatus(id, status) ? NoContent() : NotFound();
    }

    // === Push-invalidation receiver ===
    // A peer ran the local PushInvalidationService and is telling us their catalog changed.
    // They identify themselves via X-Federation-Share (a key WE issued) + their public URL.
    // We match the URL against our RemoteServers and drop the cached digest for that peer
    // so the next sync round actually re-pulls.

    [AllowAnonymous]
    [HttpPost("Invalidate")]
    public IActionResult ReceiveInvalidate([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] Services.InvalidatePayload payload)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (payload is null || string.IsNullOrEmpty(payload.FromBaseUrl)) return BadRequest();

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var match = config.RemoteServers.FirstOrDefault(s => Services.PeerUrl.SameHost(s.BaseUrl, payload.FromBaseUrl));
        if (match is null)
        {
            _logger.LogDebug("Invalidate from {Url} ignored — no matching RemoteServer", payload.FromBaseUrl);
            return NoContent();
        }

        _store.InvalidateDigest(match.Id);
        _logger.LogInformation("Invalidated cached digest for {Peer} per push notification", match.Name);
        return NoContent();
    }

    // === Anonymous, expiring, use-capped video share links ===
    // Admin generates one per video; hands the URL to anyone. No Jellyfin auth required to view.

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("PublicShares")]
    public IActionResult CreatePublicShare([FromBody] CreatePublicShareRequest req,
        [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        if (string.IsNullOrWhiteSpace(req.ItemId)) return BadRequest("ItemId required");
        if (!Guid.TryParse(req.ItemId, out var itemGuid)) return BadRequest("ItemId not a guid");

        var item = library.GetItemById(itemGuid);
        if (item is null) return NotFound("Item not found");

        var token = store.Create(req.ItemId!, req.ExpiresUtc, req.MaxUses, User?.Identity?.Name);
        var url = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/Federation/Public/{token}";
        return Ok(new { token, url, expiresUtc = req.ExpiresUtc, maxUses = req.MaxUses, itemName = item.Name });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("PublicShares/{token}")]
    public IActionResult RevokePublicShare(string token, [FromServices] Services.PublicShareStore store)
    {
        store.Revoke(token);
        return NoContent();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("PublicShares")]
    public IActionResult ListPublicShares([FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        var rows = store.ListAll(limit: 500).Select(s =>
        {
            var name = Guid.TryParse(s.ItemId, out var g) ? library.GetItemById(g)?.Name : null;
            return new
            {
                s.Token,
                s.ItemId,
                ItemName = name ?? "(deleted)",
                s.ExpiresUtc,
                s.MaxUses,
                s.UsedCount,
                s.CreatedUtc,
                Expired = s.ExpiresUtc.HasValue && s.ExpiresUtc < DateTime.UtcNow,
                Exhausted = s.MaxUses.HasValue && s.UsedCount >= s.MaxUses.Value
            };
        });
        return Ok(rows);
    }

    [AllowAnonymous]
    [HttpGet("Public/{token}")]
    public IActionResult PublicViewer(string token, [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        // Consume happens HERE — viewer-page load = one use. The /Stream endpoint below
        // re-validates (token exists + not expired) but does NOT decrement, so the same
        // viewer can seek / re-request Range without exhausting the cap.
        // Trade-off: a viewer reloading the page = another consume. Considered acceptable
        // — admin's mental model is "5 people can watch", not "5 byte ranges".
        var consumedItemId = store.TryConsume(token);
        if (consumedItemId is null)
        {
            // Distinguish 'unknown' from 'denied' via GetInfo for a friendlier 410 message.
            var probe = store.GetInfo(token);
            if (probe is null) return NotFound();
            if (probe.ExpiresUtc.HasValue && probe.ExpiresUtc < DateTime.UtcNow) return StatusCode(410, "Link expired");
            return StatusCode(410, "Link exhausted");
        }

        var info = store.GetInfo(token)!;
        if (!Guid.TryParse(info.ItemId, out var g)) return NotFound();
        var item = library.GetItemById(g);
        var name = item?.Name ?? "(unknown)";

        var html = $@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><title>{System.Net.WebUtility.HtmlEncode(name)}</title>
<style>
body{{background:#111;color:#eee;font-family:system-ui;margin:0;padding:2rem;text-align:center}}
video{{max-width:100%;max-height:80vh}}
h1{{font-weight:400;font-size:1.2rem}}
.meta{{color:#888;font-size:.8rem;margin-top:1rem}}
</style></head><body>
<h1>{System.Net.WebUtility.HtmlEncode(name)}</h1>
<video controls autoplay src=""/Federation/Public/{token}/Stream""></video>
<div class=""meta"">Shared link — {info.UsedCount + 1}{(info.MaxUses.HasValue ? "/" + info.MaxUses : "")} use{(info.MaxUses == 1 ? "" : "s")}{(info.ExpiresUtc.HasValue ? $" · expires {info.ExpiresUtc:yyyy-MM-dd HH:mm} UTC" : "")}</div>
</body></html>";
        return Content(html, "text/html; charset=utf-8");
    }

    [AllowAnonymous]
    [HttpGet("Public/{token}/Stream")]
    public IActionResult PublicStream(string token,
        [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        // Validate-only path — does NOT consume. Consumption already happened in the viewer
        // page load (PublicViewer above). Each browser Range request hits us once; we just
        // confirm the token still exists and hasn't expired, then serve bytes.
        var info = store.GetInfo(token);
        if (info is null) return NotFound();
        if (info.ExpiresUtc.HasValue && info.ExpiresUtc < DateTime.UtcNow) return StatusCode(410, "Link expired");

        if (!Guid.TryParse(info.ItemId, out var g)) return NotFound();
        var item = library.GetItemById(g);
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path)) return NotFound();

        var contentType = GuessContentType(item.Path);
        return PhysicalFile(item.Path, contentType, enableRangeProcessing: true);
    }

    private static string GuessContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            _ => "application/octet-stream"
        };
    }

    [HttpGet("Image/{serverId}/{itemId}/{imageType}")]
    public async Task<IActionResult> ProxyImage(string serverId, string itemId, string imageType, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return NotFound();
        if (!Guid.TryParseExact(itemId, "N", out _) && !Guid.TryParse(itemId, out _)) return BadRequest();
        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        var url = $"{server.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(itemId)}/Images/{Uri.EscapeDataString(imageType)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Emby-Token", server.ApiKey);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return NotFound();

        Response.StatusCode = (int)resp.StatusCode;
        CopySafeHeaders(resp.Content.Headers, Response.Headers);
        await resp.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Audit/Recent")]
    public IActionResult RecentAudit([FromQuery] int limit = 100)
        => Ok(_store.RecentAudits(Math.Clamp(limit, 1, 1000)));

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Stats")]
    public IActionResult Stats([FromServices] Services.FederationStatsService stats)
        => Ok(stats.Build());

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Catalog/Digest")]
    public IActionResult CatalogDigest([FromServices] Services.LocalCatalogDigest digest)
        => Ok(digest.Compute());

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Catalog/Items")]
    public IActionResult CatalogItems([FromServices] Services.LocalCatalogDigest digest)
        => Ok(digest.List());

    // === Share-key scoped endpoints ===
    // Peers query these with X-Federation-Share header. The key is bound to a subset of
    // libraries (or all). This lets the user split library access per-friend without
    // exposing their full Jellyfin token.

    [AllowAnonymous]
    [HttpGet("Share/Catalog/Digest")]
    public IActionResult ShareCatalogDigest([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromServices] Services.LocalCatalogDigest digest)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (!IsWithinSchedule(key)) return StatusCode(403, "Outside allowed-hours window");
        return Ok(digest.Compute(key.LibraryIds, key.BlockedTags, key.MaxOfficialRating, key.StrictUnknownRating));
    }

    [AllowAnonymous]
    [HttpGet("Share/Catalog/Items")]
    public IActionResult ShareCatalogItems([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromServices] Services.LocalCatalogDigest digest)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (!IsWithinSchedule(key)) return StatusCode(403, "Outside allowed-hours window");
        return Ok(digest.List(key.LibraryIds, key.BlockedTags, key.MaxOfficialRating, key.StrictUnknownRating));
    }

    private static bool IsWithinSchedule(Configuration.ShareKey key)
    {
        var tzId = key.ScheduleTimeZoneId;
        TimeZoneInfo tz;
        try
        {
            tz = string.IsNullOrEmpty(tzId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            tz = TimeZoneInfo.Local; // unknown id → fall back, don't lock peers out
        }
        var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).TimeOfDay;
        return Services.ScheduleWindow.IsWithin(key.AllowedHoursStart, key.AllowedHoursEnd, nowInTz);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Shares")]
    public IActionResult ListShares()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return Ok(Array.Empty<object>());
        return Ok(config.Shares.Select(s => new
        {
            s.Id,
            s.Label,
            s.LibraryIds,
            s.Enabled,
            s.CreatedUtc,
            ApiKeyPreview = s.ApiKey.Length > 8 ? s.ApiKey[..4] + "…" + s.ApiKey[^4..] : "***"
        }));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Shares")]
    public IActionResult CreateShare([FromBody] CreateShareRequest req)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var key = new Configuration.ShareKey
        {
            Label = req.Label ?? "Unnamed share",
            LibraryIds = req.LibraryIds ?? new List<string>(),
            AllowedHoursStart = string.IsNullOrWhiteSpace(req.AllowedHoursStart) ? null : req.AllowedHoursStart,
            AllowedHoursEnd = string.IsNullOrWhiteSpace(req.AllowedHoursEnd) ? null : req.AllowedHoursEnd,
            ScheduleTimeZoneId = string.IsNullOrWhiteSpace(req.ScheduleTimeZoneId) ? null : req.ScheduleTimeZoneId,
            BlockedTags = req.BlockedTags ?? new List<string>(),
            MaxOfficialRating = string.IsNullOrWhiteSpace(req.MaxOfficialRating) ? null : req.MaxOfficialRating,
            StrictUnknownRating = req.StrictUnknownRating,
            ApiKey = GenerateApiKey(),
            Enabled = true
        };
        config.Shares.Add(key);
        Plugin.Instance!.SaveConfiguration();
        return Ok(new { key.Id, key.Label, key.ApiKey, key.LibraryIds });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("Shares/{id}")]
    public IActionResult DeleteShare(Guid id)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var removed = config.Shares.RemoveAll(s => s.Id == id);
        if (removed == 0) return NotFound();
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    private static Configuration.ShareKey? ResolveShareKey(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        var config = Plugin.Instance?.Configuration;
        if (config is null) return null;
        // Constant-time comparison — same length is necessary; CryptographicOperations.FixedTimeEquals
        // throws on length mismatch, so equalize via byte spans of equal size.
        var presentedBytes = System.Text.Encoding.UTF8.GetBytes(presented);
        foreach (var s in config.Shares)
        {
            if (!s.Enabled) continue;
            var storedBytes = System.Text.Encoding.UTF8.GetBytes(s.ApiKey);
            if (storedBytes.Length != presentedBytes.Length) continue;
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, presentedBytes))
                return s;
        }
        return null;
    }

    private static string GenerateApiKey()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Sync/Trigger")]
    public IActionResult TriggerSync([FromServices] MediaBrowser.Model.Tasks.ITaskManager taskManager)
    {
        taskManager.Execute<Services.FederationSyncTask>();
        return Ok(new { triggered = true });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Peers/Status")]
    public IActionResult PeersStatus([FromServices] Services.PeerHealthRegistry health)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return Ok(Array.Empty<object>());
        return Ok(config.RemoteServers.Select(s =>
        {
            var h = health.Get(s.Id);
            return new
            {
                s.Id,
                s.Name,
                s.BaseUrl,
                s.Enabled,
                Online = h.Online,
                LastCheckUtc = h.LastCheckUtc,
                LastRttMs = h.LastRttMs
            };
        }));
    }

    [HttpGet("Search")]
    public async Task<IActionResult> Search([FromQuery] string searchTerm, [FromQuery] int limit = 25, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(searchTerm))
            return Ok(new { Results = Array.Empty<object>() });

        var tasks = config.RemoteServers
            .Where(s => s.Enabled)
            .Select(s => QueryPeerAsync(s, searchTerm, limit, ct))
            .ToList();

        var bags = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = bags.SelectMany(x => x).Take(limit * config.RemoteServers.Count).ToList();
        return Ok(new { TotalRecordCount = merged.Count, Results = merged });
    }

    private async Task<List<FederatedSearchHit>> QueryPeerAsync(Configuration.RemoteServer server, string searchTerm, int limit, CancellationToken ct)
    {
        var results = new List<FederatedSearchHit>();
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(server.BaseUrl.TrimEnd('/'));
            http.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"/Search/Hints?searchTerm={Uri.EscapeDataString(searchTerm)}&limit={limit}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return results;

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("SearchHints", out var hints)) return results;

            foreach (var hit in hints.EnumerateArray())
            {
                results.Add(new FederatedSearchHit
                {
                    PeerId = server.Id,
                    PeerName = server.Name,
                    ItemId = hit.TryGetProperty("Id", out var id) ? id.GetString() : null,
                    Name = hit.TryGetProperty("Name", out var n) ? n.GetString() : null,
                    Type = hit.TryGetProperty("Type", out var t) ? t.GetString() : null,
                    Year = hit.TryGetProperty("ProductionYear", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                    PrimaryImageUrl = $"{server.BaseUrl.TrimEnd('/')}/Items/{(hit.TryGetProperty("Id", out var iid) ? iid.GetString() : string.Empty)}/Images/Primary"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Federated search failed for {Peer}", server.Name);
        }
        return results;
    }
}

public class CreateShareRequest
{
    public string? Label { get; set; }
    public List<string>? LibraryIds { get; set; }
    public string? AllowedHoursStart { get; set; }
    public string? AllowedHoursEnd { get; set; }
    public string? ScheduleTimeZoneId { get; set; }
    public List<string>? BlockedTags { get; set; }
    public string? MaxOfficialRating { get; set; }
    public bool StrictUnknownRating { get; set; }
}

public class CreatePublicShareRequest
{
    public string? ItemId { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public int? MaxUses { get; set; }
}

public class IncomingRequestPayload
{
    public string? FromBaseUrl { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? Note { get; set; }
}

public class SendRequestPayload
{
    public string? PeerId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? Note { get; set; }
}

public class FederatedSearchHit
{
    public Guid PeerId { get; set; }
    public string? PeerName { get; set; }
    public string? ItemId { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public int? Year { get; set; }
    public string? PrimaryImageUrl { get; set; }
}
