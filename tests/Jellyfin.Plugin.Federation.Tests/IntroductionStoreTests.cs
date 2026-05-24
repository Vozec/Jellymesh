using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class IntroductionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IntroductionStore _store;

    public IntroductionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fed-intrstore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new IntroductionStore(new TestAppPaths(_tempDir), NullLogger<IntroductionStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void InsertActiveOrGet_first_call_returns_isNew()
    {
        var (id, isNew, existing) = _store.InsertActiveOrGet("issuer", "https://x:443",
            introducerKeyId: Guid.NewGuid(), issuedKeyId: Guid.NewGuid(), hopCount: 1, note: null);

        Assert.True(id > 0);
        Assert.True(isNew);
        Assert.Null(existing);
    }

    [Fact]
    public void InsertActiveOrGet_second_call_for_same_url_returns_existing_key_id()
    {
        var firstKey = Guid.NewGuid();
        var (firstId, _, _) = _store.InsertActiveOrGet("issuer", "https://x:443",
            Guid.NewGuid(), firstKey, 1, null);

        var (secondId, isNew, existing) = _store.InsertActiveOrGet("issuer", "https://x:443",
            Guid.NewGuid(), Guid.NewGuid(), 1, null);

        Assert.False(isNew);
        Assert.Equal(firstId, secondId);
        Assert.Equal(firstKey.ToString(), existing);
    }

    [Fact]
    public void InsertActiveOrGet_different_role_doesnt_conflict()
    {
        _store.InsertActiveOrGet("issuer", "https://x:443", Guid.NewGuid(), Guid.NewGuid(), 1, null);
        var (_, isNew, _) = _store.InsertActiveOrGet("forwarder", "https://x:443",
            Guid.NewGuid(), Guid.NewGuid(), 1, null);
        Assert.True(isNew);
    }

    [Fact]
    public void ListIssuedBy_default_filters_revoked_rows_out()
    {
        var introducer = Guid.NewGuid();
        var keyA = Guid.NewGuid();
        var (idA, _, _) = _store.InsertActiveOrGet("issuer", "https://a:443", introducer, keyA, 1, null);
        var keyB = Guid.NewGuid();
        _store.InsertActiveOrGet("issuer", "https://b:443", introducer, keyB, 1, null);

        _store.UpdateStatus(idA, "revoked");

        var visible = _store.ListIssuedBy(introducer);
        Assert.Single(visible);
        Assert.Equal(keyB, visible[0].IssuedKeyId);
    }

    [Fact]
    public void ListIssuedBy_includeRevoked_surfaces_them_for_cascade_walk()
    {
        var introducer = Guid.NewGuid();
        var (idA, _, _) = _store.InsertActiveOrGet("issuer", "https://a:443", introducer, Guid.NewGuid(), 1, null);
        _store.InsertActiveOrGet("issuer", "https://b:443", introducer, Guid.NewGuid(), 1, null);
        _store.UpdateStatus(idA, "revoked");

        var all = _store.ListIssuedBy(introducer, includeRevoked: true);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Status == "revoked");
        Assert.Contains(all, r => r.Status == "active");
    }

    [Fact]
    public void Activate_only_flips_pending_rows()
    {
        var introducer = Guid.NewGuid();
        var pendingId = _store.InsertPending("issuer", "https://p:443", introducer, 1, null);

        Assert.True(_store.Activate(pendingId, Guid.NewGuid()));
        Assert.False(_store.Activate(pendingId, Guid.NewGuid())); // already active, no-op
    }

    [Fact]
    public void CountRecentByIntroducer_counts_active_plus_pending_only()
    {
        var introducer = Guid.NewGuid();
        var pending1 = _store.InsertPending("issuer", "https://a:443", introducer, 1, null);
        _store.InsertActiveOrGet("issuer", "https://b:443", introducer, Guid.NewGuid(), 1, null);
        var pending2 = _store.InsertPending("issuer", "https://c:443", introducer, 1, null);
        _store.UpdateStatus(pending2, "rejected"); // shouldn't count

        var (hour, day) = _store.CountRecentByIntroducer(introducer);

        Assert.Equal(2, hour); // pending1 + active b
        Assert.Equal(2, day);
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
