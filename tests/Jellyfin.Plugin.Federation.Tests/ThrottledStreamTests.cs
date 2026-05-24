using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class ThrottledStreamTests
{
    [Fact]
    public async Task Read_at_cap_takes_at_least_expected_time()
    {
        // 100 KB at 50 KB/s = ~2s minimum
        var payload = new byte[100 * 1024];
        var src = new MemoryStream(payload);
        var throttled = new ThrottledStream(src, bytesPerSecond: 50 * 1024);

        var sw = Stopwatch.StartNew();
        var buf = new byte[8192];
        long total = 0;
        int n;
        while ((n = await throttled.ReadAsync(buf)) > 0) total += n;
        sw.Stop();

        Assert.Equal(payload.Length, total);
        Assert.True(sw.ElapsedMilliseconds >= 1500, $"expected >=1500ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Cap_zero_disables_throttling()
    {
        var payload = new byte[1024 * 1024];
        var throttled = new ThrottledStream(new MemoryStream(payload), bytesPerSecond: 0);

        var sw = Stopwatch.StartNew();
        var buf = new byte[8192];
        long total = 0;
        int n;
        while ((n = await throttled.ReadAsync(buf)) > 0) total += n;
        sw.Stop();

        Assert.Equal(payload.Length, total);
        Assert.True(sw.ElapsedMilliseconds < 500, $"expected fast read, got {sw.ElapsedMilliseconds}ms");
    }
}
