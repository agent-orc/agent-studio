
using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract for the docs/system/contracts/code-patterns.md loader: well-formed
/// YAML blocks become CodePatternRule records; malformed blocks are
/// skipped without breaking the rest; severity defaults to Warn.
/// </summary>
public class CodePatternRuleLoaderTests
{
    [Fact]
    public void ParseRules_ReadsValidBlock()
    {
        const string md = """
            # Header
            ```yaml
            id: my-rule
            title: My rule
            description: do not foo when you can bar
            filePattern: \.cs$
            candidateMarker: foo\(
            badVariant: foo\(prompt
            severityIfBad: High
            ```
            """;
        var rules = CodePatternRuleLoader.ParseRules(md);
        Assert.Single(rules);
        var r = rules[0];
        Assert.Equal("my-rule", r.Id);
        Assert.Equal("My rule", r.Title);
        Assert.Equal(@"\.cs$", r.FilePattern);
        Assert.NotNull(r.BadVariant);
        Assert.True(r.BadVariant!.IsMatch("foo(prompt"));
        Assert.Equal(DriftSeverity.High, r.SeverityIfBad);
    }

    [Fact]
    public void ParseRules_SupportsFoldedScalar()
    {
        const string md = """
            ```yaml
            id: folded
            title: Folded description rule
            description: >
              First continuation line.
              Second continuation line.
            filePattern: \.ts$
            candidateMarker: fetch\(
            goodVariant: X-Client-Id
            ```
            """;
        var rules = CodePatternRuleLoader.ParseRules(md);
        Assert.Single(rules);
        Assert.Contains("First continuation", rules[0].CanonicalDescription);
        Assert.Contains("Second continuation", rules[0].CanonicalDescription);
    }

    [Fact]
    public void ParseRules_SkipsMalformedBlocks()
    {
        const string md = """
            ```yaml
            id: good
            title: Valid rule
            filePattern: \.cs$
            candidateMarker: bar
            badVariant: bar\(broken
            ```

            ```yaml
            id: malformed-rule
            filePattern: \.cs$
            candidateMarker: [invalid regex (
            ```

            ```yaml
            id: missing-required
            title: skip me
            ```
            """;
        var rules = CodePatternRuleLoader.ParseRules(md);
        Assert.Single(rules);
        Assert.Equal("good", rules[0].Id);
    }

    [Fact]
    public void LoadFromFile_MissingFile_ReturnsEmpty()
    {
        var rules = CodePatternRuleLoader.LoadFromFile("/nonexistent/path.md");
        Assert.Empty(rules);
    }

    [Fact]
    public void LoadEffectiveRules_MergesDocsRulesWithDefaults()
    {
        // The repo includes docs/system/contracts/code-patterns.md; the live merge should
        // include the hardcoded defaults plus at least one docs rule.
        var repo = LocateRepoRoot();
        if (repo is null) return; // CI/out-of-tree builds may not see it
        var effective = CodePatternDriftAnalysisService.LoadEffectiveRules(repo);
        Assert.True(effective.Count > CodePatternDriftAnalysisService.DefaultRules.Count,
            $"expected docs rules on top of {CodePatternDriftAnalysisService.DefaultRules.Count} defaults; got {effective.Count}");
        // Default rule IDs stay reachable
        foreach (var d in CodePatternDriftAnalysisService.DefaultRules)
        {
            Assert.Contains(effective, e => e.Id == d.Id);
        }
    }

    private static string? LocateRepoRoot()
        => RepositoryRoot.TryFind();
}
