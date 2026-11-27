// clock-independent: runtime log timestamps and verdict times are parsed data; nothing is compared with now.
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRunner.WorkContainers;
using Xunit;

namespace AgentRunner.Tests.WorkContainers;

/// <summary>
/// SM-11 (security-model Dossier, decision SM-D6 option H2): one fresh
/// container per attempt with its own UID, only that attempt's repository, no
/// runtime socket, the work-zone network, and the container executor as the
/// Linux default once the host's canary verdict is go.
/// </summary>
public class AttemptContainmentTests
{
    private static readonly WorkContainerLimits Limits = new(3, 3L << 30, 1024);

    private static AttemptContainmentFacts Ready() => new(
        IsWindows: false, IsLinux: true, Mode: WorkContainerMode.Contained, CanaryGo: true,
        LauncherInstalled: true, ServiceUserHasRuntimeAccess: false, RoleImagePinned: true,
        ZonePolicyReady: true, NetworkInWorkZone: true);

    [Fact]
    public void Linux_hosts_default_to_contained_mode_on_the_work_zone_network()
    {
        var linux = WorkContainerSettings.FromEnvironment(_ => null, 12, 62L << 30, "/state", isLinux: true);
        Assert.Equal(WorkContainerMode.Contained, linux.Mode);
        Assert.Equal(WorkContainerSettings.DefaultNetwork, linux.Network);
        Assert.Equal(WorkContainerSettings.DefaultLauncherPath, linux.Launcher);

        var windows = WorkContainerSettings.FromEnvironment(_ => null, 12, 62L << 30, "/state", isLinux: false);
        Assert.Equal(WorkContainerMode.Off, windows.Mode);

        var rollback = WorkContainerSettings.FromEnvironment(
            name => name == WorkContainerSettings.ModeVariable ? "off" : null, 12, 62L << 30, "/state", isLinux: true);
        Assert.Equal(WorkContainerMode.Off, rollback.Mode);
    }

    [Theory]
    [InlineData(nameof(AttemptContainmentFacts.CanaryGo), ContainmentVerdict.Process, false, "canary-verdict-missing")]
    [InlineData(nameof(AttemptContainmentFacts.ServiceUserHasRuntimeAccess), ContainmentVerdict.Refuse, true, "runtime-group-member")]
    [InlineData(nameof(AttemptContainmentFacts.LauncherInstalled), ContainmentVerdict.Refuse, true, "launcher-missing")]
    [InlineData(nameof(AttemptContainmentFacts.RoleImagePinned), ContainmentVerdict.Refuse, true, "role-image-unpinned")]
    [InlineData(nameof(AttemptContainmentFacts.ZonePolicyReady), ContainmentVerdict.Refuse, true, "zone-policy-unavailable")]
    [InlineData(nameof(AttemptContainmentFacts.NetworkInWorkZone), ContainmentVerdict.Refuse, true, "network-outside-work-zone")]
    [InlineData("", ContainmentVerdict.Container, true, "contained-default")]
    public void Contained_mode_runs_in_a_container_after_the_canary_and_fails_closed_without_a_prerequisite(
        string flipped, ContainmentVerdict verdict, bool enforced, string reason)
    {
        var facts = flipped switch
        {
            nameof(AttemptContainmentFacts.CanaryGo) => Ready() with { CanaryGo = false },
            nameof(AttemptContainmentFacts.ServiceUserHasRuntimeAccess) => Ready() with { ServiceUserHasRuntimeAccess = true },
            nameof(AttemptContainmentFacts.LauncherInstalled) => Ready() with { LauncherInstalled = false },
            nameof(AttemptContainmentFacts.RoleImagePinned) => Ready() with { RoleImagePinned = false },
            nameof(AttemptContainmentFacts.ZonePolicyReady) => Ready() with { ZonePolicyReady = false },
            nameof(AttemptContainmentFacts.NetworkInWorkZone) => Ready() with { NetworkInWorkZone = false },
            _ => Ready(),
        };

        var decision = AttemptContainmentPolicy.Decide(facts);

        Assert.Equal(verdict, decision.Verdict);
        Assert.Equal(enforced, decision.Enforced);
        Assert.StartsWith(reason, decision.Reason);
    }

    [Theory]
    [InlineData(WorkContainerMode.Off, "container-mode-off")]
    [InlineData(WorkContainerMode.Shadow, "container-shadow")]
    public void Explicit_rollback_modes_keep_the_process_executor_and_enforce_nothing(WorkContainerMode mode, string reason)
    {
        var decision = AttemptContainmentPolicy.Decide(Ready() with { Mode = mode });
        Assert.Equal(ContainmentVerdict.Process, decision.Verdict);
        Assert.False(decision.Enforced);
        Assert.StartsWith(reason, decision.Reason);
    }

    [Fact]
    public void Windows_keeps_the_job_object_executor_even_in_contained_mode()
    {
        var decision = AttemptContainmentPolicy.Decide(Ready() with { IsWindows = true, IsLinux = false });
        Assert.Equal(ContainmentVerdict.Process, decision.Verdict);
        Assert.False(decision.Enforced);
    }

    [Fact]
    public void Every_attempt_gets_its_own_unprivileged_uid_distinct_from_live_attempts()
    {
        var first = WorkContainerIdentity.Allocate("attempt-1", 3, new HashSet<int>());
        Assert.True(WorkContainerIdentity.IsAttemptUid(first));
        Assert.NotEqual(10001, first);
        Assert.Equal(first, WorkContainerIdentity.Allocate("attempt-1", 3, new HashSet<int>()));

        var taken = WorkContainerIdentity.Allocate("attempt-2", 3, new HashSet<int> { first });
        Assert.NotEqual(first, taken);
        var collided = WorkContainerIdentity.Allocate("attempt-1", 3, new HashSet<int> { first });
        Assert.NotEqual(first, collided);
        Assert.True(WorkContainerIdentity.IsAttemptUid(collided));
        Assert.False(WorkContainerIdentity.IsAttemptUid(0));
        Assert.False(WorkContainerIdentity.IsAttemptUid(10001));
    }

    [Fact]
    public void Launch_plan_runs_as_the_attempt_uid_and_mounts_only_that_repository_caches()
    {
        var attempt = WorkContainerPolicyTests.Attempt();
        var plan = WorkContainerLaunchPlanner.Plan(attempt, WorkContainerPolicyTests.Digest, Limits,
            WorkContainerPolicyTests.Settings(), uid: 212_345);
        var args = plan.CreateArguments;

        Assert.Equal(212_345, plan.Uid);
        Assert.Contains("212345:10001", args);
        Assert.Contains($"{WorkContainerIdentity.UidLabel}=212345", args);
        Assert.Contains(args, value => value.StartsWith("/run/agent-secrets:", StringComparison.Ordinal)
                                       && value.Contains("uid=212345", StringComparison.Ordinal));
        // Regression: one shared mirror volume held every project's repository.
        Assert.Contains("type=volume,source=agent-cache-repo-agent-studio-v7,target=/cache/repo", args);
        Assert.Contains("type=volume,source=agent-cache-npm-agent-studio-v7,target=/cache/npm", args);
        Assert.DoesNotContain("type=volume,source=agent-cache-repo-v7,target=/cache/repo", args);
        Assert.All(plan.Mounts.Where(mount => mount.Target.StartsWith("/cache/", StringComparison.Ordinal)),
            mount => Assert.Contains("-agent-studio-", mount.Source, StringComparison.Ordinal));

        var other = WorkContainerLaunchPlanner.Plan(attempt with { RepositoryKey = "other-project" },
            WorkContainerPolicyTests.Digest, Limits, WorkContainerPolicyTests.Settings(), uid: 212_346);
        static IEnumerable<string> Caches(WorkContainerLaunchPlan item)
            => item.Mounts.Where(mount => mount.Target.StartsWith("/cache/", StringComparison.Ordinal)).Select(mount => mount.Source);
        Assert.Empty(Caches(plan).Intersect(Caches(other)));
        Assert.Contains("GIT_CONFIG_KEY_0=safe.directory", args);
        Assert.Contains("GIT_CONFIG_VALUE_1=/cache/repo/agent-studio.git", args);
    }

    [Fact]
    public void Prepare_step_hands_the_attempt_volume_to_the_attempt_uid_without_network_or_extra_capabilities()
    {
        var plan = WorkContainerLaunchPlanner.Plan(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            Limits, WorkContainerPolicyTests.Settings(), uid: 212_345);
        var args = plan.PrepareArguments;

        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        AssertPair(args, "--network", "none");
        AssertPair(args, "--cap-drop", "ALL");
        Assert.Equal(["CHOWN", "FOWNER"], CapAdds(args));
        Assert.Equal("212345", args[^1]);
        Assert.Null(WorkContainerLauncherPolicy.Refusal(args, LauncherContext()));
    }

    [Fact]
    public void Narrow_launcher_admits_exactly_the_executor_plan()
    {
        var context = LauncherContext();
        var plan = WorkContainerLaunchPlanner.Plan(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            Limits, WorkContainerPolicyTests.Settings() with { Network = "agent-work" }, uid: 212_345);
        Assert.Null(WorkContainerLauncherPolicy.Refusal(plan.CreateArguments, context));
        foreach (var call in new[]
                 {
                     new[] { "version", "--format", "{{.Server.Version}}" },
                     ["image", "inspect", "--format", "{{.Id}}", WorkContainerPolicyTests.Digest],
                     ["pull", "--quiet", WorkContainerPolicyTests.Digest],
                     ["start", plan.ContainerName],
                     ["wait", plan.ContainerName],
                     ["logs", "--timestamps", plan.ContainerName],
                     ["rm", "--force", "--volumes", plan.ContainerName],
                     ["volume", "rm", "--force", plan.AttemptVolume],
                     ["volume", "create", "--label", "agent-studio.work-container=1", plan.AttemptVolume],
                     ["ps", "--all", "--no-trunc", "--filter", "label=agent-studio.work-container=1",
                         "--filter", "label=agent-studio.host=h", "--format", "{{json .}}"],
                     ["volume", "ls", "--filter", "label=agent-studio.work-container=1", "--format", "{{json .}}"],
                     ["exec", "--interactive", plan.ContainerName, "/bin/sh", "-c", WorkContainerLaunchPlanner.InjectTokenScript],
                 })
            Assert.Null(WorkContainerLauncherPolicy.Refusal(call, context));
    }

    public static TheoryData<string, string[]> LauncherRefusals()
    {
        var plan = WorkContainerLaunchPlanner.Plan(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            Limits, WorkContainerPolicyTests.Settings() with { Network = "agent-work" }, uid: 212_345);
        string[] Replace(string flag, string value)
        {
            var copy = plan.CreateArguments.ToArray();
            copy[Array.IndexOf(copy, flag) + 1] = value;
            return copy;
        }
        string[] Insert(params string[] extra) => [plan.CreateArguments[0], .. extra, .. plan.CreateArguments.Skip(1)];
        var image = plan.CreateArguments.ToList().IndexOf(WorkContainerPolicyTests.Digest);
        return new TheoryData<string, string[]>
        {
            { "privileged", Insert("--privileged") },
            { "runtime socket bind", Insert("--mount", "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock,readonly") },
            { "provider home bind", Insert("--mount", "type=bind,source=/var/lib/agent-host/.claude,target=/mnt/claude,readonly") },
            { "volume shorthand", Insert("-v", "/:/host") },
            { "host network", Replace("--network", "host") },
            { "network outside work zone", Replace("--network", "bridge") },
            { "root user", Replace("--user", "0:0") },
            { "shared legacy uid", Replace("--user", "10001:10001") },
            { "capability", Insert("--cap-add", "SYS_ADMIN") },
            { "seccomp off", Replace("--security-opt", "seccomp=unconfined") },
            { "tmpfs bind options", Replace("--tmpfs", "/tmp:rw,rbind,nosuid,nodev,size=1g") },
            { "foreign name", Replace("--name", "studio-api") },
            { "unpinned image", [.. plan.CreateArguments.Take(image), "agent-work:latest", .. plan.CreateArguments.Skip(image + 1)] },
            { "foreign entry", [.. plan.CreateArguments.Take(image + 1), "/bin/sh", "-c", "cat /etc/shadow"] },
            { "exec any command", ["exec", plan.ContainerName, "/bin/sh", "-c", "id"] },
            { "start root preparation before inspection", ["start", plan.PrepareContainerName] },
            { "exec into root preparation", ["exec", "--interactive", plan.PrepareContainerName, "/bin/sh", "-c", WorkContainerLaunchPlanner.InjectTokenScript] },
            { "run any container", ["run", "--rm", WorkContainerPolicyTests.Digest, "/bin/sh"] },
            { "remove any container", ["rm", "--force", "studio-api"] },
            { "remove a cache volume", ["volume", "rm", "--force", "agent-cache-repo-agent-studio-v7"] },
            { "unknown verb", ["system", "prune", "--force"] },
            { "empty", [] },
        };
    }

    [Theory]
    [MemberData(nameof(LauncherRefusals))]
    public void Narrow_launcher_refuses_everything_else(string name, string[] args)
        => Assert.True(WorkContainerLauncherPolicy.Refusal(args, LauncherContext()) is not null, name);

    [Theory]
    [InlineData("/etc/.", "/mnt/source")]
    [InlineData("/var/../run", "/mnt/source")]
    [InlineData("/var", "/mnt/source")]
    [InlineData("/srv/mirrors/project.git", "/bin/sh")]
    [InlineData("/srv/mirrors/project.git", "/usr/bin")]
    public void Launcher_refuses_host_aliases_parent_roots_and_executable_targets(string source, string target)
    {
        var plan = BoundaryPlan();
        var args = plan.CreateArguments.ToList();
        args.InsertRange(1, ["--mount", $"type=bind,source={source},target={target},readonly"]);
        Assert.NotNull(WorkContainerLauncherPolicy.Refusal(args, LauncherContext()));
    }

    [Fact]
    public void Launcher_refuses_unapproved_digests_for_inspect_pull_create_and_prepare()
    {
        var plan = BoundaryPlan();
        var other = "unapproved.invalid/work@sha256:" + new string('a', 64);
        foreach (var args in new IReadOnlyList<string>[]
                 {
                     new[] { "pull", "--quiet", other },
                     new[] { "image", "inspect", "--format", "{{.Id}}", other },
                     plan.CreateArguments.Select(value => value == plan.Image ? other : value).ToArray(),
                     plan.PrepareArguments.Select(value => value == plan.Image ? other : value).ToArray(),
                 })
            Assert.NotNull(WorkContainerLauncherPolicy.Refusal(args, LauncherContext()));
    }

    [Fact]
    public void Launcher_refuses_all_host_binds_during_root_preparation()
    {
        var args = BoundaryPlan().PrepareArguments.ToList();
        args.InsertRange(args.IndexOf(WorkContainerPolicyTests.Digest),
            ["--mount", "type=bind,source=/srv/mirrors/project.git,target=/mnt/source,readonly"]);
        Assert.NotNull(WorkContainerLauncherPolicy.Refusal(args, LauncherContext()));
    }

    [Fact]
    public void Both_launch_paths_override_image_entrypoint_metadata()
    {
        var plan = BoundaryPlan();
        AssertPair(plan.CreateArguments, "--entrypoint", "/usr/bin/env");
        AssertPair(plan.PrepareArguments, "--entrypoint", "/usr/bin/env");
    }

    [Theory]
    [InlineData("0:644:regular file\n0:755:directory\n", true)]
    [InlineData("1000:644:regular file\n0:755:directory\n", false)]
    [InlineData("0:664:regular file\n0:755:directory\n", false)]
    [InlineData("0:644:regular file\n0:775:directory\n", false)]
    [InlineData("0:644:symbolic link\n0:755:directory\n", false)]
    [InlineData("0:644:regular file\n0:1777:directory\n", false)]
    public void Release_authority_requires_root_ownership_and_immutable_unlinked_ancestors(string metadata, bool trusted)
        => Assert.Equal(trusted, RoleImageManifest.TrustedMetadata(metadata, directory: false, count: 2));

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Caller_owned_release_files_and_symlink_sources_have_no_root_capability()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var manifest = Path.Combine(state.Path, "images.json");
        await File.WriteAllTextAsync(manifest, RoleImageManifest.Empty().Promote("coding", WorkContainerPolicyTests.Digest, null).Serialize());
        await Assert.ThrowsAsync<InvalidDataException>(() => RoleImageManifest.LoadLauncherReleaseAsync(manifest, default));
        var source = Directory.CreateDirectory(Path.Combine(state.Path, "source.git")).FullName;
        var alias = Path.Combine(state.Path, "alias.git");
        Directory.CreateSymbolicLink(alias, source);
        Assert.False(await RoleImageManifest.LauncherSourceReadyAsync(alias, default));
        Assert.False(await RoleImageManifest.LauncherSourceReadyAsync(source + "/../source.git", default));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Runtime_lookup_pins_a_local_install_and_refuses_untrusted_shadowing(string runtime)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var local = Directory.CreateDirectory(Path.Combine(state.Path, "local", "bin")).FullName;
        var system = Directory.CreateDirectory(Path.Combine(state.Path, "usr", "bin")).FullName;
        var binary = Path.Combine(local, runtime);
        await File.WriteAllTextAsync(binary, "fixture executable");
        var checkedPaths = new List<string>();
        Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path)
        {
            checkedPaths.Add(path);
            return RoleImageManifest.OpenPath(-100, path);
        }
        using (var executable = WorkContainerLauncherCommand.ResolveRuntime(runtime, [local, system], default, Open))
            Assert.Equal("fixture executable", await File.ReadAllTextAsync(executable.ExecutionPath));
        Assert.Equal(new[] { binary }, checkedPaths);
        Assert.Contains("/usr/local/bin", WorkContainerLauncherCommand.RuntimeSearchDirectories);
        Assert.Throws<InvalidDataException>(() => WorkContainerLauncherCommand.ResolveRuntime(
            runtime, [local, system], default, _ => throw new InvalidDataException("untrusted installation")));
        File.Delete(binary);
        await File.WriteAllTextAsync(Path.Combine(system, runtime), "system fixture");
        checkedPaths.Clear();
        using (var executable = WorkContainerLauncherCommand.ResolveRuntime(runtime, [local, system], default, Open))
            Assert.Equal("system fixture", await File.ReadAllTextAsync(executable.ExecutionPath));
        Assert.Equal(new[] { binary, Path.Combine(system, runtime) }, checkedPaths);
        File.Delete(Path.Combine(system, runtime));
        Assert.Throws<IOException>(() => WorkContainerLauncherCommand.ResolveRuntime(runtime, [local, system], default, Open));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Runtime_execution_uses_the_checked_inode_after_a_file_or_parent_name_is_replaced(bool replaceParent)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var directory = Directory.CreateDirectory(Path.Combine(state.Path, "bin")).FullName;
        var binary = Path.Combine(directory, "docker");
        await File.WriteAllTextAsync(binary, "#!/bin/sh\nprintf approved");
        File.SetUnixFileMode(binary, (UnixFileMode)0x1c0);
        using var executable = WorkContainerLauncherCommand.ResolveRuntime("docker", [directory], default,
            path => RoleImageManifest.OpenPath(-100, path));
        var descriptorPath = executable.ExecutionPath;
        if (replaceParent)
        {
            Directory.Move(directory, directory + ".parked");
            Directory.CreateDirectory(directory);
        }
        else File.Move(binary, binary + ".parked");
        await File.WriteAllTextAsync(binary, "#!/bin/sh\nprintf substituted; exit 91");
        File.SetUnixFileMode(binary, (UnixFileMode)0x1c0);
        var result = await ProcessRunner.RunAsync(descriptorPath, []);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("approved", result.StdOut.Trim());
        executable.Dispose();
        Assert.False(File.Exists(descriptorPath));
    }

    [Theory]
    [InlineData("/usr/bin/true")]
    [InlineData("/bin/true")]
    [InlineData("/bin/sh")]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Runtime_capabilities_validate_system_aliases_and_execute_the_pinned_binary(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var handle = RoleImageManifest.OpenRootControlled(path, directory: false, executable: true);
        using var executable = new WorkContainerLauncherCommand.RuntimeExecutable(handle);
        var result = await ProcessRunner.RunAsync(executable.ExecutionPath, path.EndsWith("/sh", StringComparison.Ordinal) ? ["-c", "exit 0"] : []);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Runtime_capabilities_refuse_caller_owned_paths_even_through_system_aliases()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var binary = Path.Combine(state.Path, "docker");
        await File.WriteAllTextAsync(binary, "#!/bin/sh\nexit 0");
        File.SetUnixFileMode(binary, (UnixFileMode)0x1c0);
        File.CreateSymbolicLink(Path.Combine(state.Path, "podman"), "/usr/bin/true");
        Assert.Throws<InvalidDataException>(() => RoleImageManifest.OpenRootControlled(binary, directory: false, executable: true));
        Assert.Throws<InvalidDataException>(() => RoleImageManifest.OpenRootControlled(
            Path.Combine(state.Path, "podman"), directory: false, executable: true));
        Assert.Throws<InvalidDataException>(() => RoleImageManifest.OpenRootControlled("/usr/bin", directory: false, executable: true));
        Assert.Throws<InvalidDataException>(() => RoleImageManifest.OpenRootControlled("/etc/hosts", directory: false, executable: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Snapshot_copy_uses_the_checked_inode_when_a_source_name_is_swapped_for_a_link(bool swapDirectory)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(state.Path, "source.git")).FullName;
        var objects = Directory.CreateDirectory(Path.Combine(source, "objects")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "HEAD"), "ref: refs/heads/main");
        await File.WriteAllTextAsync(Path.Combine(objects, "payload"), "approved repository data");
        var forbidden = Directory.CreateDirectory(Path.Combine(state.Path, "credentials")).FullName;
        // Build disposable credential-shaped content at runtime only.
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(Path.Combine(forbidden, "payload"), secret);
        var destination = Directory.CreateDirectory(Path.Combine(state.Path, "snapshot")).FullName;
        var uid = await ProcessRunner.RunAsync("/usr/bin/id", ["-u"]);
        Assert.Equal(0, uid.ExitCode);
        var owner = uint.Parse(uid.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        using var handle = RoleImageManifest.OpenPath(-100, source, directory: true);
        var swapped = false;
        void Swap(string name)
        {
            if (swapped || name != (swapDirectory ? "objects" : "payload")) return;
            swapped = true;
            if (swapDirectory)
            {
                Directory.Move(objects, Path.Combine(state.Path, "parked-objects"));
                Directory.CreateSymbolicLink(objects, forbidden);
            }
            else
            {
                File.Move(Path.Combine(objects, "payload"), Path.Combine(state.Path, "parked-payload"));
                File.CreateSymbolicLink(Path.Combine(objects, "payload"), Path.Combine(forbidden, "payload"));
            }
        }
        var copy = RoleImageManifest.WalkSourceAsync(handle, destination, owner, default, Swap);
        if (swapDirectory) await Assert.ThrowsAsync<InvalidDataException>(() => copy);
        else await copy;
        Assert.True(swapped);
        Assert.Equal("approved repository data", await File.ReadAllTextAsync(Path.Combine(destination, "objects", "payload")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Snapshot_copy_refuses_links_and_sockets_before_reading_them(bool socketEntry)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(state.Path, "source.git")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "objects"));
        await File.WriteAllTextAsync(Path.Combine(source, "HEAD"), "ref: refs/heads/main");
        var forbidden = Path.Combine(source, "forbidden");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (socketEntry) socket.Bind(new UnixDomainSocketEndPoint(forbidden));
        else File.CreateSymbolicLink(forbidden, Path.Combine(state.Path, "missing-fixture"));
        var uid = await ProcessRunner.RunAsync("/usr/bin/id", ["-u"]);
        Assert.Equal(0, uid.ExitCode);
        using var handle = RoleImageManifest.OpenPath(-100, source, directory: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => RoleImageManifest.WalkSourceAsync(handle, null,
            uint.Parse(uid.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture), default));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Snapshot_copy_preserves_restricted_file_and_directory_read_permissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(state.Path, "source.git")).FullName;
        var objects = Directory.CreateDirectory(Path.Combine(source, "objects")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "HEAD"), "ref: refs/heads/main");
        await File.WriteAllTextAsync(Path.Combine(objects, "restricted"), "fixture data");
        File.SetUnixFileMode(source, (UnixFileMode)0x1e8); // 0750
        File.SetUnixFileMode(objects, (UnixFileMode)0x1c0); // 0700
        File.SetUnixFileMode(Path.Combine(source, "HEAD"), (UnixFileMode)0x1a0); // 0640
        File.SetUnixFileMode(Path.Combine(objects, "restricted"), (UnixFileMode)0x180); // 0600
        var destination = Directory.CreateDirectory(Path.Combine(state.Path, "snapshot")).FullName;
        var uid = await ProcessRunner.RunAsync("/usr/bin/id", ["-u"]);
        Assert.Equal(0, uid.ExitCode);
        using var handle = RoleImageManifest.OpenPath(-100, source, directory: true);
        await RoleImageManifest.WalkSourceAsync(handle, destination,
            uint.Parse(uid.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture), default);
        foreach (var relative in new[] { "", "objects", "HEAD", "objects/restricted" })
            Assert.Equal(File.GetUnixFileMode(Path.Combine(source, relative)), File.GetUnixFileMode(Path.Combine(destination, relative)));
        var originalGroup = await ProcessRunner.RunAsync("/usr/bin/stat", ["--format=%g", Path.Combine(source, "HEAD")]);
        var snapshotGroup = await ProcessRunner.RunAsync("/usr/bin/stat", ["--format=%g", Path.Combine(destination, "HEAD")]);
        Assert.Equal(0, originalGroup.ExitCode);
        Assert.Equal(0, snapshotGroup.ExitCode);
        Assert.Equal(originalGroup.StdOut, snapshotGroup.StdOut);
    }

    [Theory]
    [InlineData("0:600:socket")]
    [InlineData("0:600:fifo")]
    [InlineData("0:600:character special file")]
    [InlineData("0:644:symbolic link")]
    public void Approved_source_content_cannot_include_sockets_links_or_devices(string metadata)
    {
        Assert.False(RoleImageManifest.TrustedMetadata(metadata, directory: false, count: 1));
        Assert.False(RoleImageManifest.TrustedMetadata(metadata, directory: true, count: 1));
    }

    [Fact]
    public void Role_capability_cannot_be_replaced_by_another_approved_role_or_a_rollback_digest()
    {
        var context = LauncherContext() with
        {
            ApprovedImages = RoleImageManifest.Empty().Promote("gate", WorkContainerPolicyTests.Digest, null)
                .Promote("review", WorkContainerPolicyTests.OtherDigest, null),
        };
        foreach (var call in new[] { BoundaryPlan().CreateArguments, BoundaryPlan().PrepareArguments })
        {
            var otherRoleImage = call.Select(value => value == WorkContainerPolicyTests.Digest ? WorkContainerPolicyTests.OtherDigest : value).ToArray();
            Assert.NotNull(WorkContainerLauncherPolicy.Refusal(otherRoleImage, context));
        }
        context = context with { ApprovedImages = context.ApprovedImages.Promote("gate", WorkContainerPolicyTests.OtherDigest, null) };
        Assert.NotNull(WorkContainerLauncherPolicy.Refusal(["pull", "--quiet", WorkContainerPolicyTests.Digest], context));
        Assert.NotNull(WorkContainerLauncherPolicy.Refusal(BoundaryPlan().CreateArguments, new(new HashSet<string>(["agent-work"]))));
        Assert.Null(WorkContainerLauncherPolicy.Refusal(["rm", "--force", "--volumes", BoundaryPlan().ContainerName], context));
    }

    [Theory]
    [InlineData("local", "{}", true)]
    [InlineData("local", "null", true)]
    [InlineData("local", "{\"type\":\"none\",\"o\":\"bind\",\"device\":\"/var\"}", false)]
    [InlineData("local", "{\"type\":\"nfs\",\"device\":\"fixture:/root\"}", false)]
    [InlineData("foreign-driver", "{}", false)]
    public void Daemon_volume_capabilities_refuse_bind_driver_options_and_plugins(string driver, string options, bool admitted)
    {
        var json = "{\"Name\":\"agent-work-fixture\",\"Driver\":\"" + driver
                   + "\",\"Scope\":\"local\",\"Options\":" + options + ",\"Mountpoint\":\"/fixture/volume/_data\"}";
        Assert.Equal(admitted, WorkContainerLauncherCommand.VolumeCapabilityRefusal(json, "agent-work-fixture", out _) is null);
    }

    [Fact]
    public void Effective_mount_inspection_refuses_implicit_image_volumes_and_substituted_bind_sources()
    {
        var plan = BoundaryPlan();
        var paths = VolumePaths(plan);
        var mounts = EffectiveMounts(plan, preparation: false);
        Assert.Null(WorkContainerLauncherPolicy.EffectiveMountsRefusal(JsonSerializer.Serialize(mounts), plan.CreateArguments, paths));
        Assert.Null(WorkContainerLauncherPolicy.EffectiveMountsRefusal(
            JsonSerializer.Serialize(mounts.Take(plan.Mounts.Count)), plan.CreateArguments, paths));
        mounts.Add(new { Type = "volume", Name = "implicit", Source = "/fixture/implicit", Destination = "/usr/bin", RW = true });
        Assert.NotNull(WorkContainerLauncherPolicy.EffectiveMountsRefusal(JsonSerializer.Serialize(mounts), plan.CreateArguments, paths));
        var changed = JsonSerializer.Serialize(EffectiveMounts(plan, preparation: false)).Replace("/srv/mirrors/project.git", "/var", StringComparison.Ordinal);
        Assert.NotNull(WorkContainerLauncherPolicy.EffectiveMountsRefusal(changed, plan.CreateArguments, paths));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_helper_starts_only_after_effective_mount_inspection_and_is_removed_on_refusal(bool injectUnexpectedMount)
    {
        var plan = WorkContainerLaunchPlanner.Plan(WorkContainerPolicyTests.Attempt() with { RepositorySource = "https://example.invalid/project.git" },
            WorkContainerPolicyTests.Digest, Limits, WorkContainerPolicyTests.Settings(), uid: 212_345);
        var calls = new List<string>();
        var paths = VolumePaths(plan);
        var mounts = EffectiveMounts(plan, preparation: true);
        if (injectUnexpectedMount)
            mounts.Add(new { Type = "bind", Source = "/fixture/credentials", Destination = "/bin/sh", RW = false });
        Task<ProcessResult> Run(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            calls.Add(string.Join(' ', args));
            var json = args is ["volume", "inspect", ..]
                ? JsonSerializer.Serialize(new { Name = args[^1], Driver = "local", Scope = "local", Options = new { }, Mountpoint = paths[args[^1]] })
                : args[0] == "inspect" ? JsonSerializer.Serialize(mounts) : "";
            return Task.FromResult(new ProcessResult(0, json, ""));
        }
        var result = await WorkContainerLauncherCommand.ExecuteAdmittedAsync(plan.PrepareArguments, null, Run, default);
        Assert.Equal(injectUnexpectedMount ? WorkContainerLauncherCommand.RefusedExitCode : 0, result.ExitCode);
        Assert.Contains(calls, call => call.StartsWith("create ", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("run ", StringComparison.Ordinal));
        Assert.Equal(!injectUnexpectedMount, calls.Any(call => call.StartsWith("start --attach ", StringComparison.Ordinal)));
        Assert.True(calls.FindIndex(call => call.StartsWith("inspect ", StringComparison.Ordinal)) > calls.FindIndex(call => call.StartsWith("create ", StringComparison.Ordinal)));
        Assert.StartsWith("rm --force --volumes ", calls[^1]);
    }

    [Fact]
    public void Work_command_flags_are_never_interpreted_as_runtime_capabilities()
    {
        var args = BoundaryPlan().CreateArguments.Concat(["--mount", "type=bind,source=/var,target=/work,readonly"]).ToArray();
        Assert.Equal(BoundaryPlan().Mounts.Count, WorkContainerLauncherPolicy.Values(args, "--mount").Count());
    }

    private static Dictionary<string, string> VolumePaths(WorkContainerLaunchPlan plan)
        => plan.Mounts.Where(mount => mount.Type == "volume").ToDictionary(mount => mount.Source, mount => "/fixture/" + mount.Source + "/_data");

    private static List<object> EffectiveMounts(WorkContainerLaunchPlan plan, bool preparation)
    {
        var paths = VolumePaths(plan);
        var mounts = plan.Mounts.Where(mount => !preparation || mount.Type == "volume").Select(mount => (object)new
        {
            Type = mount.Type, Name = mount.Source, Source = mount.Type == "volume" ? paths[mount.Source] : mount.Source,
            Destination = mount.Target, RW = !mount.ReadOnly,
        }).ToList();
        if (!preparation)
            mounts.AddRange(new[] { "/tmp", WorkContainerLaunchPlanner.SecretDirectory }.Select(target => (object)new { Type = "tmpfs", Destination = target }));
        return mounts;
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Disposable_runtime_hides_fixture_credentials_and_sockets_and_overrides_image_entrypoint()
    {
        // Opt in only to an explicitly provisioned disposable daemon, never the
        // machine's default runtime. The base is a pinned role image with Git.
        var endpoint = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_DOCKER_HOST");
        var baseImage = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_IMAGE");
        Skip.IfNot(OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess && endpoint is { Length: > 0 }
                   && RoleImageManifest.IsPinned(baseImage),
            "Requires root, a disposable AGENT_LAUNCHER_TEST_DOCKER_HOST and a pinned AGENT_LAUNCHER_TEST_IMAGE with Git; never uses the host daemon by default.");
        var key = "fixture-" + Guid.NewGuid().ToString("N");
        var root = Directory.CreateDirectory("/var/lib/agent-launcher-" + key).FullName;
        var image = "";
        WorkContainerLaunchPlan? plan = null;
        var volumes = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        Task<ProcessResult> Runtime(IReadOnlyList<string> args, string? stdin, CancellationToken token)
            => ProcessRunner.RunAsync("/usr/bin/docker", ["--host", endpoint!, .. args], stdin: stdin, ct: token);
        async Task<ProcessResult> CheckedRuntime(params string[] args)
        {
            var result = await Runtime(args, null, ct);
            Assert.True(result.ExitCode == 0, result.StdErr);
            return result;
        }
        try
        {
            var daemonLabels = await CheckedRuntime("info", "--format", "{{json .Labels}}");
            Assert.Contains("agent-studio.launcher-test=1", daemonLabels.StdOut, StringComparison.Ordinal);
            var source = Path.Combine(root, "source.git");
            var checkout = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            async Task Git(string directory, params string[] args)
            {
                var result = await ProcessRunner.RunAsync("git", ["-C", directory, .. args], ct: ct);
                Assert.True(result.ExitCode == 0, result.StdErr);
            }
            await Git(checkout, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(checkout, "fixture.txt"), "disposable repository", ct);
            await Git(checkout, "add", "fixture.txt");
            await Git(checkout, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture");
            await Git(root, "clone", "--quiet", "--bare", checkout, source);
            var sha = await ProcessRunner.RunAsync("git", ["-C", checkout, "rev-parse", "HEAD"], ct: ct);
            Assert.Equal(0, sha.ExitCode);
            var credentials = Directory.CreateDirectory(Path.Combine(root, "host-credentials", ".aws")).FullName;
            await File.WriteAllTextAsync(Path.Combine(credentials, "credentials"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), ct);
            var socketPath = Path.Combine(root, "host-runtime.sock");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            await File.WriteAllTextAsync(Path.Combine(root, "Dockerfile"),
                "FROM " + baseImage + "\nENTRYPOINT [\"/bin/sh\",\"-c\",\"touch /work/unexpected-entrypoint; exit 91\"]\n", ct);
            await CheckedRuntime("build", "--quiet", "--iidfile", Path.Combine(root, "image-id"), root);
            image = (await File.ReadAllTextAsync(Path.Combine(root, "image-id"), ct)).Trim();
            var probe = "test ! -e /work/unexpected-entrypoint && test ! -e '" + credentials
                        + "' && test ! -e '" + socketPath + "' && test -z \"$(find /work /cache /mnt/source -type s -print -quit)\""
                        + " && test -z \"$(find /work /cache /mnt/source -name credentials -o -name .aws -o -name .ssh)\"";
            var attempt = WorkContainerPolicyTests.Attempt() with
            {
                AttemptId = key, RepositoryKey = key, RepositorySource = source, ExactSha = sha.StdOut.Trim(),
                Command = ["/bin/sh", "-c", probe],
            };
            plan = WorkContainerLaunchPlanner.Plan(attempt, image, new(2, 512L << 20, 256),
                WorkContainerPolicyTests.Settings() with { Network = "none" }, uid: 212_345);
            foreach (var volume in plan.Mounts.Where(mount => mount.Type == "volume")) volumes.Add(volume.Source);
            var releasePath = Path.Combine(root, "release.json");
            await File.WriteAllTextAsync(releasePath, (RoleImageManifest.Empty().Promote(attempt.Role, image, "fixture") with { Sources = [source] }).Serialize(), ct);
            var release = await RoleImageManifest.LoadLauncherReleaseAsync(releasePath, ct);
            var context = new WorkContainerLauncherContext(new HashSet<string>(["none"]))
            {
                ApprovedImages = release, BindSources = new HashSet<string>(release.Sources), Volumes = volumes,
            };
            Assert.True(await RoleImageManifest.LauncherSourceReadyAsync(source, ct));
            var nestedSocket = Path.Combine(source, "objects", "fixture.sock");
            using (var forbidden = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                forbidden.Bind(new UnixDomainSocketEndPoint(nestedSocket));
                Assert.False(await RoleImageManifest.LauncherSourceReadyAsync(source, ct));
            }
            File.Delete(nestedSocket);
            var link = Path.Combine(source, "credential-link");
            Directory.CreateSymbolicLink(link, credentials);
            Assert.False(await RoleImageManifest.LauncherSourceReadyAsync(source, ct));
            Directory.Delete(link);
            foreach (var args in new[] { plan.PrepareArguments, plan.CreateArguments })
            {
                Assert.Null(WorkContainerLauncherPolicy.Refusal(args, context));
                var result = await WorkContainerLauncherCommand.ExecuteAdmittedAsync(args, null, Runtime, ct, Path.Combine(root, "snapshots"));
                Assert.True(result.ExitCode == 0, result.StdErr);
            }
            var snapshot = Path.Combine(root, "snapshots", plan.ContainerName, "source.git");
            Assert.True(Directory.Exists(snapshot));
            // Swap the originally admitted source before the later start call.
            // The daemon must consume only the private validated snapshot.
            Directory.Move(source, Path.Combine(root, "parked-source.git"));
            Directory.CreateSymbolicLink(source, Path.GetDirectoryName(credentials)!);
            await CheckedRuntime("start", plan.ContainerName);
            var waited = await CheckedRuntime("wait", plan.ContainerName);
            Assert.Equal("0", waited.StdOut.Trim());
            var effective = await CheckedRuntime("inspect", "--format", "{{json .Mounts}}", plan.ContainerName);
            Assert.Contains(snapshot, effective.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(credentials, effective.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(socketPath, effective.StdOut, StringComparison.Ordinal);
            var resultDirectory = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
            if (resultDirectory is { Length: > 0 })
                await File.WriteAllTextAsync(Path.Combine(resultDirectory, "disposable-runtime-mounts.json"), effective.StdOut, ct);
        }
        finally
        {
            if (plan is not null)
            {
                await WorkContainerLauncherCommand.ExecuteAdmittedAsync(["rm", "--force", "--volumes", plan.ContainerName],
                    null, Runtime, default, Path.Combine(root, "snapshots"));
                Assert.False(Directory.Exists(Path.Combine(root, "snapshots", plan.ContainerName)));
                await Runtime(["rm", "--force", "--volumes", plan.PrepareContainerName], null, default);
                foreach (var volume in volumes) await Runtime(["volume", "rm", "--force", volume], null, default);
            }
            if (image.Length > 0) await Runtime(["image", "rm", "--force", image], null, default);
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkContainerLaunchPlan BoundaryPlan()
        => WorkContainerLaunchPlanner.Plan(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            Limits, WorkContainerPolicyTests.Settings() with { Network = "agent-work" }, uid: 212_345);

    [Fact]
    public void Narrow_launcher_refuses_every_create_while_the_zone_policy_is_unavailable()
    {
        var plan = WorkContainerLaunchPlanner.Plan(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            Limits, WorkContainerPolicyTests.Settings() with { Network = "agent-work" }, uid: 212_345);
        var refusal = WorkContainerLauncherPolicy.Refusal(plan.CreateArguments,
            new WorkContainerLauncherContext(new HashSet<string>(StringComparer.Ordinal)));
        Assert.StartsWith("zone-policy-unavailable", refusal);
    }

    [Fact]
    public void Probe_verdict_passes_only_when_every_containment_check_passed()
    {
        var log = string.Join('\n',
            "2026-11-03T10:00:00.000000000Z AGENT_PHASE first-command",
            "2026-11-03T10:00:01.000000000Z AGENT_PROBE identity pass uid=212345",
            "2026-11-03T10:00:01.000000000Z AGENT_PROBE read pass /etc/agent-runner/runner-credential",
            "2026-11-03T10:00:01.000000000Z AGENT_PROBE read pass /var/lib/agent-host/.claude",
            "2026-11-03T10:00:01.000000000Z AGENT_PROBE checkout pass only=agent-studio.git",
            "2026-11-03T10:00:01.000000000Z AGENT_PROBE socket pass none",
            "2026-11-03T10:00:01.000000000Z AGENT_PROBE environment pass none",
            "2026-11-03T10:00:02.000000000Z AGENT_PROBE egress pass 192.0.2.10:443 blocked",
            "2026-11-03T10:00:02.000000000Z AGENT_PHASE exit 0");
        var report = ContainmentProbeReport.Parse(log);
        Assert.True(report.Passed);
        Assert.Equal(7, report.Checks.Count);

        var leaked = ContainmentProbeReport.Parse(log.Replace(
            "AGENT_PROBE socket pass none", "AGENT_PROBE socket fail /var/run/docker.sock", StringComparison.Ordinal));
        Assert.False(leaked.Passed);
        Assert.Contains(leaked.Checks, check => check is { Kind: "socket", Passed: false });

        var missing = ContainmentProbeReport.Parse(string.Join('\n',
            log.Split('\n').Where(line => !line.Contains("AGENT_PROBE egress", StringComparison.Ordinal))));
        Assert.False(missing.Passed);
        Assert.Contains("egress", missing.MissingKinds);
    }

    [Fact]
    public void Probe_script_checks_every_forbidden_path_the_socket_and_the_blocked_address()
    {
        var script = ContainmentProbeScript.Build(
            ["/etc/agent-runner/runner-credential", "/srv/projects/other/checkout"], ["192.0.2.10:443"], "agent-studio");

        Assert.Contains("probe_read '/etc/agent-runner/runner-credential'", script);
        Assert.Contains("probe_read '/srv/projects/other/checkout'", script);
        Assert.Contains("probe_egress '192.0.2.10' '443'", script);
        Assert.Contains("docker.sock", script);
        Assert.Contains("probe_checkout 'agent-studio'", script);
        Assert.Throws<ArgumentException>(() => ContainmentProbeScript.Build(["/etc/'; rm -rf /work; '"], [], "agent-studio"));
        Assert.Throws<ArgumentException>(() => ContainmentProbeScript.Build([], ["example;:443"], "agent-studio"));
    }

    [Fact]
    public async Task Probe_attempt_runs_through_the_contained_executor_and_records_the_verdict()
    {
        using var state = new TempDir();
        var runtime = new WorkContainerExecutorTests.FakeRuntime
        {
            Log = string.Join('\n',
                "2026-11-03T10:00:00.000000000Z AGENT_PHASE materialize",
                "2026-11-03T10:00:00.500000000Z AGENT_SHA_VERIFIED " + WorkContainerPolicyTests.Sha,
                "2026-11-03T10:00:01.000000000Z AGENT_PHASE first-command",
                "2026-11-03T10:00:01.000000000Z AGENT_PROBE identity pass uid=212345",
                "2026-11-03T10:00:01.000000000Z AGENT_PROBE read pass /etc/agent-runner/runner-credential",
                "2026-11-03T10:00:01.000000000Z AGENT_PROBE checkout pass only=agent-studio.git",
                "2026-11-03T10:00:01.000000000Z AGENT_PROBE socket pass none",
                "2026-11-03T10:00:01.000000000Z AGENT_PROBE environment pass none",
                "2026-11-03T10:00:02.000000000Z AGENT_PROBE egress pass 192.0.2.10:443 blocked",
                "2026-11-03T10:00:02.000000000Z AGENT_PHASE exit 0"),
        };
        var settings = WorkContainerPolicyTests.Settings() with { StateDirectory = state.Path, Network = "agent-work" };
        var executor = new WorkContainerExecutor(runtime, settings, () => new HostMemory(56L << 30, 62L << 30));
        var probe = new ContainmentProbeOptions("host-a", "agent-studio", "/srv/mirrors/project.git",
            WorkContainerPolicyTests.Sha, WorkContainerPolicyTests.Digest,
            ["/etc/agent-runner/runner-credential"], ["192.0.2.10:443"], Path.Combine(state.Path, "probe"));

        var result = await ContainmentProbe.RunAsync(executor, probe, default);

        Assert.True(result.Report.Passed, string.Join("; ", result.Report.Checks.Select(check => check.ToString())));
        Assert.True(result.Receipt.CleanupVerified);
        Assert.True(File.Exists(Path.Combine(state.Path, "probe", "containment-probe.json")));
        Assert.Contains(runtime.Calls, call => call.StartsWith("prepare ", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Calls, call => call.Contains("docker.sock", StringComparison.Ordinal));
    }

    [Fact]
    public void Canary_verdict_file_records_go_only_from_a_passing_rollout_threshold()
    {
        using var state = new TempDir();
        Assert.False(WorkContainerCanaryVerdict.IsGo(state.Path));
        var fail = new RolloutVerdict(false, [new RolloutCheck("cold image pull", "<= 180 s", "not measured", false)]);
        WorkContainerCanaryVerdict.Record(state.Path, fail, "host-a", RecordedAt);
        Assert.False(WorkContainerCanaryVerdict.IsGo(state.Path));

        var go = new RolloutVerdict(true, [new RolloutCheck("cold image pull", "<= 180 s", "61.0 s", true)]);
        WorkContainerCanaryVerdict.Record(state.Path, go, "host-a", RecordedAt);
        Assert.True(WorkContainerCanaryVerdict.IsGo(state.Path));

        File.WriteAllText(Path.Combine(state.Path, WorkContainerCanaryVerdict.FileName), "{ not json");
        Assert.False(WorkContainerCanaryVerdict.IsGo(state.Path));
    }

    [Theory]
    [InlineData("Groups:\t1000 998", true)]
    [InlineData("Groups:\t1000 27", false)]
    [InlineData("Groups:", false)]
    public void Runtime_group_membership_is_detected_from_the_process_groups(string groups, bool member)
        => Assert.Equal(member, WorkContainerRuntimeAccess.HasRuntimeGroup(groups,
            "root:x:0:\nsudo:x:27:agent\ndocker:x:998:agent\nagent:x:1000:\n"));

    [Fact]
    public async Task Launcher_client_sends_one_request_and_reads_the_launcher_answer()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;
        using var state = new TempDir();
        var path = Path.Combine(state.Path, "launcher.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptAsync();
            await using var stream = new NetworkStream(connection);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var request = JsonSerializer.Deserialize<WorkContainerLauncherRequest>(
                (await reader.ReadLineAsync())!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var answer = JsonSerializer.Serialize(new WorkContainerLauncherResponse(
                7, string.Join(' ', request.Arguments), request.Stdin ?? "none"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"));
        });

        var result = await WorkContainerLauncherClient.RunAsync(path, ["start", "agent-work-a-f1"], "stdin-value", default);
        await server;

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("start agent-work-a-f1", result.StdOut);
        Assert.Equal("stdin-value", result.StdErr);
        var missing = await WorkContainerLauncherClient.RunAsync(Path.Combine(state.Path, "absent.sock"), ["version"], null, default);
        Assert.Equal(125, missing.ExitCode);
    }

    [Fact]
    public async Task Launch_command_refuses_to_run_without_root()
    {
        if (Environment.IsPrivilegedProcess) return;
        var error = new StringWriter();
        var exit = await WorkContainerLauncherCommand.RunAsync(["--stdio"], new StringReader("{}"), TextWriter.Null, error, default);
        Assert.Equal(2, exit);
        Assert.Contains("only as root", error.ToString(), StringComparison.Ordinal);
    }

    private static readonly DateTime RecordedAt = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private static WorkContainerLauncherContext LauncherContext()
        => new(new HashSet<string>(["agent-work"], StringComparer.Ordinal))
        {
            ApprovedImages = RoleImageManifest.Empty().Promote("gate", WorkContainerPolicyTests.Digest, "fixture"),
            BindSources = new HashSet<string>(["/srv/mirrors/project.git"], StringComparer.Ordinal),
            Volumes = BoundaryPlan().Mounts.Where(mount => mount.Type == "volume").Select(mount => mount.Source).ToHashSet(StringComparer.Ordinal),
        };

    private static string[] CapAdds(IReadOnlyList<string> args)
        => args.Select((value, index) => (value, index))
            .Where(item => item.value == "--cap-add")
            .Select(item => args[item.index + 1]).ToArray();

    private static void AssertPair(IReadOnlyList<string> args, string name, string value)
    {
        var index = args.ToList().IndexOf(name);
        Assert.True(index >= 0, $"{name} missing");
        Assert.Equal(value, args[index + 1]);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("sm11-").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
