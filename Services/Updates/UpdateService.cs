using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MediaDownloader.Services.Notifications;

namespace MediaDownloader.Services.Updates;

/// <summary>A newer GitHub release than the running build.</summary>
/// <param name="AssetUrl">Direct download URL of the zip built for this OS/arch, if the release has one.</param>
/// <param name="ChecksumsUrl">
/// URL of the release's SHA256SUMS.txt, when it publishes one. Releases from before that file was
/// added to the release workflow have none.
/// </param>
public record UpdateInfo(Version Version, string TagName, string ReleaseUrl, string? AssetUrl, string? AssetName,
    string? ChecksumsUrl = null);

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
    private int _installStarted;

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

        // Pick the asset built for this OS/arch, e.g. MediaDownloader-1.1.0-osx-arm64.zip, plus the
        // release's checksum manifest so the download can be verified before it replaces this app.
        string? assetUrl = null, assetName = null, checksumsUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Equals(ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                    checksumsUrl = asset.GetProperty("browser_download_url").GetString();
                else if (assetName is null && name.Contains(PlatformRid(), StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                }
            }
        }

        var releaseUrl = root.GetProperty("html_url").GetString() ?? ReleasesPageUrl;
        Available = new UpdateInfo(latest, tag, releaseUrl, assetUrl, assetName, checksumsUrl);
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
        if (update is null || Interlocked.CompareExchange(ref _installStarted, 1, 0) != 0) return;

        var bundle = MacAppBundlePath();
        if (!OperatingSystem.IsMacOS() || bundle is null || update.AssetUrl is null)
        {
            OpenInBrowser(update.ReleaseUrl);
            Interlocked.Exchange(ref _installStarted, 0);
            return;
        }

        Installing = true;
        StateChanged?.Invoke();
        string? staging = null;
        try
        {
            staging = Directory.CreateTempSubdirectory("mediadownloader-update-").FullName;

            var zipPath = Path.Combine(staging, "update.zip");
            var http = _httpFactory.CreateClient("github-download");
            await using (var download = await http.GetStreamAsync(update.AssetUrl))
            await using (var file = File.Create(zipPath))
            {
                await download.CopyToAsync(file);
            }

            await VerifyChecksumAsync(update, zipPath);

            // ditto preserves bundle structure, symlinks and permissions (unlike ZipFile.ExtractToDirectory).
            await RunAsync("/usr/bin/ditto", ["-x", "-k", zipPath, staging]);
            var newApp = Path.Combine(staging, "MediaDownloader.app");
            if (!File.Exists(Path.Combine(newApp, "Contents", "MacOS", "MediaDownloader")))
                throw new InvalidOperationException("Downloaded bundle is missing the MediaDownloader executable");

            // Copy beside the destination and validate its signature while the old app is still
            // running. Certificate-signed installations also require Gatekeeper acceptance.
            // The helper only renames bundles after shutdown.
            var work = await MacUpdateInstaller.PrepareAsync(bundle, newApp);
            MacUpdateInstaller.Launch(bundle, work);

            _logger.LogInformation("Update {Tag} staged; shutting down for install", update.TagName);
            // Graceful host shutdown; Program.cs exits the process once the host stops.
            _lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update install failed");
            Installing = false;
            Interlocked.Exchange(ref _installStarted, 0);
            StateChanged?.Invoke();
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not clean update download staging"); }
            }
        }
    }

    /// <summary>Name of the release asset listing each zip's SHA-256, published by the release workflow.</summary>
    private const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>
    /// Checks the downloaded zip against the SHA-256 the release published for it, and refuses the
    /// install on a mismatch.
    ///
    /// This is an integrity check, not a trust anchor: it catches a truncated or corrupted download
    /// and a zip swapped out from under the release, but anyone able to replace the asset could
    /// replace the manifest alongside it. Real tamper-resistance would need the release signed with
    /// a key whose public half ships in this binary. Releases published before the workflow started
    /// emitting the manifest cannot be automatically installed. The macOS installer also requires
    /// a valid bundle signature before shutdown; certificate-signed installations additionally
    /// require Gatekeeper acceptance. Ad-hoc signatures establish integrity, not publisher identity.
    /// </summary>
    private async Task VerifyChecksumAsync(UpdateInfo update, string zipPath)
    {
        if (update.ChecksumsUrl is null)
        {
            throw new InvalidOperationException($"Release {update.TagName} has no {ChecksumsAssetName}; update refused.");
        }

        var manifest = await _httpFactory.CreateClient("github").GetStringAsync(update.ChecksumsUrl);
        var expected = FindChecksum(manifest, update.AssetName!)
            ?? throw new InvalidOperationException($"{ChecksumsAssetName} has no entry for {update.AssetName}");

        await using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Checksum mismatch for {update.AssetName} (expected {expected}, got {actual}) — update refused");

        _logger.LogInformation("Verified {Asset} against {File}", update.AssetName, ChecksumsAssetName);
    }

    /// <summary>Reads one "&lt;sha256&gt;  &lt;filename&gt;" line out of a sha256sum-style manifest.</summary>
    internal static string? FindChecksum(string manifest, string assetName)
    {
        foreach (var line in manifest.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // sha256sum marks binary mode with a "*" before the name.
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(assetName, StringComparison.Ordinal))
                return parts[0];
        }
        return null;
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

    /// <summary>
    /// Runs a helper process. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, which
    /// passes each one to the OS verbatim — no quoting or escaping of paths to get wrong.
    /// </summary>
    private static async Task RunAsync(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited with {p.ExitCode}");
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
