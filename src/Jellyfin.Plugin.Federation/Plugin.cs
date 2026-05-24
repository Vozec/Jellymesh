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

    public override string Name => "Federation";

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
