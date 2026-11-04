using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Carried_passed_agent_aspect_is_admitted_to_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-CARRIED-ADMISSION", "docs/carried-admission.md",
            settleReview: false);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("completion", "completion", "sh", ["true"])],
            ["requirement-fit", "completion"],
            CarriedVerdicts: [new Contract.ReviewVerdictDto("requirement-fit", "pass",
                "carried-over", "Prior review passed.", CarriedOverFrom: "review-prior")],
            BuildTestDeferredToBatch: true);
        Assert.True(authority.ReplanPendingReview(member.ReviewAttemptId, plan).Accepted);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!;

        var pending = await factory.Services.GetRequiredService<BatchGatePilotService>()
            .EnqueueAsync(task, review, authority.GetRun(member.RunAttemptId)!, _clock,
                CancellationToken.None);

        Assert.Equal(review.AttemptId, pending.ReviewAttemptId);
        Assert.Equal(review.AttemptId,
            factory.Services.GetRequiredService<BatchGateStore>().ReadPending(review.AttemptId)?.ReviewAttemptId);
    }

    [Fact]
    public async Task Carried_passed_agent_aspect_can_run_emergency_fallback()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-CARRIED-FALLBACK", "docs/carried-fallback.md",
            settleReview: false);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("completion", "completion", "sh", ["true"])],
            ["requirement-fit", "completion"],
            CarriedVerdicts: [new Contract.ReviewVerdictDto("requirement-fit", "pass",
                "carried-over", "Prior review passed.", CarriedOverFrom: "review-prior")],
            BuildTestDeferredToBatch: true);
        Assert.True(authority.ReplanPendingReview(member.ReviewAttemptId, plan).Accepted);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!;
        _gate.Fallback = request => Red(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .RunEmergencyFallbackAsync(task, review, authority.GetRun(member.RunAttemptId)!,
                _clock, CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
    }

    [Fact]
    public async Task Plan_without_an_agent_aspect_is_refused_and_alarms_the_card()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-NO-AGENT-ADMISSION", "docs/no-agent-admission.md",
            settleReview: false);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("completion", "completion", "sh", ["true"])],
            ["completion"], BuildTestDeferredToBatch: true);
        Assert.True(authority.ReplanPendingReview(member.ReviewAttemptId, plan).Accepted);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!;
        var admission = factory.Services.GetRequiredService<EngineBatchAdmission>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admission.AdmitSettledAsync(task, review));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admission.AdmitSettledAsync(task, review));

        Assert.Null(factory.Services.GetRequiredService<BatchGateStore>().ReadPending(review.AttemptId));
        AssertLane(member.Key, TaskStates.AutoReview);
        var health = factory.Services.GetRequiredService<PipelineHealthService>();
        Assert.Contains(health.Snapshot(ProjectName)!.Alerts,
            alert => alert.Kind == PipelineHealthConventions.BatchAdmissionFailedKind
                && alert.JobId == member.Key
                && alert.Detail.Contains("passed per-card model review", StringComparison.Ordinal));
        Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath),
            entry => entry.Topic == OrchestratorLogTopics.PipelineHealth
                && entry.JobId == member.Key
                && entry.Reasoning?.Contains(review.AttemptId, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Recovery_admits_a_settled_review_with_no_batch_entry()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-STRANDED-ADMISSION", "docs/stranded-admission.md",
            settleReview: false);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(
            factory.Services);

        Assert.Equal(1, await recovery.RearmDeferredBatchAdmissionsAsync(CancellationToken.None));
        Assert.Equal(0, await recovery.RearmDeferredBatchAdmissionsAsync(CancellationToken.None));

        Assert.Equal(review.AttemptId,
            factory.Services.GetRequiredService<BatchGateStore>().ReadPending(review.AttemptId)?.ReviewAttemptId);
        AssertLane(member.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Recovery_alarms_a_corrupt_ownership_and_continues_to_another_card()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var blocked = SeedMember(factory, "DOC-CORRUPT-OWNERSHIP", "docs/corrupt-ownership.md",
            settleReview: false);
        var blockedReview = Settle(factory, blocked, ReviewTerminalOutcome.Pass);
        var healthy = SeedMember(factory, "DOC-AFTER-CORRUPT", "docs/after-corrupt.md",
            settleReview: false);
        var healthyReview = Settle(factory, healthy, ReviewTerminalOutcome.Pass);
        File.WriteAllText(Path.Combine(TaskPaths.LogsDir(CardFolder(blocked.Key)),
            "batch-gate-ownership.json"), "{invalid");
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(
            factory.Services);

        Assert.Equal(1, await recovery.RearmDeferredBatchAdmissionsAsync(CancellationToken.None));

        Assert.Equal(healthyReview.AttemptId,
            factory.Services.GetRequiredService<BatchGateStore>()
                .ReadPending(healthyReview.AttemptId)?.ReviewAttemptId);
        Assert.Contains(factory.Services.GetRequiredService<PipelineHealthService>()
                .Snapshot(ProjectName)!.Alerts,
            alert => alert.Kind == PipelineHealthConventions.BatchAdmissionFailedKind
                && alert.JobId == blocked.Key
                && alert.Detail.Contains(blockedReview.AttemptId, StringComparison.Ordinal));
    }
}
