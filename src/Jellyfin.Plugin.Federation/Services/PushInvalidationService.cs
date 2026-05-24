using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        // Only structural mutations (ItemAdded / ItemRemoved) change the catalog membership
        // hash. ItemUpdated fires on every metadata refresh / image scan / NFO touch - DO NOT
        // re-subscribe to it or peers will be flooded with invalidations on routine background
        // work, defeating the gossip-digest anti-spam guarantee. (Restored design comment.)
        _libraryManager.ItemAdded += OnItemMutated;
        _libraryManager.ItemRemoved += OnItemMutated;
        _health.HealthChanged += OnHealthChanged;
        _logger.LogInformation("Federation push-invalidation hook armed.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; } // catches TaskCanceledException + base

                var config = Plugin.Instance?.Configuration;
                if (config is null || string.IsNullOrWhiteSpace(config.PublicBaseUrl))
                {
                    continue; // not configured for push - gossip-pull still runs
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
            _health.HealthChanged -= OnHealthChanged;
        }
    }

    private void OnHealthChanged(object? sender, PeerHealthChangedEventArgs e)
    {
        // When a peer flips back online, accelerate its retry: set NextAttemptUtc to now so
        // the next tick (within 5s) re-fires instead of waiting for the next backoff window
        // (which could be 8 minutes deep if the peer had been failing for a while).
        try
        {
            if (!e.Online) return;
            if (_retries.TryGetValue(e.PeerId, out var state))
            {
                _retries[e.PeerId] = new RetryState
                {
                    AttemptCount = state.AttemptCount,
                    NextAttemptUtc = DateTime.UtcNow
                };
                _logger.LogDebug("Peer {Id} back online, retry NextAttempt advanced to now", e.PeerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnHealthChanged swallowed exception");
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
        // skip this tick - the next tick handles the new mark with its own debounce.
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

        // Parallelize per peer - one timing-out peer (10s) shouldn't block all others. The
        // tick loop only runs every 5s; N serial peers at full timeout = ~N*10s of stalled
        // pushes. Task.WhenAll bounds total wait to the slowest peer.
        var tasks = new List<Task>(peers.Length);
        foreach (var peer in peers)
        {
            // Re-enqueue offline peers as a retry (short backoff) so the push pipeline keeps
            // trying once health flips back, instead of silently dropping the invalidation
            // until the NEXT local mutation. Without this, a peer that goes offline during
            // the debounce window misses the push entirely; gossip-pull is the only fallback.
            if (!_health.IsOnline(peer.Id))
            {
                _logger.LogDebug("Push to {Peer} deferred - health=offline, queued for retry", peer.Name);
                ScheduleRetry(peer);
                continue;
            }

            var p = peer;
            tasks.Add(Task.Run(async () =>
            {
                var ok = await TrySendAsync(http, p, payload, ct).ConfigureAwait(false);
                if (ok) _retries.TryRemove(p.Id, out _);
                else ScheduleRetry(p);
            }, ct));
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown - retry entries die with the dict */ }
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
            if (!string.IsNullOrEmpty(peer.BasicAuthUser) || !string.IsNullOrEmpty(peer.BasicAuthPass))
            {
                var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{peer.BasicAuthUser}:{peer.BasicAuthPass}"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }
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

    /// <summary>Atomic read-modify-write of the per-peer retry counter. Now safe under parallel
    /// callers (FireToPeersAsync uses Task.WhenAll). Returns the new attempt count or 0 if
    /// the entry was removed due to give-up.</summary>
    private void ScheduleRetry(Configuration.RemoteServer peer)
    {
        var hitMax = false;
        _retries.AddOrUpdate(
            peer.Id,
            _ =>
            {
                var d = RetrySchedule.NextDelay(1)!.Value;
                return new RetryState { AttemptCount = 1, NextAttemptUtc = DateTime.UtcNow.Add(d) };
            },
            (_, prev) =>
            {
                var nextAttempt = prev.AttemptCount + 1;
                var d = RetrySchedule.NextDelay(nextAttempt);
                if (d is null) { hitMax = true; return prev; } // marker - we'll remove after
                return new RetryState { AttemptCount = nextAttempt, NextAttemptUtc = DateTime.UtcNow.Add(d.Value) };
            });
        if (hitMax)
        {
            _logger.LogWarning("Push to {Peer} gave up after {Max} attempts; gossip-pull will catch up on next sync", peer.Name, RetrySchedule.MaxAttempts);
            _retries.TryRemove(peer.Id, out _);
        }
    }

    private void OnItemMutated(object? sender, ItemChangeEventArgs e)
    {
        // Defensive try/catch - this handler runs on Jellyfin's event dispatcher thread.
        // An uncaught exception here would propagate up and break OTHER subscribers
        // downstream of us.
        try
        {
            if (e.Item is null) return;
            var kind = e.Item.GetType().Name;
            if (kind != "Movie" && kind != "Series" && kind != "Episode") return;
            Interlocked.Exchange(ref _dirtyTicks, DateTime.UtcNow.Ticks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnItemMutated swallowed exception");
        }
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
