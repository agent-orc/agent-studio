using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over <see cref="ImplementationContinueGuardPolicy"/>
/// (AGT-2825): a concept or planning card must reject an
/// implementation-flavored continue prompt with a reason naming the mode and
/// its promotion route, unless the caller sets <c>modeOverride</c>. Coding
/// and research cards, and prompts that don't read as code requests, are
/// always allowed. Pinned without a filesystem, a process, or a DI graph.
/// </summary>
public sealed class ImplementationContinueGuardPolicyTests
{
    private const string ImplementationPrompt = "Bump the demo route count from 81 to 83.";
    private const string DecisionPrompt = "D1: use the direct-merge approach.";

    [Fact]
    public void ConceptCard_ImplementationPrompt_NoOverride_Rejects()
    {
        var decision = ImplementationContinueGuardPolicy.Decide(TaskModes.Concept, ImplementationPrompt, modeOverride: false);

        Assert.Equal(ImplementationContinueAction.Reject, decision.Action);
        Assert.Contains("concept", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("promote-concept", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningCard_ImplementationPrompt_NoOverride_Rejects()
    {
        var decision = ImplementationContinueGuardPolicy.Decide(TaskModes.Planning, ImplementationPrompt, modeOverride: false);

        Assert.Equal(ImplementationContinueAction.Reject, decision.Action);
        Assert.Contains("planning", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("promote-to-coding", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskModes.Concept)]
    [InlineData(TaskModes.Planning)]
    public void ExplicitModeOverride_AlwaysAllows(string mode)
    {
        var decision = ImplementationContinueGuardPolicy.Decide(mode, ImplementationPrompt, modeOverride: true);

        Assert.Equal(ImplementationContinueAction.Allow, decision.Action);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData(TaskModes.Concept)]
    [InlineData(TaskModes.Planning)]
    public void DocumentAppropriatePrompt_Allowed_EvenWithoutOverride(string mode)
    {
        var decision = ImplementationContinueGuardPolicy.Decide(mode, DecisionPrompt, modeOverride: false);

        Assert.Equal(ImplementationContinueAction.Allow, decision.Action);
    }

    [Theory]
    [InlineData(TaskModes.Coding)]
    [InlineData(TaskModes.Research)]
    [InlineData(null)]
    public void UnguardedMode_ImplementationPrompt_Allowed(string? mode)
    {
        var decision = ImplementationContinueGuardPolicy.Decide(mode, ImplementationPrompt, modeOverride: false);

        Assert.Equal(ImplementationContinueAction.Allow, decision.Action);
    }
}
