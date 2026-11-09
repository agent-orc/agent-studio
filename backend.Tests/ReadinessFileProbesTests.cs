using AgentStudio.Readiness;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// File-system facts of the readiness report (AGT-3412): which mount holds a
/// data folder, whether a state folder takes writes, and which Git lock files
/// a repository holds.
/// </summary>
public sealed class ReadinessFileProbesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "readiness-files-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public ReadinessFileProbesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // A Docker container with the networked stack's binds; /data/home is missing.
    private const string ContainerMountInfo =
        "2150 1803 0:244 / / rw,relatime master:603 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/A\n"
        + "2151 2150 0:247 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw\n"
        + "2160 2150 252:1 /srv/agent-studio/data/workspace /data/workspace rw,relatime - ext4 /dev/vda1 rw\n"
        + "2161 2150 252:1 /srv/agent-studio/data/projects /data/projects rw,relatime - ext4 /dev/vda1 rw\n"
        + "2162 2150 252:1 /srv/my\\040checkouts/demo /data/projects/my\\040demo rw,relatime - ext4 /dev/vda1 rw\n"
        + "2163 2150 252:1 /var/lib/docker/containers/x/hostname /etc/hostname rw,relatime - ext4 /dev/vda1 rw\n";

    [Fact]
    public void Mountinfo_resolves_each_folder_to_its_longest_mount()
    {
        var mounts = ReadinessFileProbes.ParseMountInfo(ContainerMountInfo);

        Assert.Equal(6, mounts.Count);
        Assert.Equal("/data/workspace", ReadinessFileProbes.MountFor(mounts, "/data/workspace")!.MountPoint);
        Assert.Equal("/data/workspace", ReadinessFileProbes.MountFor(mounts, "/data/workspace/.metadata")!.MountPoint);
        Assert.Equal("/data/projects/my demo", ReadinessFileProbes.MountFor(mounts, "/data/projects/my demo/src")!.MountPoint);
        Assert.Equal("/data/projects", ReadinessFileProbes.MountFor(mounts, "/data/projects/other")!.MountPoint);
        Assert.Equal(new MountEntry("/", "overlay"), ReadinessFileProbes.MountFor(mounts, "/data/home"));
        Assert.Equal("/", ReadinessFileProbes.MountFor(mounts, "/data/workspace-old")!.MountPoint);
    }

    [Fact]
    public void An_overlay_root_or_dockerenv_means_a_container()
    {
        var container = ReadinessFileProbes.ParseMountInfo(ContainerMountInfo);
        var host = ReadinessFileProbes.ParseMountInfo("22 1 252:1 / / rw,relatime shared:1 - ext4 /dev/vda1 rw\n");

        Assert.True(ReadinessFileProbes.RunsInContainer(container, dockerEnvExists: false));
        Assert.False(ReadinessFileProbes.RunsInContainer(host, dockerEnvExists: false));
        Assert.True(ReadinessFileProbes.RunsInContainer(host, dockerEnvExists: true));
    }

    [Fact]
    public void A_home_folder_in_the_container_layer_becomes_a_failing_line()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        var mounts = new[] { new MountEntry("/", "overlay") };

        var fact = ReadinessFileProbes.DataFolder("home", home, "home", mounts, null);
        var check = ServerReadinessPolicy.DataFolder(fact, runsInContainer: true);

        Assert.True(fact.Exists);
        Assert.Equal("/", fact.MountPoint);
        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Contains("inside the container layer", check.Meaning);
    }

    [Fact]
    public void A_missing_data_folder_is_reported_missing()
    {
        var fact = ReadinessFileProbes.DataFolder("workspace", Path.Combine(_root, "absent"), "workspace", null, null);

        Assert.False(fact.Exists);
        Assert.Null(fact.MountPoint);
    }

    [Fact]
    public void A_writable_state_folder_keeps_no_probe_file()
    {
        var fact = ReadinessFileProbes.StateFolder("workspace", _root);

        Assert.True(fact.Writable, fact.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void A_state_folder_that_cannot_be_written_reports_the_error()
    {
        var fact = ReadinessFileProbes.StateFolder("workspace", Path.Combine(_root, "absent"));

        Assert.False(fact.Writable);
        Assert.False(string.IsNullOrWhiteSpace(fact.Error));
        Assert.Equal(ReadinessStates.Fail, ServerReadinessPolicy.StateFolder(fact).State);
    }

    [Fact]
    public void Git_locks_are_listed_with_their_age_in_a_checkout_and_a_linked_worktree()
    {
        var repository = Path.Combine(_root, "repo");
        var gitDir = Path.Combine(repository, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        var index = Path.Combine(gitDir, "index.lock");
        var refLock = Path.Combine(gitDir, "refs", "heads", "main.lock");
        File.WriteAllText(index, "");
        File.WriteAllText(refLock, "");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        var now = _clock.GetUtcNow();
        File.SetLastWriteTimeUtc(index, now.UtcDateTime.AddMinutes(-70));
        File.SetLastWriteTimeUtc(refLock, now.UtcDateTime.AddMinutes(-3));

        var locks = ReadinessFileProbes.GitLocks(repository, now).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();

        Assert.Equal([index, refLock], locks.Select(item => item.Path));
        Assert.Equal(TimeSpan.FromMinutes(70), locks[0].Age);
        var check = ServerReadinessPolicy.GitLocks(ReadinessScopes.Server, "workspace", locks);
        Assert.Equal(ReadinessStates.Warn, check.State);
        Assert.Contains(index, check.Command);
        Assert.DoesNotContain(refLock, check.Command);

        var worktree = Path.Combine(_root, "worktree");
        var worktreeGitDir = Path.Combine(gitDir, "worktrees", "wt");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");
        File.WriteAllText(Path.Combine(worktreeGitDir, "index.lock"), "");

        Assert.Equal(Path.Combine(worktreeGitDir, "index.lock"), Assert.Single(ReadinessFileProbes.GitLocks(worktree, now)).Path);
        Assert.Empty(ReadinessFileProbes.GitLocks(Path.Combine(_root, "absent"), now));
    }
}
