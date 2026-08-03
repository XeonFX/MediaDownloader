using FluentAssertions;
using MediaDownloader.Services.Security;

namespace MediaDownloader.Tests.Services.Security;

public class SecureFilePermissionsTests
{
    [Fact]
    public void RestrictToCurrentUser_SetsOwnerOnlyUnixMode()
    {
        if (OperatingSystem.IsWindows())
            return; // The guarded Windows ACL branch is compiled; this assertion is Unix-specific.

        var path = Path.Combine(Path.GetTempPath(), $"mediadownloader-permissions-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "secret");

            SecureFilePermissions.RestrictToCurrentUser(path);

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
