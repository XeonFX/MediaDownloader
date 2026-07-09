using MediaDownloader.Components;
using MediaDownloader.Data;
using MediaDownloader.Services;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Localization;
using MediaDownloader.Services.Tray;
using MediaDownloader.Services.Updates;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;

// Configured before the host so startup failures (bad config, port binding, DB open) are logged
// too. Console (dev/tray-off) + a rolling daily file — the packaged macOS app has no console once
// launched from Finder/`open`, so the file is the only place logs survive to actually debug it.
// This is a "bootstrap" logger per Serilog's own two-stage pattern: builder.Host.UseSerilog()
// below replaces it with the final logger once configuration is available (needed to read
// Sentry:Dsn from appsettings.json/environment before deciding whether to add that sink).
var isDev = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development"
    || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
Log.Logger = ConfigureCommonSinks(new LoggerConfiguration(), isDev).CreateLogger();

// Console + rolling file, shared by both the bootstrap logger above and the final one built by
// UseSerilog() below (mutates and returns the same LoggerConfiguration instance it's given).
static LoggerConfiguration ConfigureCommonSinks(LoggerConfiguration config, bool isDev) => config
    .MinimumLevel.Is(isDev ? LogEventLevel.Debug : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.WithProperty("Version", UpdateService.CurrentVersionText)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppPaths.LogsDirectory, "app-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 10_000_000,
        rollOnFileSizeLimit: true);

// Catch exceptions that never make it into a try/catch anywhere else — a crashing background
// thread (e.g. inside DownloadManager's timer callback) would otherwise fail silently.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", e.IsTerminating);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error(e.Exception, "Unobserved task exception");
    e.SetObserved();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

try
{
    await RunApp(args);
}
catch (HostAbortedException)
{
    // EF Core's design-time tooling (`dotnet ef migrations add`, `database update`,
    // `migrations has-pending-model-changes`, …) launches this entry point only to resolve the
    // DbContext: it subscribes to the "HostBuilt" diagnostic event, grabs the service provider the
    // moment builder.Build() fires, then throws HostAbortedException to stop the app from actually
    // running. It's the documented, expected signal — not a crash — so swallow it rather than
    // logging Fatal (which shipped a bogus "Application terminated unexpectedly" event to Sentry
    // from every `dotnet ef` command run on a dev machine).
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    // Unreachable on macOS in tray mode: MacTrayApp.Run blocks forever and the process instead
    // exits via Environment.Exit(0) once shutdown completes, which skips finally blocks. That
    // path is covered separately by the ProcessExit handler registered above.
    Log.CloseAndFlush();
}

async Task RunApp(string[] hostArgs)
{
// Pin the content root to the app's own directory. The default is the *current working
// directory*, which is "/" when macOS launches the .app bundle via Finder/`open` — static
// assets then resolve against /wwwroot and get served as empty 200s.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    ConfigureCommonSinks(loggerConfiguration, isDev);

    // Optional: ships Error+ events to Sentry for remote crash monitoring. Empty/absent by
    // default — set Sentry:Dsn in appsettings.json or the Sentry__Dsn environment variable
    // (ASP.NET Core's double-underscore config convention) to enable. A Sentry DSN is a
    // write-only ingestion endpoint (not a secret credential
    // — Sentry's own docs say it's safe to ship in client binaries), so it's fine to bake into a
    // release build; it just shouldn't be assumed to grant any read/account access if it leaks.
    var dsn = context.Configuration["Sentry:Dsn"];
    if (!string.IsNullOrWhiteSpace(dsn))
    {
        loggerConfiguration.WriteTo.Sentry(o =>
        {
            o.Dsn = dsn;
            o.Release = UpdateService.CurrentVersionText;
            o.Environment = isDev ? "development" : "production";
            o.MinimumEventLevel = LogEventLevel.Error; // Error/Fatal become Sentry issues
            // Warning+, not Information+: Info-level logs include download/series titles and are
            // otherwise attached verbatim as breadcrumbs on every reported issue — that's real user
            // activity (and, for private trackers, an account username) leaving the machine on any
            // unrelated crash. Warning+ still gives useful context without the activity log.
            o.MinimumBreadcrumbLevel = LogEventLevel.Warning;
        });
    }
});

// Port: honour an explicit --urls/ASPNETCORE_URLS/launchSettings value; otherwise bind our
// default port, walking forward if another app already holds it (5000 is out — macOS AirPlay
// Receiver squats on it). The tray menu's Dashboard item reads the actual bound URL at runtime.
if (string.IsNullOrEmpty(builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls($"http://localhost:{FindFreePort(47820)}");
}

// AllowedHosts (appsettings.json) is restricted to localhost/127.0.0.1/[::1] rather than "*" —
// ASP.NET Core's Host Filtering middleware picks this up automatically, and since this app only
// ever binds to localhost, there's no reason to accept any other Host header (defends against
// DNS-rebinding-style attacks from a page open in the browser).

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddSecretProtection();

// Database (SQLite in the per-user data directory; next to the executable on non-macOS)
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={AppPaths.DatabasePath}"));

builder.Services.AddAppHttpClients();
builder.Services.AddTorrentSearch();

// UI localization — languages live in Resources/i18n/*.json.
builder.Services.AddSingleton<LocalizationService>();

builder.Services.AddNotificationChannels();
builder.Services.AddDownloadEngine(builder.Configuration);
builder.Services.AddSelfUpdate();
builder.Services.AddDataServices();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

// Create/upgrade the database schema (via EF Core migrations — see AppDbContext.MigrateAsync for
// the safe path from the old EnsureCreated()-based schema) and the settings row, on first run.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.MigrateAsync();
    await db.GetSettingsAsync();
}

// Restore the persisted UI language now that the settings row exists.
await app.Services.GetRequiredService<LocalizationService>().InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Safe to set unconditionally: it's a strictly local desktop app, but these cost nothing and mean
// there's a baseline of defense if this Kestrel instance is ever fronted by a proxy or otherwise
// made reachable beyond localhost.
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "same-origin");
    await next();
});

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health");

// On macOS and Windows, run as a tray/menu-bar agent: start Kestrel on background threads and
// hand the main thread to the native event loop the tray icon needs (AppKit's run loop on macOS,
// a Win32 message loop on Windows). Set MD_NO_TRAY=1 to run headless instead (used by the
// dev/preview profile). Any other OS just runs the web host normally.
if (Environment.GetEnvironmentVariable("MD_NO_TRAY") != "1" && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()))
{
    // Block (stay on the main thread) rather than await, so the native run loop gets thread 0.
    app.StartAsync().GetAwaiter().GetResult();
    // SIGTERM/Ctrl-C only *signal* shutdown — normally app.Run() notices and stops the host, but
    // here the main thread is parked in the tray's event loop, which would leave a zombie process
    // whose host never stops. Watch for the signal on a background thread, run the graceful
    // shutdown, then exit the process.
    _ = Task.Run(async () =>
    {
        await app.WaitForShutdownAsync();
        Environment.Exit(0);
    });
    var dashboardUrl = DashboardUrl(app);
    var downloadManager = app.Services.GetRequiredService<DownloadManager>();
    var updateService = app.Services.GetRequiredService<UpdateService>();
    if (OperatingSystem.IsMacOS())
        MacTrayApp.Run(app, dashboardUrl, downloadManager, updateService);
    else
        WindowsTrayApp.Run(app, dashboardUrl, downloadManager, updateService);
}
else
{
    app.Run();
}
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
