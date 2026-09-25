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

    /// <summary>
    /// AGT-2826: the release id alone does not say how old a host is. The
    /// pipeline stamps the build instant into the id, so the reported identity
    /// carries a comparable timestamp without trusting file mtimes.
    /// </summary>
    [Fact]
    public void Describe_reads_version_commit_and_build_instant_from_the_release()
    {
        var identity = RunnerReleaseIdentity.Describe(
            "agt-2650b-20260812T064049Z-ca5cbd6ff",
            informationalVersion: "0.2.7+ca5cbd6ff9a1",
            assemblyVersion: "0.2.7",
            configuredCommit: "",
            configuredBuiltAt: "");

        Assert.Equal("agt-2650b-20260812T064049Z-ca5cbd6ff", identity.ReleaseId);
        Assert.Equal("0.2.7", identity.Version);
        Assert.Equal("ca5cbd6ff9a1", identity.Commit);
        Assert.Equal(new DateTime(2026, 8, 12, 6, 40, 49, DateTimeKind.Utc), identity.BuiltAt);
    }

    [Fact]
    public void Describe_prefers_the_configured_commit_and_build_instant()
    {
        var identity = RunnerReleaseIdentity.Describe(
            "agt-2650b-20260812T064049Z-ca5cbd6ff",
            informationalVersion: "0.2.7+ca5cbd6ff9a1",
            assemblyVersion: "0.2.7",
            configuredCommit: " deadbeef ",
            configuredBuiltAt: "2026-09-01T10:00:00Z");

        Assert.Equal("deadbeef", identity.Commit);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), identity.BuiltAt);
    }

    /// <summary>
    /// A host built outside the release pipeline reports what it knows. An
    /// invented build stamp would be read as drift evidence, so the field stays
    /// null and the server falls back to a version ordering.
    /// </summary>
    [Fact]
    public void Describe_leaves_unknown_facts_null_for_an_unstamped_release()
    {
        var identity = RunnerReleaseIdentity.Describe(
            "local-dev",
            informationalVersion: "0.3.0",
            assemblyVersion: "0.3.0",
            configuredCommit: "",
            configuredBuiltAt: "");

        Assert.Equal("local-dev", identity.ReleaseId);
        Assert.Equal("0.3.0", identity.Version);
        Assert.Null(identity.Commit);
        Assert.Null(identity.BuiltAt);
    }

    [Theory]
    [InlineData("agt-2650b-20260812T064049Z-ca5cbd6ff", true)]
    [InlineData("release-20260812T064049Z", true)]
    [InlineData("release-2026-08-12", false)]
    [InlineData("", false)]
    public void ParseReleaseStamp_only_accepts_a_compact_utc_stamp(string releaseId, bool parsed)
        => Assert.Equal(parsed, RunnerReleaseIdentity.ParseReleaseStamp(releaseId) is not null);

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
