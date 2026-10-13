using Xunit;

namespace AgentStudio.Tests;

public sealed class DossierImplementationContractTests
{
    [Fact]
    public void Review_AcceptsOneDatedAppendAndReturnsItsCompactStatus()
    {
        var review = DossierImplementationContract.Review(
            Document(string.Empty),
            Document(Entry("AGT-42", "API slice", "Delivered the bounded context API.")),
            "AGT-42");

        Assert.True(review.IsComplete, string.Join(" ", review.Findings));
        Assert.False(review.Idempotent);
        Assert.Equal("API slice", review.Slice);
        Assert.Equal("2026-08-10", review.DeliveredAt);
        Assert.Contains("bounded context API", review.Delivered);
    }

    [Fact]
    public void Review_RejectsChangesOutsideTheImplementationLog()
    {
        var review = DossierImplementationContract.Review(
            Document(string.Empty, "Keep this decision."),
            Document(Entry("AGT-42", "API slice", "Delivered the context API."), "Reworded decision."),
            "AGT-42");

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding =>
            finding.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Review_RejectsEditingOrReorderingExistingEntries()
    {
        var existing = Entry("AGT-41", "Storage", "Delivered the durable context store.");
        var review = DossierImplementationContract.Review(
            Document(existing),
            Document(
                existing.Replace("durable", "rewritten", StringComparison.Ordinal)
                + Entry("AGT-42", "API slice", "Delivered the bounded context API.")),
            "AGT-42");

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding =>
            finding.Contains("edited or reordered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Review_AllowsTheCanonicalSectionToBeAddedWithoutTouchingExistingContent()
    {
        const string before = "<main><section id=\"decision\">Keep this decision.</section></main>";
        var after = Document(Entry("AGT-42", "API slice", "Delivered the bounded context API."));

        var review = DossierImplementationContract.Review(before, after, "AGT-42");

        Assert.True(review.IsComplete, string.Join(" ", review.Findings));
    }

    [Fact]
    public void Review_IsIdempotentWhenTheCardsEntryAlreadyExists()
    {
        var dossier = Document(Entry("AGT-42", "API slice", "Delivered the bounded context API."));

        var review = DossierImplementationContract.Review(dossier, dossier, "AGT-42");

        Assert.True(review.IsComplete, string.Join(" ", review.Findings));
        Assert.True(review.Idempotent);
    }

    [Fact]
    public void Review_RejectsAppendingAnotherCardsEntryInTheSameDelivery()
    {
        var review = DossierImplementationContract.Review(
            Document(string.Empty),
            Document(
                Entry("AGT-41", "Storage", "Delivered the durable context store.")
                + Entry("AGT-42", "API slice", "Delivered the bounded context API.")),
            "AGT-42");

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding =>
            finding.Contains("only its own", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Review_QuotesTheExpectedMarkersWhenTheSectionIsMissing()
    {
        const string after = "<main><section id=\"decision\">Keep this decision.</section></main>";

        var review = DossierImplementationContract.Review(after, after, "AGT-42");

        Assert.False(review.IsComplete);
        Assert.True(review.IsAbsence);
        var finding = Assert.Single(review.Findings);
        Assert.Contains(DossierImplementationContract.SectionStartMarker, finding);
        Assert.Contains(DossierImplementationContract.LogStartMarker, finding);
    }

    private static string Document(string log, string decision = "Keep this decision.") =>
        "<main><section id=\"decision\">" + decision + "</section>"
        + DossierImplementationContract.SectionStartMarker
        + "<section id=\"implementation\"><ol>"
        + DossierImplementationContract.LogStartMarker
        + log
        + DossierImplementationContract.LogEndMarker
        + "</ol></section>"
        + DossierImplementationContract.SectionEndMarker
        + "</main>";

    private static string Entry(string taskKey, string slice, string delivered) =>
        $"<li data-implementation-entry=\"\" data-task-key=\"{taskKey}\" "
        + $"data-delivered-at=\"2026-08-10\" data-slice=\"{slice}\">"
        + $"<strong>{taskKey}</strong> {delivered}</li>";
}
