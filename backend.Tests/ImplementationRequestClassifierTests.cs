using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The "does this follow-up prompt ask for a source-code change?" classifier
/// (AGT-2825, closing AGT-2795). Biased broad, the opposite of
/// <see cref="SteerQuestionClassifier"/>: a missed signal reproduces the
/// incident, a false positive only costs one <c>modeOverride</c> resend.
/// </summary>
public sealed class ImplementationRequestClassifierTests
{
    [Theory]
    // The AGT-2795 reproduction itself: an implementation fix framed as a
    // small numeric change to a route count.
    [InlineData("Bump the demo route count from 81 to 83.")]
    [InlineData("The demo route count needs to go from 81 to 83, please fix.")]
    [InlineData("Please fix the bug where the export button throws a null reference.")]
    [InlineData("There's a regression in the endpoint - can you patch it?")]
    [InlineData("Implement the retry logic we discussed.")]
    [InlineData("Refactor TaskRunnerService.cs to extract the admission check.")]
    [InlineData("Add a new controller action for the export route.")]
    [InlineData("The build error in ProjectRunner.cs needs a fix.")]
    [InlineData("Resolve the merge conflict in the migration.")]
    [InlineData("Bump the retry limit from 3 to 5 in the handler.")]
    public void RecognizesImplementationPrompts(string prompt)
        => Assert.True(ImplementationRequestClassifier.LooksLikeImplementationRequest(prompt));

    [Theory]
    // Concept/planning-appropriate follow-ups: decision answers, dossier
    // text revisions, scoping questions - none of these ask for code.
    [InlineData("D1: use the direct-merge approach for the retry policy.")]
    [InlineData("Please expand the risk section with the vendor concern.")]
    [InlineData("Add a diagram for the ingestion pipeline to section 3 of the dossier.")]
    [InlineData("Which of the two approaches do you recommend and why?")]
    [InlineData("Answer D1 through D5 with your recommendation for each.")]
    [InlineData("Tighten the summary paragraph, it's too long.")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsDocumentOrDecisionPrompts(string? prompt)
        => Assert.False(ImplementationRequestClassifier.LooksLikeImplementationRequest(prompt));
}
