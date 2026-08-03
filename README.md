# MediaDownloader

A self-hosted torrent search-and-download manager built with **Blazor Server**, **MudBlazor**, **MonoTorrent**, and **EF Core + SQLite**. Search multiple torrent sites, download via a built-in BitTorrent engine, and let it grab new episodes of your shows automatically.

> **Legal notice:** downloading copyrighted material without permission may be illegal in your jurisdiction. This project is for downloading content you are legally entitled to (Linux ISOs, public-domain media, your own files, etc.). Use responsibly.

## Features

- **Multi-source search** across seven providers in parallel, with results streamed in as each source responds:
  - **The Pirate Bay** — apibay JSON API, with automatic fallback to HTML mirrors when the primary domain is blocked
  - **1337x** — via public mirrors (magnet + description resolved lazily, on demand)
  - **RARBG** — via TheRARBG's JSON API, the archive that succeeded RARBG (only the description is resolved lazily; magnets come straight from the listing)
  - **Torrents-CSV** — open torrent index aggregating several sources
  - **Nyaa** — anime-focused site search, sorted server-side by seeders
  - **EZTV** — TV-only tracker (best-effort: its API has no keyword filter, so recent releases are paged through and filtered client-side)
  - **PTE** — a private tracker requiring a saved account; serves `.torrent` files instead of magnets
- **Relevance filtering** so loosely-matching sources don't return unrelated junk
- **Per-source result counts** shown above the results — each source reports how many rows it returned, how many the relevance filter dropped, and whether it failed outright, so a broken scraper or a blocked site can't hide behind an empty results list
- **Per-source toggle** — disable any provider you don't want searched (Settings → Search sources)
- **Per-provider credentials** for sources that require an account (e.g. PTE), stored encrypted alongside the source's toggle in Settings
- **Built-in downloader** (MonoTorrent) with live progress, speed, peers, and status; pause / resume / retry / delete
- **Manual vs. automatic downloads** — search downloads live on the Downloads page; episodes grabbed by a series task are managed from the Series Tasks page
- **Series tasks** — define a show with a start (and optional end) episode; the app periodically checks for new episodes and downloads them automatically. Understands `S01E05`, `1x05`, `Episode 5`, `Ep05`, and anime-style `Show - 05` titles
- **Configurable post-download behavior** — stop seeding immediately (default) or keep seeding
- **Metadata timeout** — a torrent that can't find peers is surfaced as an error instead of hanging on "Fetching metadata" forever
- **Notifications** on download start / finish via Email (SMTP, via MailKit), desktop (browser Notification API), push (ntfy), and Telegram
- **UI localization** — English, Polish, German, French, Spanish, Italian, Portuguese, and Russian, switchable live from Settings
- **Secrets encrypted at rest** — provider account passwords, the SMTP password, and the Telegram bot token are encrypted in the database via ASP.NET Core Data Protection
- **Self-updating** — checks GitHub Releases periodically; macOS can download and swap the `.app` bundle in place, other platforms open the release page
- **System tray / menu bar integration** on both macOS and Windows — shows active downloads and total speed, with quick access to the dashboard and update status
- **Persistent & resumable** — everything is stored in SQLite (via real EF Core migrations) and auto-saved on every change; on restart the app reloads and resumes all downloads and series tasks
- **Structured logging** (Serilog: console + rolling daily file) with optional Sentry crash reporting, off by default in development

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run
```

Then open the URL printed in the console (default <http://localhost:5170> in dev). Outside dev the app picks port **47820**, walking forward automatically if it's taken.

On macOS the SQLite database and torrent cache live in `~/Library/Application Support/MediaDownloader/`; on other platforms they are created next to the executable. The default download folder is `~/Downloads/MediaDownloader`; change it on the Settings page.

### Tests

```bash
dotnet test
```

Unit tests (xUnit + FluentAssertions) live in `MediaDownloader.Tests/`, covering the pure-logic pieces (episode parsing, search relevance/dedup, magnet link building, byte-size formatting, provider HTML/JSON parsing via fixtures), the secret-encryption round trip, and the legacy-database migration path. They never touch the network.

Provider parsers are additionally checked against full, unedited pages captured from each live site (`Fixtures/live-*`), asserting the invariants a healthy parse must hold — plausible row counts, titles that look like release names, sizes and peer counts that parsed, UTC dates.

#### Live provider checks

```bash
MD_LIVE_TESTS=1 dotnet test --filter Category=Live
```

Hits the real sites and fails if a provider returns nothing or unparseable rows. Skipped unless `MD_LIVE_TESTS=1`, and run weekly by the `Provider health` workflow — fixtures can only prove the parsers still handle the markup as captured, so this is what catches a site changing its HTML or starting to block us.

### macOS menu-bar app

```bash
./build-macos-app.sh
open dist/MediaDownloader.app
```

Builds a self-contained `dist/MediaDownloader.app` that lives in the menu bar (no Dock icon): the icon shows the live download speed, and its menu lists active downloads, opens the dashboard in the browser, shows update status, and quits the app. Pass `--rid osx-arm64` / `--rid osx-x64` (default matches the host) to target a specific architecture. Icon assets are generated by `Assets/makeicon.swift` (`swift Assets/makeicon.swift <outdir>`, then `iconutil -c icns` — see the script).

### Windows build

```powershell
./build-windows-app.ps1
```

Produces `dist/MediaDownloader-<version>-win-<rid>.zip` (`-Rid win-x64` or `win-arm64`, default `win-x64`). Unzip anywhere, run `MediaDownloader.exe`, and open the dashboard URL it prints (default <http://localhost:47820>). Windows also gets a system-tray icon with the same menu as the macOS build (active downloads, dashboard shortcut, update status, quit).

Set the `MD_NO_TRAY=1` environment variable on either platform to run headless (plain web server, no tray/menu-bar icon) — this is what the dev/preview launch profile uses.

## Updates & releases

The app checks the [GitHub releases](https://github.com/XeonFX/MediaDownloader/releases) of this repo every 6 hours (`Services/Updates/UpdateService.cs`, skipped in dev). When a newer version is found it sends a notification through the enabled channels, and the tray/menu-bar menu gains an **"Update to vX.Y.Z"** item. On macOS (from a `.app` bundle), clicking it downloads the matching `osx-<arch>` zip, replaces the bundle, and relaunches; everywhere else (Windows, dev runs) the item/notification links to the release page instead.

Publishing a release:

1. Bump `<Version>` in `MediaDownloader.csproj` and commit (keeps local builds in sync with the tag).
2. Tag and push: `git tag v1.1.0 && git push origin main v1.1.0`
3. The `Release` GitHub Actions workflow (`.github/workflows/release.yml`) builds `MediaDownloader-<version>-osx-arm64.zip`, `-osx-x64.zip`, `-win-x64.zip`, and `-win-arm64.zip`, stamped with the tag version, and attaches them to an auto-generated GitHub Release.

The updater matches assets by the RID in the file name, so keep the `MediaDownloader-<version>-<rid>.zip` naming if you rename anything.

## Agent access (MCP + REST)

MediaDownloader can be controlled by AI agents and scripts without clicking through the UI. Enable
**Agent access** in Settings, then connect an MCP client to the URL shown there:

```bash
claude mcp add --transport http mediadownloader http://localhost:47820/mcp
```

The port starts at `47820` and moves upward when occupied. The resolved URLs and bearer token are
written to `endpoint.json` in the app data directory (`~/Library/Application Support/MediaDownloader`
on macOS and `%LOCALAPPDATA%\MediaDownloader` on Windows); the file is written atomically and locked
to the current user with mode `0600` or an explicit Windows ACL. The Settings page always shows the
resolved local MCP URL and a copy-ready command.

REST lives under `/api`, with its OpenAPI document at `/openapi/v1.json`. For example:

```bash
curl -s -X POST http://localhost:47820/api/search \
  -H 'content-type: application/json' \
  -d '{"query":"ubuntu"}'
```

Loopback requests need no token. Access from another device is off by default. Enabling it is an
authorization gate; MediaDownloader deliberately does **not** open a plaintext LAN listener because
that would expose the bearer token. Put a TLS reverse proxy on the same machine in front of the
loopback URL (Caddy's `tls internal` plus `reverse_proxy 127.0.0.1:47820` is one option), or configure
Kestrel explicitly with an HTTPS listener and certificate, then restart. Forwarded client/protocol
headers are accepted only from a one-hop loopback proxy. Remote requests must use HTTPS and send
`Authorization: Bearer <token>`; direct remote HTTP receives `426 Upgrade Required`.

Only `/api`, `/mcp`, and `/openapi` are reachable remotely. The Blazor UI, Settings, static assets,
health endpoint, and circuit are hidden from non-loopback clients. Cross-origin browser requests are
rejected even with a token. Agent access includes torrent search, download lifecycle operations, and
series-task CRUD/checks. Settings are read-only, and deleting a download keeps its files unless
`deleteFiles: true` is explicitly supplied. Regenerating the token asks for confirmation because it
immediately invalidates existing remote clients.

Three further limits apply specifically because an agent chooses its arguments after reading titles
and descriptions fetched from torrent sites — untrusted text that can carry instructions:

- **Save folders are confined to the download folder.** `start_download`'s `folder` and a series
  task's `downloadFolder` must resolve inside it, symlinks included. Otherwise a chosen folder plus
  a chosen torrent would write attacker-named files anywhere you can write. To save elsewhere,
  change the download folder in Settings.
- **Search is rate limited** (a burst of 10, refilling one per 3s) across MCP and REST alike. One
  search queries every enabled source, and sustained bursts are what gets a client blocked by these
  sites. Exceeding it returns `429` with a wait hint rather than blocking.
- **`update_series_task` only changes fields you pass.** Anything omitted keeps its current value,
  so renaming a rule cannot silently reset its season, start episode, or interval — or re-enable one
  you had switched off.

Over REST the same rule is split across two verbs: `PATCH /api/series/{id}` changes the fields you
send, while `PUT /api/series/{id}` replaces the whole rule and therefore *requires* every field —
a partial `PUT` is rejected with a `400` naming what was missing, rather than quietly resetting it.
Use `PUT` when you need to clear a field back to empty, which `PATCH` cannot express (its nulls mean
"leave alone").

## Project structure

```
MediaDownloader/
├── Program.cs                  # DI wiring, HTTP clients, DB migration, logging bootstrap, app startup
├── appsettings.json             # non-secret config: Sentry DSN, download-engine/series-monitor tuning
├── Components/                  # Blazor UI
│   ├── Pages/                   #   Downloads, Search, Series, Settings, Error
│   ├── Layout/ · Dialogs/       #   shell + folder-picker/prompt dialogs
│   ├── Shared/                  #   DownloadActions (shared row-action buttons)
│   ├── LocalizedComponentBase.cs #  base class wiring live language-switch re-renders
│   └── App.razor · Routes.razor · _Imports.razor
├── Data/                        # persistence
│   ├── AppDbContext.cs          #   DbContext + MigrateAsync (EF Core migrations, incl. legacy-DB adoption)
│   ├── Migrations/              #   EF Core migrations (dotnet ef migrations add ...)
│   └── Entities/                #   AppSettings, DownloadItem, SeriesTask, ProviderCredential
├── Services/
│   ├── ByteSize.cs, AppPaths.cs, FireAndForget.cs, DatabaseHealthCheck.cs, NativeFolderPicker.cs
│   ├── Downloads/               #   DownloadManager (MonoTorrent engine, hosted service)
│   ├── Series/                  #   SeriesMonitor (background episode checker), EpisodeParser, SeriesTaskService
│   ├── Torrents/                #   ITorrentSearchProvider + providers + aggregator (auto-discovered)
│   ├── Notifications/           #   INotifier + channels + dispatcher
│   ├── Security/                #   SecretProtector (encrypts secrets at rest)
│   ├── Api/                     #   shared agent facade + REST, MCP tools, auth, result handles
│   ├── Settings/                #   AppSettingsService (data-access behind the Settings page)
│   ├── Tray/                    #   macOS/Windows system-tray integration (shared menu model + actions)
│   ├── Localization/            #   LocalizationService (Resources/i18n/*.json)
│   └── Updates/                 #   UpdateService (GitHub release check + self-update)
├── Resources/i18n/              # en, pl, de, fr, es, it, pt, ru translations
├── MediaDownloader.Tests/       # xUnit + FluentAssertions unit tests (+ HTML/JSON fixtures)
└── wwwroot/                     # static assets (favicon, app.js for theme/notifications)
```

Configuration (SMTP credentials, tokens, folders, provider toggles, etc.) is stored in the SQLite database, **not** in any committed file. Secrets (provider account passwords, SMTP password, Telegram bot token) are encrypted at rest via `Services/Security/SecretProtector.cs`. The database schema is managed with real EF Core migrations (`Data/Migrations/`); `AppDbContext.MigrateAsync()` also handles adopting a pre-migrations database from older installs.

## Extending

### Add a torrent source

Implement `ITorrentSearchProvider` in `Services/Torrents/` — no registration needed. `Program.cs` scans the assembly on startup and registers every non-abstract class implementing the interface as a singleton automatically:

```csharp
public class MySiteProvider : ITorrentSearchProvider
{
    public string Name => "MySite";
    public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default) { ... }
}
```

It automatically appears in the Search page, the series-task source dropdown, and the per-source toggles in Settings. Use `Magnet.Build(infoHash, name)` to construct magnet links with a known-good tracker set. Implement `ITorrentDetailsProvider` if the search listing doesn't carry a magnet/description and needs a lazy per-result detail fetch, or `ITorrentFileSource` if the site serves `.torrent` files instead of magnets (private trackers). Set `RequiresCredentials => true` if the source needs a saved account (`Settings` grows a username/password pair for it automatically, and searches skip it until credentials are saved).

### Add a notification channel

Implement `INotifier` in `Services/Notifications/` and register it in `ServiceCollectionExtensions.AddNotificationChannels`. `IsEnabled(AppSettings)` decides whether the channel fires; add any settings it needs to the `AppSettings` entity and the Settings page.

> **Schema note:** the app uses real EF Core migrations. After changing an entity, run `dotnet ef migrations add <Name> -o Data/Migrations --context AppDbContext` to generate the migration (requires the `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`), and `dotnet ef migrations has-pending-model-changes` to confirm the model and migrations are in sync before committing. `AppDbContext.MigrateAsync()` applies pending migrations on startup — no manual patching needed.

## Telegram setup

1. Message **@BotFather**, create a bot, and paste the token into Settings
2. Find your chat id (e.g. message **@userinfobot**) and enter it
3. Send `/start` to your bot once so it is allowed to message you

## Tech stack

.NET 10 · MudBlazor 9.4 · Microsoft.EntityFrameworkCore.Sqlite 10.0 · ModelContextProtocol 2.0 · MonoTorrent 3.0 · AngleSharp 1.5 (HTML parsing) · MailKit 4.17 (SMTP) · Serilog.AspNetCore + Serilog.Sinks.File (logging) · Sentry.Serilog (optional crash reporting) · xUnit + FluentAssertions (tests)

## License

[MIT](LICENSE)
