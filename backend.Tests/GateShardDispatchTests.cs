using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: the dispatcher side of gate sharding. It hands every host the
/// same recorded timings, spans hosts only when more than one gate slot is
/// free, merges one verdict, and records complete timings for the next plan.
/// The gate log lists shards, hosts, durations and test counts.
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

    private static GateSlotUsageTracker Slots(int busy, int total)
    {
        var tracker = new GateSlotUsageTracker();
        tracker.Configure(total);
        for (var i = 0; i < busy; i++) tracker.Acquired("run-" + i, total);
        return tracker;
    }

    [Fact]
    public async Task Without_a_free_slot_on_a_second_host_the_run_stays_whole_on_the_primary()
    {
        var primary = new FakeHostTransport((_, _) => Task.FromResult(Green(Suite("kori-gate", 4, 0, 1, 2, 3))));
        var secondary = new FakeHostTransport((_, _) => throw new InvalidOperationException("must not be used"));
        var transport = new ShardedRemoteGateTransport(
            new GateShardHost("kori-gate", primary, Slots(0, 3)),
            [new GateShardHost("agent-runner-01", secondary, Slots(1, 1))],
            Store(), NullLogger<ShardedRemoteGateTransport>.Instance);

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
        var transport = new ShardedRemoteGateTransport(
            new GateShardHost("kori-gate", primary, Slots(1, 2)),
            [new GateShardHost("agent-runner-01", secondary, Slots(0, 1))],
            store, NullLogger<ShardedRemoteGateTransport>.Instance);

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
    public void Placement_needs_a_free_slot_on_the_primary_and_skips_busy_or_unreachable_hosts()
    {
        Assert.Empty(GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(2, 2),
            [(new GateSlotUsage(0, 1), "reachable")]));
        Assert.Equal([1], GateShardPlacementPolicy.SelectAdditionalHosts(new GateSlotUsage(0, 2),
        [
            (new GateSlotUsage(1, 1), "reachable"),
            (new GateSlotUsage(0, 2), "unverified"),
            (new GateSlotUsage(0, 2), "unreachable"),
        ]));
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
        var transport = new ShardedRemoteGateTransport(
            new GateShardHost("kori-gate", primary, Slots(0, 2)),
            [new GateShardHost("agent-runner-01", secondary, Slots(0, 1))],
            Store(), NullLogger<ShardedRemoteGateTransport>.Instance);

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
        var transport = new ShardedRemoteGateTransport(
            new GateShardHost("kori-gate", primary, Slots(0, 1)), [], store, NullLogger<ShardedRemoteGateTransport>.Instance);
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
