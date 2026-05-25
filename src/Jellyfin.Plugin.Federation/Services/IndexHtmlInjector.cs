using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Patches Jellyfin's web/index.html on plugin load to include a script tag pointing at
/// /Federation/Asset/jellymesh-item.js. This is how the Share button gets added to the item
/// page without requiring admins to install a browser userscript. The injection is
/// idempotent (skipped if the marker comment is already there) and reversible (admin can
/// remove the block by hand).
/// </summary>
public class IndexHtmlInjector : IHostedService
{
    private const string MarkerStart = "<!-- jellymesh-inject-begin -->";
    private const string MarkerEnd = "<!-- jellymesh-inject-end -->";
    private readonly IApplicationPaths _paths;
    private readonly ILogger<IndexHtmlInjector> _logger;

    public IndexHtmlInjector(IApplicationPaths paths, ILogger<IndexHtmlInjector> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = LocateIndexHtml();
            if (indexPath is null)
            {
                _logger.LogDebug("Jellymesh: web/index.html not found, skipping script injection");
                return Task.CompletedTask;
            }
            InjectIfMissing(indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellymesh: failed to inject item-page script into web/index.html (skipping)");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string? LocateIndexHtml()
    {
        // Standard Docker install: /jellyfin/jellyfin-web/index.html
        // Distro install: /usr/share/jellyfin-web/index.html
        // ProgramDataPath is the config dir; WebPath is what we want.
        var candidates = new[]
        {
            Path.Combine(_paths.WebPath ?? string.Empty, "index.html"),
            "/jellyfin/jellyfin-web/index.html",
            "/usr/share/jellyfin-web/index.html"
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        }
        return null;
    }

    private void InjectIfMissing(string indexPath)
    {
        var html = File.ReadAllText(indexPath);
        if (html.Contains(MarkerStart, StringComparison.Ordinal))
        {
            _logger.LogDebug("Jellymesh: item-page script tag already present in {Path}", indexPath);
            return;
        }
        var snippet = MarkerStart + "\n<script defer src=\"/Federation/Asset/jellymesh-item.js\"></script>\n" + MarkerEnd + "\n";
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        string updated;
        if (bodyClose >= 0)
        {
            updated = html.Insert(bodyClose, snippet);
        }
        else
        {
            updated = html + "\n" + snippet;
        }
        File.WriteAllText(indexPath, updated);
        _logger.LogInformation("Jellymesh: injected item-page script tag into {Path}", indexPath);
    }
}
