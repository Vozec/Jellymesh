using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Patches Jellyfin's web/index.html on plugin load to include a script tag pointing at
/// /Federation/Asset/jellymesh-item.js?v={contentHash}. The marker block is REWRITTEN on
/// every startup so a plugin upgrade busts the browser cache for the JS asset.
/// </summary>
public class IndexHtmlInjector : IHostedService
{
    private const string MarkerStart = "<!-- jellymesh-inject-begin -->";
    private const string MarkerEnd = "<!-- jellymesh-inject-end -->";
    private static readonly Regex MarkerBlock = new(
        Regex.Escape(MarkerStart) + ".*?" + Regex.Escape(MarkerEnd) + @"\s*",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
            InjectOrUpdate(indexPath);
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

    private string ComputeAssetVersion()
    {
        // Hash the embedded jellymesh-item.js so the script tag query string flips whenever
        // we ship a new build, busting the 10-minute browser cache without bumping the plugin
        // version manually.
        try
        {
            var asm = typeof(Plugin).Assembly;
            using var s = asm.GetManifestResourceStream(typeof(Plugin).Namespace + ".Assets.jellymesh-item.js");
            if (s is null) return Plugin.Instance?.Version?.ToString() ?? "0";
            var hash = SHA256.HashData(ReadAll(s));
            return Convert.ToHexString(hash, 0, 6);
        }
        catch { return Plugin.Instance?.Version?.ToString() ?? "0"; }
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private void InjectOrUpdate(string indexPath)
    {
        var html = File.ReadAllText(indexPath);
        var version = ComputeAssetVersion();
        var snippet = MarkerStart + "\n<script defer src=\"/Federation/Asset/jellymesh-item.js?v=" + version + "\"></script>\n" + MarkerEnd + "\n";

        string updated;
        if (MarkerBlock.IsMatch(html))
        {
            updated = MarkerBlock.Replace(html, snippet);
            if (updated == html) return; // identical -> no I/O, no log spam
        }
        else
        {
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            updated = bodyClose >= 0 ? html.Insert(bodyClose, snippet) : html + "\n" + snippet;
        }
        File.WriteAllText(indexPath, updated);
        _logger.LogInformation("Jellymesh: item-page script tag updated in {Path} (asset v{Version})", indexPath, version);
    }
}
