using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RewriteFedIdsTests
{
    private const string PeerN = "b0dfffc31b764a219d93c82dc2e6d6ae";
    private const string RemoteId = "a0e9ea89a085ac4cf2020cae5c200291";
    private const string FedId = "fed_b0dfffc31b764a219d93c82dc2e6d6ae_a0e9ea89a085ac4cf2020cae5c200291";

    [Fact]
    public void ItemId_field_is_rewritten()
    {
        var body = $"{{\"ItemId\":\"{FedId}\",\"PositionTicks\":42}}";
        var got = FederationInterceptMiddleware.RewriteFedIdsForPeer(body, PeerN, RemoteId);
        Assert.Contains($"\"ItemId\":\"{RemoteId}\"", got);
        Assert.Contains("\"PositionTicks\":42", got);
        Assert.DoesNotContain("fed_", got);
    }

    [Fact]
    public void MediaSourceId_field_is_rewritten_alongside_ItemId()
    {
        var body = $"{{\"ItemId\":\"{FedId}\",\"MediaSourceId\":\"{FedId}\"}}";
        var got = FederationInterceptMiddleware.RewriteFedIdsForPeer(body, PeerN, RemoteId);
        Assert.Contains($"\"ItemId\":\"{RemoteId}\"", got);
        Assert.Contains($"\"MediaSourceId\":\"{RemoteId}\"", got);
    }

    [Fact]
    public void String_with_fed_substring_inside_unrelated_field_is_left_alone()
    {
        // The previous regex implementation would have corrupted Title here. Tree-walk only
        // rewrites the value when it equals the fed_<peerN>_<x> prefix exactly.
        var title = $"the {FedId} sessions";
        var body = $"{{\"ItemId\":\"{FedId}\",\"Title\":\"{title}\"}}";
        var got = FederationInterceptMiddleware.RewriteFedIdsForPeer(body, PeerN, RemoteId);
        Assert.Contains($"\"ItemId\":\"{RemoteId}\"", got);
        Assert.Contains($"\"Title\":\"{title}\"", got);
    }

    [Fact]
    public void Different_peer_prefix_is_not_touched()
    {
        var otherFedId = "fed_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_someitem";
        var body = $"{{\"ItemId\":\"{otherFedId}\"}}";
        var got = FederationInterceptMiddleware.RewriteFedIdsForPeer(body, PeerN, RemoteId);
        Assert.Contains($"\"ItemId\":\"{otherFedId}\"", got);
    }

    [Fact]
    public void Nested_object_is_walked()
    {
        var body = $"{{\"Session\":{{\"ItemId\":\"{FedId}\"}}}}";
        var got = FederationInterceptMiddleware.RewriteFedIdsForPeer(body, PeerN, RemoteId);
        Assert.Contains($"\"ItemId\":\"{RemoteId}\"", got);
    }

    [Fact]
    public void Non_json_body_passes_through_unchanged()
    {
        var body = "not even json";
        var got = FederationInterceptMiddleware.RewriteFedIdsForPeer(body, PeerN, RemoteId);
        Assert.Equal(body, got);
    }
}
