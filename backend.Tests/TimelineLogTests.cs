using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Schema + round-trip contract for the ADR-0049 ledger writer. The wire
/// format is camelCase JSONL, one event per line, best-effort, and tolerant
/// of torn trailing lines on read.
/// </summary>
public class TimelineLogTests : IDisposable
{
    private readonly string _jobFolder;
    private readonly TimelineLog _log = new(NullLogger<TimelineLog>.Instance);

    public TimelineLogTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "timeline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Append_ThenReadAll_RoundTripsEveryField()
    {
        var ok = _log.Append(_jobFolder,
            kind: TimelineEventKinds.OrchestratorEscalated,
            actor: TimelineActors.Orchestrator,
            summary: "Handed to a human.",
            runId: "session-42",
            payloadRef: "status.md",
            details: new Dictionary<string, string> { ["cause"] = "needs-input-escalate", ["reason"] = "Needs a call." });
        Assert.True(ok);

        var evt = Assert.Single(_log.ReadAll(_jobFolder));
        Assert.Equal(TimelineEventKinds.OrchestratorEscalated, evt.Kind);
        Assert.Equal(TimelineActors.Orchestrator, evt.Actor);
        Assert.Equal("Handed to a human.", evt.Summary);
        Assert.Equal("session-42", evt.RunId);
        Assert.Equal("status.md", evt.PayloadRef);
        Assert.Equal("needs-input-escalate", evt.Details?["cause"]);
        Assert.Equal("Needs a call.", evt.Details?["reason"]);
        Assert.NotEqual(default, evt.Ts);
    }

    [Fact]
    public void Workflow_review_event_records_the_human_and_candidate_digest()
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(".github/workflows/check.yml"))).ToLowerInvariant();
        Assert.True(_log.Append(_jobFolder, TimelineEventKinds.WorkflowChangeReviewed,
            TimelineActors.Human("reviewer"), "Reviewed CI workflow candidates before commit.",
            details: new Dictionary<string, string> { ["pathCount"] = "1", ["pathsSha256"] = digest }));

        var entry = Assert.Single(_log.ReadAll(_jobFolder));
        Assert.Equal("workflow_change_reviewed", entry.Kind);
        Assert.Equal("human:reviewer", entry.Actor);
        Assert.Equal(digest, entry.Details?["pathsSha256"]);
    }

    [Fact]
    public void Append_WritesCamelCaseJsonl_OmittingNulls()
    {
        _log.Append(_jobFolder,
            kind: TimelineEventKinds.PromptCreated,
            actor: TimelineActors.System,
            summary: "Prompt written.");

        var path = TaskPaths.TimelineLog(_jobFolder);
        var raw = File.ReadAllText(path).Trim();

        // camelCase property names on the wire.
        Assert.Contains("\"ts\":", raw);
        Assert.Contains("\"kind\":\"prompt_created\"", raw);
        Assert.Contains("\"actor\":\"system\"", raw);
        Assert.Contains("\"summary\":\"Prompt written.\"", raw);
        // WhenWritingNull: optional fields are absent, not null literals.
        Assert.DoesNotContain("\"runId\"", raw);
        Assert.DoesNotContain("\"payloadRef\"", raw);
        Assert.DoesNotContain("\"details\"", raw);
        // One row, no pretty-printing.
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public void ReadAll_PreservesAppendOrder_AcrossMultipleEvents()
    {
        _log.Append(_jobFolder, TimelineEventKinds.PromptCreated, TimelineActors.System, "1");
        _log.Append(_jobFolder, TimelineEventKinds.AgentRunStarted, TimelineActors.Agent, "2");
        _log.Append(_jobFolder, TimelineEventKinds.AgentRunFinished, TimelineActors.Agent, "3");

        var events = _log.ReadAll(_jobFolder);
        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "1", "2", "3" }, events.Select(e => e.Summary));
    }

    [Fact]
    public void ReadAll_SkipsTornTrailingLine_KeepsValidEvents()
    {
        _log.Append(_jobFolder, TimelineEventKinds.PromptCreated, TimelineActors.System, "valid");
        // Simulate a torn write: append a half-line with no newline contract.
        File.AppendAllText(TaskPaths.TimelineLog(_jobFolder), "{\"kind\":\"agent_run_st");

        var events = _log.ReadAll(_jobFolder);
        var evt = Assert.Single(events);
        Assert.Equal("valid", evt.Summary);
    }

    [Fact]
    public void ReadAll_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(_log.ReadAll(_jobFolder));
    }

    [Fact]
    public void Append_ReadOnlyContainmentViolation_RoundTrips()
    {
        // ADR-0049 requires a new kind to ship with a writer test. The runner
        // emits this when a read-only (planning / research) run leaves a dirty
        // working tree; the wire string is the closed-enum constant and the
        // details bag carries the mode, changed-file count, and capped file list.
        var ok = _log.Append(_jobFolder,
            kind: TimelineEventKinds.ReadOnlyContainmentViolation,
            actor: TimelineActors.System,
            summary: "Read-only planning run left 2 changed file(s) - containment violation (not auto-reverted)",
            details: new Dictionary<string, string>
            {
                ["mode"] = "planning",
                ["changedFiles"] = "2",
                ["files"] = "src/a.cs, src/b.cs",
            });
        Assert.True(ok);

        var evt = Assert.Single(_log.ReadAll(_jobFolder));
        Assert.Equal("read_only_containment_violation", evt.Kind);
        Assert.Equal(TimelineActors.System, evt.Actor);
        Assert.Equal("planning", evt.Details?["mode"]);
        Assert.Equal("2", evt.Details?["changedFiles"]);
        Assert.Equal("src/a.cs, src/b.cs", evt.Details?["files"]);
    }
}
