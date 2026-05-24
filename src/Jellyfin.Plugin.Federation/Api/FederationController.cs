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

        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
            Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in resp.Content.Headers)
            Response.Headers[h.Key] = h.Value.ToArray();
        Response.Headers.Remove("transfer-encoding");

        var auditId = _store.BeginAudit(server.Id, itemId, User?.Identity?.Name);
        var bytesServed = 0L;
        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
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
            _store.CompleteAudit(auditId, bytesServed);
        }
        return new EmptyResult();
    }

    [HttpGet("Audit/Recent")]
    public IActionResult RecentAudit([FromQuery] int limit = 100)
        => Ok(_store.RecentAudits(Math.Clamp(limit, 1, 1000)));

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
