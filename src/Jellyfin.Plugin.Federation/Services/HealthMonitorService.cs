using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

public class HealthMonitorService : BackgroundService
{
    private readonly RemoteJellyfinClient _client;
    private readonly PeerHealthRegistry _registry;
    private readonly PeerHealthHistoryStore _history;
    private readonly WebhookDispatcher _webhook;
    private readonly PeerLibraryCache _libCache;
    private readonly ILogger<HealthMonitorService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public HealthMonitorService(RemoteJellyfinClient client, PeerHealthRegistry registry, PeerHealthHistoryStore history, WebhookDispatcher webhook, PeerLibraryCache libCache, ILogger<HealthMonitorService> logger)
    {
        _client = client;
        _registry = registry;
        _history = history;
        _webhook = webhook;
        _libCache = libCache;
        _logger = logger;
        _registry.HealthChanged += OnHealthChanged;
    }

    private void OnHealthChanged(object? sender, PeerHealthChangedEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        var name = config?.RemoteServers.FirstOrDefault(s => s.Id == e.PeerId)?.Name ?? e.PeerId.ToString();
        _webhook.Fire(e.Online ? "peer-online" : "peer-offline",
            $"Peer {name} is now {(e.Online ? "online" : "offline")}",
            new { e.PeerId, e.Online });
        _libCache?.InvalidatePeer(e.PeerId);
    }

    // Throttle disk persistence: probe in-memory every 30s for online detection, but only
    // write a row to peer_health_samples every PersistEvery or on state change. Cuts the
    // health-table write rate from ~2/min/peer to ~12/h/peer in steady state, which keeps
    // the WAL file small (was growing past 1 MB during a single test session) and the
    // physical disk quiet.
    private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTime LastWrite, bool LastOnline)> _lastPersist = new();

    // Peers that returned a permanent refusal (401 bad share key / 403 auto-provision disabled)
    // to ProvisionMediaKey. We stop re-asking them every cycle so a refusing peer isn't hit
    // 2,880×/day forever; cleared only on plugin restart.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _provisionGaveUp = new();

    // Adaptive probe cadence: online peers are pinged every cycle (30s) for fast down-detection;
    // a peer found offline is backed off to one probe every 5 min so a long-down peer isn't hit
    // 2/min forever. A gated peer with no entry here is "due now". Coming back online removes the
    // gate, so reconnection is picked up within at most one DownProbeInterval.
    private static readonly TimeSpan DownProbeInterval = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _nextProbe = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Federation health monitor started, interval {Interval}s", CheckInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
          try
          {
            var config = Plugin.Instance?.Configuration;
            if (config is not null)
            {
                // Snapshot the list before fanning out: the admin UI can mutate the live
                // RemoteServers collection, and lazily enumerating it inside Task.WhenAll would
                // throw "collection was modified".
                var servers = config.RemoteServers.Where(s => s.Enabled).ToList();
                // Only probe peers that are due this cycle: online peers (no gate) every tick,
                // offline peers at most once per DownProbeInterval.
                var nowTick = DateTime.UtcNow;
                var due = servers.Where(s => !_nextProbe.TryGetValue(s.Id, out var t) || nowTick >= t).ToList();
                var checks = due.Select(async server =>
                {
                    var sw = Stopwatch.StartNew();
                    // Bound a single hung peer so it can't stall the whole round (BuildClient's
                    // own timeout is 30s, tuned for sync pulls, not 30s-per-cycle liveness pings).
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(8));
                    var online = await _client.PingAsync(server, pingCts.Token).ConfigureAwait(false);
                    sw.Stop();
                    _registry.Update(server.Id, online, sw.Elapsed);

                    // Back a down peer off to the slow cadence; clear the gate once it answers.
                    if (online) _nextProbe.TryRemove(server.Id, out _);
                    else _nextProbe[server.Id] = DateTime.UtcNow + DownProbeInterval;

                    var now = DateTime.UtcNow;
                    var should = !_lastPersist.TryGetValue(server.Id, out var last)
                        || last.LastOnline != online
                        || (now - last.LastWrite) >= PersistEvery;
                    if (should)
                    {
                        _history.Append(server.Id, online, online ? (int)sw.ElapsedMilliseconds : null);
                        _lastPersist[server.Id] = (now, online);
                    }
                });

                try
                {
                    await Task.WhenAll(checks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check round failed");
                }

                // Auto-provision the media API key for any peer we hold a share key for but no
                // media key yet, so the admin only ever exchanges ONE secret. Iterate the snapshot
                // (not the live list) and skip peers that already refused permanently.
                foreach (var server in servers.Where(s =>
                    !string.IsNullOrEmpty(s.FederationShareKey) && string.IsNullOrEmpty(s.ApiKey)
                    && !_provisionGaveUp.ContainsKey(s.Id) && _registry.IsOnline(s.Id)))
                {
                    try
                    {
                        var (mediaKey, status) = await _client.ProvisionMediaKeyAsync(server, stoppingToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(mediaKey))
                        {
                            lock (Plugin.ConfigWriteLock)
                            {
                                var live = Plugin.Instance?.Configuration?.RemoteServers.FirstOrDefault(s => s.Id == server.Id);
                                if (live is not null && string.IsNullOrEmpty(live.ApiKey))
                                {
                                    live.ApiKey = mediaKey;
                                    Plugin.Instance?.SaveConfiguration();
                                    _logger.LogInformation("Auto-provisioned media key from peer {Peer}", server.Name);
                                }
                            }
                        }
                        else if (status is 401 or 403)
                        {
                            // Permanent refusal: bad share key, or the peer disabled auto-provision.
                            // Stop asking so we don't hammer it every 30s indefinitely.
                            _provisionGaveUp.TryAdd(server.Id, 0);
                            _logger.LogInformation("Media key auto-provision refused by {Peer} (HTTP {Status}); will not retry until restart", server.Name, status);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Media key auto-provision for {Peer} failed", server.Name);
                    }
                }
            }
          }
          catch (Exception ex)
          {
            // Last-resort guard: an unhandled throw out of ExecuteAsync stops the whole host
            // (BackgroundServiceExceptionBehavior.StopHost). Never let one bad cycle kill Jellyfin.
            _logger.LogError(ex, "Health monitor cycle failed; continuing");
          }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }
}
