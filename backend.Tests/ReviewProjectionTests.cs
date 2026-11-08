using AgentStudio.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-projection-" + Guid.NewGuid().ToString("N"));

    public ReviewProjectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task BoardLookup_RefChangeReusesLedgers_OneWriteRefreshesOneCard_AndRepairFindsMissedWrite()
    {
        var tasks = Enumerable.Range(0, 3)
            .Select(i => Job($"AGT-{i}", CreateJobFolder($"card-{i}"))).ToArray();
        var reads = new List<string>();
        foreach (var task in tasks)
            SidecarFileGeneration.Observe(Path.Combine(task.FolderPath,
                AgentStudio.Runner.ReviewRoundBudgetStore.FileName));
        var service = new ReviewProjectionService(
            new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance))
        {
            LedgerReadObserver = reads.Add,
        };
        Task<TaskListGitProjection> Board() => TaskListGitProjectionCache.BuildProjectionAsync(
            tasks,
            _ => new Dictionary<string, TaskMergeSignal>(),
            _ => new Dictionary<string, TaskIntegrationStatus>(),
            _ => new Dictionary<string, TaskPublishSignal>(),
            _ => new Dictionary<string, TaskTestRunEvidence>(),
            service.BuildLookup);
        var first = await Board();
        Assert.Equal(3, reads.Count);

        // The next repository projection represents a ref change with no card sidecar change.
        var refProjection = await Board();
        Assert.Equal(3, reads.Count);
        Assert.Same(first.ReviewProjection[tasks[0].TaskKey], refProjection.ReviewProjection[tasks[0].TaskKey]);

        var changed = tasks[0];
        AgentStudio.Runner.ReviewRoundBudgetStore.Record(changed.FolderPath,
            AgentStudio.OrchestrationDecisions.ReviewRoundBudgetLedger.Empty,
            new AgentStudio.OrchestrationDecisions.DeliveredReviewRound("round-1", [], []));
        var afterWrite = await Board();
        Assert.Equal(4, reads.Count);
        Assert.Equal(changed.FolderPath, reads[^1]);
        Assert.Equal(1, afterWrite.ReviewProjection[changed.TaskKey].RoundBudget!.Delivered);
        Assert.Same(refProjection.ReviewProjection[tasks[1].TaskKey], afterWrite.ReviewProjection[tasks[1].TaskKey]);

        // An external write with no watcher callback is found by the rotating repair probe.
        var missed = tasks[1];
        var path = Path.Combine(missed.FolderPath, AgentStudio.Runner.ReviewRoundBudgetStore.FileName);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
            new AgentStudio.OrchestrationDecisions.ReviewRoundBudgetLedger([
                new AgentStudio.OrchestrationDecisions.DeliveredReviewRound("round-2", [], []),
            ])));
        var version = SidecarFileGeneration.FolderVersion(missed.FolderPath);
        for (var i = 0; i < 1024 && SidecarFileGeneration.FolderVersion(missed.FolderPath) == version; i++)
            SidecarFileGeneration.Repair();
        Assert.NotEqual(version, SidecarFileGeneration.FolderVersion(missed.FolderPath));
        var afterRepair = await Board();
        Assert.Equal(5, reads.Count);
        Assert.Equal(missed.FolderPath, reads[^1]);
        Assert.Equal(1, afterRepair.ReviewProjection[missed.TaskKey].RoundBudget!.Delivered);
    }

    /// <summary>
    /// AGT-2689: seven remote-review-grade reports (all ProductFailure, all
    /// build-tests passing, all blocked on the same documentation-impact
    /// finding), a failed delivery gate, and an operator-decision park. The
    /// projection must name all seven rounds, the blocking aspect with its
    /// quoted reason, the passing build/tests, the failed delivery gate, and
    /// the pending decision - never "0 rounds" or "Grade not recorded".
    /// </summary>
    [Fact]
    public void Read_NamesSevenRemoteRoundsTheBlockingAspectAndTheFailedGate_ForAnAgt2689LikeCard()
    {
        var folder = CreateJobFolder("agt-2689-review-evidence");
        var commit = new string('a', 40);
        for (var i = 0; i < 7; i++)
        {
            var receivedAt = new DateTime(2026, 8, 31, 12, 18, 0, DateTimeKind.Utc).AddMinutes(49 * i);
            WriteRemoteReviewReport(folder, $"review_{i:00}", receivedAt, commit, outcome: "ProductFailure");
        }

        var timelineLog = new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);
        timelineLog.Append(
            folder,
            AgentStudio.Shared.TimelineEventKinds.LaneChanged,
            actor: "remote-review:review_ad5cca8e3178425fb9ba9cabe329d50e",
            summary: "Lane changed from 4-auto-review to 5-human-review.",
            details: new Dictionary<string, string> { ["from"] = "4-auto-review", ["to"] = "5-human-review" });
        timelineLog.Append(
            folder,
            AgentStudio.Shared.TimelineEventKinds.IntegrationFailed,
            actor: "system",
            summary: "Remote delivery gate failed before integration.");

        AgentStudio.Tasks.ParkedBlockerMarker.Write(
            folder,
            new AgentStudio.Tasks.ParkedBlockerRecord
            {
                BlockerType = "operator-decision",
                Lane = AgentStudio.Shared.TaskStates.HumanReview,
                ParkedAt = new DateTime(2026, 8, 31, 17, 30, 0, DateTimeKind.Utc),
                Reason = "Documentation-impact block needs an operator decision.",
            });

        var task = Job("AGT-2689", folder);
        var service = new ReviewProjectionService(timelineLog);

        var projection = service.Read(task);

        Assert.Equal(7, projection.Rounds);
        Assert.Equal("remote", projection.LatestPlane);
        Assert.Equal("ProductFailure", projection.LatestOutcome);
        Assert.All(projection.Attempts, attempt => Assert.Equal("passed", attempt.BuildTestsResult));

        var blocking = Assert.Single(projection.BlockingAspects);
        Assert.Equal("documentation-impact", blocking.Aspect);
        Assert.Equal(
            "Public API and state-file contract changed without corresponding load-bearing doc updates",
            blocking.Reason);

        Assert.Equal(ReviewDeliveryStates.GateFailed, projection.Delivery.Status);
        Assert.Equal("Remote delivery gate failed before integration.", projection.Delivery.Reason);

        Assert.True(projection.DecisionRequired.Required);
        Assert.Equal("parked-blocker", projection.DecisionRequired.Source);
        Assert.Equal("Documentation-impact block needs an operator decision.", projection.DecisionRequired.Reason);
    }

    [Fact]
    public void Read_ReturnsEmptyProjection_WhenTaskFolderHasNoReviewArtifacts()
    {
        var folder = CreateJobFolder("no-review-yet");
        var task = Job("AGT-1", folder);
        var service = new ReviewProjectionService(new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance));

        var projection = service.Read(task);

        Assert.Equal(0, projection.Rounds);
        Assert.Empty(projection.Attempts);
        Assert.Null(projection.LatestOutcome);
        Assert.Empty(projection.BlockingAspects);
        Assert.Equal(ReviewDeliveryStates.NotAttempted, projection.Delivery.Status);
        Assert.False(projection.DecisionRequired.Required);
    }

    /// <summary>
    /// AGT-3369: the service keeps parsed timelines against the file's length
    /// and write time. A timeline that grew after the first read must be read
    /// again, an unchanged one must give the same answer.
    /// </summary>
    [Fact]
    public void Read_SeesATimelineEventAppendedAfterAnEarlierRead()
    {
        var folder = CreateJobFolder("timeline-memo");
        var timelineLog = new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);
        timelineLog.Append(folder, AgentStudio.Shared.TimelineEventKinds.LaneChanged, actor: "system",
            summary: "Lane changed from 4-auto-review to 5-human-review.");
        var task = Job("AGT-3369", folder);
        var service = new ReviewProjectionService(timelineLog);

        Assert.Equal(ReviewDeliveryStates.NotAttempted, service.Read(task).Delivery.Status);
        Assert.Equal(ReviewDeliveryStates.NotAttempted, service.Read(task).Delivery.Status);

        timelineLog.Append(folder, AgentStudio.Shared.TimelineEventKinds.IntegrationFailed, actor: "system",
            summary: "Remote delivery gate failed before integration.");

        var projection = service.Read(task);
        Assert.Equal(ReviewDeliveryStates.GateFailed, projection.Delivery.Status);
        Assert.Equal("Remote delivery gate failed before integration.", projection.Delivery.Reason);
    }

    [Fact]
    public void Read_CountsALocalCodeReviewGradeAttempt()
    {
        var folder = CreateJobFolder("local-grade-only");
        File.WriteAllText(
            Path.Combine(folder, "code-review-grade-2026-08-01T00-00-00Z.md"),
            """
            ---
            verdict: pass
            grade: B
            summary: Solid, small gaps.
            model: claude-sonnet-5
            runAt: 2026-08-01T00:00:00Z
            commit: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
            ---

            Body not read by the projection.
            """);
        var task = Job("AGT-2", folder);
        var service = new ReviewProjectionService(new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance));

        var projection = service.Read(task);

        Assert.Equal(1, projection.Rounds);
        Assert.Equal("local", projection.LatestPlane);
        Assert.Equal("pass", projection.LatestOutcome);
        var attempt = Assert.Single(projection.Attempts);
        Assert.Equal(ReviewPlane.Local, attempt.Plane);
        Assert.Equal("B", attempt.Grade);
        Assert.Empty(attempt.Aspects);
    }

    [Fact]
    public void Read_ReadsIntegratedDelivery_WhenARetrySucceededAfterAnEarlierFailedGate()
    {
        var folder = CreateJobFolder("retried-integration");
        var timelineLog = new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);
        timelineLog.Append(folder, AgentStudio.Shared.TimelineEventKinds.IntegrationFailed, "system", "First attempt failed.");
        timelineLog.Append(folder, AgentStudio.Shared.TimelineEventKinds.IntegrationSucceeded, "system", "Retry merged cleanly.");
        var task = Job("AGT-3", folder);
        var service = new ReviewProjectionService(timelineLog);

        var projection = service.Read(task);

        Assert.Equal(ReviewDeliveryStates.Integrated, projection.Delivery.Status);
        Assert.Null(projection.Delivery.Reason);
    }

    [Fact]
    public void Read_FallsBackToTheNewestEscalationEvent_WhenNoParkedBlockerExists()
    {
        var folder = CreateJobFolder("escalated-no-park");
        var timelineLog = new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);
        timelineLog.Append(
            folder,
            AgentStudio.Shared.TimelineEventKinds.OrchestratorEscalated,
            "orchestrator",
            "The orchestrator could not decide unattended and asked a human.");
        var task = Job("AGT-4", folder);
        var service = new ReviewProjectionService(timelineLog);

        var projection = service.Read(task);

        Assert.True(projection.DecisionRequired.Required);
        Assert.Equal("escalation-event", projection.DecisionRequired.Source);
        Assert.Equal("The orchestrator could not decide unattended and asked a human.", projection.DecisionRequired.Reason);
    }

    private string CreateJobFolder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteRemoteReviewReport(
        string folder,
        string attemptId,
        DateTime receivedAt,
        string commit,
        string outcome)
    {
        File.WriteAllText(
            Path.Combine(folder, $"remote-review-grade-{attemptId}.md"),
            $"""
             ---
             type: remote-review-grade
             attemptId: "{attemptId}"
             receivedAt: {receivedAt:yyyy-MM-ddTHH:mm:ssZ}
             outcome: "{outcome}"
             expectedResultSha: "{commit}"
             actualHead: "{commit}"
             ---

             ## Aspect verdicts

             | Aspect | Status | Classification | Summary |
             | --- | --- | --- | --- |
             | build-tests | pass | CommandPassed | Review command 'verify-1' passed. |
             | build-tests | pass | CommandPassed | Review command 'verify-2' passed. |
             | requirement-fit | pass | RemoteAspectVerdict | Requirements match the implementation. |
             | code-quality | pass | RemoteAspectVerdict | Code quality passed. |
             | tests-and-evidence | pass | RemoteAspectVerdict | Tests and evidence passed. |
             | documentation-impact | block | RemoteAspectVerdict | Public API and state-file contract changed without corresponding load-bearing doc updates. |

             ## Command evidence

             | Phase | Workspace | Step | Location | Host / executor | Command | Exit | Budget | Output | Errors |
             | --- | --- | --- | --- | --- | --- | ---: | --- | --- | --- |
             | verification | candidate | verify-1 | local | review / executor | `dotnet build` | 0 | verify: 100/1000 ms | stdout | stderr |
             | verification | candidate | verify-2 | local | review / executor | `dotnet test` | 0 | verify: 528000/7200000 ms | stdout | stderr |
             """);
    }

    private static AgentStudio.Shared.TaskInfo Job(string id, string folder) => new()
    {
        Id = id,
        TaskKey = "PROJ-001::" + id,
        Key = id,
        Title = id,
        State = AgentStudio.Shared.TaskStates.HumanReview,
        WatchPath = Path.GetDirectoryName(folder) ?? folder,
        ProjectName = "Demo",
        FolderPath = folder,
    };
}
