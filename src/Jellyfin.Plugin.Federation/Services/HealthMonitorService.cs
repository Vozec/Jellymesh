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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Federation health monitor started, interval {Interval}s", CheckInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is not null)
            {
                var checks = config.RemoteServers.Where(s => s.Enabled).Select(async server =>
                {
                    var sw = Stopwatch.StartNew();
                    var online = await _client.PingAsync(server, stoppingToken).ConfigureAwait(false);
                    sw.Stop();
                    _registry.Update(server.Id, online, sw.Elapsed);

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
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }
}
