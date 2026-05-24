using System;
using System.Linq;
using System.Net.Http;
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
    private readonly ILogger<FederationController> _logger;

    public FederationController(IHttpClientFactory httpClientFactory, ILogger<FederationController> logger)
    {
        _httpClientFactory = httpClientFactory;
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

        await resp.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    [HttpGet("Peers/Status")]
    public IActionResult PeersStatus()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return Ok(Array.Empty<object>());
        return Ok(config.RemoteServers.Select(s => new { s.Id, s.Name, s.BaseUrl, s.Enabled }));
    }
}
