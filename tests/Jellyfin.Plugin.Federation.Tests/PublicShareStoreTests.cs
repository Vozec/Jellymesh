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
