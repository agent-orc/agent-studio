using System;
using System.Collections.Generic;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the continuous-decision scanner contract (ADR-0027). Two
/// invariants matter here:
/// <list type="bullet">
///   <item><c>NEEDS_INPUT</c> and <c>BLOCKED</c> sentinels in the agent
///   stream produce a typed <see cref="PendingDecision"/> entry.</item>
///   <item>A subsequent <c>user</c> / <c>orchestrator</c> / <c>supervisor</c>
///   line resolves the sentinel and the scanner returns null. <c>DONE</c>
///   and <c>NOOP</c> are post-run signals; the scanner ignores them.</item>
/// </list>
/// </summary>
public class PendingDecisionScannerTests
{
    private static CliOutputLine Line(string text, string stream = "stdout")
        => new() { Timestamp = DateTime.UtcNow, Stream = stream, Text = text };

    [Fact]
    public void DetectsNeedsInputInAgentStream()
    {
        var lines = new List<CliOutputLine>
        {
            Line("starting"),
            Line("[[TASK_NEEDS_INPUT: which column should be primary?]]")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.NotNull(hit);
        Assert.Equal(PendingDecisionKind.NeedsInput, hit!.Kind);
        Assert.Equal("which column should be primary?", hit.Reason);
    }

    [Fact]
    public void DetectsBlocked()
    {
        var lines = new List<CliOutputLine>
        {
            Line("[[TASK_BLOCKED: cannot find token]]")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.NotNull(hit);
        Assert.Equal(PendingDecisionKind.Blocked, hit!.Kind);
        Assert.Equal("cannot find token", hit.Reason);
    }

    [Fact]
    public void IgnoresDoneAndNoOpSentinels()
    {
        var lines = new List<CliOutputLine>
        {
            Line("[[TASK_DONE]]"),
            Line("[[TASK_NOOP]]")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.Null(hit);
    }

    [Fact]
    public void ResolvedByUserStreamFollowUp()
    {
        var lines = new List<CliOutputLine>
        {
            Line("[[TASK_NEEDS_INPUT: pick A or B]]"),
            Line("pick A", stream: "user")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.Null(hit);
    }

    [Fact]
    public void ResolvedByOrchestratorStreamFollowUp()
    {
        var lines = new List<CliOutputLine>
        {
            Line("[[TASK_NEEDS_INPUT: pick A or B]]"),
            Line("[reissue] Decision: A.", stream: "orchestrator")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.Null(hit);
    }

    [Fact]
    public void DoesNotMatchInsideUserOrOrchestratorLines()
    {
        // Echoed user input should not trigger a banner: the [user] line
        // already represents resolution, and historic chat replays carry
        // bracketed quotes of the previous sentinel.
        var lines = new List<CliOutputLine>
        {
            Line("[[TASK_NEEDS_INPUT: pick A]]", stream: "user"),
            Line("[[TASK_NEEDS_INPUT: pick A]]", stream: "orchestrator")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.Null(hit);
    }

    [Fact]
    public void LatestUnresolvedSentinelWins()
    {
        var lines = new List<CliOutputLine>
        {
            Line("[[TASK_NEEDS_INPUT: pick A or B]]"),
            Line("pick A", stream: "user"),
            Line("working..."),
            Line("[[TASK_NEEDS_INPUT: now pick C or D]]")
        };

        var hit = PendingDecisionScanner.Scan(lines);

        Assert.NotNull(hit);
        Assert.Equal("now pick C or D", hit!.Reason);
    }

    [Fact]
    public void ReturnsNullOnEmptyBuffer()
    {
        Assert.Null(PendingDecisionScanner.Scan(null));
        Assert.Null(PendingDecisionScanner.Scan(new List<CliOutputLine>()));
    }

    [Fact]
    public void TailWindowBoundsTheScan()
    {
        var lines = new List<CliOutputLine>();
        lines.Add(Line("[[TASK_NEEDS_INPUT: ancient question]]"));
        for (int i = 0; i < 250; i++) lines.Add(Line($"line {i}"));

        // Default window 200 lines: the ancient sentinel falls outside.
        var hit = PendingDecisionScanner.Scan(lines);
        Assert.Null(hit);

        // Wider window picks it up.
        var wider = PendingDecisionScanner.Scan(lines, tailLines: 1000);
        Assert.NotNull(wider);
    }

    [Theory]
    [InlineData("supervisor", "escalating", true)]
    [InlineData("orchestrator", "follow-up", true)]
    [InlineData("user", "answer", true)]
    [InlineData("stdout", "[supervisor] legacy tag in the text", true)]
    [InlineData("stdout", "[user] legacy tag in the text", true)]
    [InlineData("stdout", "plain agent text", false)]
    [InlineData("system", "taskboard marker", false)]
    public void IsFollowUpLine_MatchesTheStreamAndTheLegacyTextTag(string stream, string text, bool expected)
    {
        // Shared by the scan's resolution check and the Blocked fork reader.
        Assert.Equal(expected, PendingDecisionScanner.IsFollowUpLine(Line(text, stream)));
    }
}
