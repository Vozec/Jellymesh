using System;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Canonical form for peer base-URL comparison. Two URLs are considered the same peer when
/// their (scheme, host, port) triple matches — case-insensitive on scheme/host, trailing
/// path/query/fragment ignored. Avoids the brittle "string-equal-after-TrimEnd-slash"
/// pattern that drifted on http vs https, explicit-vs-default port, and trailing-path.
/// </summary>
public static class PeerUrl
{
    /// <summary>Returns a stable canonical form ("scheme://host:port") or null if the input
    /// is null/whitespace/unparseable. Default port is included so two configurations that
    /// differ only by explicit-vs-implicit port match.</summary>
    public static string? Canonicalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var input = raw.Trim();

        // Bare hostnames (no scheme) are REJECTED. The previous behaviour silently prepended
        // https:// which broke matching for plain-HTTP LAN deployments (peer.local:8096 was
        // canonicalized to https:// but the real server is http:// on 8096 — SameHost returned
        // false on every legitimate call). Admin must supply an explicit scheme so we don't
        // guess wrong.
        if (!input.Contains("://", StringComparison.Ordinal)) return null;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;

        // Uri.Port returns the default for the scheme if none was specified — so
        // https://x and https://x:443 produce the same canonical string.
        return $"{u.Scheme.ToLowerInvariant()}://{u.Host.ToLowerInvariant()}:{u.Port}";
    }

    public static bool SameHost(string? a, string? b)
    {
        var ca = Canonicalize(a);
        var cb = Canonicalize(b);
        return ca is not null && cb is not null && string.Equals(ca, cb, StringComparison.Ordinal);
    }
}
