using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RequestStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RequestStore _store;

    public RequestStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fed-req-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new RequestStore(new TestAppPaths(_tempDir), NullLogger<RequestStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Insert_then_List_returns_in_creation_order_desc()
    {
        _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "1", Title = "A" });
        System.Threading.Thread.Sleep(10);
        _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "2", Title = "B" });

        var rows = _store.List("in").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("B", rows[0].Title);
        Assert.Equal("A", rows[1].Title);
    }

    [Fact]
    public void Inbound_duplicate_pending_request_is_idempotent()
    {
        var first = _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "42", Title = "X" });
        var second = _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "42", Title = "X" });

        Assert.NotNull(first);
        Assert.Null(second); // uniq index prevents the dup row, RETURNING comes back null
        Assert.Single(_store.List("in", "pending"));
    }

    [Fact]
    public void Same_tmdb_from_different_peer_is_allowed()
    {
        _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "42", Title = "X" });
        var b = _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://b", TmdbId = "42", Title = "X" });
        Assert.NotNull(b);
        Assert.Equal(2, _store.List("in").Count);
    }

    [Fact]
    public void Status_change_unblocks_next_pending_dup_from_same_peer()
    {
        var id = _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "9", Title = "Y" })!.Value;
        // Now dismiss/accept the existing row - the uniq index is filtered on status='pending'
        // so a fresh request for the same item is allowed back in.
        _store.UpdateStatus(id, "dismissed");
        var again = _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "9", Title = "Y" });
        Assert.NotNull(again);
        Assert.Equal(1, _store.List("in", "pending").Count);
        Assert.Equal(1, _store.List("in", "dismissed").Count);
    }

    [Fact]
    public void List_filters_by_status()
    {
        var a = _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "1", Title = "A" })!.Value;
        _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "2", Title = "B" });
        _store.UpdateStatus(a, "accepted");

        Assert.Single(_store.List("in", "pending"));
        Assert.Single(_store.List("in", "accepted"));
        Assert.Empty(_store.List("in", "declined"));
        Assert.Equal(2, _store.List("in").Count);
    }

    [Fact]
    public void Direction_partition_is_enforced()
    {
        _store.Insert(new FederationRequest { Direction = "in", PeerUrl = "https://a", TmdbId = "1", Title = "A" });
        _store.Insert(new FederationRequest { Direction = "out", PeerUrl = "https://b", TmdbId = "1", Title = "A" });
        Assert.Single(_store.List("in"));
        Assert.Single(_store.List("out"));
    }

    [Fact]
    public void UpdateStatus_returns_false_on_unknown_id()
    {
        Assert.False(_store.UpdateStatus(99999, "accepted"));
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
