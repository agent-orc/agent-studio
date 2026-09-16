using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over <see cref="ContinueModeGuardPolicy"/> (AGT-2795): a
/// continue on a concept or planning card must not silently re-run the card
/// with an implementation-shaped prompt. No filesystem, process, or DI graph
/// - the decision is pure over the card's mode, the prompt, and the caller's
/// explicit override.
/// </summary>
public class ContinueModeGuardPolicyTests
{
    private static ContinueModeGuardFacts Facts(string? mode, string prompt, string? modeOverride = null) =>
        new("AGT-2795", mode, prompt, modeOverride);

    [Theory]
    [InlineData(TaskModes.Coding)]
    [InlineData(TaskModes.Research)]
    [InlineData(null)] // unknown/empty normalizes to coding
    public void UnguardedMode_AlwaysAllowed_EvenWithAnImplementationPrompt(string? mode)
    {
        var decision = ContinueModeGuardPolicy.Decide(Facts(mode, "Fix the demo route count, change it from 81 to 83."));

        Assert.Equal(ContinueModeGuardAction.Allow, decision.Action);
        Assert.Null(decision.Message);
    }

    [Theory]
    [InlineData(TaskModes.Concept)]
    [InlineData(TaskModes.Planning)]
    public void GuardedMode_ConceptualPrompt_Allowed(string mode)
    {
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(mode, "What do you think of Option B versus Option C for the retention window?"));

        Assert.Equal(ContinueModeGuardAction.Allow, decision.Action);
    }

    // The AGT-2795 reproduction shape: an implementation fix prompt against a
    // card that had been repurposed into the concept card of a Dossier.
    [Fact]
    public void ConceptMode_ImplementationPrompt_NoOverride_Rejected()
    {
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(TaskModes.Concept, "Fix the demo route count, change it from 81 to 83."));

        Assert.Equal(ContinueModeGuardAction.Reject, decision.Action);
        Assert.Contains("'concept' mode", decision.Message);
        Assert.Contains("/api/tasks/AGT-2795/promote-concept", decision.Message);
    }

    [Fact]
    public void PlanningMode_ImplementationPrompt_NoOverride_Rejected_NamesPromoteToCoding()
    {
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(TaskModes.Planning, "Implement the retry limit change we discussed."));

        Assert.Equal(ContinueModeGuardAction.Reject, decision.Action);
        Assert.Contains("'planning' mode", decision.Message);
        Assert.Contains("/api/tasks/AGT-2795/promote-to-coding", decision.Message);
    }

    [Theory]
    [InlineData(TaskModes.Concept)]
    [InlineData(TaskModes.Planning)]
    public void GuardedMode_ImplementationPrompt_MatchingOverride_Allowed(string mode)
    {
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(mode, "Fix the demo route count, change it from 81 to 83.", modeOverride: mode));

        Assert.Equal(ContinueModeGuardAction.Allow, decision.Action);
    }

    [Fact]
    public void ConceptMode_ImplementationPrompt_MismatchedOverride_StillRejected()
    {
        // Confirmation must name the card's actual mode; a caller confirming
        // the wrong mode (or garbage) does not bypass the guard.
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(TaskModes.Concept, "Fix the demo route count.", modeOverride: TaskModes.Planning));

        Assert.Equal(ContinueModeGuardAction.Reject, decision.Action);
    }

    [Fact]
    public void ConceptMode_ImplementationPrompt_BlankOverride_StillRejected()
    {
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(TaskModes.Concept, "Fix the demo route count.", modeOverride: "   "));

        Assert.Equal(ContinueModeGuardAction.Reject, decision.Action);
    }

    [Theory]
    [InlineData("Fix the login bug.")]
    [InlineData("Please implement the retry counter.")]
    [InlineData("Refactor the auth middleware.")]
    [InlineData("Can you patch the null check in RunPlanner?")]
    [InlineData("Update the endpoint to accept a second parameter.")]
    [InlineData("Bump the route count from 81 to 83.")]
    [InlineData("```csharp\nreturn 83;\n```")]
    public void LooksLikeImplementationRequest_TrueForCodeShapedPrompts(string prompt)
        => Assert.True(ContinueModeGuardPolicy.LooksLikeImplementationRequest(prompt));

    [Theory]
    [InlineData("What are the tradeoffs between these two approaches?")]
    [InlineData("Please summarize the current state of the dossier.")]
    [InlineData("I like option A, please write up the decision rationale.")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeImplementationRequest_FalseForConceptualPrompts(string? prompt)
        => Assert.False(ContinueModeGuardPolicy.LooksLikeImplementationRequest(prompt));
}
