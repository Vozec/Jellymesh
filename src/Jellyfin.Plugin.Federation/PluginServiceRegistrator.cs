using Jellyfin.Plugin.Federation.Providers;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Federation;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RemoteItemStore>();
        serviceCollection.AddSingleton<RemoteJellyfinClient>();
        serviceCollection.AddSingleton<LocalCatalogDigest>();
        serviceCollection.AddSingleton<PublicShareStore>();
        serviceCollection.AddSingleton<FederationStatsService>();
        serviceCollection.AddSingleton<RequestStore>();
        serviceCollection.AddSingleton<IntroductionStore>();
        serviceCollection.AddSingleton<IntroductionService>();
        serviceCollection.AddSingleton<PeerAccessStore>();
        serviceCollection.AddSingleton<InboundAuditStore>();
        serviceCollection.AddSingleton<PeerHealthHistoryStore>();
        serviceCollection.AddSingleton<QuotaService>();
        serviceCollection.AddSingleton<PeerLibraryCache>();
        serviceCollection.AddSingleton<SyncProgressTracker>();
        serviceCollection.AddSingleton<WebhookDispatcher>();
        serviceCollection.AddHostedService<RetentionCleanupService>();
        serviceCollection.AddHostedService<IndexHtmlInjector>();
        serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FederationStartupFilter>();
        serviceCollection.AddSingleton<DiagnosticsService>();
        serviceCollection.AddHostedService<StartupReportService>();
        serviceCollection.AddSingleton<IMediaSourceProvider, FederatedMediaSourceProvider>();
        // FriendsLibraryChannel removed: the federated home sections + 'Merge into local
        // library' flow replaced it. The channel showed up as a confusing 'Friends Library'
        // entry alongside the user's real libraries.
        serviceCollection.AddHostedService<WatchStateSyncService>();
        serviceCollection.AddSingleton<PeerHealthRegistry>();
        serviceCollection.AddHostedService<HealthMonitorService>();
        serviceCollection.AddHostedService<PushInvalidationService>();
    }
}
