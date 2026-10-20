using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentStudio.Secrets;

/// <summary>
/// Owner-only protection for the key file and the store and audit
/// directories. On Unix, directories get mode 700 and files mode 600. On
/// Windows, a created directory or key file gets a protected ACL (inherited
/// entries removed) that grants full control to the service account alone;
/// files created inside such a directory inherit it. A key file is verified on
/// every load: on Unix no group or other bit may be set, on Windows the owner
/// must be the service account, SYSTEM or the local Administrators group, and
/// no allow entry may grant read, write, permission or ownership rights to
/// anyone else. On Unix the owner is not compared: a mode-600 file of another
/// account cannot be opened by the service unless it runs as root.
/// </summary>
internal static class OwnerOnlyAccess
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates <paramref name="path"/> owner-only when it does not exist yet. Parents are created with default access.</summary>
    public static void CreateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
            return;
        }
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        new DirectoryInfo(path).Create(WindowsDirectorySecurity());
    }

    /// <summary>Creates a new file that only the service account can open; fails when the file exists.</summary>
    public static FileStream CreateNewFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.None, 4096, FileOptions.None, WindowsFileSecurity());
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnlyFile
        });
    }

    /// <summary>Unix create mode for files written into an owner-only directory; Windows files inherit the directory ACL.</summary>
    public static void ApplyCreateMode(FileStreamOptions options)
    {
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
    }

    /// <summary>Refuses a key file that someone other than the service account can read or change.</summary>
    public static void RejectForeignAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RejectForeignWindowsAccess(path);
            return;
        }
        if ((File.GetUnixFileMode(path) & OtherAccess) != 0)
            throw new SecretKeyUnavailableException(
                "The key-encryption key file is accessible to other users; restrict it to the service account (chmod 600).");
    }

    [SupportedOSPlatform("windows")]
    private static void RejectForeignWindowsAccess(string path)
    {
        const FileSystemRights sensitive = FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData
                                           | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var account = ServiceAccount();
        var security = new FileInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner, account))
            throw new SecretKeyUnavailableException(
                "The key-encryption key file is owned by another account; it must be owned by the service account, SYSTEM or Administrators.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & sensitive) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Trusted(sid, account)) continue;
            throw new SecretKeyUnavailableException(
                $"The key-encryption key file grants access to {rule.IdentityReference.Value}; restrict its ACL to the service account.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Trusted(SecurityIdentifier sid, SecurityIdentifier account)
        => sid == account || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity WindowsDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(ServiceAccount(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity WindowsFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(ServiceAccount(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ServiceAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new SecretKeyUnavailableException("The service account has no Windows user identity.");
    }
}
