using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

public sealed class FailureInterventionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "failure-intervention-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public FailureInterventionTests()
    {
        _project = Path.Combine(_root, "projects", "demo");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData("ToolUnavailable", "ReviewInfra", "The 'gpt-5.4-mini' model is not supported when using Codex with a ChatGPT account.", "infrastructure", "ReviewInfra/ToolUnavailable")]
    [InlineData("build-gate-failed", null, "Tests failed: Expected 2, actual 3", "product", "gate/build-gate-failed")]
    [InlineData("build-gate-failed", null, "dotnet restore timeout", "infrastructure", "gate/build-gate-failed")]
    [InlineData("MissingSource", null, "source checkout missing", "infrastructure", "gate/MissingSource")]
    [InlineData("IntegrationError", null, "origin is not configured", "infrastructure", "integration/configuration")]
    [InlineData("FetchTimeout", null, "fetch timed out after 30 seconds", "infrastructure", "integration/configuration")]
    [InlineData("crash-as-completion", null, "process exited -1", "infrastructure", "run/crash-as-completion")]
    public void DeterministicRules_ClassifyRequiredFailureBoundaries(
        string code, string? outcome, string evidence, string domain, string failureClass)
    {
        var result = FailureInterventionPolicy.Classify(new FailureCommandEvidence(
            code, outcome, 1, 4_000, evidence, evidence));

        Assert.NotNull(result);
        Assert.True(result!.Deterministic);
        Assert.Equal(domain, result.Domain);
        Assert.Equal(failureClass, result.FailureClass);
    }

    [Fact]
    public void Reporting_AggregatesOpenClosedAndResolutionDurations()
    {
        var first = DateTime.Parse("2026-09-09T18:06:00Z").ToUniversalTime();
        var items = new[]
        {
            new FailureInterventionRecord
            {
                FollowUpKey = "AGT-2801", Status = "open", FailureClass = "ReviewInfra/ToolUnavailable",
                FirstFailureAt = first, CreatedAt = first.AddMinutes(2), AffectedCards = ["AGT-2707"],
            },
            new FailureInterventionRecord
            {
                FollowUpKey = "AGT-2802", Status = "closed", FailureClass = "gate/MissingSource",
                FirstFailureAt = first, CreatedAt = first.AddMinutes(1), ResolvedAt = first.AddMinutes(11),
                AffectedCards = ["AGT-2755"],
            },
        };

        var report = FailureInterventionReporting.Summarize(items);
        Assert.Equal(2, report.Count);
        Assert.Equal(1, report.Open);
        Assert.Equal(1, report.Closed);
        Assert.Equal((long)TimeSpan.FromMinutes(11).TotalMilliseconds, items[1].TimeToResolutionMs);
    }

    [Fact]
    public async Task DrivingIncident_ReplayCreatesExactlyOneFollowUpForTwoOrigins()
    {
        var (scanner, mutations, service, log, timeline, pipeline) = Build();
        var first = CreateOrigin(scanner, mutations, "AGT-2707 replay");
        var second = CreateOrigin(scanner, mutations, "AGT-2755 replay");
        pipeline.Begin(first.FolderPath, PipelineCatalogue.Standard, first.ProjectName, first.Id);
        pipeline.Begin(second.FolderPath, PipelineCatalogue.Standard, second.ProjectName, second.Id);
        var fixture = JsonSerializer.Deserialize<IncidentFixture>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "failure-intervention", "review-model-withdrawal.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var firstObservation = fixture.Observations[0];
        var secondObservation = fixture.Observations[1];
        var evidence = new FailureCommandEvidence(
            fixture.FailureCode, fixture.Outcome, firstObservation.ExitCode, firstObservation.DurationMs,
            firstObservation.StdoutTail, firstObservation.StderrTail, firstObservation.StepId,
            firstObservation.EvidencePointers, fixture.FirstFailureAt);

        var raised = await service.RaiseAsync(first, evidence);
        var attached = await service.RaiseAsync(second, evidence with
        {
            ExitCode = secondObservation.ExitCode,
            DurationMs = secondObservation.DurationMs,
            StdoutTail = secondObservation.StdoutTail,
            StderrTail = secondObservation.StderrTail,
            StepId = secondObservation.StepId,
            EvidencePointers = secondObservation.EvidencePointers,
        });

        Assert.True(raised.Created);
        Assert.False(attached.Created);
        Assert.Equal(raised.Intervention.Id, attached.Intervention.Id);
        var item = Assert.Single(service.List(_project));
        Assert.Equal(2, item.AffectedCards.Count);
        Assert.Contains(first.Key!, item.AffectedCards);
        Assert.Contains(second.Key!, item.AffectedCards);
        Assert.True(item.TimeToCreationMs >= (long)TimeSpan.FromHours(37).TotalMilliseconds);

        var followUp = scanner.FindJob(item.FollowUpTaskId, _project);
        Assert.NotNull(followUp);
        Assert.Single(scanner.ScanAllJobs(), task => task.CreationSource == TimelineActors.Orchestrator);
        Assert.Equal("orchestrator", followUp!.CreationSource);
        Assert.Equal("Orchestrator", followUp.CreatedBy);
        Assert.NotEqual(TimelineActors.Orchestrator, followUp.Agent);
        Assert.Equal(TaskStates.Preparation, followUp.State);
        Assert.Equal(2, followUp.References.FollowUpOf.Count);
        var followUpPrompt = File.ReadAllText(Path.Combine(followUp.FolderPath, "prompt.md"));
        Assert.Contains(first.Key!, followUpPrompt);
        Assert.Contains(second.Key!, followUpPrompt);
        var followUpTimeline = timeline.ReadAll(followUp.FolderPath);
        var createdEvent = Assert.Single(followUpTimeline, timelineEvent =>
            timelineEvent.Kind == TimelineEventKinds.PromptCreated);
        Assert.Equal(TimelineActors.Orchestrator, createdEvent.Actor);
        Assert.Equal("orchestrator", createdEvent.Details!["creationSource"]);

        var firstRoundTrip = scanner.FindJob(first.Id, _project)!;
        var secondRoundTrip = scanner.FindJob(second.Id, _project)!;
        Assert.Contains(item.FollowUpKey, firstRoundTrip.References.RaisedFollowUps);
        Assert.Contains(item.FollowUpKey, firstRoundTrip.References.BlockedBy);
        Assert.Contains(item.FollowUpKey, secondRoundTrip.References.RaisedFollowUps);
        Assert.Contains(item.FollowUpKey, secondRoundTrip.References.BlockedBy);
        Assert.Equal($"waiting on {item.FollowUpKey}: review toolchain unavailable", attached.WaitReason);
        var originTimeline = timeline.ReadAll(firstRoundTrip.FolderPath);
        Assert.Contains(originTimeline, timelineEvent =>
            timelineEvent.Kind == TimelineEventKinds.FailureInterventionRaised
            && timelineEvent.Actor == TimelineActors.Orchestrator);

        var feed = log.Read(_project);
        Assert.Equal(2, feed.Count(entry => entry.Topic == OrchestratorLogTopics.FailureIntervention));
        Assert.All(feed.Where(entry => entry.Topic == OrchestratorLogTopics.FailureIntervention),
            entry => Assert.Contains("intervention raised:", entry.Summary));
        var originPipeline = pipeline.Read(firstRoundTrip.FolderPath)!;
        Assert.Contains(originPipeline.Steps, step =>
            step.StepId == PipelineCatalogue.FailureInterventionStepId
            && step.Verdict == "intervention-raised"
            && step.VerdictSummary!.Contains(item.FollowUpKey));
        Assert.Contains(originPipeline.Steps, step =>
            step.StepId == PipelineCatalogue.OrchestratorDecisionStepId
            && step.Verdict == "intervention"
            && step.VerdictSummary!.Contains(item.FollowUpKey));

        var report = FailureInterventionReporting.Summarize(service.List(_project));
        Assert.Equal(1, report.Count);
        Assert.Equal(1, report.Open);
        Assert.Equal(0, report.Closed);
        var reportLine = FailureInterventionReporting.Line(item);
        Assert.Contains("ReviewInfra/ToolUnavailable", reportLine);
        Assert.Contains(first.Key!, reportLine);
        Assert.Contains(second.Key!, reportLine);
        WriteEvidenceSnapshots(
            item, followUp, firstRoundTrip, followUpTimeline, originTimeline, originPipeline, feed, reportLine);
    }

    private static void WriteEvidenceSnapshots(
        FailureInterventionRecord item,
        TaskInfo followUp,
        TaskInfo origin,
        IReadOnlyList<TimelineEvent> followUpTimeline,
        IReadOnlyList<TimelineEvent> originTimeline,
        PipelineExecutionRecord originPipeline,
        IReadOnlyList<OrchestratorLogEntry> feed,
        string reportLine)
    {
        var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
        if (string.IsNullOrWhiteSpace(results)) return;
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "driving-incident-replay.json"),
            JsonSerializer.Serialize(new
            {
                intervention = item,
                followUp,
                origin,
                followUpTimeline,
                originTimeline,
                originPipeline,
                feed,
            },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var key = WebUtility.HtmlEncode(item.FollowUpKey);
        File.WriteAllText(Path.Combine(results, "origin-card-chip.dom.html"),
            $"<button data-testid=\"task-card-intervention\" data-intervention-state=\"open\" type=\"button\"><span aria-hidden=\"true\">↗</span> {key} · Preparation · open</button>");
        File.WriteAllText(Path.Combine(results, "origin-detail-follow-up.dom.html"),
            $"<section data-testid=\"references-section\"><div data-testid=\"references-row-raisedFollowUps\"><button>{key} · Preparation · open</button></div></section>");
        File.WriteAllText(Path.Combine(results, "follow-up-header.dom.html"),
            $"<header><span data-testid=\"detail-key-chip\">{key}</span><span data-testid=\"detail-created-by-orchestrator\">Created by Orchestrator</span></header>");
        File.WriteAllText(Path.Combine(results, "orchestrator-feed-line.dom.html"),
            $"<article data-topic=\"failure-intervention\">{WebUtility.HtmlEncode(feed[0].Summary)}</article>");
        File.WriteAllText(Path.Combine(results, "reporting-item.dom.html"),
            $"<li data-testid=\"intervention-report-item\">{WebUtility.HtmlEncode(reportLine)}</li>");
    }

    private TaskInfo CreateOrigin(TaskScannerService scanner, TaskMutationService mutations, string title)
    {
        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = title,
            WatchPath = _project,
            TargetState = TaskStates.AutoReview,
            PromptMarkdown = "Replay the recorded review incident.",
        });
        Assert.NotNull(id);
        return scanner.FindJob(id!, _project)!;
    }

    private (TaskScannerService Scanner, TaskMutationService Mutations, FailureInterventionService Service,
        OrchestratorLog Log, TimelineLog Timeline, PipelineExecutionLog Pipeline) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = _project,
            ["WatchPaths:0:RootPath"] = _project,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_project, "demo", DefaultWorkspace.Id);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline);
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var service = new FailureInterventionService(mutations, scanner, timeline, log,
            NullLogger<FailureInterventionService>.Instance, prompts, pipelineLog: pipeline);
        return (scanner, mutations, service, log, timeline, pipeline);
    }

    private sealed record IncidentFixture(
        string FailureCode,
        string Outcome,
        DateTime FirstFailureAt,
        IReadOnlyList<IncidentObservation> Observations);

    private sealed record IncidentObservation(
        string Card,
        int ExitCode,
        long DurationMs,
        string StdoutTail,
        string StderrTail,
        string StepId,
        IReadOnlyList<string> EvidencePointers);
}
