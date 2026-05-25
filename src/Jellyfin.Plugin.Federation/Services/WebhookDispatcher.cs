using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Fire-and-forget POST to the configured WebhookUrl for events the admin opted into.
/// Generic JSON body, or Discord-compatible "content" field when WebhookDiscordFormat is on.
/// Failures are logged at Debug to avoid spamming logs when the webhook is mis-configured.
/// </summary>
public class WebhookDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(IHttpClientFactory httpClientFactory, ILogger<WebhookDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void Fire(string eventName, string summary, object? payload = null)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.WebhookUrl)) return;

        var events = (config.WebhookEvents ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matched = false;
        foreach (var e in events) if (string.Equals(e, eventName, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
        if (!matched) return;

        var url = config.WebhookUrl;
        var discordMode = config.WebhookDiscordFormat;

        _ = Task.Run(async () =>
        {
            try
            {
                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                HttpContent body;
                if (discordMode)
                {
                    body = JsonContent.Create(new { content = $"[Jellymesh] {eventName}: {summary}" });
                }
                else
                {
                    body = JsonContent.Create(new { @event = eventName, summary, ts = DateTime.UtcNow.ToString("O"), payload });
                }
                using var resp = await http.PostAsync(url, body, CancellationToken.None).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    _logger.LogDebug("Webhook returned {Status} for event {Event}", (int)resp.StatusCode, eventName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Webhook POST failed");
            }
        });
    }
}
