using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationGateCapacityTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Merge_slot_starts_while_all_four_suite_slots_are_held()
    {
        using var workspace = new TempWorkspace("integration-capacity");
        var slots = new MachineGateSlots(workspace.Combine("gate"));
        var leases = new List<MachineGateSlots.Lease>();
        try
        {
            for (var i = 0; i < 4; i++)
                leases.Add((await slots.AcquireAsync(4, TimeSpan.FromSeconds(1), null, CancellationToken.None))!);
            using var merge = await slots.AcquireAsync(4, TimeSpan.FromSeconds(1), null,
                CancellationToken.None, integrationPriority: true);
            Assert.NotNull(merge);
            Assert.Equal((4, 5), (merge.Slot, merge.Count));
            Assert.Equal(4, leases.Count(lease => lease is not null));
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 3)]
    public async Task Queue_timeout_retries_same_compile_subject_with_a_three_attempt_limit(int failures, int expected)
    {
        var runner = new QueueRunner(failures);
        var gate = new PreDevelopBuildGate(runner) { CapacityRetryDelay = TimeSpan.Zero };
        var result = await gate.RunAsync(new BuildTestGateRequest("repo", new string('a', 40), "merge"),
            ["backend/source.cs"], null, TimeSpan.FromMinutes(1), CancellationToken.None,
            postMergeLevel: TestExecutionLevels.CompileOnly);
        Assert.Equal(expected, runner.Requests.Count);
        Assert.Equal(expected, result.GateAttempts);
        Assert.Equal(expected - 1, result.GateRetryReasons.Count);
        Assert.Equal(expected * 10L, result.StageDurationsMs["gateQueueWait"]);
        Assert.Equal(failures < 3 ? BuildTestGateVerdict.Ok : BuildTestGateVerdict.Fail, result.Verdict);
        Assert.All(runner.Requests, request =>
        {
            Assert.True(request.IntegrationPriority);
            Assert.Equal(TestExecutionLevels.CompileOnly, request.RequiredTestLevel);
            Assert.Equal(new string('a', 40), request.ExpectedSha);
        });
    }

    [Fact]
    public void Reserved_admission_survives_remote_request_serialization()
    {
        var request = new BuildTestGateRequest("repo", new string('a', 40), "merge") { IntegrationPriority = true };
        var roundTrip = JsonSerializer.Deserialize<BuildTestGateRequest>(
            JsonSerializer.Serialize(request, RemoteBuildTestGateRunner.Json), RemoteBuildTestGateRunner.Json);
        Assert.True(roundTrip!.IntegrationPriority);
    }

    private sealed class QueueRunner(int failures) : IBuildTestGateRunner
    {
        public List<BuildTestGateRequest> Requests { get; } = [];
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
        {
            Requests.Add(request);
            var failed = Requests.Count <= failures;
            return Task.FromResult(new BuildTestGateResult(failed ? BuildTestGateVerdict.Fail : BuildTestGateVerdict.Ok,
                null, 0, "", "queue", false, false)
            {
                StageDurationsMs = new Dictionary<string, long> { ["gateQueueWait"] = 10 }, StageTimingsVersion = 1,
                FailureKind = failed ? BuildTestGateFailureKind.Timeout : BuildTestGateFailureKind.None,
                ViolatedBudget = failed ? new("machine-gate-queue", 120000, 120006, "queue") : null,
            });
        }
    }
}
