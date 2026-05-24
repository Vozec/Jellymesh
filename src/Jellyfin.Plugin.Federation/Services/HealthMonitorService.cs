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
    private readonly ILogger<HealthMonitorService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public HealthMonitorService(RemoteJellyfinClient client, PeerHealthRegistry registry, ILogger<HealthMonitorService> logger)
    {
        _client = client;
        _registry = registry;
        _logger = logger;
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
