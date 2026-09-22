using System.Diagnostics;
using FluentAssertions;
using MediaDownloader.Services.Updates;

namespace MediaDownloader.Tests.Services.Updates;

public class MacUpdateInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("md-update-tests-").FullName;

    [Theory]
    [InlineData("copy")]
    [InlineData("signature")]
    [InlineData("gatekeeper")]
    public async Task PreparationFailureLeavesInstalledAppUntouched(string failure)
    {
        if (OperatingSystem.IsWindows()) return; // The installer is a macOS bash program.
        var (bundle, source, work, script) = Setup(failure);
        (await Run(script, "prepare", bundle, source, work)).Should().NotBe(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("old");
        Directory.Exists(Path.Combine(work, "Previous.app")).Should().BeFalse();
    }

    [Fact]
    public async Task AdHocInstallationAcceptsValidUpdatesWithoutAppleNotarization()
    {
        if (OperatingSystem.IsWindows()) return;
        // Gatekeeper would reject ad-hoc code; the installed app's signing mode decides whether
        // notarization is required. Integrity and application-identifier checks still run.
        var (bundle, source, work, script) = Setup("gatekeeper", adHoc: true);
        (await Run(script, "prepare", bundle, source, work)).Should().Be(0);
        (await Run(script, "install", bundle, source, work, "2147483647")).Should().Be(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("new");
        File.ReadAllText(Path.Combine(work, "Previous.app", "version")).Should().Be("old");
    }

    [Fact]
    public async Task AdHocInstallationStillRejectsAnInvalidSignature()
    {
        if (OperatingSystem.IsWindows()) return;
        var (bundle, source, work, script) = Setup("signature", adHoc: true);
        (await Run(script, "prepare", bundle, source, work)).Should().NotBe(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("old");
    }

    [Theory]
    [InlineData("promote")]
    [InlineData("open")]
    public async Task InstallFailureRestoresThePreviousApp(string failure)
    {
        if (OperatingSystem.IsWindows()) return;
        var (bundle, source, work, script) = Setup(failure);
        (await Run(script, "prepare", bundle, source, work)).Should().Be(0);
        (await Run(script, "install", bundle, source, work, "2147483647")).Should().NotBe(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("old");
        File.ReadAllText(Path.Combine(work, "install.log")).Should().Contain("restored");
    }

    [Fact]
    public async Task SuccessInstallsNewAppAndKeepsBackupEvenWithShellCharactersInPath()
    {
        if (OperatingSystem.IsWindows()) return;
        var (bundle, source, work, script) = Setup("");
        (await Run(script, "prepare", bundle, source, work)).Should().Be(0);
        (await Run(script, "install", bundle, source, work, "2147483647")).Should().Be(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("new");
        File.ReadAllText(Path.Combine(work, "Previous.app", "version")).Should().Be("old");
    }

    [Fact]
    public async Task AStillRunningAppIsNeverReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        var (bundle, source, work, script) = Setup("");
        (await Run(script, "prepare", bundle, source, work)).Should().Be(0);
        (await Run(script, "install", bundle, source, work, Environment.ProcessId.ToString())).Should().NotBe(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("old");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidStagingNeverMovesTheInstalledApp(bool existingBackup)
    {
        if (OperatingSystem.IsWindows()) return;
        var (bundle, source, work, script) = Setup("");
        if (existingBackup)
        {
            (await Run(script, "prepare", bundle, source, work)).Should().Be(0);
            Directory.CreateDirectory(Path.Combine(work, "Previous.app"));
            File.WriteAllText(Path.Combine(work, "Previous.app", "marker"), "keep backup");
        }
        (await Run(script, "install", bundle, source, work, "2147483647")).Should().NotBe(0);
        File.ReadAllText(Path.Combine(bundle, "version")).Should().Be("old");
        if (existingBackup)
            File.ReadAllText(Path.Combine(work, "Previous.app", "marker")).Should().Be("keep backup");
    }

    private (string Bundle, string Source, string Work, string Script) Setup(string failure, bool adHoc = false)
    {
        var bundle = Directory.CreateDirectory(Path.Combine(_root, "App $dollar `literal` (space).app")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(_root, "source.app")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        File.WriteAllText(Path.Combine(bundle, "version"), "old");
        File.WriteAllText(Path.Combine(source, "version"), "new");

        // Exercise the production shell's real copy/rename/rollback flow in a temporary directory.
        // Only OS signing/launch tools and the wait delay are replaced; no test switches ship in it.
        var script = MacUpdateInstaller.Script
            .Replace("/usr/bin/ditto", Tool("copy", failure == "copy" ? "exit 42" : "/bin/cp -R \"$1\" \"$2\""))
            .Replace("/usr/bin/codesign", Tool("sign", adHoc
                ? "if [ \"$1\" = --display ]; then echo 'Signature=adhoc' >&2; exit 0; fi\n" +
                  (failure == "signature" ? "exit 42" : "exit 0")
                : failure == "signature" ? "exit 42" : "exit 0"))
            .Replace("/usr/sbin/spctl", Tool("assess", failure == "gatekeeper" ? "exit 42" : "exit 0"))
            .Replace("/usr/bin/open", Tool("open", failure == "open"
                ? "[ \"$(cat \"$1/version\")\" != new ]" : "exit 0"))
            .Replace("/bin/sleep", Tool("sleep", "exit 0"));
        if (failure == "promote")
            script = script.Replace("/bin/mv", Tool("move", "case \"$1\" in */Replacement.app) exit 42 ;; esac\n/bin/mv \"$@\""));
        var path = Path.Combine(work, "install.sh");
        File.WriteAllText(path, script);
        return (bundle, source, work, path);
    }

    private string Tool(string name, string body)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "#!/bin/bash\nset -eu\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return "'" + path.Replace("'", "'\\''") + "'";
    }

    private static async Task<int> Run(string script, params string[] args)
    {
        var info = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        info.ArgumentList.Add(script);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return process.ExitCode;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
