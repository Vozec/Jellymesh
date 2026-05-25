using System;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Resolves the per-peer rate limit for an inbound call. Counts inbound rows in the audit
/// table within the configured window and compares against the peer's InboundReqPerHourLimit.
/// </summary>
public class QuotaService
{
    private readonly InboundAuditStore _audit;

    public QuotaService(InboundAuditStore audit)
    {
        _audit = audit;
    }

    public record Decision(bool Allowed, string? Reason, int? RetryAfterSeconds);

    /// <summary>Check whether a peer-attributed inbound call should proceed.</summary>
    public Decision CheckInbound(RemoteServer? peer)
    {
        if (peer is null) return new Decision(true, null, null);
        if (peer.InboundReqPerHourLimit <= 0) return new Decision(true, null, null);

        var count = _audit.CountForPeer(peer.Id, windowHours: 1);
        if (count < peer.InboundReqPerHourLimit) return new Decision(true, null, null);

        // Round up to the next hour boundary as a coarse Retry-After hint. Better than nothing
        // and avoids per-request math; the peer will back off and retry.
        var seconds = 3600 - (int)(DateTime.UtcNow.TimeOfDay.TotalSeconds % 3600);
        return new Decision(false, $"inbound rate limit {peer.InboundReqPerHourLimit}/hour exceeded", seconds);
    }

    /// <summary>Check whether serving N more bytes to this peer would exceed the per-day quota.</summary>
    public Decision CheckOutboundBytes(RemoteServer? peer, long bytesAboutToServe)
    {
        if (peer is null) return new Decision(true, null, null);
        if (peer.OutboundBytesPerDayLimit <= 0) return new Decision(true, null, null);

        var already = _audit.BytesForPeer(peer.Id, windowHours: 24);
        if (already + bytesAboutToServe <= peer.OutboundBytesPerDayLimit) return new Decision(true, null, null);

        var seconds = 86400 - (int)(DateTime.UtcNow.TimeOfDay.TotalSeconds);
        return new Decision(false, $"outbound bytes quota {peer.OutboundBytesPerDayLimit}/day exceeded ({already} already served)", seconds);
    }
}
