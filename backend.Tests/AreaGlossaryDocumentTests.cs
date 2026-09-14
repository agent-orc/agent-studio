using AgentStudio.Areas;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The glossary page is a wiki article, so its format has to survive a round
/// trip and a hand edit. These tests pin both directions without touching a
/// repository.
/// </summary>
public class AreaGlossaryDocumentTests
{
    private static readonly AreaDefinition Area = new()
    {
        Id = "delivery-chain",
        Label = "Delivery chain",
        Description = "Integration, merge, and recovery.",
        Source = AreaSources.Product,
        GlossaryPath = "docs/areas/delivery-chain/glossary.md",
    };

    [Fact]
    public void RepoRelativePath_PutsTheGlossaryUnderTheArea() =>
        Assert.Equal("docs/areas/observation/glossary.md",
            AreaGlossaryDocument.RepoRelativePath("observation"));

    [Fact]
    public void Render_WritesFrontMatterWithTheAreaAndItsTag()
    {
        var markdown = AreaGlossaryDocument.Render(Area, []);

        Assert.StartsWith("---\narea: delivery-chain\ntags: [delivery-chain]\n---\n", markdown);
        Assert.Contains("# Delivery chain glossary", markdown);
        Assert.Contains("No terms are defined yet.", markdown);
    }

    [Fact]
    public void RenderThenParse_RoundTripsTermsDefinitionsAndSynonyms()
    {
        List<GlossaryTerm> terms =
        [
            new() { Term = "Integration branch", Definition = "The branch a finished task is merged into.", Synonyms = ["delivery branch", "integration ref"] },
            new() { Term = "Bounce", Definition = "A run that returns to preparation without a delivery." },
        ];

        var parsed = AreaGlossaryDocument.Parse(AreaGlossaryDocument.Render(Area, terms));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Integration branch", parsed[0].Term);
        Assert.Equal("The branch a finished task is merged into.", parsed[0].Definition);
        Assert.Equal(["delivery branch", "integration ref"], parsed[0].Synonyms);
        Assert.Equal("Bounce", parsed[1].Term);
        Assert.Empty(parsed[1].Synonyms);
    }

    [Fact]
    public void Parse_IgnoresProseAboveTheFirstTermAndKeepsHandEditsReadable()
    {
        var parsed = AreaGlossaryDocument.Parse("""
            ---
            area: observation
            ---

            # Observation glossary

            Some prose an operator wrote by hand.

            ## Probe

            A bounded check the watcher runs.
            It may wrap over two lines.

            Synonyms: watcher probe
            """);

        var term = Assert.Single(parsed);
        Assert.Equal("Probe", term.Term);
        Assert.Equal("A bounded check the watcher runs. It may wrap over two lines.", term.Definition);
        Assert.Equal(["watcher probe"], term.Synonyms);
    }

    [Fact]
    public void Parse_DropsATermWithoutADefinition() =>
        Assert.Empty(AreaGlossaryDocument.Parse("## Orphan\n\n## Other\n\nA definition.\n")
            .Where(term => term.Term == "Orphan"));

    [Fact]
    public void Parse_OfEmptyInputIsEmpty() => Assert.Empty(AreaGlossaryDocument.Parse(null));

    [Fact]
    public void ValidateTerms_AcceptsAWellFormedGlossary() =>
        Assert.Null(AreaGlossaryDocument.ValidateTerms(
            [new GlossaryTerm { Term = "Probe", Definition = "A bounded check." }]));

    [Fact]
    public void ValidateTerms_RefusesAMissingTermDefinitionOrDuplicate()
    {
        Assert.Contains("needs a term", AreaGlossaryDocument.ValidateTerms(
            [new GlossaryTerm { Term = " ", Definition = "x" }]));
        Assert.Contains("needs a definition", AreaGlossaryDocument.ValidateTerms(
            [new GlossaryTerm { Term = "Probe", Definition = "" }]));
        Assert.Contains("declared twice", AreaGlossaryDocument.ValidateTerms(
            [
                new GlossaryTerm { Term = "Probe", Definition = "One." },
                new GlossaryTerm { Term = "probe", Definition = "Two." },
            ]));
    }

    [Fact]
    public void ValidateTerms_RefusesAnOverlongDefinition() =>
        Assert.Contains("at most 1000 characters", AreaGlossaryDocument.ValidateTerms(
            [new GlossaryTerm { Term = "Probe", Definition = new string('x', 1001) }]));
}
