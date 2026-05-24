using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Emits a one-line summary of the federation's configuration at Jellyfin startup. Saves
/// admins from running /Federation/Diagnostics or grepping the config XML to answer
/// "is the plugin alive and what does it know about?".
/// </summary>
public class StartupReportService : IHostedService
{
    private readonly ILogger<StartupReportService> _logger;

    public StartupReportService(ILogger<StartupReportService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogInformation("Federation plugin loaded — configuration not yet available.");
            return Task.CompletedTask;
        }

        var enabledPeers = config.RemoteServers.Count(s => s.Enabled);
        var disabledPeers = config.RemoteServers.Count - enabledPeers;
        var keysIssued = config.Shares.Count;
        var keysBound = config.Shares.Count(s => !string.IsNullOrEmpty(s.BoundPeerUrl));
        var pushEnabled = !string.IsNullOrWhiteSpace(config.PublicBaseUrl);
        var dedupEnabled = config.EnableDedup;
        var watchSync = config.EnableWatchStateSync;

        _logger.LogInformation(
            "Federation {Version} loaded — peers: {Enabled} enabled, {Disabled} disabled · shares: {Issued} ({Bound} bound) · push: {Push} · dedup: {Dedup} · watch-sync: {WatchSync}",
            Plugin.Instance!.Version, enabledPeers, disabledPeers, keysIssued, keysBound,
            pushEnabled ? "on" : "off", dedupEnabled ? "on" : "off", watchSync ? "on" : "off");

        // Surface common misconfigurations as a single WARN line at boot — admin sees one
        // log line in the dashboard instead of debugging silent no-ops later.
        if (enabledPeers > 0 && !pushEnabled)
            _logger.LogWarning("Federation: peers configured but PublicBaseUrl is empty — push invalidation disabled, gossip-pull only");
        if (config.RemoteServers.Any(s => s.Enabled && !string.IsNullOrEmpty(s.LocalUserIdForSync) && string.IsNullOrEmpty(s.RemoteUserId)))
            _logger.LogWarning("Federation: at least one peer has LocalUserIdForSync set without RemoteUserId — pull-direction watch sync will no-op for those peers");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
