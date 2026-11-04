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
            { "foreign name", Replace("--name", "studio-api") },
            { "unpinned image", [.. plan.CreateArguments.Take(image), "agent-work:latest", .. plan.CreateArguments.Skip(image + 1)] },
            { "foreign entry", [.. plan.CreateArguments.Take(image + 1), "/bin/sh", "-c", "cat /etc/shadow"] },
            { "exec any command", ["exec", plan.ContainerName, "/bin/sh", "-c", "id"] },
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
        => new(new HashSet<string>(["agent-work"], StringComparer.Ordinal));

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
