using System.Diagnostics;

namespace MediaDownloader.Services.Updates;

internal static class MacUpdateInstaller
{
    internal static string Script
    {
        get
        {
            using var stream = typeof(MacUpdateInstaller).Assembly.GetManifestResourceStream(
                "MediaDownloader.Resources.Updates.install-macos.sh")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    internal static async Task<string> PrepareAsync(string bundle, string sourceApp)
    {
        var parent = Path.GetDirectoryName(bundle)!;
        var work = Path.Combine(parent, $".MediaDownloader-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var script = Path.Combine(work, "install.sh");
        await File.WriteAllTextAsync(script, Script);
        using var process = Process.Start(new ProcessStartInfo("/bin/bash")
        {
            ArgumentList = { script, "prepare", bundle, sourceApp, work },
            UseShellExecute = false
        })!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Update preparation failed; the installed app is unchanged. See {work}/install.log");
        return work;
    }

    internal static void Launch(string bundle, string work)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/nohup")
        {
            ArgumentList = { "/bin/bash", Path.Combine(work, "install.sh"), "install", bundle, "",
                work, Environment.ProcessId.ToString() },
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start the update installer.");
    }
}
