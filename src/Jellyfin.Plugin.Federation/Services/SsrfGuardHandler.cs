using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// DelegatingHandler attached to the named "federation" HttpClient. Every peer-bound request
/// (PlaybackInfo proxy, /Videos byte proxy, merge fan-out, session forward, user-id probe)
/// flows through here, so a single check covers them all: refuse anything SsrfGuard rejects
/// (bad scheme, link-local/metadata target, host outside the peer allowlist).
/// </summary>
public sealed class SsrfGuardHandler : DelegatingHandler
{
    private readonly ILogger<SsrfGuardHandler> _logger;

    public SsrfGuardHandler(ILogger<SsrfGuardHandler> logger) => _logger = logger;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var reason = "no request uri";
        if (uri is null || !SsrfGuard.IsAllowed(uri, out reason))
        {
            _logger.LogWarning("Blocked outbound federation request to {Uri}: {Reason}", uri, reason);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                ReasonPhrase = "Blocked by federation SSRF guard",
                RequestMessage = request,
            });
        }
        return base.SendAsync(request, cancellationToken);
    }
}
