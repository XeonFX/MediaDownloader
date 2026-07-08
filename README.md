# MediaDownloader

A self-hosted torrent search-and-download manager built with **Blazor Server**, **MudBlazor**, **MonoTorrent**, and **EF Core + SQLite**. Search multiple torrent sites, download via a built-in BitTorrent engine, and let it grab new episodes of your shows automatically.

> **Legal notice:** downloading copyrighted material without permission may be illegal in your jurisdiction. This project is for downloading content you are legally entitled to (Linux ISOs, public-domain media, your own files, etc.). Use responsibly.

## Features

- **Multi-source search** across several providers in parallel, with results streamed in as each source responds:
  - **The Pirate Bay** — apibay JSON API, with automatic fallback to HTML mirrors when the primary domain is blocked
  - **1337x** — via public mirrors (top results resolved to magnets on demand)
  - **Torrents-CSV** — open torrent index
  - **Nyaa** — anime-focused RSS feed
- **Relevance filtering** so loosely-matching sources don't return unrelated junk
- **Per-source toggle** — disable any provider you don't want searched (Settings → Search sources)
- **Built-in downloader** (MonoTorrent) with live progress, speed, peers, and status; pause / resume / retry / delete
- **Manual vs. automatic downloads** — search downloads live on the Downloads page; episodes grabbed by a series task are managed from the Series Tasks page
- **Series tasks** — define a show with a start (and optional end) episode; the app periodically checks for new episodes and downloads them automatically. Understands `S01E05`, `1x05`, `Episode 5`, `Ep05`, and anime-style `Show - 05` titles
- **Configurable post-download behavior** — stop seeding immediately (default) or keep seeding
- **Metadata timeout** — a torrent that can't find peers is surfaced as an error instead of hanging on "Fetching metadata" forever
- **Notifications** on download start / finish via Email (SMTP), desktop (browser Notification API), push (ntfy), and Telegram
- **Persistent & resumable** — everything is stored in SQLite and auto-saved on every change; on restart the app reloads and resumes all downloads and series tasks

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run
```

Then open the URL printed in the console (default <http://localhost:5170>).

The SQLite database (`mediadownloader.db`) and torrent cache are created next to the executable on first run — both are gitignored. The default download folder is `~/Downloads/MediaDownloader`; change it on the Settings page.

## Project structure

```
MediaDownloader/
├── Program.cs                 # DI wiring, HTTP clients, DB bootstrap, app startup
├── appsettings.json           # logging config (no secrets)
├── Components/                 # Blazor UI
│   ├── Pages/                  #   Downloads, Search, Series, Settings, Error
│   ├── Layout/ · Dialogs/      #   shell + folder-picker/prompt dialogs
│   └── App.razor · Routes.razor · _Imports.razor
├── Data/                       # persistence
│   ├── AppDbContext.cs         #   DbContext + EnsureSchemaAsync column patching
│   └── Entities/               #   AppSettings, DownloadItem, SeriesTask
├── Services/
│   ├── ByteSize.cs             #   shared size formatting/parsing
│   ├── Downloads/              #   DownloadManager (MonoTorrent engine, hosted service)
│   ├── Series/                 #   SeriesMonitor (background episode checker) + EpisodeParser
│   ├── Torrents/               #   ITorrentSearchProvider + providers + aggregator
│   └── Notifications/          #   INotifier + channels + dispatcher
└── wwwroot/                    # static assets
```

Configuration (SMTP credentials, tokens, folders, provider toggles, etc.) is stored in the SQLite database, **not** in any committed file.

## Extending

### Add a torrent source

Implement `ITorrentSearchProvider` in `Services/Torrents/` and register it in `Program.cs`:

```csharp
public class MySiteProvider : ITorrentSearchProvider
{
    public string Name => "MySite";
    public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default) { ... }
}
```

```csharp
builder.Services.AddSingleton<ITorrentSearchProvider, MySiteProvider>();
```

It automatically appears in the Search page, the series-task source dropdown, and the per-source toggles in Settings. Use `Magnet.Build(infoHash, name)` to construct magnet links with a known-good tracker set.

### Add a notification channel

Implement `INotifier` in `Services/Notifications/` and register it the same way. `IsEnabled(AppSettings)` decides whether the channel fires; add any settings it needs to the `AppSettings` entity and the Settings page.

> **Schema note:** the app uses EF Core's `EnsureCreated()` rather than migrations, which never alters an existing table. When you add a column to an existing entity, add a matching `EnsureColumnAsync(...)` call in `AppDbContext.EnsureSchemaAsync()` so existing databases are patched on startup.

## Telegram setup

1. Message **@BotFather**, create a bot, and paste the token into Settings
2. Find your chat id (e.g. message **@userinfobot**) and enter it
3. Send `/start` to your bot once so it is allowed to message you

## Tech stack

MudBlazor 9.4 · Microsoft.EntityFrameworkCore.Sqlite 10.0 · MonoTorrent 3.0 · .NET 10

## License

[MIT](LICENSE)
