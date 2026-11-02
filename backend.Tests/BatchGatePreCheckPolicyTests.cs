using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the fixed date is input to a pure admission policy and never meets the wall clock.
public sealed class BatchGatePreCheckPolicyTests
{
    [Theory]
    [InlineData(false, null, false, false, BatchGatePreCheckAction.Admit)]
    [InlineData(true, null, false, false, BatchGatePreCheckAction.Run)]
    [InlineData(true, "green", false, false, BatchGatePreCheckAction.Admit)]
    [InlineData(true, "red", false, false, BatchGatePreCheckAction.ReturnToAgent)]
    [InlineData(true, null, true, false, BatchGatePreCheckAction.Admit)]
    [InlineData(true, "red", true, false, BatchGatePreCheckAction.ReturnToAgent)]
    [InlineData(true, null, false, true, BatchGatePreCheckAction.Admit)]
    public void Admission_matrix(bool enabled, string? outcome, bool expired, bool owned,
        BatchGatePreCheckAction expected)
    {
        var at = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, BatchGatePreCheckPolicy.Decide(enabled, outcome, at,
            at.AddMinutes(expired ? 20 : 5), 20, owned));
    }
}
