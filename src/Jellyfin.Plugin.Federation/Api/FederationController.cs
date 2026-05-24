using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        var upstream = $"{server.BaseUrl.TrimEnd('/')}/Videos/{itemId}/stream?static=true";
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

        var sender = payload.FromBaseUrl.TrimEnd('/');
        var match = config.RemoteServers.FirstOrDefault(s =>
            string.Equals(s.BaseUrl?.TrimEnd('/'), sender, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _logger.LogDebug("Invalidate from {Url} ignored — no matching RemoteServer", sender);
            return NoContent();
        }

        _store.InvalidateDigest(match.Id);
        _logger.LogInformation("Invalidated cached digest for {Peer} per push notification", match.Name);
        return NoContent();
    }

    // === Anonymous, expiring, use-capped video share links ===
    // Admin generates one per video; hands the URL to anyone. No Jellyfin auth required to view.

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

    [HttpDelete("PublicShares/{token}")]
    public IActionResult RevokePublicShare(string token, [FromServices] Services.PublicShareStore store)
    {
        store.Revoke(token);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("Public/{token}")]
    public IActionResult PublicViewer(string token, [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        var info = store.GetInfo(token);
        if (info is null) return NotFound();
        if (info.ExpiresUtc.HasValue && info.ExpiresUtc < DateTime.UtcNow) return StatusCode(410, "Link expired");
        if (info.MaxUses.HasValue && info.UsedCount >= info.MaxUses.Value) return StatusCode(410, "Link exhausted");

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
    public async Task<IActionResult> PublicStream(string token,
        [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library,
        CancellationToken ct)
    {
        var itemId = store.TryConsume(token);
        if (itemId is null) return StatusCode(410, "Link invalid, expired, or exhausted");

        if (!Guid.TryParse(itemId, out var g)) return NotFound();
        var item = library.GetItemById(g);
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path)) return NotFound();

        // Stream the raw file with Range support so the browser <video> tag can seek.
        // Direct-play codecs only — no transcoding from this endpoint.
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
        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        var url = $"{server.BaseUrl.TrimEnd('/')}/Items/{itemId}/Images/{Uri.EscapeDataString(imageType)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Emby-Token", server.ApiKey);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return NotFound();

        Response.StatusCode = (int)resp.StatusCode;
        CopySafeHeaders(resp.Content.Headers, Response.Headers);
        await resp.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    [HttpGet("Audit/Recent")]
    public IActionResult RecentAudit([FromQuery] int limit = 100)
        => Ok(_store.RecentAudits(Math.Clamp(limit, 1, 1000)));

    [HttpGet("Catalog/Digest")]
    public IActionResult CatalogDigest([FromServices] Services.LocalCatalogDigest digest)
        => Ok(digest.Compute());

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
        return Ok(digest.Compute(key.LibraryIds));
    }

    [AllowAnonymous]
    [HttpGet("Share/Catalog/Items")]
    public IActionResult ShareCatalogItems([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromServices] Services.LocalCatalogDigest digest)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        return Ok(digest.List(key.LibraryIds));
    }

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

    [HttpPost("Shares")]
    public IActionResult CreateShare([FromBody] CreateShareRequest req)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var key = new Configuration.ShareKey
        {
            Label = req.Label ?? "Unnamed share",
            LibraryIds = req.LibraryIds ?? new List<string>(),
            ApiKey = GenerateApiKey(),
            Enabled = true
        };
        config.Shares.Add(key);
        Plugin.Instance!.SaveConfiguration();
        return Ok(new { key.Id, key.Label, key.ApiKey, key.LibraryIds });
    }

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

    [HttpPost("Sync/Trigger")]
    public IActionResult TriggerSync([FromServices] MediaBrowser.Model.Tasks.ITaskManager taskManager)
    {
        taskManager.Execute<Services.FederationSyncTask>();
        return Ok(new { triggered = true });
    }

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
}

public class CreatePublicShareRequest
{
    public string? ItemId { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public int? MaxUses { get; set; }
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
