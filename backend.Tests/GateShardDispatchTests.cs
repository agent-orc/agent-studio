using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: the dispatcher side of gate sharding. It hands every host the
/// same recorded timings, spans hosts only when the subject declares a
/// sharded suite and more than one gate slot is free, merges one verdict, and
/// records complete timings for the next plan. The gate log lists shards,
/// hosts, durations and test counts.
/// </summary>
public sealed class GateShardDispatchTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-shard-dispatch-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero));

    public GateShardDispatchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException exception) { AgentStudio.Diagnostics.SilentCatch.Note(exception, "gate shard dispatch temp dir"); }
    }

    private static GateShardEvidence Shard(int index, string host, string outcome = GateShardOutcomes.Passed)
        => new(index, host, index % 2, 3, 1200, 61_000 + index, 0, outcome, new GateTestCounts(250, 249, 0, 1), []);

    private static GateShardSuiteEvidence Suite(string host, int count, params int[] indexes)
        => new("backend-routine", "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound",
            count, GateShardCountSources.ExpectedDuration, GateShardStrategies.Hash, "plan-1", 2, 64_000,
            indexes.Select(index => Shard(index, host)).ToArray())
        {
            Hosts = [host],
            SerialWallMs = indexes.Length * 61_000L,
            ClassDurationsMs = indexes.ToDictionary(index => $"Class{index}Tests", index => 1000d + index),
        };

    private static BuildTestGateResult Green(params GateShardSuiteEvidence[] suites)
        => new(BuildTestGateVerdict.Ok, 0, 70_000, "", "verify gate passed", true, false)
        {
            ExpectedSha = Sha,
            TestedSha = Sha,
            TestShards = suites,
        };

    private static BuildTestGateRequest Request() => new("/srv/repo", Sha, "test") { Project = "agent-studio" };

    private const string SuiteCommand =
        "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound";

    private static Func<BuildTestGateRequest, CancellationToken, Task<IReadOnlyList<ProjectTestSuite>>> Subject(int? shards)
        => (_, _) => Task.FromResult<IReadOnlyList<ProjectTestSuite>>(
            [new ProjectTestSuite("backend-routine", "routine", 1440, SuiteCommand) { Shards = shards }]);

    private ShardedRemoteGateTransport Transport(GateShardHost primary, IReadOnlyList<GateShardHost> additional,
        GateShardTimingStore store, int? shards = 2)
        => new(primary, additional, store, NullLogger<ShardedRemoteGateTransport>.Instance, _clock, Subject(shards));

    private sealed class FakeHostTransport(Func<BuildTestGateRequest, CancellationToken, Task<BuildTestGateResult>> run)
        : IRemoteGateTransport
    {
        public List<BuildTestGateRequest> Requests { get; } = [];

        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request, IReadOnlyList<string>? changedFiles,
            BuildProfile? profile, PostStepMode mode, TimeSpan timeout, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return run(request, ct);
        }
    }

    private GateShardTimingStore Store() => new(Path.Combine(_root, "timings"), _clock);

    private GateSlotUsageTracker Slots(int busy, int total)
    {
        var tracker = new GateSlotUsageTracker(clock: _clock);
        tracker.Configure(total);
        for (var i = 0; i < busy; i++) tracker.Acquired("run-" + i, total);
        return tracker;
    }

    [Fact]
    public async Task Without_a_free_slot_on_a_second_host_the_run_stays_whole_on_the_primary()
    {
        var primary = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("kori-gate", 4, 0, 1, 2, 3))));
        var secondary = new FakeHostTransport((_, _) => throw new InvalidOperationException("must not be used"));
        var transport = Transport(
            new GateShardHost("kori-gate", primary, Slots(0, 3)),
            [new GateShardHost("agent-runner-01", secondary, Slots(1, 1))],
            Store());

        var result = await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        var sent = Assert.Single(primary.Requests);
        Assert.Null(sent.Sharding!.Part);
        Assert.Same(GateShardTimingSnapshot.Empty, sent.Sharding.Timings);
        Assert.Empty(secondary.Requests);
    }

    [Fact]
    public async Task Two_free_slots_on_two_hosts_split_the_run_and_both_hosts_get_the_same_timings()
    {
        var store = Store();
        store.Record("agent-studio", [Suite("kori-gate", 2, 0, 1)]);
        var primary = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("kori-gate", 2, 0))));
        var secondary = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("agent-runner-01", 2, 1))));
        var transport = Transport(
            new GateShardHost("kori-gate", primary, Slots(1, 2)),
            [new GateShardHost("agent-runner-01", secondary, Slots(0, 1))],
            store);

        var result = await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(new GateShardPart(0, 2), primary.Requests.Single().Sharding!.Part);
        Assert.Equal(new GateShardPart(1, 2), secondary.Requests.Single().Sharding!.Part);
        var timings = primary.Requests.Single().Sharding!.Timings!.For("backend-routine")!;
        Assert.Equal(timings.ClassDurationsMs, secondary.Requests.Single().Sharding!.Timings!.For("backend-routine")!.ClassDurationsMs);
        Assert.Equal(122_000, timings.SerialWallMs);
        var suite = Assert.Single(result.TestShards);
        Assert.Equal(["kori-gate", "agent-runner-01"], suite.Hosts);
        Assert.Equal([0, 1], suite.Shards.Select(shard => shard.Index));
    }

    [Fact]
    public void Placement_needs_a_sharded_suite_and_a_free_primary_slot_and_skips_busy_or_unreachable_hosts()
    {
        var now = _clock.GetUtcNow();
        static GateShardHostState Host(int busy, int total, string reachability = "reachable", DateTimeOffset? failedAt = null)
            => new(new GateSlotUsage(busy, total), reachability, failedAt);

        Assert.Empty(GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(2, 2), [Host(0, 1)], 2, now));
        Assert.Empty(GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(0, 2), [Host(0, 1)], 1, now));
        Assert.Empty(GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(0, 2), [Host(0, 1)], 0, now));
        Assert.Equal([1], GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(0, 2),
        [
            Host(1, 1),
            Host(0, 2, "unverified"),
            Host(0, 2, "unreachable", now - TimeSpan.FromMinutes(1)),
        ], 4, now));
    }

    [Fact]
    public void A_run_never_uses_more_hosts_than_its_shard_count()
    {
        GateShardHostState[] free = [.. Enumerable.Range(0, 4).Select(_ => new GateShardHostState(new GateSlotUsage(0, 1), "reachable", null))];

        Assert.Equal([0], GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(0, 2), free, 2, _clock.GetUtcNow()));
        Assert.Equal([0, 1], GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(0, 2), free, 3, _clock.GetUtcNow()));
    }

    [Fact]
    public void The_dispatcher_counts_only_declared_shardable_suites_that_plan_to_several_shards()
    {
        ProjectTestSuite Suite(string id, int? shards, string command = SuiteCommand, int seconds = 1440)
            => new(id, "routine", seconds, command) { Shards = shards };
        var empty = GateShardTimingSnapshot.Empty;

        Assert.Equal(0, GateShardPlacementPolicy.PlannedShardCount([Suite("a", null), Suite("b", 1)], empty));
        Assert.Equal(0, GateShardPlacementPolicy.PlannedShardCount(
            [Suite("a", 3, "dotnet test A.Tests/A.Tests.csproj --filter Category!=MachineBound")], empty));
        Assert.Equal(3, GateShardPlacementPolicy.PlannedShardCount([Suite("a", null), Suite("b", 3)], empty));
        Assert.Equal(4, GateShardPlacementPolicy.PlannedShardCount([Suite("a", ProjectTestSuite.AutoShards)], empty));
        Assert.Equal(1, GateShardPlacementPolicy.PlannedShardCount([Suite("a", ProjectTestSuite.AutoShards, seconds: 60)], empty));
    }

    [Fact]
    public async Task A_free_shard_host_without_a_sharded_suite_leaves_the_run_whole_on_the_primary()
    {
        var primary = new FakeHostTransport((_, _) => Task.FromResult(Green()));
        var secondary = new FakeHostTransport((_, _) => throw new InvalidOperationException("must not be used"));
        var transport = Transport(
            new GateShardHost("kori-gate", primary, Slots(0, 2)),
            [new GateShardHost("agent-runner-01", secondary, Slots(0, 1))],
            Store(), shards: null);

        var result = await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Null(Assert.Single(primary.Requests).Sharding!.Part);
        Assert.Empty(secondary.Requests);
    }

    [Fact]
    public async Task A_healthy_pull_host_is_the_primary_and_receives_an_unsplit_run()
    {
        var pullStore = new PullGateStore(Path.Combine(_root, "pull"), _clock);
        pullStore.Register("gate-1", "kori-gate");
        var pull = new FakeHostTransport((request, _) => Task.FromResult(
            request.Sharding?.Part is null ? Green() : Green(Suite("kori-gate", 2, 0))));
        var ssh = new FakeHostTransport((_, _) => throw new InvalidOperationException("a healthy pull host is preferred"));
        var shardHost = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("agent-runner-01", 2, 1))));
        ShardedRemoteGateTransport Over(int? shards) => Transport(
            new GateShardHost("kori-gate", new ConventionalGateTransport(pullStore, pull, ssh), Slots(0, 2)),
            [new GateShardHost("agent-runner-01", shardHost, Slots(0, 1))],
            Store(), shards);

        var whole = await Over(shards: null).RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, whole.Verdict);
        Assert.Null(Assert.Single(pull.Requests).Sharding!.Part);
        Assert.Empty(shardHost.Requests);

        // With a sharded suite the pull host still runs part 0; the shard host (SSH) runs part 1.
        var split = await Over(shards: 2).RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, split.Verdict);
        Assert.Equal(new GateShardPart(0, 2), pull.Requests[1].Sharding!.Part);
        Assert.Equal(new GateShardPart(1, 2), Assert.Single(shardHost.Requests).Sharding!.Part);
        Assert.Empty(ssh.Requests);
    }

    [Fact]
    public async Task An_unreachable_shard_host_is_tried_again_after_the_cooldown()
    {
        var primary = new FakeHostTransport((request, _) => Task.FromResult(
            request.Sharding?.Part is null ? Green() : Green(Suite("kori-gate", 2, 0))));
        var secondary = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("agent-runner-01", 2, 1))));
        var secondarySlots = Slots(0, 1);
        secondarySlots.Failed();
        var transport = Transport(
            new GateShardHost("kori-gate", primary, Slots(0, 2)),
            [new GateShardHost("agent-runner-01", secondary, secondarySlots)],
            Store());

        await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(secondary.Requests);

        _clock.Advance(GateShardPlacementPolicy.UnreachableCooldown);
        var result = await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(new GateShardPart(1, 2), Assert.Single(secondary.Requests).Sharding!.Part);
    }

    [SkippableFact]
    public async Task The_dispatcher_reads_the_suites_of_the_subject_commit_not_of_its_checkout()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a POSIX git fixture path.");
        var repository = Path.Combine(_root, "subject");
        Directory.CreateDirectory(Path.Combine(repository, ".agent-studio"));
        Git(repository, "init", "--quiet");
        string Commit(string shardsLine)
        {
            File.WriteAllText(Path.Combine(repository, ".agent-studio", "project.yml"), $"""
                schemaVersion: 1
                stack: [dotnet]
                toolVersions:
                commands:
                  prepare: .agent-studio/prepare
                  build:
                  test:
                    - {SuiteCommand}
                  lint:
                testSuites:
                  - id: backend-routine
                    category: routine
                    expectedDurationSeconds: 1440
                {shardsLine}
                    command: {SuiteCommand}
                cachePaths: [bin]
                capabilities: [linux]
                environment:
                  CI: "true"
                """);
            Git(repository, "add", "-A");
            Git(repository, "-c", "user.name=gate", "-c", "user.email=gate@example.invalid", "commit", "--quiet", "-m", "subject");
            return Git(repository, "rev-parse", "HEAD").Trim();
        }
        var sharded = Commit("    shards: 2");
        var whole = Commit("");

        var primary = new FakeHostTransport((request, _) => Task.FromResult(
            request.Sharding?.Part is null ? Green() : Green(Suite("kori-gate", 2, 0)) with { TestedSha = request.ExpectedSha }));
        var secondary = new FakeHostTransport((request, _) => Task.FromResult(
            Green(Suite("agent-runner-01", 2, 1)) with { TestedSha = request.ExpectedSha }));
        var transport = new ShardedRemoteGateTransport(
            new GateShardHost("kori-gate", primary, Slots(0, 2)),
            [new GateShardHost("agent-runner-01", secondary, Slots(0, 1))],
            Store(), NullLogger<ShardedRemoteGateTransport>.Instance, _clock);
        BuildTestGateRequest At(string sha) => new(repository, sha, "test") { Project = "subject" };

        await transport.RunAsync(At(whole), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(secondary.Requests);
        await transport.RunAsync(At(sharded), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Equal(new GateShardPart(1, 2), Assert.Single(secondary.Requests).Sharding!.Part);
        // An unknown commit cannot be read and keeps the run whole.
        await transport.RunAsync(At(new string('f', 40)), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Single(secondary.Requests);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    [Fact]
    public async Task A_failing_shard_host_stops_its_sibling_and_the_run_is_an_infrastructure_verdict()
    {
        var primaryStopped = false;
        var primary = new FakeHostTransport(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { primaryStopped = true; throw; }
            return Green();
        });
        var secondary = new FakeHostTransport((_, _) => throw new IOException("ssh: connect to host agent-runner-01 port 22: Connection refused"));
        var transport = Transport(
            new GateShardHost("kori-gate", primary, Slots(0, 2)),
            [new GateShardHost("agent-runner-01", secondary, Slots(0, 1))],
            Store());

        var result = await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.True(primaryStopped);
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        Assert.Contains("partial infrastructure failure on agent-runner-01", result.Reason);
        Assert.Contains("Connection refused", result.Reason);
    }

    [Fact]
    public async Task Measured_timings_of_a_complete_run_feed_the_next_plan_and_partial_ones_do_not()
    {
        var store = Store();
        var complete = Suite("kori-gate", 2, 0, 1);
        var partial = Suite("kori-gate", 3, 0, 2) with { SuiteId = "runner-routine" };
        var red = Suite("kori-gate", 1, 0) with
        {
            SuiteId = "task-server-routine",
            Shards = [Shard(0, "kori-gate", GateShardOutcomes.Failed)],
        };

        Assert.Equal(1, store.Record("agent-studio", [complete, partial, red]));

        var snapshot = store.Read("agent-studio");
        Assert.Equal(["backend-routine"], snapshot.Suites.Keys);
        Assert.Equal(complete.ClassDurationsMs, snapshot.For("backend-routine")!.ClassDurationsMs);
        Assert.Equal(GateShardTimingSnapshot.Empty.Suites, store.Read("other-project").Suites);

        var primary = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("kori-gate", 2, 0, 1) with
        {
            ClassDurationsMs = new Dictionary<string, double> { ["NewTests"] = 42 },
        })));
        var transport = Transport(
            new GateShardHost("kori-gate", primary, Slots(0, 1)), [], store);
        await transport.RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(complete.ClassDurationsMs, primary.Requests.Single().Sharding!.Timings!.For("backend-routine")!.ClassDurationsMs);
        Assert.Equal(["NewTests"], store.Read("agent-studio").For("backend-routine")!.ClassDurationsMs.Keys);
    }

    [Fact]
    public void An_unreadable_timing_file_plans_by_hashing()
    {
        var store = Store();
        store.Record("agent-studio", [Suite("kori-gate", 1, 0)]);
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "timings"))) File.WriteAllText(file, "{ not json");

        Assert.Empty(store.Read("agent-studio").Suites);
    }

    [Fact]
    public void The_gate_log_lists_shards_hosts_durations_and_test_counts_but_not_class_timings()
    {
        var jobFolder = Path.Combine(_root, "job");
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green(Suite("kori-gate", 2, 0))),
            new("agent-runner-01", new GateShardPart(1, 2), Green(Suite("agent-runner-01", 2, 1))),
        ]);

        IntegrationGateReceipts.Record(jobFolder, "pre-develop-build-gate", merged, time: _clock);

        var log = File.ReadAllText(Path.Combine(jobFolder, "post-steps", "pre-develop-build-gate-1.log"));
        var start = log.IndexOf("--- test-shards.json ---\n", StringComparison.Ordinal);
        Assert.True(start >= 0, log);
        var json = log[(start + "--- test-shards.json ---\n".Length)..log.IndexOf("--- last-300-lines ---", StringComparison.Ordinal)];
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var suite = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal("backend-routine", suite.GetProperty("SuiteId").GetString());
        Assert.Equal(2, suite.GetProperty("ShardCount").GetInt32());
        Assert.Equal(["kori-gate", "agent-runner-01"], suite.GetProperty("Hosts").EnumerateArray().Select(host => host.GetString()));
        Assert.Equal(500, suite.GetProperty("Tests").GetProperty("Total").GetInt32());
        var shards = suite.GetProperty("Shards").EnumerateArray().ToArray();
        Assert.Equal([1, 2], shards.Select(shard => shard.GetProperty("Shard").GetInt32()));
        Assert.Equal(["kori-gate", "agent-runner-01"], shards.Select(shard => shard.GetProperty("Host").GetString()));
        Assert.Equal([61_000L, 61_001L], shards.Select(shard => shard.GetProperty("DurationMs").GetInt64()));
        Assert.Equal(249, shards[0].GetProperty("Tests").GetProperty("Passed").GetInt32());
        Assert.Equal("passed", shards[1].GetProperty("Outcome").GetString());
        Assert.DoesNotContain("ClassDurationsMs", json);
        Assert.Empty(IntegrationGateReceipts.TestShardReport(Green()));
    }

    [Fact]
    public void A_shard_part_has_its_own_verdict_cache_identity_and_a_whole_gate_keeps_its_digest()
    {
        IReadOnlyList<VerifyCommand> commands = [new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test A.csproj")];
        var whole = GateResultCache.ProfileDigest(Request(), null, PostStepMode.Fail, commands, "toolchain");
        var withTimings = GateResultCache.ProfileDigest(
            Request() with { Sharding = new GateShardRequest { Timings = GateShardTimingSnapshot.Empty } },
            null, PostStepMode.Fail, commands, "toolchain");
        var part = GateResultCache.ProfileDigest(
            Request() with { Sharding = new GateShardRequest { Part = new GateShardPart(1, 2) } },
            null, PostStepMode.Fail, commands, "toolchain");

        Assert.Equal(whole, withTimings);
        Assert.NotEqual(whole, part);
    }

    [Fact]
    public void A_part_cached_under_one_timing_snapshot_is_not_reused_by_a_run_planned_from_another()
    {
        // Part 0 cached from plan A, merged with a fresh part 1 planned from B,
        // reads as "different shard plans" on every retry: the cache would
        // livelock the run. A new snapshot therefore misses the part cache.
        IReadOnlyList<VerifyCommand> commands = [new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test A.csproj")];
        GateShardTimingSnapshot Snapshot(double ms) => new(new Dictionary<string, GateShardSuiteTimings>
        {
            ["backend-routine"] = new(new Dictionary<string, double> { ["Ns.ATests"] = ms, ["Ns.BTests"] = 10 }, 1000),
        });
        string Digest(GateShardTimingSnapshot snapshot) => GateResultCache.ProfileDigest(
            Request() with { Sharding = new GateShardRequest { Part = new GateShardPart(0, 2), Timings = snapshot } },
            null, PostStepMode.Fail, commands, "toolchain");

        Assert.Equal(Digest(Snapshot(100)), Digest(Snapshot(100)));
        Assert.NotEqual(Digest(Snapshot(100)), Digest(Snapshot(900)));
        Assert.NotEqual(Digest(Snapshot(100)), Digest(GateShardTimingSnapshot.Empty));
    }

    [Fact]
    public void The_remote_worker_rejects_an_invalid_host_part_or_invalid_timings()
    {
        var runId = new string('a', 32);
        RemoteGateInvocation Invocation(GateShardRequest sharding) => new(1, runId, new string('b', 64),
            Request() with { Sharding = sharding }, null, null, PostStepMode.Fail, 60, 120);

        RemoteGateWorker.Validate(Invocation(new GateShardRequest { Part = new GateShardPart(1, 2) }), runId);
        Assert.Throws<InvalidDataException>(() =>
            RemoteGateWorker.Validate(Invocation(new GateShardRequest { Part = new GateShardPart(2, 2) }), runId));
        var invalid = new GateShardTimingSnapshot(new Dictionary<string, GateShardSuiteTimings>
        {
            ["backend-routine"] = new(new Dictionary<string, double> { ["ATests"] = double.NaN }, 0),
        });
        Assert.Throws<InvalidDataException>(() =>
            RemoteGateWorker.Validate(Invocation(new GateShardRequest { Timings = invalid }), runId));
        var oversized = new GateShardTimingSnapshot(new Dictionary<string, GateShardSuiteTimings>
        {
            ["backend-routine"] = new(Enumerable.Range(0, 8000).ToDictionary(
                index => $"AgentStudio.Tests.Features.Pipeline.Gates.VeryLongTestClassNameNumber{index:D5}Tests.{new string('x', 60)}",
                index => (double)index), 0),
        });
        Assert.False(oversized.IsWithinBounds);
        Assert.Throws<InvalidDataException>(() =>
            RemoteGateWorker.Validate(Invocation(new GateShardRequest { Timings = oversized }), runId));
    }

    [Fact]
    public async Task Recorded_timings_beyond_the_size_bound_are_neither_stored_nor_sent()
    {
        var store = Store();
        var huge = Suite("kori-gate", 1, 0) with
        {
            ClassDurationsMs = Enumerable.Range(0, 8000).ToDictionary(
                index => $"AgentStudio.Tests.Features.Pipeline.Gates.VeryLongTestClassNameNumber{index:D5}Tests.{new string('x', 60)}",
                index => (double)index),
        };

        Assert.Equal(0, store.Record("agent-studio", [huge]));
        var primary = new FakeHostTransport((_, _) => Task.FromResult(Green()));
        await Transport(new GateShardHost("kori-gate", primary, Slots(0, 1)), [], store)
            .RunAsync(Request(), null, null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(primary.Requests.Single().Sharding!.Timings!.Suites);
    }

    [Fact]
    public void Project_definitions_accept_a_bounded_shard_count_per_suite()
    {
        static ProjectDefinitionReadResult Read(string shards) => ProjectDefinitionReader.Parse($"""
            schemaVersion: 1
            stack: [dotnet]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
                - dotnet test A.Tests/A.Tests.csproj --no-build
              lint:
            testSuites:
              - id: a-routine
                category: routine
                expectedDurationSeconds: 600
                shards: {shards}
                command: dotnet test A.Tests/A.Tests.csproj --no-build
            cachePaths: [bin]
            capabilities: [linux]
            environment:
              CI: "true"
            """);

        var valid = Read("4");
        Assert.Empty(valid.Issues);
        Assert.Equal(4, valid.Definition!.TestSuites.Single().Shards);
        var auto = Read("auto");
        Assert.Empty(auto.Issues);
        Assert.Equal(ProjectTestSuite.AutoShards, auto.Definition!.TestSuites.Single().Shards);
        Assert.Contains(Read("0").Issues, issue => issue.Code == "suite-shards-invalid");
        Assert.Contains(Read("lots").Issues, issue => issue.Code == "suite-shards-invalid");
        Assert.DoesNotContain(Read("16").Issues, issue => issue.Path.EndsWith(".shards", StringComparison.Ordinal));
    }
}
