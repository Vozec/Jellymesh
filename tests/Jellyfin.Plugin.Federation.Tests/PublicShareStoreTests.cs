using System;
using System.IO;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PublicShareStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PublicShareStore _store;

    public PublicShareStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fed-share-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new PublicShareStore(new TestAppPaths(_tempDir), NullLogger<PublicShareStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Create_then_TryConsume_returns_item_and_increments_use_count()
    {
        var token = _store.Create("item-abc", expiresUtc: null, maxUses: null, creator: "me");
        Assert.False(string.IsNullOrEmpty(token));

        var itemId = _store.TryConsume(token);
        Assert.Equal("item-abc", itemId);

        var info = _store.GetInfo(token)!;
        Assert.Equal(1, info.UsedCount);
    }

    [Fact]
    public void TryConsume_returns_null_when_token_unknown()
    {
        Assert.Null(_store.TryConsume("bogus-token"));
    }

    [Fact]
    public void TryConsume_respects_max_uses_cap()
    {
        var token = _store.Create("item-cap", expiresUtc: null, maxUses: 2, creator: null);

        Assert.Equal("item-cap", _store.TryConsume(token));
        Assert.Equal("item-cap", _store.TryConsume(token));
        // Third attempt → exhausted, returns null. Counter does NOT advance past max.
        Assert.Null(_store.TryConsume(token));

        var info = _store.GetInfo(token)!;
        Assert.Equal(2, info.UsedCount);
    }

    [Fact]
    public void TryConsume_returns_null_after_expiry()
    {
        var token = _store.Create("item-exp", expiresUtc: DateTime.UtcNow.AddSeconds(-1), maxUses: null, creator: null);
        Assert.Null(_store.TryConsume(token));

        var info = _store.GetInfo(token)!;
        Assert.Equal(0, info.UsedCount); // expired → not consumed
    }

    [Fact]
    public void Revoke_removes_share()
    {
        var token = _store.Create("item-rev", null, null, null);
        _store.Revoke(token);
        Assert.Null(_store.GetInfo(token));
        Assert.Null(_store.TryConsume(token));
    }

    [Fact]
    public void TryConsume_is_atomic_under_parallel_callers()
    {
        // Two callers race on a single-use token. Exactly one must succeed.
        var token = _store.Create("item-race", null, maxUses: 1, null);

        var results = new System.Collections.Concurrent.ConcurrentBag<string?>();
        var b = new System.Threading.Barrier(8);
        var threads = new System.Threading.Thread[8];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new System.Threading.Thread(() =>
            {
                b.SignalAndWait();
                results.Add(_store.TryConsume(token));
            });
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();

        var winners = 0;
        foreach (var r in results) if (r is not null) winners++;
        Assert.Equal(1, winners);
        Assert.Equal(1, _store.GetInfo(token)!.UsedCount);
    }

    [Fact]
    public void TryConsume_caps_at_MaxUses_under_high_contention()
    {
        // 50 concurrent callers, cap=10. Atomic UPDATE…WHERE used_count < max_uses RETURNING
        // must let exactly 10 win - not 50 (no cap), not 40 (over-count), not 5 (busy-throw
        // before retry). This is the regression test for the DEFERRED-tx pre-check race fix.
        var token = _store.Create("item-burst", null, maxUses: 10, null);

        var results = new System.Collections.Concurrent.ConcurrentBag<string?>();
        var b = new System.Threading.Barrier(50);
        var threads = new System.Threading.Thread[50];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new System.Threading.Thread(() =>
            {
                b.SignalAndWait();
                results.Add(_store.TryConsume(token));
            });
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();

        var winners = 0;
        foreach (var r in results) if (r is not null) winners++;
        Assert.Equal(10, winners);
        Assert.Equal(10, _store.GetInfo(token)!.UsedCount);
    }

    [Fact]
    public void Create_with_local_kind_expiry_does_not_silently_drift()
    {
        // Reproduce the timestamp-Kind bug: caller passes DateTime.UtcNow without explicit Kind.
        // After the RoundtripKind fix it should still trip the expiry on a value far in the past.
        var pastUnspecified = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-30), DateTimeKind.Unspecified);
        var token = _store.Create("item-past", pastUnspecified, null, null);
        Assert.Null(_store.TryConsume(token));
    }

    private class TestAppPaths : IApplicationPaths
    {
        public TestAppPaths(string dir) { DataPath = dir; }
        public string ProgramDataPath => DataPath;
        public string WebPath => DataPath;
        public string ProgramSystemPath => DataPath;
        public string DataPath { get; }
        public string ImageCachePath => DataPath;
        public string PluginsPath => DataPath;
        public string PluginConfigurationsPath => DataPath;
        public string LogDirectoryPath => DataPath;
        public string ConfigurationDirectoryPath => DataPath;
        public string SystemConfigurationFilePath => Path.Combine(DataPath, "system.xml");
        public string CachePath => DataPath;
        public string TempDirectory => DataPath;
        public string VirtualDataPath => DataPath;
    }
}
