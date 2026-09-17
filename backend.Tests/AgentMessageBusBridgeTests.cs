using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Phase A bridge contract (docs/system/architecture/bus/agent-message-bus.md section 9):
/// every existing structured signal we elected to project onto the bus produces
/// a typed <see cref="AgentMessage"/> with the right kind, severity, and
/// participant. The legacy raw streams (cli-output.log, observations.jsonl,
/// interventions.jsonl, orchestrator.jsonl) remain canonical; these tests
/// verify only that the typed projection is correct.
/// </summary>
public sealed class AgentMessageBusBridgeTests : IDisposable
{
    private readonly string _workspace;
    private readonly AgentMessageBusStore _store;
    private readonly AgentMessageBusBridge _bridge;

    public AgentMessageBusBridgeTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "bus-bridge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        _store = new AgentMessageBusStore();
        _bridge = new AgentMessageBusBridge(_store, config, NullLogger<AgentMessageBusBridge>.Instance);
    }

    public void Dispose() => TestTempRoot.TryDelete(_workspace);

    [Fact]
    public async Task SeedBuiltInParticipantsAsync_RegistersStandardSet()
    {
        await _bridge.SeedBuiltInParticipantsAsync();
        var participants = _store.ListParticipants(_workspace);
        Assert.Contains(participants, p => p.Id == "user");
        Assert.Contains(participants, p => p.Id == "runtime:taskboard");
        Assert.Contains(participants, p => p.Id == "orchestrator");
        Assert.Contains(participants, p => p.Id == "agent:claude" && p.Cli == "claude");
        Assert.Contains(participants, p => p.Id == "agent:codex" && p.Cli == "codex");
    }

    [Fact]
    public async Task EmitOrchestratorChatAsync_ProducesDecisionMessageWithMappedSeverity()
    {
        var info = NewJobInfo();
        await _bridge.EmitOrchestratorChatAsync(info, OrchestratorMessageKind.GiveUp, "stuck loop hit ceiling");

        var msgs = _store.Recent(_workspace, info.ProjectName, 10);
        var emitted = Assert.Single(msgs);
        Assert.Equal("decision", emitted.Kind);
        Assert.Equal("High", emitted.Severity);
        Assert.Equal($"orchestrator:{info.ProjectName}", emitted.ParticipantId);
        Assert.Equal(info.Id, emitted.JobId);
        Assert.Equal("giveup", emitted.Topic);
        Assert.Contains("stuck loop", emitted.Body);
        Assert.NotNull(emitted.Artifacts);
        Assert.Contains(emitted.Artifacts!, a => a.Kind == "log-slice");
    }

    [Theory]
    [InlineData(OrchestratorMessageKind.Decision, "Info", "decision")]
    [InlineData(OrchestratorMessageKind.Reissue, "Warn", "reissue")]
    [InlineData(OrchestratorMessageKind.HeuristicFallback, "Warn", "heuristicfallback")]
    [InlineData(OrchestratorMessageKind.SoftIntervention, "Warn", "soft-intervention")]
    [InlineData(OrchestratorMessageKind.PermissionBlocked, "High", "permission-blocked")]
    [InlineData(OrchestratorMessageKind.WatchdogTimeout, "High", "watchdog-timeout")]
    [InlineData(OrchestratorMessageKind.EmptyFastExit, "High", "empty-fast-exit")]
    [InlineData(OrchestratorMessageKind.MissingTerminalSentinel, "Warn", "missing-terminal-sentinel")]
    [InlineData(OrchestratorMessageKind.InfraCrash, "High", "infra-crash")]
    [InlineData(OrchestratorMessageKind.OrchestratorInconclusive, "Warn", "orchestrator-inconclusive")]
    [InlineData(OrchestratorMessageKind.CliLaunchFailed, "Warn", "cli-launch-failed")]
    [InlineData(OrchestratorMessageKind.AuthRefreshFailed, "High", "auth-refresh-failed")]
    [InlineData(OrchestratorMessageKind.GiveUp, "High", "giveup")]
    [InlineData(OrchestratorMessageKind.IntegrationConflict, "High", "integration-conflict")]
    [InlineData(OrchestratorMessageKind.IntegrationError, "High", "integration-error")]
    [InlineData(OrchestratorMessageKind.TaskBranchUnpushed, "Warn", "task-branch-unpushed")]
    public async Task EmitOrchestratorChatAsync_MapsKindToSeverityAndTopic(OrchestratorMessageKind kind, string expectedSeverity, string expectedTopic)
    {
        var info = NewJobInfo();
        await _bridge.EmitOrchestratorChatAsync(info, kind, "test");
        var msg = Assert.Single(_store.Recent(_workspace, info.ProjectName, 10));
        Assert.Equal(expectedSeverity, msg.Severity);
        Assert.Equal(expectedTopic, msg.Topic);
    }

    [Fact]
    public async Task EmitSupervisorChatAsync_TagDrivesKindAndSeverity()
    {
        var info = NewJobInfo();
        await _bridge.EmitSupervisorChatAsync(info, "cancel-run", "auto: quota-critical");
        await _bridge.EmitSupervisorChatAsync(info, "chat-note", "supervisor batch summary");
        await _bridge.EmitSupervisorChatAsync(info, "cycle-resume-failed", "host could not resume");

        var msgs = _store.Recent(_workspace, info.ProjectName, 10);
        Assert.Equal(3, msgs.Count);
        var cancel = Assert.Single(msgs, m => m.Topic == "cancel-run");
        Assert.Equal("intervention", cancel.Kind);
        Assert.Equal("Info", cancel.Severity);
        var chat = Assert.Single(msgs, m => m.Topic == "chat-note");
        Assert.Equal("advisory", chat.Kind);
        var fail = Assert.Single(msgs, m => m.Topic == "cycle-resume-failed");
        Assert.Equal("High", fail.Severity);
    }

    [Fact]
    public async Task EmitAdvisoryAsync_MirrorsSupervisorAdvisoryFields()
    {
        var advisory = new SupervisorAdvisory(
            CreatedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Severity: SupervisorSeverity.High,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: "agent silent for 10 minutes",
            JobId: "job-123");

        await _bridge.EmitAdvisoryAsync(advisory);

        var msg = Assert.Single(_store.Recent(_workspace, advisory.Project, 10));
        Assert.Equal("advisory", msg.Kind);
        Assert.Equal("High", msg.Severity);
        Assert.Equal("supervisor:agent-taskboard", msg.ParticipantId);
        Assert.Equal("evidence", msg.Role);
        Assert.Equal("no-progress", msg.Topic);
        Assert.Equal(advisory.JobId, msg.JobId);
        Assert.Equal(advisory.CreatedAt, msg.CreatedAt);
        Assert.NotNull(msg.Artifacts);
        Assert.Contains(msg.Artifacts!, a => a.Kind == "supervisor-advisory");
        Assert.Contains(msg.Tags!, t => t == "hardcheck");
    }

    [Fact]
    public async Task EmitInterventionAsync_MirrorsSupervisorInterventionFields()
    {
        var intervention = new SupervisorIntervention(
            CreatedAt: new DateTime(2026, 5, 5, 13, 0, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Kind: SupervisorInterventionKind.ForceFail,
            Source: SupervisorSource.AutoIntervention,
            Reason: "tool-call repeat 12x",
            JobId: "job-456");

        await _bridge.EmitInterventionAsync(intervention);

        var msg = Assert.Single(_store.Recent(_workspace, intervention.Project, 10));
        Assert.Equal("intervention", msg.Kind);
        Assert.Equal("High", msg.Severity);
        Assert.Equal("supervisor:agent-taskboard", msg.ParticipantId);
        Assert.Equal("ForceFail", msg.Topic);
        Assert.Equal(intervention.JobId, msg.JobId);
        Assert.Contains(msg.Tags!, t => t == "supervisor-intervention");
    }

    [Fact]
    public async Task EmitUserPromptAsync_RecordsAsQuestionFromUser()
    {
        var info = NewJobInfo();
        await _bridge.EmitUserPromptAsync(info, "please retry with claude haiku", "continue");

        var msg = Assert.Single(_store.Recent(_workspace, info.ProjectName, 10));
        Assert.Equal("question", msg.Kind);
        Assert.Equal("user", msg.ParticipantId);
        Assert.Equal("actor", msg.Role);
        Assert.Contains("retry with claude haiku", msg.Body);
        Assert.Contains(msg.Tags!, t => t == "mode:continue");
    }

    [Fact]
    public async Task EmitRunStartedAndFinishedAsync_ShareDerivedRunId()
    {
        var info = NewJobInfo();
        var startedAt = new DateTime(2026, 5, 5, 9, 30, 0, DateTimeKind.Utc);
        await _bridge.EmitRunStartedAsync(info, "claude", startedAt, sessionId: "abc-123", intent: "AutoPickup");
        await _bridge.EmitRunFinishedAsync(info, "claude", startedAt, "completed", durationSeconds: 42.5, agentOutcome: "Done");

        var msgs = _store.Recent(_workspace, info.ProjectName, 10);
        Assert.Equal(2, msgs.Count);
        var started = Assert.Single(msgs, m => m.Topic == "RunStarted");
        var finished = Assert.Single(msgs, m => m.Topic == "RunFinished");
        Assert.Equal(started.RunId, finished.RunId);
        Assert.Equal("lifecycle", started.Kind);
        Assert.Equal("Info", started.Severity);
        Assert.Equal("Info", finished.Severity);
        Assert.Equal("abc-123", started.CliSessionId);
        Assert.Equal(AgentMessageBusBridge.DeriveRunId(info.Id, startedAt), started.RunId);
    }

    [Fact]
    public async Task EmitRunFinishedAsync_SeverityTracksStatus()
    {
        var info = NewJobInfo();
        var t = DateTime.UtcNow;
        await _bridge.EmitRunFinishedAsync(info, "codex", t, "failed", 1.0, null);
        await _bridge.EmitRunFinishedAsync(info, "codex", t.AddSeconds(1), "cancelled", 1.0, null);
        await _bridge.EmitRunFinishedAsync(info, "codex", t.AddSeconds(2), "completed", 1.0, null);

        var msgs = _store.Recent(_workspace, info.ProjectName, 10);
        Assert.Equal("High", msgs.Single(m => (m.Payload!.Value.GetProperty("status").GetString()) == "failed").Severity);
        Assert.Equal("Warn", msgs.Single(m => (m.Payload!.Value.GetProperty("status").GetString()) == "cancelled").Severity);
        Assert.Equal("Info", msgs.Single(m => (m.Payload!.Value.GetProperty("status").GetString()) == "completed").Severity);
    }

    [Fact]
    public async Task EmitRunStopRequestedAsync_RecordsTriggerSeparately()
    {
        var info = NewJobInfo();
        await _bridge.EmitRunStopRequestedAsync(info, RunStopReason.Watchdog, source: "supervisor");
        var msg = Assert.Single(_store.Recent(_workspace, info.ProjectName, 10));
        Assert.Equal("lifecycle", msg.Kind);
        Assert.Equal("RunStopRequested", msg.Topic);
        Assert.Equal("Warn", msg.Severity);
    }

    [Fact]
    public async Task EmitJobLifecycleAsync_RecordsStateTransition()
    {
        var info = NewJobInfo();
        await _bridge.EmitJobLifecycleAsync(info, "TaskStateMoved", "3-progress", "4-review", "agent reported done");
        var msg = Assert.Single(_store.Recent(_workspace, info.ProjectName, 10));
        Assert.Equal("lifecycle", msg.Kind);
        Assert.Equal("TaskStateMoved", msg.Topic);
        Assert.Contains("3-progress -> 4-review", msg.Summary);
    }

    [Fact]
    public async Task EmitTokenUsageAsync_PopulatesTokensBlock()
    {
        var usage = new OrchestratorTokenUsage
        {
            Model = "claude-opus-4-7",
            InputTokens = 1500,
            OutputTokens = 200,
            CacheReadTokens = 5000,
            CacheCreationTokens = 50,
        };
        await _bridge.EmitTokenUsageAsync(
            "agent-taskboard", "job-1",
            participantId: AgentMessageBusBridge.ParticipantOrchestrator,
            topic: "orchestrator-decision", usage);

        var msg = Assert.Single(_store.Recent(_workspace, "agent-taskboard", 10));
        Assert.Equal("token-usage", msg.Kind);
        Assert.NotNull(msg.Tokens);
        Assert.Equal(1500, msg.Tokens!.Input);
        Assert.Equal(200, msg.Tokens.Output);
        Assert.Equal(5000, msg.Tokens.CacheRead);
        Assert.Equal(50, msg.Tokens.CacheWrite);
        Assert.Equal("claude-opus-4-7", msg.Tokens.Model);
    }

    [Fact]
    public async Task EmitAsync_NoOpsWhenWorkspaceMissing()
    {
        // Build a bridge with no TaskRepository configured. Any emit must
        // succeed silently (the producer never blocks on bus availability).
        var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var unconfigured = new AgentMessageBusBridge(_store, emptyConfig, NullLogger<AgentMessageBusBridge>.Instance);

        await unconfigured.EmitOrchestratorChatAsync(NewJobInfo(), OrchestratorMessageKind.Decision, "ignored");
        // Nothing should have been written into our temp workspace.
        var ws = Path.Combine(_workspace, "logs", "bus");
        Assert.False(Directory.Exists(ws));
    }

    [Fact]
    public void DeriveRunId_DeterministicFromJobAndStartTime()
    {
        var t = new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Utc);
        var a = AgentMessageBusBridge.DeriveRunId("job-x", t);
        var b = AgentMessageBusBridge.DeriveRunId("job-x", t);
        var c = AgentMessageBusBridge.DeriveRunId("job-x", t.AddSeconds(1));
        var d = AgentMessageBusBridge.DeriveRunId("job-y", t);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public async Task RegisterSupportingAgentAsync_AddsParticipantWithSupportingAgentKind()
    {
        await _bridge.RegisterSupportingAgentAsync(
            topic: "roadmap-alignment",
            displayName: "Roadmap alignment review",
            cli: "claude",
            skill: "roadmap-alignment");

        var participants = _store.ListParticipants(_workspace);
        var p = Assert.Single(participants, x => x.Id == "support:roadmap-alignment");
        Assert.Equal("SupportingAgent", p.Kind);
        Assert.Equal("Roadmap alignment review", p.DisplayName);
        Assert.Equal("claude", p.Cli);
        Assert.Equal("roadmap-alignment", p.Skill);
    }

    [Fact]
    public async Task EmitSupportingAgentReportAsync_StructuredReport_ProducesDecisionWithBothArtifacts()
    {
        await _bridge.EmitSupportingAgentReportAsync(
            project: "agent-taskboard",
            topic: "roadmap-alignment",
            reportId: "01HXYZSTRUCTURED",
            summary: "On track. Three follow-ups suggested.",
            severity: "Warn",
            parseStatus: "Structured",
            markdownPath: "/ws/logs/analysis/agent-taskboard/01HXYZSTRUCTURED.md",
            jsonSidecarPath: "/ws/logs/analysis/agent-taskboard/01HXYZSTRUCTURED.json",
            cli: "claude",
            skill: "roadmap-alignment");

        var msg = Assert.Single(_store.Recent(_workspace, "agent-taskboard", 10));
        Assert.Equal("decision", msg.Kind);
        Assert.Equal("Warn", msg.Severity);
        Assert.Equal("evidence", msg.Role);
        Assert.Equal("support:roadmap-alignment", msg.ParticipantId);
        Assert.Equal("roadmap-alignment", msg.Topic);
        Assert.NotNull(msg.Artifacts);
        Assert.Contains(msg.Artifacts!, a => a.Kind == "markdown-report");
        Assert.Contains(msg.Artifacts!, a => a.Kind == "json-document");
        Assert.Contains(msg.Tags!, t => t == "supporting-agent");
        Assert.Contains(msg.Tags!, t => t == "roadmap-alignment");
        Assert.Contains(msg.Tags!, t => t == "parse-structured");
        Assert.Contains(msg.Tags!, t => t == "cli-claude");
        Assert.Contains(msg.Tags!, t => t == "skill-roadmap-alignment");
        Assert.NotNull(msg.Payload);
        Assert.Equal("01HXYZSTRUCTURED", msg.Payload!.Value.GetProperty("reportId").GetString());
        Assert.Equal("Structured", msg.Payload.Value.GetProperty("parseStatus").GetString());
    }

    [Fact]
    public async Task EmitSupportingAgentReportAsync_MalformedJson_ProducesObservationWithParseError()
    {
        await _bridge.EmitSupportingAgentReportAsync(
            project: "agent-taskboard",
            topic: "roadmap-alignment",
            reportId: "01HXYZBROKEN",
            summary: "Markdown body retained; JSON sidecar failed to parse.",
            severity: "Info",
            parseStatus: "MalformedJson",
            markdownPath: "/ws/logs/analysis/agent-taskboard/01HXYZBROKEN.md",
            parseError: "Unexpected token at line 4");

        var msg = Assert.Single(_store.Recent(_workspace, "agent-taskboard", 10));
        // MalformedJson must NOT be advertised as a typed verdict; it lands
        // as an observation so the timeline does not promise a structured
        // decision the report cannot back up.
        Assert.Equal("observation", msg.Kind);
        Assert.Contains(msg.Tags!, t => t == "parse-malformedjson");
        // JSON sidecar artifact is omitted when the sidecar is missing.
        Assert.Single(msg.Artifacts!, a => a.Kind == "markdown-report");
        Assert.DoesNotContain(msg.Artifacts!, a => a.Kind == "json-document");
        // Parser error is surfaced verbatim so the UI can render the raw
        // fallback warning without re-reading the report file.
        Assert.Equal("Unexpected token at line 4",
            msg.Payload!.Value.GetProperty("parseError").GetString());
    }

    [Fact]
    public async Task EmitSupportingAgentReportAsync_CriticalSeverity_CollapsesToHighOnEnvelope()
    {
        await _bridge.EmitSupportingAgentReportAsync(
            project: "agent-taskboard",
            topic: "security-audit",
            reportId: "01HXYZCRIT",
            summary: "Critical finding: hardcoded credentials in commit 9c1d3aa.",
            severity: "Critical",
            parseStatus: "Structured",
            markdownPath: "/ws/logs/analysis/agent-taskboard/01HXYZCRIT.md");

        var msg = Assert.Single(_store.Recent(_workspace, "agent-taskboard", 10));
        // Bus envelope severity ladder is Info|Warn|High; Critical collapses
        // to High but the original is preserved on the payload so the UI
        // badge can keep the louder Critical class.
        Assert.Equal("High", msg.Severity);
        Assert.Equal("Critical",
            msg.Payload!.Value.GetProperty("analysisSeverity").GetString());
    }

    [Fact]
    public void ParticipantSupportingFor_SlugifiesTopicForBusTagSafety()
    {
        Assert.Equal("support:roadmap-alignment",
            AgentMessageBusBridge.ParticipantSupportingFor("Roadmap Alignment"));
        Assert.Equal("support:security-audit",
            AgentMessageBusBridge.ParticipantSupportingFor("security_audit"));
        Assert.Equal("support:ux-ui-council",
            AgentMessageBusBridge.ParticipantSupportingFor("UX/UI council"));
    }

    [Fact]
    public async Task EmitSupportingAgentReportAsync_SteeringDocsDriftTopic_LandsAsCanonicalParticipantAndTags()
    {
        // Pins the second wired supporting-agent topic. The endpoint at
        // POST /api/analysis/{project}/actions/steering-docs-drift calls
        // EmitSupportingAgentReportAsync with
        // SteeringDocsSummaryDriftService.Topic ("steering-docs-summary-and-drift")
        // when an agent narrative is supplied; this test locks the
        // resulting participant id and tag set so a future refactor of the
        // service slug or the bridge's kebab logic surfaces immediately.
        await _bridge.EmitSupportingAgentReportAsync(
            project: "agent-taskboard",
            topic: AgentStudio.Analysis.SteeringDocsSummaryDriftService.Topic,
            reportId: "01HXYZDOCSDRIFT",
            summary: "Steering surface drifted on shim contract.",
            severity: "Warn",
            parseStatus: "Structured",
            markdownPath: "/ws/logs/analysis/agent-taskboard/01HXYZDOCSDRIFT.md",
            jsonSidecarPath: "/ws/logs/analysis/agent-taskboard/01HXYZDOCSDRIFT.json",
            skill: AgentStudio.Analysis.SteeringDocsSummaryDriftService.Topic);

        var msg = Assert.Single(_store.Recent(_workspace, "agent-taskboard", 10));
        Assert.Equal("support:steering-docs-summary-and-drift", msg.ParticipantId);
        Assert.Equal("steering-docs-summary-and-drift", msg.Topic);
        Assert.Equal("decision", msg.Kind);
        Assert.Equal("Warn", msg.Severity);
        Assert.Contains(msg.Tags!, t => t == "supporting-agent");
        Assert.Contains(msg.Tags!, t => t == "steering-docs-summary-and-drift");
        Assert.Contains(msg.Tags!, t => t == "skill-steering-docs-summary-and-drift");
        Assert.Contains(msg.Tags!, t => t == "parse-structured");
    }

    [Fact]
    public async Task OrchestratorChatLog_Append_AlsoWritesBusMessage()
    {
        // End-to-end: the chat log keeps writing cli-output.log (the canonical
        // record the activity-log parser reads); the bus mirror is verified
        // here so we know the wiring still flows after a refactor. Both
        // producers must succeed.
        var info = NewJobInfo();
        Directory.CreateDirectory(info.FolderPath);

        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance, _bridge);
        var ok = chatLog.Append(info, OrchestratorMessageKind.Reissue, "auto-retry with stronger framing");
        Assert.True(ok);

        // Canonical record: cli-output.log line.
        var logPath = Path.Combine(info.FolderPath, "logs", "cli-output.log");
        Assert.True(File.Exists(logPath));
        var canonical = File.ReadAllText(logPath);
        Assert.Contains("[orchestrator]", canonical);
        Assert.Contains("[reissue]", canonical);

        // Bus mirror: typed projection. The chat log fires-and-forgets so
        // give the background append a moment, but use a polled wait so the
        // test stays fast.
        await WaitForMessagesAsync(info.ProjectName, atLeast: 1);
        var msg = Assert.Single(_store.Recent(_workspace, info.ProjectName, 10));
        Assert.Equal("decision", msg.Kind);
        Assert.Equal("Warn", msg.Severity);
        Assert.Equal("reissue", msg.Topic);
    }

    private async Task WaitForMessagesAsync(string project, int atLeast, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_store.Recent(_workspace, project, atLeast + 1).Count >= atLeast) return;
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// AGT-2858: the fake job folder is rooted in this fixture's own workspace,
    /// not in the shared temp root. As a sibling of the temp root it was never
    /// deleted by anything, and the review host had collected 851 of them.
    /// </summary>
    private TaskInfo NewJobInfo(string id = "job-fixture", string project = "agent-taskboard")
    {
        var folder = Path.Combine(_workspace, "fake-jobs", "bus-bridge-fake-job-" + Guid.NewGuid().ToString("N"));
        return new TaskInfo
        {
            Id = id,
            TaskKey = $"watch::{id}",
            Title = "fixture job",
            State = "3-progress",
            ProjectName = project,
            WatchPath = "watch",
            FolderPath = folder,
            CliType = "claude",
        };
    }
}
