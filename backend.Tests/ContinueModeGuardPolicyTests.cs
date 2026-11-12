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
        Assert.Contains("Keep the card in concept mode", decision.Message);
        Assert.Contains("promote the card to implementation", decision.Message);
    }

    [Fact]
    public void PlanningMode_ImplementationPrompt_NoOverride_Rejected_NamesPromoteToCoding()
    {
        var decision = ContinueModeGuardPolicy.Decide(
            Facts(TaskModes.Planning, "Implement the retry limit change we discussed."));

        Assert.Equal(ContinueModeGuardAction.Reject, decision.Action);
        Assert.Contains("'planning' mode", decision.Message);
        Assert.Contains("Keep the card in planning mode", decision.Message);
    }

    // AGT-3700: the refusal is shown to the operator in the continue panel,
    // which offers both choices as buttons; it never names an API route or a
    // request field.
    [Theory]
    [InlineData(TaskModes.Concept)]
    [InlineData(TaskModes.Planning)]
    public void Refusal_NamesNoApiPathOrRequestField(string mode)
    {
        var decision = ContinueModeGuardPolicy.Decide(Facts(mode, "Fix the login bug."));

        Assert.Equal(ContinueModeGuardAction.Reject, decision.Action);
        Assert.DoesNotContain("/api/", decision.Message);
        Assert.DoesNotContain("modeOverride", decision.Message);
    }

    /// <summary>
    /// AGT-3700 corpus: ten realistic Dossier follow-ups on a concept card.
    /// Each pairs a change verb (or "fix", "rewrite") with a word the guard
    /// also reads as a code target, and each names a Dossier section,
    /// decision, or option, so none of them is a code-change request. The
    /// first is the AGT-3649 playthrough follow-up that was refused.
    /// </summary>
    public static TheoryData<string> DossierFollowUps => new()
    {
        "Please add one more section to the Dossier: D3 File naming. Compare the naming options for result files and recommend one.",
        "Update section 4 so the config migration options compare cost and risk side by side.",
        "Change the recommendation in D2 to Option B and record why the file watcher alternative lost.",
        "Remove the open question about the API versioning field; the operator answered it in sight review.",
        "Add a decision D5 on whether the schema change ships behind a feature flag.",
        "Rename the heading 'Test strategy' to 'Verification plan' and move it after the evidence section.",
        "Replace the rationale for D1: the route count grew from 81 to 83, so the measurement table needs the new number.",
        "Adjust the appendix to list every endpoint the feature touches, with the owning component for each.",
        "Rewrite the paragraph on bug triage so it reads as a decision, not a task list.",
        "Fix the typo in Option C and add the missing test coverage risk to its cons.",
    };

    [Theory]
    [MemberData(nameof(DossierFollowUps))]
    public void ConceptMode_DossierFollowUp_Allowed(string prompt)
    {
        var decision = ContinueModeGuardPolicy.Decide(Facts(TaskModes.Concept, prompt));

        Assert.Equal(ContinueModeGuardAction.Allow, decision.Action);
        Assert.Null(decision.Message);
    }

    [Theory]
    [InlineData("Add a section D3 on file naming.")]
    [InlineData("Update the open questions with the new test numbers.")]
    [InlineData("Change Option A so the config stays per project.")]
    [InlineData("See § 2: update the api field list.")]
    public void NamesDossierPart_TrueForSectionDecisionOrOptionReferences(string prompt)
        => Assert.True(ContinueModeGuardPolicy.NamesDossierPart(prompt));

    [Theory]
    [InlineData("Update the endpoint to accept a second parameter.")]
    [InlineData("Bump the route count from 81 to 83.")]
    [InlineData("Add a d3 chart to the file.")] // decision ids are upper case
    [InlineData("Change the options parser.")]
    public void NamesDossierPart_FalseForCodeOnlyPrompts(string prompt)
        => Assert.False(ContinueModeGuardPolicy.NamesDossierPart(prompt));

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
