using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using MediaDownloader.Data.Entities;

namespace MediaDownloader.Tests.Data;

public class AppSettingsValidationTests
{
    private static bool TryValidate(AppSettings settings, out List<ValidationResult> results)
    {
        results = new List<ValidationResult>();
        return Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
    }

    [Fact]
    public void FreshDefaultSettings_AreValid()
    {
        // Regression guard: a naive [EmailAddress] attribute on EmailFrom/EmailTo rejects an empty
        // string (not just a malformed one), which would break AppDbContext.GetSettingsAsync()'s
        // first-run default-row creation for every fresh install. The default object — with both
        // email fields empty — must validate cleanly.
        var settings = new AppSettings();

        TryValidate(settings, out var results).Should().BeTrue();
        results.Should().BeEmpty();
    }

    [Fact]
    public void EmptyEmailFields_AreValid()
    {
        var settings = new AppSettings { EmailFrom = "", EmailTo = "" };

        TryValidate(settings, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.com")]
    [InlineData("@no-local-part.com")]
    public void MalformedEmailFrom_FailsValidation(string value)
    {
        var settings = new AppSettings { EmailFrom = value };

        TryValidate(settings, out var results).Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(AppSettings.EmailFrom)));
    }

    [Fact]
    public void WellFormedEmailAddresses_AreValid()
    {
        var settings = new AppSettings { EmailFrom = "sender@example.com", EmailTo = "recipient@example.com" };

        TryValidate(settings, out var results).Should().BeTrue();
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void SmtpPort_OutOfRange_FailsValidation(int port)
    {
        var settings = new AppSettings { SmtpPort = port };

        TryValidate(settings, out var results).Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(AppSettings.SmtpPort)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(587)]
    [InlineData(65535)]
    public void SmtpPort_InRange_IsValid(int port)
    {
        var settings = new AppSettings { SmtpPort = port };

        TryValidate(settings, out _).Should().BeTrue();
    }

    [Fact]
    public void EmptyDownloadFolder_FailsValidation()
    {
        var settings = new AppSettings { DownloadFolder = "" };

        TryValidate(settings, out var results).Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(AppSettings.DownloadFolder)));
    }
}
