using MediaDownloader.Components;
using MediaDownloader.Data;
using MediaDownloader.Services;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Localization;
using MediaDownloader.Services.Notifications;
using MediaDownloader.Services.Series;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Services.Tray;
using MediaDownloader.Services.Updates;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// Pin the content root to the app's own directory. The default is the *current working
// directory*, which is "/" when macOS launches the .app bundle via Finder/`open` — static
// assets then resolve against /wwwroot and get served as empty 200s.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Port: honour an explicit --urls/ASPNETCORE_URLS/launchSettings value; otherwise bind our
// default port, walking forward if another app already holds it (5000 is out — macOS AirPlay
// Receiver squats on it). The tray menu's Dashboard item reads the actual bound URL at runtime.
if (string.IsNullOrEmpty(builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls($"http://localhost:{FindFreePort(47820)}");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// Database (SQLite in the per-user data directory; next to the executable on non-macOS)
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={AppPaths.DatabasePath}"));

builder.Services.AddHttpClient("torrent-search", c =>
{
    c.Timeout = TimeSpan.FromSeconds(5);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader/1.0");
});
builder.Services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));
// GitHub requires a User-Agent; the download client gets a long timeout for the release zip.
builder.Services.AddHttpClient("github", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddHttpClient("github-download", c =>
{
    c.Timeout = TimeSpan.FromMinutes(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader");
});

// Torrent search providers — discovered automatically: implement ITorrentSearchProvider and the
// new source shows up in Search and Settings without any registration here.
foreach (var providerType in typeof(ITorrentSearchProvider).Assembly.GetTypes()
             .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITorrentSearchProvider).IsAssignableFrom(t))
             .OrderBy(t => t.Name))
{
    builder.Services.Add(ServiceDescriptor.Singleton(typeof(ITorrentSearchProvider), providerType));
}
builder.Services.AddSingleton<TorrentSearchService>();
builder.Services.AddSingleton<SearchState>();
builder.Services.AddSingleton<NativeFolderPicker>();

// UI localization — languages live in Resources/i18n/*.json.
builder.Services.AddSingleton<LocalizationService>();

// Notification channels — add new channels here.
builder.Services.AddSingleton<DesktopNotifier>();
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<DesktopNotifier>());
builder.Services.AddSingleton<INotifier, EmailNotifier>();
builder.Services.AddSingleton<INotifier, NtfyPushNotifier>();
builder.Services.AddSingleton<INotifier, TelegramNotifier>();
builder.Services.AddSingleton<NotificationDispatcher>();

// Download engine + series scheduler
builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());
builder.Services.AddSingleton<SeriesMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SeriesMonitor>());

// Self-update: polls GitHub releases, surfaces new versions in the tray menu and notifications.
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

var app = builder.Build();

// Create database and settings row on first run.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await db.EnsureSchemaAsync();
    await db.GetSettingsAsync();
}

// Restore the persisted UI language now that the settings row exists.
await app.Services.GetRequiredService<LocalizationService>().InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// On macOS, run as a menu-bar agent: start Kestrel on background threads and hand the main thread
// to AppKit's run loop (required for the NSStatusItem). Set MD_NO_TRAY=1 to run headless instead
// (used by the dev/preview profile). Any other OS just runs the web host normally.
if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("MD_NO_TRAY") != "1")
{
    // Block (stay on the main thread) rather than await, so AppKit gets thread 0.
    app.StartAsync().GetAwaiter().GetResult();
    // SIGTERM/Ctrl-C only *signal* shutdown — normally app.Run() notices and stops the host,
    // but here the main thread is parked in the AppKit run loop, which would leave a zombie
    // menu-bar app whose host never stops. Watch for the signal on a background thread, run
    // the graceful shutdown, then exit the process.
    _ = Task.Run(async () =>
    {
        await app.WaitForShutdownAsync();
        Environment.Exit(0);
    });
    MacTrayApp.Run(app, DashboardUrl(app), app.Services.GetRequiredService<DownloadManager>(),
        app.Services.GetRequiredService<UpdateService>());
}
else
{
    app.Run();
}

static string DashboardUrl(WebApplication app)
{
    var url = app.Urls.FirstOrDefault() ?? "http://localhost:47820";
    // A wildcard/any-address bind isn't browsable; point the menu item at localhost.
    return url.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").Replace("//+:", "//localhost:");
}

// Returns the preferred port if free, else the first free port after it. Binding (rather than
// connecting) is the reliable probe: a port can be taken without anything accepting connections.
static int FindFreePort(int preferred)
{
    for (var port = preferred; port < preferred + 50; port++)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return port;
        }
        catch (System.Net.Sockets.SocketException)
        {
            // taken — try the next one
        }
    }
    return 0; // let the OS pick; Kestrel resolves the real port before app.Urls is read
}
