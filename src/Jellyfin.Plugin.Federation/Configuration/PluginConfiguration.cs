using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Federation.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<RemoteServer> RemoteServers { get; set; } = new();

    public int SyncIntervalMinutes { get; set; } = 60;

    public bool EnableDedup { get; set; } = true;

    public MatchStrategy MatchPriority { get; set; } = MatchStrategy.TmdbThenImdbThenTitle;

    public bool ShowRemoteOnlyItems { get; set; } = true;

    public bool EnableWatchStateSync { get; set; } = false;

    public int RemoteOfflineTimeoutSeconds { get; set; } = 5;

    public long OutboundBitrateCapBps { get; set; } = 0;
}

public class RemoteServer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string? RemoteUserId { get; set; }

    public bool Enabled { get; set; } = true;

    public List<string> AllowedLibraryIds { get; set; } = new();
}

public enum MatchStrategy
{
    TmdbOnly,
    TmdbThenImdb,
    TmdbThenImdbThenTitle
}
