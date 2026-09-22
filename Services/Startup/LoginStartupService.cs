using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaDownloader.Services.Startup;

public enum LoginStartupStatus { Unavailable, Disabled, Enabled, RequiresApproval }

/// <summary>Controls a per-user launch agent for ad-hoc signed macOS installations.</summary>
public sealed class LoginStartupService
{
    private readonly ILoginStartupBackend _backend;
    private readonly object _gate = new();

    public LoginStartupService() : this(new MacLoginStartupBackend()) { }
    internal LoginStartupService(ILoginStartupBackend backend) => _backend = backend;

    public LoginStartupStatus GetStatus()
    {
        lock (_gate) return _backend.GetStatus();
    }

    public LoginStartupStatus SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var status = _backend.GetStatus();
            if (status == LoginStartupStatus.Unavailable)
                throw new InvalidOperationException("Open the installed macOS app to change login startup.");
            if (enabled && status == LoginStartupStatus.Disabled) _backend.SetEnabled(true);
            if (!enabled && status != LoginStartupStatus.Disabled) _backend.SetEnabled(false);
            return _backend.GetStatus();
        }
    }
}

internal interface ILoginStartupBackend
{
    LoginStartupStatus GetStatus();
    void SetEnabled(bool enabled);
}

internal sealed class MacLoginStartupBackend : ILoginStartupBackend
{
    internal const string Label = "com.mediadownloader.start-at-login";
    private readonly string? _bundle;
    private readonly string _plist;
    private readonly Func<string[], string> _launchctl;
    private readonly string _domain;

    public MacLoginStartupBackend()
    {
        var executable = new DirectoryInfo(AppContext.BaseDirectory);
        if (OperatingSystem.IsMacOS() && executable.Name == "MacOS"
            && executable.Parent is { Name: "Contents", Parent: { } bundle }
            && bundle.Name.EndsWith(".app", StringComparison.Ordinal))
            _bundle = bundle.FullName;
        _plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", Label + ".plist");
        _launchctl = RunLaunchctl;
        _domain = OperatingSystem.IsMacOS() ? "gui/" + getuid() : "";
    }

    internal MacLoginStartupBackend(string bundle, string plist, Func<string[], string> launchctl)
        => (_bundle, _plist, _launchctl, _domain) = (bundle, plist, launchctl, "gui/501");

    public LoginStartupStatus GetStatus()
    {
        if (_bundle is null) return LoginStartupStatus.Unavailable;
        if (!File.Exists(_plist)) return LoginStartupStatus.Disabled;
        var overrides = _launchctl(["print-disabled", _domain]);
        return Regex.IsMatch(overrides, "\"" + Regex.Escape(Label) + "\"\\s*=>\\s*true")
            ? LoginStartupStatus.RequiresApproval : LoginStartupStatus.Enabled;
    }

    public void SetEnabled(bool enabled)
    {
        if (_bundle is null) throw new PlatformNotSupportedException();
        if (!enabled)
        {
            // Removing the registration prevents the next login launch. Keep a recoverable copy;
            // a one-shot `open` agent never owns or terminates the running application process.
            File.Move(_plist, _plist + ".disabled", overwrite: true);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        var temporary = _plist + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CreatePlist(_bundle).Save(temporary);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _launchctl(["enable", _domain + "/" + Label]);
            File.Move(temporary, _plist, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static XDocument CreatePlist(string bundle) => new(
        new XDeclaration("1.0", "UTF-8", null),
        new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
        new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", Label),
            new XElement("key", "ProgramArguments"), new XElement("array",
                new XElement("string", "/usr/bin/open"), new XElement("string", "-g"), new XElement("string", bundle)),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"))));

    private static string RunLaunchctl(string[] args)
    {
        var info = new ProcessStartInfo("/bin/launchctl")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            throw new InvalidOperationException("macOS login services did not respond.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("macOS login services: " + error.GetAwaiter().GetResult().Trim());
        return output.GetAwaiter().GetResult();
    }

    [DllImport("libc")] private static extern uint getuid();
}
