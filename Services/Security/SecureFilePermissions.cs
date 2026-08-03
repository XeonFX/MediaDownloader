using System.Security.AccessControl;
using System.Security.Principal;

namespace MediaDownloader.Services.Security;

/// <summary>Applies an owner-only permission boundary to files containing local credentials.</summary>
public static class SecureFilePermissions
{
    public static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Could not determine the current Windows user.");
            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
