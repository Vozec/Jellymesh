using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Federation;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Guards all writes to Configuration.Shares / Configuration.RemoteServers /
    /// Configuration.PublicShares and the immediately-following SaveConfiguration call.
    /// ASP.NET Core dispatches HTTP handlers concurrently across the threadpool and
    /// List&lt;T&gt; is not thread-safe; without this lock concurrent peer-facing endpoints
    /// (Introduce, Introduced, RequestReciprocalKey) racing on Shares.Add can corrupt
    /// the in-memory list or lose entries.</summary>
    public static readonly object ConfigWriteLock = new();

    public override string Name => "Jellymesh";

    public override Guid Id => Guid.Parse("9f3c2a8e-6b1d-4f7a-b3c5-1e2d9a8b7c6e");

    public override string Description =>
        "Federate multiple Jellyfin servers. Share libraries, dedupe matches, expose multi-version playback.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    };
}
