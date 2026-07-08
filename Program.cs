using MediaDownloader.Components;
using MediaDownloader.Data;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Notifications;
using MediaDownloader.Services.Series;
using MediaDownloader.Services.Torrents;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// Database (SQLite next to the executable)
var dbPath = Path.Combine(AppContext.BaseDirectory, "mediadownloader.db");
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddHttpClient("torrent-search", c =>
{
    c.Timeout = TimeSpan.FromSeconds(5);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader/1.0");
});
builder.Services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));

// Torrent search providers — add new sources here.
builder.Services.AddSingleton<ITorrentSearchProvider, PirateBayProvider>();
builder.Services.AddSingleton<ITorrentSearchProvider, NyaaProvider>();
builder.Services.AddSingleton<ITorrentSearchProvider, TorrentsCsvProvider>();
builder.Services.AddSingleton<ITorrentSearchProvider, LeetxProvider>();
builder.Services.AddSingleton<TorrentSearchService>();

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
