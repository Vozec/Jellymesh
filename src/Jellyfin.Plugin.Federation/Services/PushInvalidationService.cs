using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
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
/// with one HTTP call per item.
/// </summary>
public class PushInvalidationService : BackgroundService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PushInvalidationService> _logger;

    private long _dirtyTicks; // set to DateTime.UtcNow.Ticks when an event fires; 0 = clean.

    public PushInvalidationService(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<PushInvalidationService> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _libraryManager.ItemAdded += OnItemMutated;
        _libraryManager.ItemRemoved += OnItemMutated;
        _libraryManager.ItemUpdated += OnItemMutated;
        _logger.LogInformation("Federation push-invalidation hook armed.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

                var lastDirty = Interlocked.Read(ref _dirtyTicks);
                if (lastDirty == 0) continue;

                var config = Plugin.Instance?.Configuration;
                var debounce = TimeSpan.FromSeconds(Math.Max(5, config?.PushDebounceSeconds ?? 30));
                var elapsed = DateTime.UtcNow - new DateTime(lastDirty, DateTimeKind.Utc);
                if (elapsed < debounce) continue;

                // Atomically clear; if another event fires between read and clear we'll
                // catch it on the next tick.
                Interlocked.CompareExchange(ref _dirtyTicks, 0, lastDirty);

                if (config is null || string.IsNullOrWhiteSpace(config.PublicBaseUrl))
                {
                    _logger.LogDebug("Push-invalidation skipped: PublicBaseUrl not configured.");
                    continue;
                }

                await FireAsync(config, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _libraryManager.ItemAdded -= OnItemMutated;
            _libraryManager.ItemRemoved -= OnItemMutated;
            _libraryManager.ItemUpdated -= OnItemMutated;
        }
    }

    private void OnItemMutated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is null) return;
        // Only flag if the mutation is on a federatable item type. Pre-scan tag/people
        // updates flood the event stream; we don't care unless an actual catalog entry shifted.
        var kind = e.Item.GetType().Name;
        if (kind != "Movie" && kind != "Series" && kind != "Episode") return;
        Interlocked.Exchange(ref _dirtyTicks, DateTime.UtcNow.Ticks);
    }

    private async Task FireAsync(Configuration.PluginConfiguration config, CancellationToken ct)
    {
        var peers = config.RemoteServers
            .Where(s => s.Enabled && !string.IsNullOrEmpty(s.FederationShareKey))
            .ToArray();
        if (peers.Length == 0) return;

        var payload = new InvalidatePayload { FromBaseUrl = config.PublicBaseUrl.TrimEnd('/') };
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        foreach (var peer in peers)
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
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Push to {Peer} failed (will gossip-pull on next sync)", peer.Name);
            }
        }
    }
}

public class InvalidatePayload
{
    public string FromBaseUrl { get; set; } = string.Empty;
}
