using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The runner daemon lives for days and streams a whole agent run's stdout through
/// <see cref="BoundedOutputBuffer"/>. These pin the memory bound: the buffer keeps
/// only the tail, so a runaway run cannot grow the heap without limit, while the
/// terminal sentinel (emitted last) stays inside the retained window.
/// </summary>
public class BoundedOutputBufferTests
{
    [Fact]
    public void Short_output_is_retained_verbatim_without_an_elision_notice()
    {
        var buffer = new BoundedOutputBuffer(64 * 1024);
        buffer.Append("first");
        buffer.Append("second");

        Assert.Equal(0, buffer.DroppedLines);
        Assert.Equal("first\nsecond\n", buffer.ToString());
    }

    [Fact]
    public void Output_beyond_the_budget_drops_oldest_lines_and_keeps_the_tail()
    {
        // Budget fits only a handful of the fixed-width lines below.
        var buffer = new BoundedOutputBuffer(maxChars: 100);
        for (var i = 0; i < 1_000; i++)
            buffer.Append($"line-{i:D5}"); // 10 chars + newline

        var rendered = buffer.ToString();

        Assert.True(buffer.DroppedLines > 0, "old lines should have been evicted");
        Assert.DoesNotContain("line-00000", rendered);        // earliest dropped
        Assert.Contains("line-00999", rendered);              // newest kept
        Assert.Contains("elided to bound runner memory", rendered);
        // The retained tail stays within a small multiple of the budget.
        Assert.True(rendered.Length < 100 * 4, $"tail unexpectedly large: {rendered.Length}");
    }

    [Fact]
    public void A_single_oversized_line_is_never_fully_dropped()
    {
        var buffer = new BoundedOutputBuffer(maxChars: 8);
        var big = new string('x', 10_000);
        buffer.Append(big);

        Assert.Contains(big, buffer.ToString());
    }

    [Fact]
    public void A_terminal_sentinel_in_the_tail_is_still_recognised()
    {
        var buffer = new BoundedOutputBuffer(maxChars: 200);
        for (var i = 0; i < 500; i++) buffer.Append($"progress {i}");
        buffer.Append("[[TASK_DONE]]"); // the sign-off is emitted last

        var outcome = SentinelScanner.Scan(buffer.ToString());
        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
    }

    [Fact]
    public void Agt2736_log_cap_replay_keeps_the_question_and_terminal_sentinel()
    {
        var buffer = new BoundedOutputBuffer(maxChars: 64 * 1024);
        for (var i = 0; i < 10_000; i++) buffer.Append($"old codex frame {i:D5} {new string('x', 32)}");
        var transcript = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "needs-input", "agt-2736.codex.jsonl"));
        foreach (var line in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries)) buffer.Append(line);

        var outcome = SentinelScanner.Scan(buffer.ToString());

        Assert.True(buffer.DroppedLines > 0);
        Assert.Equal(RunOutcomeKind.NeedsInput, outcome.Kind);
        Assert.Contains("Which deployment strategy should I implement?", outcome.NeedsInputMessage);
        Assert.Contains("Option A: managed connector", outcome.NeedsInputMessage);
        Assert.Contains("Option B: direct LAN deployment", outcome.NeedsInputMessage);
    }
}
