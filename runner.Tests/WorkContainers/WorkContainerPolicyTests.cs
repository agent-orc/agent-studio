using AgentRunner.WorkContainers;
using Xunit;

namespace AgentRunner.Tests.WorkContainers;

public class WorkContainerPolicyTests
{
    internal static readonly string Digest = "ghcr.io/agent-orc/agent-work@sha256:" + new string('a', 64);
    internal static readonly string OtherDigest = "ghcr.io/agent-orc/agent-work@sha256:" + new string('b', 64);
    internal static readonly string Sha = new('c', 40);

    internal static WorkContainerSettings Settings(int maxHalfCores = 20, long maxMemoryGiB = 48) => new(
        WorkContainerMode.Canary, "docker", "/unused/images.json", Path.Combine(Path.GetTempPath(), "wc-unused"),
        maxHalfCores, maxMemoryGiB << 30, 2048L << 20, 7, "bridge");

    private static readonly WorkContainerHostBudget Roomy = new(0, 56L << 30, 62L << 30);

    [Theory]
    [InlineData(0, 3, 3, 0, "grant-empty")]
    [InlineData(3, 0, 0, 0, "allocation-stale")]
    [InlineData(3, 4, 3, 0, "allocation-stale")]
    [InlineData(21, 3, 3, 0, "profile-ceiling")]
    [InlineData(4, 3, 3, 17, "host-grant-exhausted")]
    public void Admission_denies_every_over_budget_or_stale_grant(
        int halfCores, long version, long acknowledged, int reserved, string reason)
    {
        var verdict = WorkContainerAdmissionPolicy.Decide(
            new WorkContainerGrant(version, acknowledged, halfCores), Roomy with { ReservedHalfCores = reserved }, Settings());

        Assert.False(verdict.Admitted);
        Assert.Null(verdict.Limits);
        Assert.StartsWith(reason, verdict.Reason);
    }

    [Fact]
    public void Admission_enforces_profile_memory_ceiling_and_memory_floor()
    {
        var ceiling = WorkContainerAdmissionPolicy.Decide(new WorkContainerGrant(1, 1, 4), Roomy, Settings(maxMemoryGiB: 6));
        Assert.StartsWith("profile-memory-ceiling", ceiling.Reason);

        // 62 GiB host: floor is 15% (9.3 GiB); 12 GiB available minus 8 GiB requested breaks it.
        var floor = WorkContainerAdmissionPolicy.Decide(new WorkContainerGrant(1, 1, 4),
            Roomy with { AvailableMemoryBytes = 12L << 30 }, Settings());
        Assert.StartsWith("memory-floor", floor.Reason);
    }

    [Fact]
    public void Admitted_limits_are_exactly_the_half_core_grant()
    {
        var verdict = WorkContainerAdmissionPolicy.Decide(new WorkContainerGrant(5, 5, 3), Roomy with { ReservedHalfCores = 17 }, Settings());

        Assert.True(verdict.Admitted);
        Assert.Equal("1.5", verdict.Limits!.Cpus);
        Assert.Equal(3 * (2048L << 20), verdict.Limits.MemoryBytes);
        Assert.Equal(WorkerResourceEnvelope.MinimumTasksMax, verdict.Limits.PidsLimit);
        Assert.Equal(2, verdict.Limits.ProcessorHint);
    }

    [Fact]
    public void Settings_default_to_off_and_keep_the_host_reserve()
    {
        var settings = WorkContainerSettings.FromEnvironment(_ => null, 12, 62L << 30, "/state");

        Assert.Equal(WorkContainerMode.Off, settings.Mode);
        Assert.Equal(20, settings.ProfileMaxHalfCores);
        Assert.Equal("bridge", settings.Network);
        Assert.Throws<InvalidOperationException>(() => WorkContainerSettings.FromEnvironment(
            name => name == WorkContainerSettings.NetworkVariable ? "host" : null, 12, 62L << 30, "/state"));
        Assert.Throws<InvalidOperationException>(() => WorkContainerSettings.FromEnvironment(
            name => name == WorkContainerSettings.ModeVariable ? "always" : null, 12, 62L << 30, "/state"));
    }

    [Theory]
    [InlineData("0-11\n", 12)]
    [InlineData("0-5,8,10-11", 9)]
    [InlineData("0", 1)]
    [InlineData("garbage", 0)]
    public void Host_core_count_reads_the_kernel_cpu_list(string list, int expected)
        => Assert.Equal(expected, WorkContainerSettings.CountCpuList(list));

    [Fact]
    public void Role_image_manifest_rejects_tags_and_rolls_back_to_the_previous_digest()
    {
        Assert.Throws<InvalidDataException>(() => RoleImageManifest.Parse(
            """{"schemaVersion":1,"roles":{"coding":{"current":"ghcr.io/agent-orc/agent-work:latest"}}}"""));

        var promoted = RoleImageManifest.Empty().Promote("gate", Digest, "0.9.5").Promote("gate", OtherDigest, "0.9.6");
        Assert.Equal(OtherDigest, promoted.Resolve("gate"));
        Assert.Equal(Digest, promoted.Roles["gate"].Previous);

        var rolledBack = RoleImageManifest.Parse(promoted.Serialize()).Rollback("gate");
        Assert.Equal(Digest, rolledBack.Resolve("gate"));
        Assert.Equal(OtherDigest, rolledBack.Roles["gate"].Previous);
        Assert.Throws<InvalidOperationException>(() => RoleImageManifest.Empty().Promote("coding", Digest, null).Rollback("coding"));
        Assert.Throws<ArgumentException>(() => RoleImageManifest.Empty().Promote("coding", "agent-work:v1", null));
    }

    [Fact]
    public void Launch_plan_is_non_root_read_only_fenced_and_bounded_by_the_grant()
    {
        var limits = new WorkContainerLimits(3, 3L << 30, 1024);
        var plan = WorkContainerLaunchPlanner.Plan(Attempt(), Digest, limits, Settings());
        var args = plan.CreateArguments;

        Assert.Equal("agent-work-attempt-42-f7", plan.ContainerName);
        AssertPair(args, "--user", "10001:10001");
        AssertPair(args, "--restart", "no");
        AssertPair(args, "--cpus", "1.5");
        AssertPair(args, "--memory", (3L << 30).ToString());
        AssertPair(args, "--memory-swap", (3L << 30).ToString());
        AssertPair(args, "--pids-limit", "1024");
        AssertPair(args, "--cap-drop", "ALL");
        Assert.Contains("--read-only", args);
        Assert.Contains(args, value => value.StartsWith("/run/agent-secrets:", StringComparison.Ordinal) && value.Contains("mode=0700"));
        Assert.Contains("agent-studio.fence=7", args);
        Assert.Contains("agent-studio.attempt=attempt-42", args);
        Assert.Contains("type=volume,source=agent-cache-npm-v7,target=/cache/npm", args);
        Assert.Contains("type=volume,source=agent-cache-repo-v7,target=/cache/repo", args);
        Assert.Contains("type=bind,source=/srv/mirrors/project.git,target=/mnt/source,readonly", args);
        Assert.Contains("AGENT_EXPECTED_SHA=" + Sha, args);
        Assert.DoesNotContain(args, value => value.Contains(".sock", StringComparison.Ordinal));
        Assert.Equal(Digest, args[args.ToList().IndexOf("/bin/sh") - 1]);
    }

    [Fact]
    public void Entry_script_names_shared_cache_paths_uniquely_per_attempt()
    {
        // Regression: $$ is 1 in every container, so a killed attempt's
        // "mirror.tmp.1" made the next cold clone fail with git exit 128.
        Assert.DoesNotContain("$$", WorkContainerLaunchPlanner.EntryScript);
        Assert.Contains("tmp=\"$mirror.tmp.$unique\"", WorkContainerLaunchPlanner.EntryScript);
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/user/1000/podman/podman.sock")]
    [InlineData("/home/agent/.codex")]
    [InlineData("/home/agent/.claude/credentials")]
    [InlineData("/")]
    public void Launch_plan_never_mounts_a_runtime_socket_or_provider_home(string source)
    {
        Assert.Throws<ArgumentException>(() => WorkContainerLaunchPlanner.ValidateMount(new("bind", source, "/mnt/x", true)));
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("DB_PASSWORD")]
    public void Launch_plan_refuses_secret_values_in_the_environment(string name)
    {
        var attempt = Attempt() with { Environment = new Dictionary<string, string> { [name] = "x" } };
        Assert.Throws<ArgumentException>(() => WorkContainerLaunchPlanner.Plan(attempt, Digest, new(2, 1L << 30, 1024), Settings()));
    }

    [Fact]
    public void Launch_plan_requires_a_pinned_image_exact_sha_and_bare_local_source()
    {
        var limits = new WorkContainerLimits(2, 1L << 30, 1024);
        Assert.Throws<ArgumentException>(() => WorkContainerLaunchPlanner.Plan(Attempt(), "agent-work:latest", limits, Settings()));
        Assert.Throws<ArgumentException>(() => WorkContainerLaunchPlanner.Plan(Attempt() with { ExactSha = "main" }, Digest, limits, Settings()));
        Assert.Throws<ArgumentException>(() => WorkContainerLaunchPlanner.Plan(Attempt() with { RepositorySource = "/srv/checkout" }, Digest, limits, Settings()));
        Assert.Throws<ArgumentException>(() => WorkContainerLaunchPlanner.Plan(Attempt() with { Fence = 0 }, Digest, limits, Settings()));
    }

    [Theory]
    [InlineData(null, 7, true, WorkResourceAction.FlushAndRemove, "attempt-unknown")]
    [InlineData(false, 7, true, WorkResourceAction.FlushAndRemove, "attempt-terminal")]
    [InlineData(true, 8, true, WorkResourceAction.FlushAndRemove, "fence-superseded")]
    [InlineData(true, 7, false, WorkResourceAction.FlushAndRemove, "exited-not-restartable")]
    [InlineData(true, 7, true, WorkResourceAction.Keep, "live-attempt-current-fence")]
    public void Reconciliation_keeps_only_a_running_container_of_the_live_fence(
        bool? live, long authorityFence, bool running, WorkResourceAction action, string reason)
    {
        var resource = new ManagedWorkResource("container", "agent-work-a-f7", "a", "lease-a", 7, running);
        var authority = live is null ? null : new WorkAttemptAuthority("a", "lease-a", authorityFence, live.Value);

        var verdict = WorkContainerReconciliationPolicy.Decide(resource, authority);

        Assert.Equal(action, verdict.Action);
        Assert.StartsWith(reason, verdict.Reason);
    }

    [Fact]
    public void Reconciliation_removes_a_resource_whose_lease_changed_at_the_same_fence()
    {
        var verdict = WorkContainerReconciliationPolicy.Decide(
            new ManagedWorkResource("container", "n", "a", "lease-old", 7, true), new WorkAttemptAuthority("a", "lease-new", 7, true));
        Assert.Equal(WorkResourceAction.FlushAndRemove, verdict.Action);
    }

    [Theory]
    [InlineData(true, false, WorkContainerMode.Canary, true, true, WorkExecutorKind.Process, false)]
    [InlineData(false, false, WorkContainerMode.Canary, true, true, WorkExecutorKind.Process, false)]
    [InlineData(false, true, WorkContainerMode.Off, true, true, WorkExecutorKind.Process, false)]
    [InlineData(false, true, WorkContainerMode.Shadow, true, true, WorkExecutorKind.Process, true)]
    [InlineData(false, true, WorkContainerMode.Canary, false, true, WorkExecutorKind.Process, true)]
    [InlineData(false, true, WorkContainerMode.Canary, true, false, WorkExecutorKind.Process, true)]
    [InlineData(false, true, WorkContainerMode.Canary, true, true, WorkExecutorKind.Container, false)]
    public void Executor_selection_matrix(bool windows, bool linux, WorkContainerMode mode, bool runtime, bool pinned,
        WorkExecutorKind kind, bool shadow)
    {
        var selection = WorkExecutorSelectionPolicy.Select(windows, linux, mode, runtime, pinned);
        Assert.Equal(kind, selection.Kind);
        Assert.Equal(shadow, selection.RecordContainerShadow);
    }

    [Fact]
    public void Rollout_threshold_is_go_only_when_every_recorded_number_holds()
    {
        var process = new ExecutorMeasurement(new(90, 2_000, 3_000), new(4, 10_000, 12_000), new(100, 5_000, 6_000), new(100, 8_000, 9_000), 1.0);
        var container = new ExecutorMeasurement(new(90, 4_000, 7_500), new(4, 12_000, 15_000), new(100, 5_400, 7_000), new(100, 12_000, 14_000), 1.0);
        var evidence = new RolloutEvidence(container, process, 60_000, true, 100, 100, 0, 3, 3, 0);

        Assert.True(WorkContainerRolloutThreshold.Evaluate(evidence).Go);

        var slowGate = WorkContainerRolloutThreshold.Evaluate(evidence with { Container = container with { GateWall = new(100, 5_600, 7_000) } });
        Assert.False(slowGate.Go);
        Assert.Contains(slowGate.Checks, check => check.Name == "gate wall p50" && !check.Passed);
        Assert.False(WorkContainerRolloutThreshold.Evaluate(evidence with { LiveResidue = 1 }).Go);
        Assert.False(WorkContainerRolloutThreshold.Evaluate(evidence with { Resurrections = 1 }).Go);
        Assert.False(WorkContainerRolloutThreshold.Evaluate(evidence with { ImagePullMs = null }).Go);
        Assert.False(WorkContainerRolloutThreshold.Evaluate(evidence with { ImagePullCold = false }).Go);
    }

    [Fact]
    public void Latency_summary_uses_nearest_rank_percentiles()
    {
        var summary = LatencySummary.Of(Enumerable.Range(1, 10).Select(value => (double?)value).Append(null));
        Assert.Equal(10, summary.Count);
        Assert.Equal(5, summary.P50);
        Assert.Equal(9, summary.P90);
    }

    internal static WorkContainerAttempt Attempt() => new(
        "agent-runner-01", "gate", "attempt-42", "lease-42", 7, 3, "agent-studio", "/srv/mirrors/project.git", Sha,
        ["/bin/sh", "-c", "true"]);

    private static void AssertPair(IReadOnlyList<string> args, string name, string value)
    {
        var index = args.ToList().IndexOf(name);
        Assert.True(index >= 0, $"{name} missing");
        Assert.Equal(value, args[index + 1]);
    }
}
