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
        // Named client for all peer-bound proxy calls. AllowAutoRedirect=false stops a peer
        // 3xx from replaying our X-Emby-Token / Basic creds to an attacker-controlled redirect
        // target. Used by FederationInterceptMiddleware via CreateClient("federation").
        serviceCollection.AddTransient<Services.SsrfGuardHandler>();
        // Apply the mutual-TLS primary handler (per-host client cert + private-CA validation) to
        // EVERY HttpClient the factory creates, so all peer call sites get mTLS without changes.
        serviceCollection.ConfigureHttpClientDefaults(b =>
            b.ConfigurePrimaryHttpMessageHandler(() => Services.PeerMtls.BuildHandler(allowAutoRedirect: true)));
        // The federation client keeps AllowAutoRedirect=false (stops a peer 3xx replaying creds)
        // plus the SSRF guard, on top of the same mTLS handler.
        serviceCollection.AddHttpClient("federation")
            .ConfigurePrimaryHttpMessageHandler(() => Services.PeerMtls.BuildHandler(allowAutoRedirect: false))
            .AddHttpMessageHandler<Services.SsrfGuardHandler>();
        // First-contact handshake client (AccessRequest/Granted/Invite). The target is not yet a
        // configured peer, so the allowlist SSRF guard would wrongly block it; instead the caller
        // gates the destination with SsrfGuard.IsSafePeerBaseUrl. Still no-redirect + mTLS so a
        // peer 3xx can't replay our Basic creds to an attacker-controlled host.
        serviceCollection.AddHttpClient("federation-direct")
            .ConfigurePrimaryHttpMessageHandler(() => Services.PeerMtls.BuildHandler(allowAutoRedirect: false));
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
