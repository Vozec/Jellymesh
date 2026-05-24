using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Live self-test against configured peers. Runs the same probes the background services
/// would run (ping, digest fetch, share-key probe) and returns a per-peer report - saves
/// admin from grepping logs when a 2-peer setup mis-behaves.
/// </summary>
public class DiagnosticsService
{
    private readonly RemoteJellyfinClient _client;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PeerHealthRegistry _health;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(
        RemoteJellyfinClient client,
        IHttpClientFactory httpClientFactory,
        PeerHealthRegistry health,
        ILogger<DiagnosticsService> logger)
    {
        _client = client;
        _httpClientFactory = httpClientFactory;
        _health = health;
        _logger = logger;
    }

    public async Task<DiagnosticsReport> RunAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return new DiagnosticsReport { GeneratedUtc = DateTime.UtcNow };

        var peers = config.RemoteServers.Where(s => s.Enabled).ToArray();

        // Per-peer probes in parallel - one slow peer doesn't stall the whole diagnostic.
        var probeTasks = peers.Select(p => ProbePeerAsync(p, config, ct)).ToArray();
        var probes = await Task.WhenAll(probeTasks).ConfigureAwait(false);

        return new DiagnosticsReport
        {
            GeneratedUtc = DateTime.UtcNow,
            OurPublicBaseUrl = config.PublicBaseUrl,
            PushInvalidationConfigured = !string.IsNullOrWhiteSpace(config.PublicBaseUrl),
            EnabledPeerCount = peers.Length,
            DisabledPeerCount = config.RemoteServers.Count - peers.Length,
            Peers = probes
        };
    }

    private async Task<PeerProbe> ProbePeerAsync(Configuration.RemoteServer peer, Configuration.PluginConfiguration config, CancellationToken ct)
    {
        var probe = new PeerProbe
        {
            Id = peer.Id,
            Name = peer.Name,
            BaseUrl = peer.BaseUrl,
            CachedOnline = _health.Get(peer.Id).Online,
            CachedRttMs = _health.Get(peer.Id).LastRttMs,
            Checks = new List<DiagnosticCheck>()
        };

        // 1. URL parseability - catches the bare-host bug class early.
        probe.Checks.Add(Check("BaseUrl canonicalizes", () =>
        {
            var canon = PeerUrl.Canonicalize(peer.BaseUrl);
            if (canon is null) throw new Exception("BaseUrl missing http:// or https:// scheme");
            return canon;
        }));

        // 2. Has-key sanity.
        probe.Checks.Add(Check("Jellyfin API key set", () =>
            string.IsNullOrEmpty(peer.ApiKey) ? throw new Exception("ApiKey empty - stream proxy will 401") : "yes"));

        probe.Checks.Add(Check("Federation share key set", () =>
            string.IsNullOrEmpty(peer.FederationShareKey) ? "no (peer pull will fall through to legacy /Items)" : "yes"));

        // 3. Live ping.
        var pingWatch = Stopwatch.StartNew();
        var pinged = await _client.PingAsync(peer, ct).ConfigureAwait(false);
        pingWatch.Stop();
        probe.Checks.Add(new DiagnosticCheck
        {
            Name = "Live ping /System/Info/Public",
            Ok = pinged,
            Detail = pinged ? $"{pingWatch.ElapsedMilliseconds} ms" : "no response"
        });

        if (!pinged) return probe; // skip further probes if unreachable

        // 4. Digest fetch (works only if peer runs the plugin).
        var digestWatch = Stopwatch.StartNew();
        var digest = await _client.FetchDigestAsync(peer, ct).ConfigureAwait(false);
        digestWatch.Stop();
        probe.Checks.Add(new DiagnosticCheck
        {
            Name = "Digest endpoint /Federation/Catalog/Digest",
            Ok = digest is not null,
            Detail = digest is { } d ? $"{d.Count} items, hash {d.Hash[..8]}…, {digestWatch.ElapsedMilliseconds} ms" : "no digest (peer plugin missing or share key wrong)"
        });

        // 5. Stream-proxy reachability - HEAD on the upstream /Videos endpoint with auth.
        var streamUrl = $"{peer.BaseUrl.TrimEnd('/')}/System/Info";
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            probe.Checks.Add(new DiagnosticCheck
            {
                Name = "Authenticated GET /System/Info",
                Ok = resp.IsSuccessStatusCode,
                Detail = $"HTTP {(int)resp.StatusCode}"
            });
        }
        catch (Exception ex)
        {
            probe.Checks.Add(new DiagnosticCheck { Name = "Authenticated GET /System/Info", Ok = false, Detail = ex.Message });
        }

        // 6. If peer is configured with LocalUserIdForSync, check pull-direction sync wiring.
        if (!string.IsNullOrEmpty(peer.LocalUserIdForSync))
        {
            probe.Checks.Add(Check("LocalUserIdForSync parses as Guid", () =>
                Guid.TryParse(peer.LocalUserIdForSync, out _) ? "yes" : throw new Exception("not a UUID - pull watch sync will warn at runtime")));

            probe.Checks.Add(string.IsNullOrEmpty(peer.RemoteUserId)
                ? new DiagnosticCheck { Name = "RemoteUserId set (required for pull sync)", Ok = false, Detail = "missing - pull watch sync will silently no-op" }
                : new DiagnosticCheck { Name = "RemoteUserId set", Ok = true, Detail = "yes" });
        }

        return probe;
    }

    private static DiagnosticCheck Check(string name, Func<string> body)
    {
        try { return new DiagnosticCheck { Name = name, Ok = true, Detail = body() }; }
        catch (Exception ex) { return new DiagnosticCheck { Name = name, Ok = false, Detail = ex.Message }; }
    }
}

public class DiagnosticsReport
{
    public DateTime GeneratedUtc { get; set; }
    public string? OurPublicBaseUrl { get; set; }
    public bool PushInvalidationConfigured { get; set; }
    public int EnabledPeerCount { get; set; }
    public int DisabledPeerCount { get; set; }
    public PeerProbe[] Peers { get; set; } = Array.Empty<PeerProbe>();
}

public class PeerProbe
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool CachedOnline { get; set; }
    public int CachedRttMs { get; set; }
    public List<DiagnosticCheck> Checks { get; set; } = new();
}

public class DiagnosticCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Detail { get; set; } = string.Empty;
}
