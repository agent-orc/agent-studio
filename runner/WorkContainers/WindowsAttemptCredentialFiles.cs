using System.Security.Principal;

namespace AgentRunner.WorkContainers;

public sealed record WindowsAttemptCredentialFile(string Directory, string TokenFile,
    string StorageMode, string DeletionStatus);

public interface IWindowsEncryptedVolumeProbe
{
    Task<bool> IsEncryptedAsync(string volumeRoot, CancellationToken ct);
}

/// <summary>Fails closed unless BitLocker reports a fully encrypted Windows volume.</summary>
public sealed class BitLockerVolumeProbe : IWindowsEncryptedVolumeProbe
{
    public async Task<bool> IsEncryptedAsync(string volumeRoot, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var drive = Path.GetPathRoot(Path.GetFullPath(volumeRoot));
        if (drive is null || drive.Length < 2) return false;
        var result = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command",
                "(Get-BitLockerVolume -MountPoint '" + drive[..2] + "').VolumeStatus"],
            environment: MinimalWindowsEnvironment(), clearEnvironment: true, ct: ct);
        return result.Success && result.StdOut.Trim() == "FullyEncrypted";
    }

    internal static IReadOnlyDictionary<string, string?> MinimalWindowsEnvironment()
        => new Dictionary<string, string?>
        {
            ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
        };
}

/// <summary>Windows process-executor files live outside the worktree on an encrypted, ACL-restricted volume.</summary>
public sealed class WindowsAttemptCredentialFiles(string encryptedVolumeRoot, IWindowsEncryptedVolumeProbe probe)
{
    public async Task<WindowsAttemptCredentialFile> StageAsync(string attemptId, long fence,
        string token, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows attempt credential files require Windows.");
        var root = Path.GetFullPath(encryptedVolumeRoot);
        ProtectedHostPath.EnsureOutsideRepository(root);
        if (!await probe.IsEncryptedAsync(root, ct))
            throw new InvalidOperationException("Attempt credential volume is not verified as encrypted.");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Windows runner SID is unavailable.");
        Directory.CreateDirectory(root);
        await RestrictAsync(root, sid, ct);
        var directory = Path.Combine(root, WorkContainerLaunchPlanner.Sanitize(attemptId) + "-f" + fence);
        Directory.CreateDirectory(directory);
        await RestrictAsync(directory, sid, ct);
        var file = Path.Combine(directory, "github-token");
        try
        {
            await File.WriteAllTextAsync(file, token, ct);
            await RestrictAsync(file, sid, ct);
            return new WindowsAttemptCredentialFile(directory, file, "windows-acl-encrypted-volume", "pending");
        }
        catch
        {
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public Task<WindowsAttemptCredentialFile> DeleteAsync(WindowsAttemptCredentialFile staged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(staged.TokenFile)) File.Delete(staged.TokenFile);
        if (Directory.Exists(staged.Directory)) Directory.Delete(staged.Directory, true);
        return Task.FromResult(staged with
        {
            DeletionStatus = !File.Exists(staged.TokenFile) && !Directory.Exists(staged.Directory)
                ? "deleted" : "deletion-failed",
        });
    }

    private static async Task RestrictAsync(string path, string sid, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("icacls.exe",
            [path, "/inheritance:r", "/grant:r", "*" + sid +
                (Directory.Exists(path) ? ":(OI)(CI)F" : ":F"),
                "/remove:g", "*S-1-1-0", "*S-1-5-11", "*S-1-5-32-545"],
            environment: BitLockerVolumeProbe.MinimalWindowsEnvironment(), clearEnvironment: true, ct: ct);
        if (!result.Success) throw new InvalidOperationException("Could not restrict Windows attempt credential ACL.");
    }
}
