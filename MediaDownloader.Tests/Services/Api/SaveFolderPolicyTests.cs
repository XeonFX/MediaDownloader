using FluentAssertions;
using MediaDownloader.Services.Api;

namespace MediaDownloader.Tests.Services.Api;

/// <summary>
/// Covers the boundary that keeps an agent-chosen save folder inside the download root.
///
/// This is the check standing between "an agent picks where files land" and an arbitrary filesystem
/// write: torrent filenames come from torrent metadata, so a chosen folder plus a chosen torrent
/// writes attacker-controlled content wherever the user can write. An agent picks these arguments
/// after reading titles and descriptions fetched from the internet, so the untrusted input reaches
/// this function by design, not by accident.
/// </summary>
public class SaveFolderPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-folder-policy-" + Guid.NewGuid().ToString("N"));

    public SaveFolderPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void NoFolderRequested_FallsBackToTheDefault()
    {
        SaveFolderPolicy.Resolve(null, _root).Should().BeNull();
        SaveFolderPolicy.Resolve("   ", _root).Should().BeNull();
    }

    [Fact]
    public void TheRootItself_IsAllowed()
    {
        SaveFolderPolicy.Resolve(_root, _root).Should().Be(Path.TrimEndingDirectorySeparator(_root));
    }

    [Fact]
    public void ASubfolder_IsAllowed_AndDoesNotNeedToExistYet()
    {
        var nested = Path.Combine(_root, "Shows", "Season 3");

        SaveFolderPolicy.Resolve(nested, _root).Should().Be(nested);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/tmp")]
    public void AnAbsolutePathOutsideTheRoot_IsRejected(string outside)
    {
        var act = () => SaveFolderPolicy.Resolve(outside, _root);

        act.Should().Throw<AgentApiException>().WithMessage("*outside*");
    }

    [Fact]
    public void TraversalOutOfTheRoot_IsRejected()
    {
        var escape = Path.Combine(_root, "..", "..", "somewhere-else");

        var act = () => SaveFolderPolicy.Resolve(escape, _root);

        act.Should().Throw<AgentApiException>().WithMessage("*outside*");
    }

    [Fact]
    public void ASiblingSharingTheRootsNamePrefix_IsRejected()
    {
        // "/data/downloads-evil" starts with "/data/downloads" as a string but is not inside it.
        var act = () => SaveFolderPolicy.Resolve(_root + "-evil", _root);

        act.Should().Throw<AgentApiException>().WithMessage("*outside*");
    }

    [Fact]
    public void ASymlinkInsideTheRootPointingOutside_IsRejected()
    {
        // A purely textual containment check passes this: the path really does start with the root.
        // Only resolving the link shows it lands elsewhere.
        var outside = Path.Combine(Path.GetTempPath(), "md-folder-policy-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "escape-hatch");
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            var act = () => SaveFolderPolicy.Resolve(Path.Combine(link, "payload"), _root);

            act.Should().Throw<AgentApiException>().WithMessage("*outside*");
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ARelativePathIsResolvedAgainstTheProcess_AndSoIsRejected()
    {
        // Relative paths are not "relative to the download root" — resolving them against the
        // working directory puts them outside it, which is the safe reading.
        var act = () => SaveFolderPolicy.Resolve("Shows", _root);

        act.Should().Throw<AgentApiException>().WithMessage("*outside*");
    }
}
