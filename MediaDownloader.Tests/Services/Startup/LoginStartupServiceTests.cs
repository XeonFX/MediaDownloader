using FluentAssertions;
using MediaDownloader.Services.Startup;

namespace MediaDownloader.Tests.Services.Startup;

public class LoginStartupServiceTests
{
    [Fact]
    public void LaunchAgentPreservesPathsAndCanBeDisabledWithoutStoppingTheApp()
    {
        var root = Directory.CreateTempSubdirectory("md-startup-");
        try
        {
            var path = Path.Combine(root.FullName, "startup.plist");
            var calls = new List<string>();
            var backend = new MacLoginStartupBackend("/Applications/App & ' Test.app", path, args =>
            { calls.Add(string.Join(" ", args)); return ""; });
            backend.SetEnabled(true);
            var document = System.Xml.Linq.XDocument.Load(path);
            document.Descendants("array").Single().Elements().Select(e => e.Value).Should()
                .Equal("/usr/bin/open", "-g", "/Applications/App & ' Test.app");
            backend.GetStatus().Should().Be(LoginStartupStatus.Enabled);
            backend.SetEnabled(false);
            backend.GetStatus().Should().Be(LoginStartupStatus.Disabled);
            File.Exists(path + ".disabled").Should().BeTrue();
            calls.Should().NotContain(c => c.Contains("bootout") || c.Contains("kill"));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void FailedRegistrationLeavesNoStartupItem()
    {
        var root = Directory.CreateTempSubdirectory("md-startup-");
        try
        {
            var path = Path.Combine(root.FullName, "startup.plist");
            var backend = new MacLoginStartupBackend("/Applications/App.app", path,
                _ => throw new InvalidOperationException("launchctl failed"));
            var change = () => backend.SetEnabled(true);
            change.Should().Throw<InvalidOperationException>();
            Directory.GetFiles(root.FullName).Should().BeEmpty();
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void LaunchdDisabledOverrideIsVisible()
    {
        var root = Directory.CreateTempSubdirectory("md-startup-");
        try
        {
            var path = Path.Combine(root.FullName, "startup.plist");
            File.WriteAllText(path, "registered");
            var backend = new MacLoginStartupBackend("/Applications/App.app", path,
                _ => "disabled services = {\n\t\"com.mediadownloader.start-at-login\" => true\n}");
            backend.GetStatus().Should().Be(LoginStartupStatus.RequiresApproval);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void ReadsExternalChangesInsteadOfPersistingASecondPreference()
    {
        var backend = new Backend();
        var service = new LoginStartupService(backend);
        service.SetEnabled(true).Should().Be(LoginStartupStatus.Enabled);
        backend.Status = LoginStartupStatus.Disabled;
        service.GetStatus().Should().Be(LoginStartupStatus.Disabled);
    }

    [Theory]
    [InlineData(LoginStartupStatus.Enabled, true)]
    [InlineData(LoginStartupStatus.Disabled, false)]
    [InlineData(LoginStartupStatus.RequiresApproval, true)]
    public void RepeatedRequestsDoNotRegisterAgain(LoginStartupStatus status, bool enabled)
    {
        var backend = new Backend { Status = status };
        new LoginStartupService(backend).SetEnabled(enabled).Should().Be(status);
        backend.Changes.Should().Be(0);
    }

    [Fact]
    public void ApprovalRequiredIsNotReportedAsEnabledAndCanBeCancelled()
    {
        var backend = new Backend { NeedsApproval = true };
        var service = new LoginStartupService(backend);
        service.SetEnabled(true).Should().Be(LoginStartupStatus.RequiresApproval);
        service.SetEnabled(false).Should().Be(LoginStartupStatus.Disabled);
    }

    [Fact]
    public void NativeFailurePropagatesAndDoesNotChangePreference()
    {
        var backend = new Backend { Fail = true };
        var service = new LoginStartupService(backend);
        var change = () => service.SetEnabled(true);
        change.Should().Throw<InvalidOperationException>().WithMessage("Denied by macOS");
        service.GetStatus().Should().Be(LoginStartupStatus.Disabled);
    }

    [Fact]
    public void UnsupportedRunsCannotRegister()
    {
        var backend = new Backend { Status = LoginStartupStatus.Unavailable };
        var change = () => new LoginStartupService(backend).SetEnabled(true);
        change.Should().Throw<InvalidOperationException>();
        backend.Changes.Should().Be(0);
    }

    private sealed class Backend : ILoginStartupBackend
    {
        public LoginStartupStatus Status = LoginStartupStatus.Disabled;
        public bool NeedsApproval;
        public bool Fail;
        public int Changes;
        public LoginStartupStatus GetStatus() => Status;
        public void SetEnabled(bool enabled)
        {
            if (Fail) throw new InvalidOperationException("Denied by macOS");
            Changes++;
            Status = enabled ? NeedsApproval ? LoginStartupStatus.RequiresApproval : LoginStartupStatus.Enabled
                : LoginStartupStatus.Disabled;
        }
    }
}
