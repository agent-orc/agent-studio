using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ResultFinalizationStoreTests
{
    [Fact]
    public async Task Completed_lease_admits_finalization_only_with_exact_outbox_authority()
    {
        using var temp = new TempDirectory();
        var generator = new SequencedGenerator(
            ResultSummaryGeneration.Success("# Status\n\n- Result: Success\n"));
        var store = Store(temp.Path, generator, maxAttempts: 2);
        var (_, _, _, run, lease) = await SeedClaimedAsync(store);
        await CompleteAsync(store, run.RunId, lease);

        var missing = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.FinalizeResultAsync(run.RunId,
                Request(lease, 1) with { RunnerId = null!, InstanceId = null!, LeaseId = null! },
                "runner-a", default));
        Assert.Equal("lease-not-active", missing.Code);
        var wrong = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.FinalizeResultAsync(run.RunId,
                Request(lease, 1) with { InstanceId = "another-instance" },
                "runner-a", default));
        Assert.Equal("stale-fence", wrong.Code);
        Assert.Equal(0, generator.Calls);

        var finalization = await store.FinalizeResultAsync(
            run.RunId, Request(lease, 1), "runner-a", default);
        Assert.Equal(ResultFinalizationStatus.Ready, finalization.Status);
        Assert.Contains(await store.ListArtifactsAsync(run.RunId, default),
            artifact => artifact.Name == "status.md");
    }

    [Fact]
    public async Task Release_after_completion_is_idempotent_only_for_exact_authority()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, new SequencedGenerator(), maxAttempts: 2);
        var (_, project, task, run, lease) = await SeedClaimedAsync(store);
        await CompleteAsync(store, run.RunId, lease);
        var request = new LeaseReleaseRequest(
            "runner-a", "instance-a", lease.LeaseId, lease.Fence, "runner-process-missing");

        var wrong = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReleaseLeaseAsync(run.RunId, request with { InstanceId = "another-instance" },
                "runner-a", default));
        Assert.Equal("stale-fence", wrong.Code);
        var missing = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReleaseLeaseAsync(run.RunId, request with { LeaseId = "" },
                "runner-a", default));
        Assert.Equal("stale-fence", missing.Code);

        var first = await store.ReleaseLeaseAsync(run.RunId, request, "runner-a", default);
        var second = await store.ReleaseLeaseAsync(run.RunId, request, "runner-a", default);
        Assert.Equal("released", first.Status);
        Assert.Equal("completed", first.Lease?.Status);
        Assert.Equal("completed", second.Lease?.Status);
        var history = await store.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
        Assert.Equal("blocked", Assert.Single(history!.Runs).Status);
        Assert.DoesNotContain(history.Audit, entry => entry.Action == "lease.released");
    }

    [Fact]
    public async Task Missing_status_retries_only_summary_then_persists_real_result()
    {
        using var temp = new TempDirectory();
        var generator = new SequencedGenerator(
            ResultSummaryGeneration.Failure("transient summary failure"),
            ResultSummaryGeneration.Success("# Status\n\n- Result: Success\n\n## Open Items\n\n- None.\n"));
        var store = Store(temp.Path, generator, maxAttempts: 3);
        var (_, project, task, run, lease) = await SeedClaimedAsync(store);

        var first = await store.FinalizeResultAsync(
            run.RunId,
            Request(lease, 1),
            "runner-a",
            default);
        Assert.Equal(ResultFinalizationStatus.Retryable, first.Status);
        Assert.Equal(1, first.Attempt);
        Assert.DoesNotContain(
            await store.ListArtifactsAsync(run.RunId, default),
            artifact => artifact.Name == "status.md");

        var second = await store.FinalizeResultAsync(
            run.RunId,
            Request(lease, 2),
            "runner-a",
            default);
        Assert.Equal(ResultFinalizationStatus.Ready, second.Status);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(2, generator.Calls);
        var status = Assert.Single(
            await store.ListArtifactsAsync(run.RunId, default),
            artifact => artifact.Name == "status.md");
        var content = await store.GetArtifactContentAsync(run.RunId, status.ArtifactId, default);
        var statusMarkdown = Encoding.UTF8.GetString(Convert.FromBase64String(content!.ContentBase64));
        Assert.Contains("- Result: Success", statusMarkdown);
        Assert.DoesNotContain("agent-studio:result-scaffold", statusMarkdown);

        var history = await store.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
        Assert.Equal(ResultFinalizationStatus.Ready, history!.ResultFinalization!.Status);
        Assert.Equal(2, history.ResultFinalization.Attempt);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.ResultFinalizationRetryable);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.ResultFinalizationReady);
    }

    [Fact]
    public async Task Exhausted_summary_budget_is_typed_degraded_and_core_completion_remains_reviewable()
    {
        using var temp = new TempDirectory();
        var generator = new SequencedGenerator(
            ResultSummaryGeneration.Failure("summary unavailable"),
            ResultSummaryGeneration.Failure("summary still unavailable"));
        var store = Store(temp.Path, generator, maxAttempts: 2);
        var (_, project, task, run, lease) = await SeedClaimedAsync(store);

        var retry = await store.FinalizeResultAsync(
            run.RunId,
            Request(lease, 1),
            "runner-a",
            default);
        var degraded = await store.FinalizeResultAsync(
            run.RunId,
            Request(lease, 2),
            "runner-a",
            default);

        Assert.Equal(ResultFinalizationStatus.Retryable, retry.Status);
        Assert.Equal(ResultFinalizationStatus.Degraded, degraded.Status);
        Assert.Equal(2, degraded.Attempt);
        Assert.Equal(2, degraded.MaxAttempts);
        Assert.Null(degraded.ArtifactId);

        await store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence,
                "blocked",
                "Core run completed; only Result finalization degraded.",
                IdempotencyKey: $"completion:{run.RunId}",
                Sequence: 1),
            "runner-a",
            default);

        var reviewable = await store.GetTaskAsync(project.ProjectId, task.TaskKey, default);
        Assert.Equal("4-auto-review", reviewable!.State);
        var history = await store.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
        Assert.Equal(ResultFinalizationStatus.Degraded, history!.ResultFinalization!.Status);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.ResultFinalizationDegraded);
        Assert.DoesNotContain(history.Artifacts, artifact => artifact.Name == "status.md");
        Assert.Single(history.Runs);
    }

    private static ResultFinalizationRequest Request(LeaseDto lease, int attempt)
        => new(
            "runner-a",
            "instance-a",
            lease.LeaseId,
            lease.Fence,
            attempt,
            $"result-finalization:{lease.RunId}:{attempt}");

    private static async Task CompleteAsync(TaskServerStore store, string runId, LeaseDto lease)
        => _ = await store.CompleteRunAsync(
            runId,
            new CompleteRunRequest(
                "runner-a", "instance-a", lease.LeaseId, lease.Fence,
                "blocked", "Result published.", IdempotencyKey: $"completion:{runId}", Sequence: 1),
            "runner-a", default);

    private static TaskServerStore Store(
        string path,
        IResultFinalizationSummaryGenerator generator,
        int maxAttempts)
        => new(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = path,
                ResultFinalizationMaxAttempts = maxAttempts,
            }),
            TimeProvider.System,
            generator);

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task, RunDto Run, LeaseDto Lease)>
        SeedClaimedAsync(TaskServerStore store)
    {
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Result finalization"),
            "test",
            default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "RF"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Remote concept", "Produce one decision dossier.", "2-ready"),
            "test",
            default);
        await store.RegisterRunnerAsync(
            "runner-a",
            new RegisterRunnerRequest(
                "runner",
                "host-a",
                "instance-a",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "runner-a",
            default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "runner-a",
            default);
        var proof = Encoding.UTF8.GetBytes("remote concept dossier");
        await store.IngestArtifactAsync(
            claim.Run!.RunId,
            new ArtifactIngestRequest(
                "artifact-report",
                "results/report.html",
                "text/html",
                Convert.ToBase64String(proof),
                Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant(),
                "artifact-report",
                claim.Lease!.Fence),
            "runner-a",
            default);
        return (workspace, project, task, claim.Run, claim.Lease);
    }

    private sealed class SequencedGenerator(params ResultSummaryGeneration[] results)
        : IResultFinalizationSummaryGenerator
    {
        private readonly Queue<ResultSummaryGeneration> _results = new(results);

        public int Calls { get; private set; }

        public Task<ResultSummaryGeneration> GenerateAsync(
            ResultSummaryContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : ResultSummaryGeneration.Failure("unexpected extra summary attempt"));
        }
    }
}
