using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerReleaseIdentityTests
{
    [SkippableFact]
    public void Current_symlink_target_name_is_the_advertised_release()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-host-release-{Guid.NewGuid():N}");
        var releaseId = "agt-2650b-20260812T064049Z-ca5cbd6ff";
        var release = Path.Combine(root, "releases", releaseId);
        var current = Path.Combine(root, "current");
        try
        {
            Directory.CreateDirectory(release);
            Skip.IfNot(
                TryCreateDirectoryLink(current, release),
                "This host cannot create a directory symlink or Windows junction fixture.");

            Assert.Equal(releaseId, RunnerReleaseIdentity.Resolve(
                current + Path.DirectorySeparatorChar, configured: ""));
        }
        finally
        {
            if (Directory.Exists(current)) Directory.Delete(current);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Direct_release_directory_name_is_the_advertised_release()
    {
        var releaseId = "agt-2650b-20260812T064049Z-ca5cbd6ff";
        var release = Path.Combine(Path.GetTempPath(), "agent-host", "releases", releaseId);

        Assert.Equal(releaseId, RunnerReleaseIdentity.Resolve(
            release + Path.DirectorySeparatorChar, configured: ""));
    }

    [Fact]
    public void Explicit_release_identity_wins_for_non_symlink_packages()
    {
        Assert.Equal(
            "release-explicit",
            RunnerReleaseIdentity.Resolve(AppContext.BaseDirectory, " release-explicit "));
    }

    /// <summary>
    /// AGT-2863: a detached review worker outlives the promotion that moves the
    /// <c>current</c> symlink, so the binary path recorded with its verdict has
    /// to name the release directory, not the link it was started through.
    /// </summary>
    [SkippableFact]
    public void Worker_binary_path_resolves_through_the_current_symlink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-host-binary-{Guid.NewGuid():N}");
        var releaseId = "20260917T1550Z-v0.6.0-551484dca";
        var release = Path.Combine(root, "releases", releaseId);
        var current = Path.Combine(root, "current");
        try
        {
            Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(release, "agent-host"), "binary");
            Skip.IfNot(
                TryCreateDirectoryLink(current, release),
                "This host cannot create a directory symlink or Windows junction fixture.");

            Assert.Equal(
                Path.Combine(release, "agent-host"),
                RunnerReleaseIdentity.ResolveBinaryPath(Path.Combine(current, "agent-host")));
        }
        finally
        {
            if (Directory.Exists(current)) Directory.Delete(current);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_unavailable_process_path_reports_an_unknown_binary()
        => Assert.Equal("unknown", RunnerReleaseIdentity.ResolveBinaryPath(null));

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or IOException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start);
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
