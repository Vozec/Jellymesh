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
    private readonly ILogger<HealthMonitorService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public HealthMonitorService(RemoteJellyfinClient client, PeerHealthRegistry registry, PeerHealthHistoryStore history, WebhookDispatcher webhook, ILogger<HealthMonitorService> logger)
    {
        _client = client;
        _registry = registry;
        _history = history;
        _webhook = webhook;
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
    }

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
                    _history.Append(server.Id, online, online ? (int)sw.ElapsedMilliseconds : null);
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
