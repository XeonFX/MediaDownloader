using MediaDownloader.Services.Api;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Notifications;
using MediaDownloader.Services.Security;
using MediaDownloader.Services.Series;
using MediaDownloader.Services.Settings;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Services.Updates;
using Microsoft.AspNetCore.DataProtection;

namespace MediaDownloader.Services;

/// <summary>
/// Groups the app's DI registrations by concern, so Program.cs reads as a short list of cohesive
/// steps instead of ~90 lines of individual AddSingleton/AddHttpClient calls.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Encrypts secrets (provider passwords, SMTP password, Telegram bot token) before they hit
    /// the SQLite database. Keys live next to the DB in the app data directory so they survive
    /// restarts and travel with a backup of that folder, but only decrypt on the machine that
    /// generated them.
    /// </summary>
    public static IServiceCollection AddSecretProtection(this IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName("MediaDownloader")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppPaths.DataDirectory, "keys")));
        services.AddSingleton<SecretProtector>();
        return services;
    }

    /// <summary>
    /// Named HttpClients used across the app: a short-timeout client for scraping torrent sites
    /// (mirrors rotate fast, so failing fast matters more than patience), a longer one for
    /// outgoing notification webhooks, and two GitHub clients for the self-update checker
    /// (release metadata vs. the release zip download itself, which needs a much longer timeout).
    /// </summary>
    public static IServiceCollection AddAppHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient("torrent-search", c =>
        {
            // 5s was too tight to be a backstop: apibay.org takes ~16s on a query it hasn't cached,
            // so every such search hit the timeout. Mirror fallback is now staggered (see
            // MirrorRotator), which caps the wait on a slow host at ~1.5s regardless of this value,
            // leaving the timeout free to be a genuine last resort for a host that never answers.
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader/1.0");
        });
        services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));
        // GitHub requires a User-Agent; the download client gets a long timeout for the release zip.
        services.AddHttpClient("github", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        services.AddHttpClient("github-download", c =>
        {
            c.Timeout = TimeSpan.FromMinutes(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader");
        });
        return services;
    }

    /// <summary>
    /// Torrent search providers are discovered automatically: implement ITorrentSearchProvider
    /// and the new source shows up in Search and Settings with no registration needed here.
    /// </summary>
    public static IServiceCollection AddTorrentSearch(this IServiceCollection services)
    {
        foreach (var providerType in typeof(ITorrentSearchProvider).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITorrentSearchProvider).IsAssignableFrom(t))
                     .OrderBy(t => t.Name))
        {
            services.Add(ServiceDescriptor.Singleton(typeof(ITorrentSearchProvider), providerType));
        }
        services.AddSingleton<TorrentSearchService>();
        // Scoped, not singleton: this holds one user's current query/results. Blazor Server gives
        // each browser circuit (tab) its own scope, so a singleton here would leak one tab's search
        // state into every other tab and session connected to the app.
        services.AddScoped<SearchState>();
        services.AddSingleton<NativeFolderPicker>();
        return services;
    }

    /// <summary>
    /// Notification channels. Add a new one by implementing INotifier and registering it here —
    /// IsEnabled(AppSettings) decides whether it actually fires for a given event.
    /// </summary>
    public static IServiceCollection AddNotificationChannels(this IServiceCollection services)
    {
        services.AddSingleton<DesktopNotifier>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<DesktopNotifier>());
        services.AddSingleton<INotifier, EmailNotifier>();
        services.AddSingleton<INotifier, NtfyPushNotifier>();
        services.AddSingleton<INotifier, TelegramNotifier>();
        services.AddSingleton<NotificationDispatcher>();
        return services;
    }

    /// <summary>
    /// The MonoTorrent-backed download engine and the background series-episode scheduler that
    /// queues downloads onto it automatically. Their tunable knobs (metadata timeout, poll
    /// interval, per-check episode cap) are bound from the "DownloadEngine"/"SeriesMonitor"
    /// config sections — see DownloadEngineOptions/SeriesMonitorOptions for defaults.
    /// </summary>
    public static IServiceCollection AddDownloadEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DownloadEngineOptions>(configuration.GetSection("DownloadEngine"));
        services.AddSingleton<ITorrentEngineFactory, TorrentEngineFactory>();
        services.AddSingleton<DownloadManager>();
        services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());

        services.Configure<SeriesMonitorOptions>(configuration.GetSection("SeriesMonitor"));
        services.AddSingleton<SeriesMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<SeriesMonitor>());
        return services;
    }

    /// <summary>Polls GitHub releases and surfaces new versions in the tray menu and notifications.</summary>
    public static IServiceCollection AddSelfUpdate(this IServiceCollection services)
    {
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        return services;
    }

    /// <summary>
    /// Thin data-access services that wrap IDbContextFactory for a specific page's needs (Series
    /// Tasks, Settings), so those Razor components can focus on presentation instead of running EF
    /// queries directly.
    /// </summary>
    public static IServiceCollection AddDataServices(this IServiceCollection services)
    {
        services.AddSingleton<SeriesTaskService>();
        services.AddSingleton<AppSettingsService>();
        return services;
    }

    /// <summary>
    /// The API an LLM agent drives the app through — a REST surface under /api and an MCP server at
    /// /mcp, both thin wrappers over the same <see cref="AgentApi"/> facade so they can't diverge.
    ///
    /// Registered unconditionally; whether the endpoints actually answer is a runtime setting
    /// enforced by <see cref="AgentApiAuthMiddleware"/>, so the user can switch it on in Settings
    /// without restarting. (Binding beyond loopback does need a restart — that's fixed at startup.)
    ///
    /// The MCP server is hosted in-process rather than as a stdio subprocess because DownloadManager
    /// is a singleton owning the live MonoTorrent engine and the SQLite writer: a second process
    /// could not drive the running app, only talk to it over HTTP anyway.
    /// </summary>
    public static IServiceCollection AddAgentApi(this IServiceCollection services)
    {
        services.AddSingleton<SearchResultCache>();
        services.AddSingleton<AgentRateLimiter>();
        services.AddSingleton<AgentAccess>();
        services.AddSingleton<AgentEndpointInfo>();
        services.AddSingleton<AgentApi>();

        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        return services;
    }
}
