using AgentStudio.GeneratedFiles;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2863: a Remote Review verdict produced by a detached worker of a
/// superseded agent-host release must say so on the card. The verdict itself
/// stands - a restart deliberately lets an adopted worker finish - but the
/// operator can no longer mistake it for a grade from the current release.
/// </summary>
public sealed class RemoteReviewWorkerReleaseProjectionTests : IDisposable
{
    private const string OldRelease = "20260917T1010Z-v0.5.0-tmpguard-fcdb68b24";
    private const string NewRelease = "20260917T1550Z-v0.6.0-551484dca";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-review-worker-release-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_superseded_worker_release_is_named_once_on_the_card()
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var task = Job();

        await ProjectAsync(timeline, task, Worker(OldRelease, NewRelease));
        // Evidence projection is replay-safe, so a second delivery of the same
        // attempt must not add a second notice.
        await ProjectAsync(timeline, task, Worker(OldRelease, NewRelease));

        var notice = Assert.Single(
            timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewGradedBySupersededRelease);
        Assert.Equal(
            $"Remote review graded by release {OldRelease}, current {NewRelease}",
            notice.Summary);
        Assert.Equal("review-attempt-1", notice.RunId);
        Assert.Equal(OldRelease, notice.Details!["workerReleaseId"]);
        Assert.Equal(NewRelease, notice.Details["daemonReleaseId"]);
        Assert.Equal(
            $"/opt/agent-host/releases/{OldRelease}/agent-host",
            notice.Details["workerBinaryPath"]);
        Assert.Equal("17", notice.Details["fence"]);
    }

    [Theory]
    [InlineData(NewRelease, NewRelease)]
    [InlineData(null, null)]
    public async Task A_verdict_from_the_current_release_adds_no_notice(
        string? workerRelease,
        string? daemonRelease)
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var task = Job();

        await ProjectAsync(
            timeline,
            task,
            workerRelease is null ? null : Worker(workerRelease, daemonRelease!));

        Assert.DoesNotContain(
            timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewGradedBySupersededRelease);
    }

    [Fact]
    public async Task Remote_concern_projects_the_same_passed_status_and_verdict_as_local_execution()
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var task = Job();
        var started = new DateTime(2026, 9, 18, 8, 10, 0, DateTimeKind.Utc);
        var report = Report(null) with
        {
            Commands =
            [
                new Contract.ReviewCommandEvidenceDto(
                    "aspect-code-quality", "code-quality", "codex", [], new string('a', 40),
                    new string('a', 40), new string('b', 40), started, started.AddMinutes(1), 0, null,
                    new string('c', 64), new string('d', 64),
                    ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                    AttemptId: "review-attempt-1", Model: "gpt-5.6-sol", InputTokens: 100,
                    OutputTokens: 20),
            ],
            Verdicts =
            [
                new Contract.ReviewVerdictDto(
                    "code-quality", "concerns", "RemoteAspectVerdict",
                    "Clean diff with one dead no-op assertion.", "spec and diff", "none"),
            ],
        };
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Workspace:Root"] = _root }).Build();
        var projector = new RemotePipelineReviewEvidenceProjector(
            pipeline,
            timeline,
            new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration));

        await projector.ProjectAsync(
            task, Attempt(), report, "remote-review-grade-review-attempt-1.md",
            started.AddMinutes(2), CancellationToken.None);

        var step = Assert.Single(
            pipeline.Read(task.FolderPath)!.Steps,
            candidate => candidate.StepId == "aspect-code-quality");
        Assert.Equal(PipelineStepStatus.Passed, step.Status);
        Assert.Equal("concerns", step.Verdict);
        Assert.Equal("Clean diff with one dead no-op assertion.", step.VerdictSummary);
        Assert.Equal("aspect-code-quality.md", step.EvidenceRef);
    }

    [Fact]
    public async Task Remote_aspect_command_failure_remains_a_failed_step_with_its_block_verdict()
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var task = Job();
        var started = new DateTime(2026, 9, 18, 9, 10, 0, DateTimeKind.Utc);
        var report = Report(null) with
        {
            Commands =
            [
                new Contract.ReviewCommandEvidenceDto(
                    "aspect-code-quality", "code-quality", "codex", [], new string('a', 40),
                    new string('a', 40), new string('b', 40), started, started.AddMinutes(1), 17, null,
                    new string('c', 64), new string('d', 64),
                    ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                    AttemptId: "review-attempt-1", Model: "gpt-5.6-sol"),
            ],
            Verdicts =
            [
                new Contract.ReviewVerdictDto(
                    "code-quality", "block", "CommandFailed",
                    "Review command 'aspect-code-quality' exited 17.",
                    "command:aspect-code-quality", "successful execution of aspect-code-quality"),
            ],
        };
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Workspace:Root"] = _root }).Build();
        var projector = new RemotePipelineReviewEvidenceProjector(
            pipeline,
            timeline,
            new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration));

        await projector.ProjectAsync(
            task, Attempt(), report, "remote-review-grade-review-attempt-1.md",
            started.AddMinutes(2), CancellationToken.None);

        var step = Assert.Single(
            pipeline.Read(task.FolderPath)!.Steps,
            candidate => candidate.StepId == "aspect-code-quality");
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Equal("block", step.Verdict);
        Assert.Equal("Review command 'aspect-code-quality' exited 17.", step.VerdictSummary);
    }

    private async Task ProjectAsync(
        TimelineLog timeline,
        TaskInfo task,
        Contract.ReviewWorkerProvenanceDto? worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Workspace:Root"] = _root }).Build();
        var projector = new RemotePipelineReviewEvidenceProjector(
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            timeline,
            new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration));

        await projector.ProjectAsync(
            task,
            Attempt(),
            Report(worker),
            "remote-review-grade-review-attempt-1.md",
            new DateTime(2026, 9, 17, 18, 13, 0, DateTimeKind.Utc),
            CancellationToken.None);
    }

    private static Contract.ReviewWorkerProvenanceDto Worker(string workerRelease, string daemonRelease)
        => new(
            workerRelease,
            $"/opt/agent-host/releases/{workerRelease}/agent-host",
            daemonRelease);

    private TaskInfo Job()
    {
        var folder = Path.Combine(_root, "Demo", "5-human-review", "AGT-2863");
        Directory.CreateDirectory(folder);
        return new TaskInfo
        {
            Id = "AGT-2863",
            TaskKey = "PROJ-002::AGT-2863",
            Key = "AGT-2863",
            Title = "AGT-2863",
            State = TaskStates.HumanReview,
            WatchPath = Path.Combine(_root, "Demo"),
            ProjectName = "Demo",
            FolderPath = folder,
        };
    }

    private static ReviewAttemptDto Attempt()
    {
        var created = new DateTime(2026, 9, 17, 17, 46, 0, DateTimeKind.Utc);
        return new ReviewAttemptDto(
            "review-attempt-1",
            "PROJ-002::AGT-2863",
            "example/repository",
            "run-attempt-1",
            null,
            new ReviewSubjectDto(
                "subject-1",
                "example/repository",
                new string('a', 40),
                "run-attempt-1",
                new string('d', 64),
                "policy-v1",
                [],
                created),
            AttemptLifecycleState.Leased,
            null,
            17,
            0,
            created,
            null,
            null,
            null,
            null,
            null,
            []);
    }

    private static Contract.ReviewReportRequest Report(Contract.ReviewWorkerProvenanceDto? worker)
    {
        var head = new string('a', 40);
        return new Contract.ReviewReportRequest(
            "agent-runner-01-review",
            "instance-2",
            "lease-2",
            17,
            "report-key",
            "Pass",
            null,
            "No product findings.",
            new Contract.ReviewWorkspaceProofDto(
                "example/repository", head, head, new string('b', 40), false, false,
                "workspace", "review-attempt-1-f17"),
            new Contract.ReviewEnvironmentDto(
                "agent-runner-01", "agent-runner-01-review", "instance-2", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                worker),
            [],
            [],
            []);
    }
}
