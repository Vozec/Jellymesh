using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Listens for local library mutations (Movies/Series/Episodes added or removed) and,
/// after a debounce window, POSTs an invalidation to each peer that issued us a
/// FederationShareKey. Peers respond by dropping their cached digest for us, causing the
/// next sync round to actually re-pull instead of skipping via digest-match.
///
/// Bridges the latency gap between local change and peer notice without spamming peers
/// with one HTTP call per item. Failed peers are retried with exponential backoff
/// (30s / 60s / 120s / 240s / 480s) up to MaxAttempts; a fresh local mutation resets
/// retry counters so new data always tries immediately on the next debounce tick.
/// </summary>
public class PushInvalidationService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PeerHealthRegistry _health;
    private readonly ILogger<PushInvalidationService> _logger;

    private long _dirtyTicks; // set to DateTime.UtcNow.Ticks when an event fires; 0 = clean.
    private readonly ConcurrentDictionary<Guid, RetryState> _retries = new();

    public PushInvalidationService(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        PeerHealthRegistry health,
        ILogger<PushInvalidationService> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _libraryManager.ItemAdded += OnItemMutated;
        _libraryManager.ItemRemoved += OnItemMutated;
        _logger.LogInformation("Federation push-invalidation hook armed.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }

                var config = Plugin.Instance?.Configuration;
                if (config is null || string.IsNullOrWhiteSpace(config.PublicBaseUrl))
                {
                    continue; // not configured for push — gossip-pull still runs
                }

                // Two work paths per tick:
                //   1. Fresh dirty flag past debounce → fire to ALL peers, build retry queue from failures
                //   2. Any peers in retry queue whose NextAttempt is in the past → retry just those
                await TryFireFreshAsync(config, stoppingToken).ConfigureAwait(false);
                await DrainRetryQueueAsync(config, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _libraryManager.ItemAdded -= OnItemMutated;
            _libraryManager.ItemRemoved -= OnItemMutated;
        }
    }

    private async Task TryFireFreshAsync(Configuration.PluginConfiguration config, CancellationToken ct)
    {
        var lastDirty = Interlocked.Read(ref _dirtyTicks);
        if (lastDirty == 0) return;

        var debounce = TimeSpan.FromSeconds(Math.Max(5, config.PushDebounceSeconds));
        var elapsed = DateTime.UtcNow - new DateTime(lastDirty, DateTimeKind.Utc);
        if (elapsed < debounce) return;

        // Atomic clear. If a fresh event landed between read and CAS, the CAS no-ops and we
        // skip this tick — the next tick handles the new mark with its own debounce.
        if (Interlocked.CompareExchange(ref _dirtyTicks, 0, lastDirty) != lastDirty) return;

        // Fresh data → reset retry counters for everyone. A peer that was in backoff gets a
        // fresh attempt; if THAT also fails, the new failure restarts the backoff sequence.
        _retries.Clear();

        var peers = SelectPeersFor(config);
        await FireToPeersAsync(peers, config.PublicBaseUrl, ct).ConfigureAwait(false);
    }

    private async Task DrainRetryQueueAsync(Configuration.PluginConfiguration config, CancellationToken ct)
    {
        if (_retries.IsEmpty) return;

        var now = DateTime.UtcNow;
        var due = _retries
            .Where(kv => kv.Value.NextAttemptUtc <= now)
            .Select(kv => kv.Key)
            .ToArray();
        if (due.Length == 0) return;

        var peerLookup = config.RemoteServers.ToDictionary(s => s.Id);
        var duePeers = due
            .Where(id => peerLookup.TryGetValue(id, out var s) && s.Enabled && !string.IsNullOrEmpty(s.FederationShareKey))
            .Select(id => peerLookup[id])
            .ToArray();

        await FireToPeersAsync(duePeers, config.PublicBaseUrl, ct).ConfigureAwait(false);
    }

    private Configuration.RemoteServer[] SelectPeersFor(Configuration.PluginConfiguration config)
        => config.RemoteServers
            .Where(s => s.Enabled && !string.IsNullOrEmpty(s.FederationShareKey))
            .ToArray();

    private async Task FireToPeersAsync(Configuration.RemoteServer[] peers, string ourBaseUrl, CancellationToken ct)
    {
        if (peers.Length == 0) return;

        var payload = new InvalidatePayload { FromBaseUrl = ourBaseUrl.TrimEnd('/') };
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        foreach (var peer in peers)
        {
            // Don't waste retry attempts on a peer we already know is offline — wait until
            // the health monitor flips it back, then a new event will reset and we'll retry.
            if (!_health.IsOnline(peer.Id))
            {
                _logger.LogDebug("Push to {Peer} deferred — health=offline", peer.Name);
                continue;
            }

            var ok = await TrySendAsync(http, peer, payload, ct).ConfigureAwait(false);
            if (ok)
            {
                _retries.TryRemove(peer.Id, out _);
            }
            else
            {
                ScheduleRetry(peer);
            }
        }
    }

    private async Task<bool> TrySendAsync(HttpClient http, Configuration.RemoteServer peer, InvalidatePayload payload, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{peer.BaseUrl.TrimEnd('/')}/Federation/Invalidate")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Add("X-Federation-Share", peer.FederationShareKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            _logger.LogDebug("Push to {Peer}: {Status}", peer.Name, (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Push to {Peer} failed", peer.Name);
            return false;
        }
    }

    private void ScheduleRetry(Configuration.RemoteServer peer)
    {
        var prev = _retries.TryGetValue(peer.Id, out var existing) ? existing.AttemptCount : 0;
        var delay = RetrySchedule.NextDelay(prev + 1);
        if (delay is null)
        {
            _logger.LogWarning("Push to {Peer} gave up after {Max} attempts; gossip-pull will catch up on next sync", peer.Name, RetrySchedule.MaxAttempts);
            _retries.TryRemove(peer.Id, out _);
            return;
        }
        _retries[peer.Id] = new RetryState
        {
            AttemptCount = prev + 1,
            NextAttemptUtc = DateTime.UtcNow.Add(delay.Value)
        };
    }

    private void OnItemMutated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is null) return;
        var kind = e.Item.GetType().Name;
        if (kind != "Movie" && kind != "Series" && kind != "Episode") return;
        Interlocked.Exchange(ref _dirtyTicks, DateTime.UtcNow.Ticks);
    }

    private class RetryState
    {
        public int AttemptCount { get; set; }
        public DateTime NextAttemptUtc { get; set; }
    }
}

public class InvalidatePayload
{
    public string FromBaseUrl { get; set; } = string.Empty;
}
