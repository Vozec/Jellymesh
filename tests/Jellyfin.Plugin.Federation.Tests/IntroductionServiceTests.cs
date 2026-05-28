using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class IntroductionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IntroductionStore _store;
    private readonly IntroductionService _svc;
    private readonly PluginConfiguration _config;

    public IntroductionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fed-intro-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new IntroductionStore(new TestAppPaths(_tempDir), NullLogger<IntroductionStore>.Instance);
        _svc = new IntroductionService(_store, NullLogger<IntroductionService>.Instance);
        _config = new PluginConfiguration
        {
            PublicBaseUrl = "https://a.example",
            IntroductionRatePerHour = 5,
            IntroductionRatePerDay = 50
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private ShareKey IntroducerKey(IntroductionMintMode mode = IntroductionMintMode.AutoAccept) => new()
    {
        ApiKey = "introducer-key",
        Label = "B's key on A",
        CanRequestIntroductions = true,
        MintMode = mode,
        LibraryIds = new() { "movies-lib" },
        BlockedTags = new() { "adult" },
        MaxOfficialRating = "PG-13",
        Enabled = true
    };

    [Fact]
    public void Reject_when_introducer_has_no_permission()
    {
        var key = IntroducerKey();
        key.CanRequestIntroductions = false;
        var result = _svc.TryMint(_config, key, "https://c.example", 1, null);
        Assert.Equal("no-permission", result.Status);
    }

    [Fact]
    public void Reject_when_ForUrl_lacks_scheme()
    {
        var result = _svc.TryMint(_config, IntroducerKey(), "c.example", 1, null);
        Assert.Equal("bad-url", result.Status);
    }

    [Fact]
    public void Reject_when_target_is_self()
    {
        var result = _svc.TryMint(_config, IntroducerKey(), "https://a.example", 1, null);
        Assert.Equal("self", result.Status);
    }

    [Fact]
    public void Reject_when_target_is_already_a_peer()
    {
        _config.RemoteServers.Add(new RemoteServer { BaseUrl = "https://c.example", Enabled = true });
        var result = _svc.TryMint(_config, IntroducerKey(), "https://c.example", 1, null);
        Assert.Equal("already-peer", result.Status);
    }

    [Fact]
    public void Reject_when_hop_count_exceeds_cap()
    {
        _config.IntroductionHopCap = 2;
        var result = _svc.TryMint(_config, IntroducerKey(), "https://c.example", 3, null);
        Assert.Equal("hop-cap", result.Status);
    }

    [Fact]
    public void No_cap_when_HopCap_is_null()
    {
        _config.IntroductionHopCap = null;
        var result = _svc.TryMint(_config, IntroducerKey(), "https://c.example", 999, null);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Rate_limit_per_hour_kicks_in()
    {
        _config.IntroductionRatePerHour = 2;
        var key = IntroducerKey();
        Assert.True(_svc.TryMint(_config, key, "https://c1.example", 1, null).IsSuccess);
        Assert.True(_svc.TryMint(_config, key, "https://c2.example", 1, null).IsSuccess);
        var third = _svc.TryMint(_config, key, "https://c3.example", 1, null);
        Assert.Equal("rate-limit-hour", third.Status);
    }

    [Fact]
    public void AutoAccept_mode_mints_immediately()
    {
        var result = _svc.TryMint(_config, IntroducerKey(IntroductionMintMode.AutoAccept), "https://c.example", 1, null);
        Assert.Equal("minted", result.Status);
        Assert.NotNull(result.ApiKey);
        Assert.Single(_config.Shares);
    }

    [Fact]
    public void Request_mode_queues_pending_and_does_not_mint()
    {
        var result = _svc.TryMint(_config, IntroducerKey(IntroductionMintMode.Request), "https://c.example", 1, null);
        Assert.Equal("pending", result.Status);
        Assert.NotNull(result.IntroductionId);
        Assert.Empty(_config.Shares); // no key minted yet
    }

    [Fact]
    public void Reject_mode_records_then_rejects()
    {
        var result = _svc.TryMint(_config, IntroducerKey(IntroductionMintMode.Reject), "https://c.example", 1, null);
        Assert.Equal("rejected", result.Status);
        Assert.Empty(_config.Shares);
    }

    [Fact]
    public void Minted_key_inherits_introducer_scope()
    {
        var key = IntroducerKey(IntroductionMintMode.AutoAccept);
        var result = _svc.TryMint(_config, key, "https://c.example", 1, null);
        Assert.True(result.IsSuccess);

        var minted = _config.Shares.Single();
        Assert.Equal(key.LibraryIds, minted.LibraryIds);
        Assert.Equal(key.BlockedTags, minted.BlockedTags);
        Assert.Equal(key.MaxOfficialRating, minted.MaxOfficialRating);
        Assert.Equal("https://c.example:443", minted.BoundPeerUrl);
        Assert.Equal("https://c.example:443", minted.IssuedForUrl);
        Assert.Equal(key.Id, minted.IntroducedByKeyId);
    }

    [Fact]
    public void Minted_key_cannot_re_introduce_by_default()
    {
        var result = _svc.TryMint(_config, IntroducerKey(IntroductionMintMode.AutoAccept), "https://c.example", 1, null);
        Assert.True(result.IsSuccess);
        var minted = _config.Shares.Single();

        Assert.False(minted.CanRequestIntroductions); // no auto-chain
        Assert.Equal(IntroductionMintMode.Reject, minted.MintMode);
    }

    [Fact]
    public void Concurrent_introductions_for_same_target_dedup_to_one_key()
    {
        // Two introducer keys both try to introduce the same target.
        var keyB = IntroducerKey(IntroductionMintMode.AutoAccept);
        var keyC = IntroducerKey(IntroductionMintMode.AutoAccept);
        keyB.Label = "B"; keyC.Label = "C";

        var r1 = _svc.TryMint(_config, keyB, "https://d.example", 1, null);
        var r2 = _svc.TryMint(_config, keyC, "https://d.example", 1, null);

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        // Second introducer sees the existing key, not a fresh mint.
        Assert.Equal(r1.ApiKey, r2.ApiKey);
        Assert.Equal("existing", r2.Status);
        Assert.Single(_config.Shares); // only ONE key materialized
    }

    [Fact]
    public void ApprovePending_mints_and_activates()
    {
        var key = IntroducerKey(IntroductionMintMode.Request);
        _config.Shares.Add(key); // introducer must be findable on approval
        var pending = _svc.TryMint(_config, key, "https://c.example", 1, null);
        Assert.Equal("pending", pending.Status);
        Assert.Single(_config.Shares); // only the introducer itself

        var approved = _svc.ApprovePending(_config, pending.IntroductionId!.Value);
        Assert.True(approved.IsSuccess);
        Assert.Equal(2, _config.Shares.Count); // introducer + minted
    }

    [Fact]
    public void ApprovePending_fails_when_introducer_key_was_deleted()
    {
        var key = IntroducerKey(IntroductionMintMode.Request);
        _config.Shares.Add(key);
        var pending = _svc.TryMint(_config, key, "https://c.example", 1, null);
        _config.Shares.Clear(); // simulate admin removed introducer
        var approved = _svc.ApprovePending(_config, pending.IntroductionId!.Value);
        Assert.Equal("no-introducer", approved.Status);
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
