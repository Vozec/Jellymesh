using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PeerHealthRegistryTests
{
    [Fact]
    public void IsOnline_returns_true_for_unprobed_peer()
    {
        // Cold-start fix: before the first health probe completes (~30s after start),
        // we must NOT hide federated sources from working peers.
        var reg = new PeerHealthRegistry();
        Assert.True(reg.IsOnline(Guid.NewGuid()));
    }

    [Fact]
    public void IsOnline_reflects_last_probe()
    {
        var reg = new PeerHealthRegistry();
        var id = Guid.NewGuid();

        reg.Update(id, online: true, rtt: TimeSpan.FromMilliseconds(50));
        Assert.True(reg.IsOnline(id));

        reg.Update(id, online: false, rtt: TimeSpan.FromMilliseconds(100));
        Assert.False(reg.IsOnline(id));
    }

    [Fact]
    public void Signature_changes_on_flip_only()
    {
        var reg = new PeerHealthRegistry();
        var id = Guid.NewGuid();

        reg.Update(id, online: true, rtt: TimeSpan.Zero);
        var sig1 = reg.Signature();

        // Re-confirming the same state → signature stays the same.
        reg.Update(id, online: true, rtt: TimeSpan.Zero);
        Assert.Equal(sig1, reg.Signature());

        // Real flip → signature changes.
        reg.Update(id, online: false, rtt: TimeSpan.Zero);
        Assert.NotEqual(sig1, reg.Signature());
    }

    [Fact]
    public void HealthChanged_fires_only_on_flip()
    {
        var reg = new PeerHealthRegistry();
        var id = Guid.NewGuid();
        var flips = 0;
        reg.HealthChanged += (_, _) => flips++;

        reg.Update(id, online: true, rtt: TimeSpan.Zero);   // unprobed → true: flip
        reg.Update(id, online: true, rtt: TimeSpan.Zero);   // no change
        reg.Update(id, online: true, rtt: TimeSpan.Zero);   // no change
        reg.Update(id, online: false, rtt: TimeSpan.Zero);  // true → false: flip
        reg.Update(id, online: true, rtt: TimeSpan.Zero);   // false → true: flip

        Assert.Equal(3, flips);
    }

    [Fact]
    public void Signature_distinguishes_two_peers_with_different_states()
    {
        var reg = new PeerHealthRegistry();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        reg.Update(a, online: true, rtt: TimeSpan.Zero);
        reg.Update(b, online: true, rtt: TimeSpan.Zero);
        var both = reg.Signature();

        reg.Update(b, online: false, rtt: TimeSpan.Zero);
        var oneDown = reg.Signature();

        Assert.NotEqual(both, oneDown);
    }

    [Fact]
    public void Get_returns_default_for_unknown_peer()
    {
        var reg = new PeerHealthRegistry();
        var h = reg.Get(Guid.NewGuid());
        Assert.False(h.Online);
        Assert.Equal(DateTime.MinValue, h.LastCheckUtc);
    }
}
