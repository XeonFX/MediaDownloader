using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MediaDownloader.Services.Notifications;

namespace MediaDownloader.Services.Updates;

/// <summary>A newer GitHub release than the running build.</summary>
/// <param name="AssetUrl">Direct download URL of the zip built for this OS/arch, if the release has one.</param>
public record UpdateInfo(Version Version, string TagName, string ReleaseUrl, string? AssetUrl, string? AssetName);

/// <summary>
/// Periodically checks the GitHub Releases feed for a version newer than the running build,
/// notifies once per new version through the normal notification channels, and (on macOS,
/// when running from an .app bundle) can download the release and swap itself in place.
/// </summary>
public class UpdateService : BackgroundService
{
    private const string RepoOwner = "XeonFX";
    private const string RepoName = "MediaDownloader";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpFactory;
    private readonly NotificationDispatcher _notifications;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IHostEnvironment _env;
    private readonly ILogger<UpdateService> _logger;

    private Version? _notifiedVersion;

    public UpdateService(
        IHttpClientFactory httpFactory,
        NotificationDispatcher notifications,
        IHostApplicationLifetime lifetime,
        IHostEnvironment env,
        ILogger<UpdateService> logger)
    {
        _httpFactory = httpFactory;
        _notifications = notifications;
        _lifetime = lifetime;
        _env = env;
        _logger = logger;
    }

    /// <summary>Version of the running build (the csproj <c>&lt;Version&gt;</c>, set to the tag by CI).</summary>
    public static Version CurrentVersion { get; } = ParseCurrentVersion();

    /// <summary>Display form of <see cref="CurrentVersion"/>, always three components ("1.0.0").</summary>
    public static string CurrentVersionText { get; } = Normalize(CurrentVersion).ToString(3);

    /// <summary>Set while a newer release is known; null when up to date (or never checked).</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>True while an update download/install is running.</summary>
    public bool Installing { get; private set; }

    /// <summary>True while a release check is in flight.</summary>
    public bool Checking { get; private set; }

    /// <summary>When the last check finished (periodic or manual); null before the first one.</summary>
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Why the last check failed; null when it succeeded.</summary>
    public string? LastCheckError { get; private set; }

    /// <summary>
    /// True when clicking install will actually swap the app in place (macOS .app bundle with a
    /// matching release asset) rather than just opening the release page.
    /// </summary>
    public bool CanSelfInstall =>
        OperatingSystem.IsMacOS() && MacAppBundlePath() is not null && Available?.AssetUrl is not null;

    /// <summary>Raised when <see cref="Available"/> or <see cref="Installing"/> changes.</summary>
    public event Action? StateChanged;

    public string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Don't poll GitHub (or nag about updates) from dev runs.
        if (_env.IsDevelopment()) return;

        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckNowAsync(ct);
                await Task.Delay(CheckInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs a release check (used by both the periodic loop and the manual "check now" buttons),
    /// recording <see cref="LastCheckedAt"/>/<see cref="LastCheckError"/>. Returns the update if
    /// one is available. Never throws except for cancellation.
    /// </summary>
    public async Task<UpdateInfo?> CheckNowAsync(CancellationToken ct = default)
    {
        if (Checking) return Available;
        Checking = true;
        StateChanged?.Invoke();
        try
        {
            await CheckAsync(ct);
            LastCheckError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastCheckError = ex.Message;
            _logger.LogWarning(ex, "Update check failed");
        }
        finally
        {
            Checking = false;
            LastCheckedAt = DateTimeOffset.Now;
            StateChanged?.Invoke();
        }
        return Available;
    }

    /// <summary>Queries the latest GitHub release and updates <see cref="Available"/>.</summary>
    private async Task CheckAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("github");
        using var response = await http.GetAsync(
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return; // no releases yet
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
        {
            _logger.LogWarning("Unparseable release tag {Tag}", tag);
            return;
        }

        if (Normalize(latest) <= Normalize(CurrentVersion))
        {
            if (Available is not null) { Available = null; StateChanged?.Invoke(); }
            return;
        }

        // Pick the asset built for this OS/arch, e.g. MediaDownloader-1.1.0-osx-arm64.zip.
        string? assetUrl = null, assetName = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains(PlatformRid(), StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        var releaseUrl = root.GetProperty("html_url").GetString() ?? ReleasesPageUrl;
        Available = new UpdateInfo(latest, tag, releaseUrl, assetUrl, assetName);
        StateChanged?.Invoke();
        _logger.LogInformation("Update available: {Tag} (running {Current})", tag, CurrentVersion);

        if (_notifiedVersion != latest)
        {
            _notifiedVersion = latest;
            await _notifications.DispatchAsync(new NotificationEvent(
                NotificationKind.UpdateAvailable,
                $"MediaDownloader {tag} is available",
                $"You are running {CurrentVersionText}. Open the menu-bar icon and choose " +
                $"\"Update to {tag}\" to install, or download it from {releaseUrl}"), ct);
        }
    }

    /// <summary>
    /// Installs <see cref="Available"/>: on macOS with an .app bundle, downloads the zip, extracts
    /// it, and hands off to a script that replaces the bundle and relaunches after this process
    /// exits. Anywhere else (dev runs, Windows), opens the release page in the browser instead.
    /// </summary>
    public async Task InstallAsync()
    {
        var update = Available;
        if (update is null || Installing) return;

        var bundle = MacAppBundlePath();
        if (!OperatingSystem.IsMacOS() || bundle is null || update.AssetUrl is null)
        {
            OpenInBrowser(update.ReleaseUrl);
            return;
        }

        Installing = true;
        StateChanged?.Invoke();
        try
        {
            var staging = Path.Combine(Path.GetTempPath(), $"mediadownloader-update-{update.Version}");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var zipPath = Path.Combine(staging, update.AssetName!);
            var http = _httpFactory.CreateClient("github-download");
            await using (var download = await http.GetStreamAsync(update.AssetUrl))
            await using (var file = File.Create(zipPath))
            {
                await download.CopyToAsync(file);
            }

            // ditto preserves bundle structure, symlinks and permissions (unlike ZipFile.ExtractToDirectory).
            await RunAsync("/usr/bin/ditto", $"-x -k \"{zipPath}\" \"{staging}\"");
            var newApp = Directory.GetDirectories(staging, "*.app", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"{update.AssetName} does not contain an .app bundle");
            if (!File.Exists(Path.Combine(newApp, "Contents", "MacOS", "MediaDownloader")))
                throw new InvalidOperationException("Downloaded bundle is missing the MediaDownloader executable");

            // The running bundle can't replace itself; a detached script waits for this process
            // to exit, swaps the bundle, and relaunches the new version.
            var script = Path.Combine(staging, "install.sh");
            await File.WriteAllTextAsync(script, $"""
                #!/bin/bash
                for i in $(seq 1 120); do kill -0 {Environment.ProcessId} 2>/dev/null || break; sleep 0.5; done
                rm -rf "{bundle}"
                /usr/bin/ditto "{newApp}" "{bundle}"
                /usr/bin/xattr -dr com.apple.quarantine "{bundle}" 2>/dev/null
                open "{bundle}"
                rm -rf "{staging}"
                """);
            await RunAsync("/bin/chmod", $"+x \"{script}\"");
            Process.Start(new ProcessStartInfo("/usr/bin/nohup", $"\"{script}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            });

            _logger.LogInformation("Update {Tag} staged; shutting down for install", update.TagName);
            // Graceful host shutdown; Program.cs exits the process once the host stops.
            _lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update install failed");
            Installing = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Path of the enclosing .app bundle, or null when not running from one.</summary>
    private static string? MacAppBundlePath()
    {
        // BaseDirectory inside a bundle is <App>.app/Contents/MacOS/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (d.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return d.FullName;
        }
        return null;
    }

    private static string PlatformRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        return $"linux-{arch}";
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static async Task RunAsync(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {args} exited with {p.ExitCode}");
    }

    private static Version ParseCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // InformationalVersion may carry a "+<commit>" suffix.
        var plain = info?.Split('+')[0];
        if (Version.TryParse(plain, out var v)) return v;
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    }

    /// <summary>Pads to 3 components so 1.1 == 1.1.0 compares equal.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
