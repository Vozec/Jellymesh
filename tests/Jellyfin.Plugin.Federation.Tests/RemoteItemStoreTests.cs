using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Federation.Models;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RemoteItemStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RemoteItemStore _store;

    public RemoteItemStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new RemoteItemStore(new TestAppPaths(_tempDir), NullLogger<RemoteItemStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Upsert_then_find_by_tmdb_returns_item()
    {
        var serverId = Guid.NewGuid();
        var item = new RemoteItem
        {
            ServerId = serverId,
            RemoteItemId = "abc123",
            Type = "Movie",
            Name = "Test Movie",
            ProductionYear = 2020,
            ProviderIds = new() { ["Tmdb"] = "12345", ["Imdb"] = "tt0000001" },
            LastSeenUtc = DateTime.UtcNow
        };
        _store.Upsert(item);

        var found = _store.FindMatches(tmdb: "12345", imdb: null, title: null, year: null).ToList();

        Assert.Single(found);
        Assert.Equal(serverId, found[0].ServerId);
        Assert.Equal("abc123", found[0].RemoteItemId);
    }

    [Fact]
    public void Find_falls_back_to_title_year_when_only_title_match_provided()
    {
        var item = new RemoteItem
        {
            ServerId = Guid.NewGuid(),
            RemoteItemId = "xyz",
            Type = "Movie",
            Name = "Inception",
            ProductionYear = 2010,
            LastSeenUtc = DateTime.UtcNow
        };
        _store.Upsert(item);

        var found = _store.FindMatches(tmdb: null, imdb: null, title: "Inception", year: 2010).ToList();

        Assert.Single(found);
    }

    [Fact]
    public void Audit_lifecycle_roundtrip()
    {
        var peer = Guid.NewGuid();
        var id = _store.BeginAudit(peer, "item-1", userId: "u1");
        Assert.True(id > 0);
        _store.CompleteAudit(id, bytesServed: 4096);

        var recent = _store.RecentAudits(10).ToList();
        Assert.Single(recent);
        Assert.Equal(peer, recent[0].PeerId);
        Assert.Equal(4096, recent[0].Bytes);
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
#if NET9_0_OR_GREATER
        public string TrickplayPath => DataPath;
        public string BackupPath => DataPath;
        public void MakeSanityCheckOrThrow() { }
        public void CreateAndCheckMarker(string a, string b, bool c) { }
#endif
    }
}
