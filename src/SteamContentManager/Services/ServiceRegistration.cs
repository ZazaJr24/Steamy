using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Services;

public static class ServiceRegistration
{
    public static IServiceCollection AddSteamContentManagerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAppDataStore, AppDataStore>();
        services.AddSingleton<ILocalDatabase, SqliteLocalDatabase>();
        services.AddSingleton<IDownloadQueueStore, DownloadQueueStore>();
        services.AddSingleton<ISteamLibraryService, SteamLibraryService>();
        services.AddSingleton<ILibrarySyncService, LibrarySyncService>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ISecureCredentialService, DpapiCredentialService>();
        services.AddSingleton<ILogDispatcher, WpfLogDispatcher>();
        services.AddSingleton<ILoggingService, InMemoryLoggingService>();
        services.AddSingleton<IDepotDownloaderService, DepotDownloaderService>();
        services.AddSingleton<IDownloadManager>(_ => new DownloadManager(
            _.GetRequiredService<IAppDataStore>(),
            _.GetRequiredService<ISettingsService>(),
            _.GetRequiredService<IDepotDownloaderService>(),
            _.GetRequiredService<IFileVerificationService>(),
            _.GetRequiredService<IDownloadQueueStore>(),
            _.GetRequiredService<ILoggingService>(),
            _.GetRequiredService<INotificationService>()
        ));
        services.AddSingleton<ISteamApiHealthService, DemoSteamApiHealthService>();
        services.AddSingleton<IFileVerificationService, LocalFileVerificationService>();
        services.AddSingleton<IDnsResolverService, DnsResolverService>();
        services.AddSingleton<IEndpointProbeService>(_ => new HttpEndpointProbeService());
        services.AddSingleton<IGameLocatorService, GameLocatorService>();
        services.AddSingleton<IManifestService, LocalManifestService>();
        services.AddSingleton<IProviderHealthService, DemoProviderHealthService>();
        services.AddSingleton<IRyuuGeneratorService, DemoRyuuGeneratorService>();
        services.AddSingleton<IOnlineFixSearchService>(_ => new OnlineFixSearchService());
        services.AddSingleton<IDiskSpaceService, SystemDiskSpaceService>();
        services.AddSingleton<INotificationService, LoggingNotificationService>();
        services.AddSingleton<IArtworkService>(_ => new SteamArtworkService());
        services.AddSingleton<ISteamCatalogService>(_ => new SteamCatalogService());
        services.AddSingleton<IRyuuCatalogService>(sp => new RyuuCatalogService(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<ZazaRepositoryService>(_ => new ZazaRepositoryService());
        services.AddSingleton<IDepotDownloaderCheckService>(_ => new DepotDownloaderCheckService());

        services.AddSingleton<IGitHubToolDownloadService, GitHubToolDownloadService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<DepotsViewModel>();
        services.AddSingleton<ManifestViewModel>();
        services.AddSingleton<BranchesViewModel>();
        services.AddSingleton<AchievementsViewModel>();
        services.AddSingleton<DenuvoGenerationViewModel>();
        services.AddSingleton<HubViewModel>(sp => new HubViewModel(
                sp.GetRequiredService<IAppDataStore>(),
                sp.GetRequiredService<INavigationService>(),
                sp.GetRequiredService<ILoggingService>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IDownloadManager>(),
                sp.GetRequiredService<ISteamCatalogService>(),
            sp.GetRequiredService<IDepotDownloaderCheckService>()
            ));
        services.AddSingleton<ModFixesViewModel>();
        services.AddSingleton<OnlineFixesViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DepotDownloaderViewModel>();
        services.AddSingleton<ISteamlessService, SteamlessService>();
        services.AddSingleton<SteamlessViewModel>();
        services.AddSingleton<ILocalToolRunner, LocalToolRunnerService>();
        services.AddSingleton<DenuvoActivationViewModel>();
        services.AddSingleton<ICreamInstallerDownloadService, CreamInstallerDownloadService>();
        services.AddSingleton<IDenuvoGeneratorDownloadService, DenuvoGeneratorDownloadService>();
        services.AddSingleton<CreamInstallerViewModel>();
        services.AddSingleton<XStoreUnlockerViewModel>();
        services.AddSingleton<IGameFixProvider, DemoGameFixProvider>();
        services.AddSingleton<IRyuuFixesService>(sp => new RyuuFixesService(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<IGameFixDownloadService, GameFixDownloadService>();
        services.AddSingleton<WebView2DownloadService>();
        services.AddSingleton<IRyuuSecureDownloadService, RyuuSecureDownloadService>();
        services.AddSingleton<IManifestSourceService>(sp => new ManifestSourceService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ISecureCredentialService>(),
            sp.GetRequiredService<IRyuuSecureDownloadService>(),
            sp.GetRequiredService<ILoggingService>()));
        services.AddSingleton<IRyuuGameDownloadService>(sp => new RyuuGameDownloadService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ISecureCredentialService>(),
            sp.GetRequiredService<IRyuuSecureDownloadService>(),
            sp.GetRequiredService<IManifestSourceService>(),
            sp.GetRequiredService<ILoggingService>()));
        services.AddSingleton<GameFixesViewModel>();
        services.AddSingleton<GreenLumaViewModel>();
        services.AddSingleton<GoldbergViewModel>();
        services.AddSingleton<UnsteamViewModel>();
        services.AddSingleton<ColddloaderViewModel>();
        services.AddSingleton<ScreamApiViewModel>();
        services.AddSingleton<HvFixesViewModel>();
        services.AddSingleton<SteamAutoCrackViewModel>();

        return services;
    }
}
