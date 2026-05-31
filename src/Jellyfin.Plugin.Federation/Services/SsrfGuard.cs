using System;
using System.Linq;
using System.Net;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Outbound request guard for peer-bound traffic. Two independent checks:
///   1. Allowlist: the target host must be a configured peer, our own PublicBaseUrl, loopback
///      (the merge middleware calls back into our own server), or an admin-listed extra host.
///   2. Denylist: even an allowlisted host is refused if it is a link-local / cloud-metadata
///      address (169.254.0.0/16, fe80::/10, 0.0.0.0, ::). This is what stops a peer that got
///      itself added via an introduction from pointing BaseUrl at 169.254.169.254 and making us
///      fetch cloud credentials. RFC1918 private ranges (10/8, 172.16/12, 192.168/16) are
///      deliberately allowed: the federation mesh lives on private docker IPs.
/// </summary>
public static class SsrfGuard
{
    public static bool IsAllowed(Uri uri, out string reason)
    {
        reason = string.Empty;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"scheme '{uri.Scheme}' not allowed";
            return false;
        }
        var host = uri.Host;
        if (IsDangerousHost(host))
        {
            reason = $"host '{host}' is link-local / metadata";
            return false;
        }
        if (IsLoopback(host)) return true;

        var config = Plugin.Instance?.Configuration;
        if (config is null) { reason = "no config"; return false; }

        if (HostMatches(config.PublicBaseUrl, host)) return true;
        if (config.RemoteServers.Any(s => HostMatches(s.BaseUrl, host))) return true;
        if (config.OutboundHostAllowlist.Any(h => string.Equals(h?.Trim(), host, StringComparison.OrdinalIgnoreCase))) return true;

        reason = $"host '{host}' not in peer allowlist";
        return false;
    }

    // Used when a network-originated URL is about to become a RemoteServer.BaseUrl. Rejects bad
    // schemes and metadata/link-local targets; allows private + public hosts (admin still
    // approves the peer).
    public static bool IsSafePeerBaseUrl(string? baseUrl, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            reason = "not an absolute http(s) url";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"scheme '{uri.Scheme}' not allowed";
            return false;
        }
        if (IsDangerousHost(uri.Host))
        {
            reason = $"host '{uri.Host}' is link-local / metadata";
            return false;
        }
        return true;
    }

    private static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static bool HostMatches(string? configuredBaseUrl, string host)
        => !string.IsNullOrEmpty(configuredBaseUrl)
           && Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var u)
           && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase);

    private static bool IsDangerousHost(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false; // DNS name: not resolved here
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 169.254.0.0/16 (link-local + AWS/GCP/Azure metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return true;
            // 0.0.0.0/8 "this host"
            if (b[0] == 0) return true;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (IPAddress.IPv6Any.Equals(ip)) return true;
        }
        return false;
    }
}
