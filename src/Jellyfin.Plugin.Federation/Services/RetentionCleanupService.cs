using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Drops old audit rows and health samples nightly per config.RetentionDays.</summary>
public class RetentionCleanupService : BackgroundService
{
    private readonly InboundAuditStore _audit;
    private readonly PeerHealthHistoryStore _health;
    private readonly ILogger<RetentionCleanupService> _logger;
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(6);

    public RetentionCleanupService(InboundAuditStore audit, PeerHealthHistoryStore health, ILogger<RetentionCleanupService> logger)
    {
        _audit = audit;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = Plugin.Instance?.Configuration?.RetentionDays ?? 30;
                if (days > 0)
                {
                    var window = TimeSpan.FromDays(days);
                    var a = _audit.Purge(window);
                    var h = _health.Purge(window);
                    if (a > 0 || h > 0)
                        _logger.LogInformation("Retention cleanup: pruned {Audit} audit rows + {Health} health samples (older than {Days}d)", a, h, days);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retention cleanup round failed");
            }
            try { await Task.Delay(Cadence, stoppingToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }
}
