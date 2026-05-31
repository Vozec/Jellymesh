using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Mutual-TLS support for outbound peer calls. Instead of building a separate HttpClient per
/// peer, we install one primary handler whose TLS options pick the right CLIENT certificate per
/// target host (a peer is identified by the host of its BaseUrl) and validate the peer's SERVER
/// certificate against any configured private CA. Wired in PluginServiceRegistrator onto every
/// HttpClient the factory hands out, so all existing call sites get mTLS for free. Peers without
/// a client cert are unaffected (callback returns null = no client cert presented).
/// </summary>
public static class PeerMtls
{
    private static readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new();

    public static SocketsHttpHandler BuildHandler(bool allowAutoRedirect)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        h.SslOptions.LocalCertificateSelectionCallback = SelectClientCertificate;
        h.SslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;
        return h;
    }

    private static X509Certificate SelectClientCertificate(object sender, string targetHost,
        X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers)
    {
        // Returning null (no client cert for this host) is valid at runtime; the delegate's
        // return type is non-nullable-oblivious, hence the suppression.
        var peer = FindPeerByHost(targetHost, requireClientCert: true);
        return (peer is null ? null : LoadClientCert(peer))!;
    }

    private static bool ValidateServerCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        // Default validation passed (publicly-trusted cert + matching host) -> accept.
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null) return false;
        // Otherwise: accept only if it chains to the private CA the admin configured for THIS
        // target host's peer AND the cert actually matches the host. Scoping to the one peer
        // stops a CA configured for peer A from validating a cert served by peer B, and the
        // hostname check stops a valid-but-wrong-host cert from being accepted.
        var host = (sender as System.Net.Security.SslStream)?.TargetHostName;
        if (string.IsNullOrEmpty(host)) return false;
        var cas = CollectCustomCas(host);
        if (cas is null || cas.Count == 0) return false;
        try
        {
            // Constructor can throw on a malformed blob - keep it inside the try so the TLS
            // callback fails closed instead of surfacing a raw exception.
            var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
            using var ch = new X509Chain();
            ch.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            ch.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            ch.ChainPolicy.CustomTrustStore.AddRange(cas);
            if (!ch.Build(cert2)) return false;
            return cert2.MatchesHostname(host);
        }
        catch
        {
            return false;
        }
    }

    private static Configuration.RemoteServer? FindPeerByHost(string host, bool requireClientCert)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrEmpty(host)) return null;
        foreach (var p in cfg.RemoteServers)
        {
            if (requireClientCert && string.IsNullOrWhiteSpace(p.ClientCertPem)) continue;
            if (Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u)
                && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    private static X509Certificate2? LoadClientCert(Configuration.RemoteServer p)
    {
        try
        {
            var fingerprint = Hash(p.ClientCertPem + "\n" + p.ClientCertKeyPem + "\n" + p.ClientCertPassword);
            var key = p.Id.ToString("N") + ":" + fingerprint;
            return _certCache.GetOrAdd(key, _ =>
            {
                var certPem = p.ClientCertPem;
                var keyPem = string.IsNullOrWhiteSpace(p.ClientCertKeyPem) ? p.ClientCertPem : p.ClientCertKeyPem;
                using var parsed = string.IsNullOrEmpty(p.ClientCertPassword)
                    ? X509Certificate2.CreateFromPem(certPem, keyPem)
                    : X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, p.ClientCertPassword);
                // Round-trip through PKCS#12 so the private key is persisted in a form usable for
                // TLS client authentication on every platform (the key from CreateFromPem is
                // otherwise ephemeral and rejected by the TLS stack on some runtimes).
                return new X509Certificate2(parsed.Export(X509ContentType.Pkcs12));
            });
        }
        catch
        {
            return null;
        }
    }

    // Collect only the CA(s) configured for the peer whose BaseUrl host matches the connection's
    // target host. Returns null when no such peer / no CA, so trust never bleeds across peers.
    private static X509Certificate2Collection? CollectCustomCas(string host)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return null;
        X509Certificate2Collection? col = null;
        foreach (var p in cfg.RemoteServers)
        {
            if (string.IsNullOrWhiteSpace(p.CaCertPem)) continue;
            if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u)
                || !string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                col ??= new X509Certificate2Collection();
                col.ImportFromPem(p.CaCertPem);
            }
            catch
            {
                // skip malformed CA bundle
            }
        }
        return col;
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
