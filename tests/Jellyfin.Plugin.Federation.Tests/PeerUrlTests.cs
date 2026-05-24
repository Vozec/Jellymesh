using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PeerUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://nope.example")]    // wrong scheme
    [InlineData("https://")]              // no host
    [InlineData("peer.example.com")]      // bare host: scheme required (review #4 fix)
    [InlineData("peer.example.com:8096")] // bare host:port: still rejected
    public void Canonicalize_returns_null_for_invalid(string? input)
    {
        Assert.Null(PeerUrl.Canonicalize(input));
    }

    [Theory]
    [InlineData("https://peer.example.com",            "https://peer.example.com:443")]
    [InlineData("https://peer.example.com/",           "https://peer.example.com:443")]
    [InlineData("HTTPS://Peer.Example.COM",            "https://peer.example.com:443")]
    [InlineData("https://peer.example.com:443",        "https://peer.example.com:443")]
    [InlineData("https://peer.example.com/jellyfin",   "https://peer.example.com:443")]
    [InlineData("https://peer.example.com/jellyfin/",  "https://peer.example.com:443")]
    [InlineData("http://peer.example.com:8096",        "http://peer.example.com:8096")]
    [InlineData("http://peer.example.com",             "http://peer.example.com:80")]
    public void Canonicalize_normalizes_to_scheme_host_port(string input, string expected)
    {
        Assert.Equal(expected, PeerUrl.Canonicalize(input));
    }

    [Theory]
    // The classic drifts the review flagged:
    [InlineData("https://peer.example.com",       "https://peer.example.com/",        true)]
    [InlineData("https://peer.example.com",       "https://peer.example.com:443",     true)]
    [InlineData("https://Peer.Example.COM",       "https://peer.example.com",         true)]
    [InlineData("https://peer.example.com:8096",  "https://peer.example.com",         false)] // different port
    [InlineData("http://peer.example.com",        "https://peer.example.com",         false)] // different scheme
    [InlineData("https://peer.example.com/jellyfin", "https://peer.example.com/admin", true)]  // path ignored
    [InlineData("https://a.example.com",          "https://b.example.com",            false)]
    public void SameHost_handles_classic_drifts(string a, string b, bool expected)
    {
        Assert.Equal(expected, PeerUrl.SameHost(a, b));
    }

    [Fact]
    public void SameHost_returns_false_when_either_side_is_invalid()
    {
        Assert.False(PeerUrl.SameHost(null, "https://x"));
        Assert.False(PeerUrl.SameHost("https://x", null));
        Assert.False(PeerUrl.SameHost("ftp://x", "https://x"));
    }
}
