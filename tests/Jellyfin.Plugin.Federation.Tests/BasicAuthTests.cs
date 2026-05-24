using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class BasicAuthTests
{
    [Fact]
    public void AddBasicAuth_skips_when_both_credentials_empty()
    {
        var http = new HttpClient();
        var server = new RemoteServer { BasicAuthUser = "", BasicAuthPass = "" };

        RemoteJellyfinClient.AddBasicAuth(http, server);

        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void AddBasicAuth_writes_header_when_user_only_set()
    {
        var http = new HttpClient();
        var server = new RemoteServer { BasicAuthUser = "alice", BasicAuthPass = "" };

        RemoteJellyfinClient.AddBasicAuth(http, server);

        Assert.NotNull(http.DefaultRequestHeaders.Authorization);
        Assert.Equal("Basic", http.DefaultRequestHeaders.Authorization!.Scheme);
        // base64("alice:") = "YWxpY2U6"
        Assert.Equal("YWxpY2U6", http.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public void AddBasicAuth_encodes_user_and_pass()
    {
        var http = new HttpClient();
        var server = new RemoteServer { BasicAuthUser = "alice", BasicAuthPass = "s3cret" };

        RemoteJellyfinClient.AddBasicAuth(http, server);

        Assert.NotNull(http.DefaultRequestHeaders.Authorization);
        // base64("alice:s3cret") = "YWxpY2U6czNjcmV0"
        Assert.Equal("YWxpY2U6czNjcmV0", http.DefaultRequestHeaders.Authorization!.Parameter);
    }

    [Fact]
    public void AddBasicAuth_handles_utf8_in_credentials()
    {
        var http = new HttpClient();
        var server = new RemoteServer { BasicAuthUser = "élise", BasicAuthPass = "naïve" };

        RemoteJellyfinClient.AddBasicAuth(http, server);

        Assert.NotNull(http.DefaultRequestHeaders.Authorization);
        // base64(utf8("élise:naïve"))
        var expected = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("élise:naïve"));
        Assert.Equal(expected, http.DefaultRequestHeaders.Authorization!.Parameter);
    }
}
