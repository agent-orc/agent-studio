using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchDocumentationPolicyTests
{
    public static TheoryData<string, WorkbenchDocumentationReference[], bool> Matrix => new()
    {
        { "decided", [Reference("AGT-1", exists: true, terminal: true)], true },
        { "decided", [Reference("AGT-1", exists: true, terminal: true), Reference("AGT-2", exists: true, terminal: true)], true },
        { "decided", [Reference("AGT-1", exists: true, terminal: false)], false },
        { "decided", [Reference("AGT-1", exists: false, terminal: false)], false },
        { "decided", [], false },
        { "active", [Reference("AGT-1", exists: true, terminal: true)], false },
        { "archived", [Reference("AGT-1", exists: true, terminal: true)], false },
        { "documented", [Reference("AGT-1", exists: true, terminal: true)], false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Evaluate_OnlySuggestsForDecidedItemsWithResolvedTerminalReferences(
        string status,
        WorkbenchDocumentationReference[] references,
        bool expected)
    {
        var result = WorkbenchDocumentationPolicy.Evaluate(status, references);

        Assert.Equal(expected, result.Eligible);
        Assert.Equal(references.Length, result.TotalCount);
    }

    [Fact]
    public void Evaluate_DeduplicatesKeysAndReportsOpenAndMissingCounts()
    {
        var result = WorkbenchDocumentationPolicy.Evaluate("decided",
        [
            Reference("AGT-1", exists: true, terminal: true),
            Reference("agt-1", exists: true, terminal: true),
            Reference("AGT-2", exists: true, terminal: false),
            Reference("AGT-3", exists: false, terminal: false),
        ]);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.TerminalCount);
        Assert.Equal(1, result.OpenCount);
        Assert.Equal(1, result.MissingCount);
        Assert.False(result.Eligible);
    }

    [Fact]
    public void Evaluate_NamesOpenAndMissingCardsWhileADecidedItemIsBlocked()
    {
        var result = WorkbenchDocumentationPolicy.Evaluate("decided",
        [
            Reference("AGT-1", exists: true, terminal: true),
            Reference("AGT-2", exists: true, terminal: false),
            Reference("AGT-3", exists: false, terminal: false),
        ]);

        Assert.Contains($"Still open: AGT-2 ({TaskStates.Ready}).", result.BlockedReason);
        Assert.Contains("Not found: AGT-3.", result.BlockedReason);
        Assert.DoesNotContain("AGT-1", result.BlockedReason);
        Assert.Null(WorkbenchDocumentationPolicy.Evaluate("decided",
            [Reference("AGT-1", exists: true, terminal: true)]).BlockedReason);
        Assert.Null(WorkbenchDocumentationPolicy.Evaluate("active",
            [Reference("AGT-2", exists: true, terminal: false)]).BlockedReason);
    }

    private static WorkbenchDocumentationReference Reference(
        string key,
        bool exists,
        bool terminal) => new(key, exists, terminal, terminal ? TaskStates.Completed : TaskStates.Ready);
}
