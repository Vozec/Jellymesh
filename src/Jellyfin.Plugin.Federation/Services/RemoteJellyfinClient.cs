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

    public async IAsyncEnumerable<RemoteItem> FetchItemsAsync(
        RemoteServer server,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var http = BuildClient(server);
        var fields = "ProviderIds,MediaSources,MediaStreams,Path,Width,Height,Container,Bitrate,RunTimeTicks";
        var url = $"/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields={fields}&Limit=10000";

        if (!string.IsNullOrEmpty(server.RemoteUserId))
            url = $"/Users/{server.RemoteUserId}/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields={fields}&Limit=10000";

        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("Items", out var items))
            yield break;

        foreach (var el in items.EnumerateArray())
        {
            yield return MapItem(server.Id, el);
        }
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

    private HttpClient BuildClient(RemoteServer server)
    {
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(server.BaseUrl.TrimEnd('/'));
        http.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }
}
