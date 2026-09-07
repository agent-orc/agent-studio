using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2749 item 6: live-CLI tests are excluded from gate runs by default. On
/// 2026-09-06 a live Codex review test failed because the weekly quota was gone
/// and the card was parked as a product regression (QS-89).
/// </summary>
public sealed class GateTestCategoryFilterTests
{
    [Fact]
    public void CanonicalFilterExcludesBothMachineBoundAndLiveCli()
    {
        Assert.Contains("Category!=MachineBound", GateTestCategoryFilter.Expression, StringComparison.Ordinal);
        Assert.Contains("Category!=LiveCli", GateTestCategoryFilter.Expression, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gate commands run through <c>sh -lc</c>. An unquoted <c>&amp;</c> is a
    /// shell control operator, so an unquoted expression would background the
    /// test run and report success without running anything.
    /// </summary>
    [Fact]
    public void FilterExpressionIsShellQuotedBecauseItContainsAnAmpersand()
    {
        Assert.Contains('&', GateTestCategoryFilter.Expression);
        Assert.Equal($" --filter '{GateTestCategoryFilter.Expression}'", GateTestCategoryFilter.DotNetSuffix);
        // Every ampersand sits inside the quoted span, so the shell never sees one.
        var quoted = GateTestCategoryFilter.DotNetSuffix;
        var open = quoted.IndexOf('\'');
        var close = quoted.LastIndexOf('\'');
        Assert.True(open >= 0 && close > open);
        Assert.DoesNotContain('&', quoted[..open]);
        Assert.DoesNotContain('&', quoted[(close + 1)..]);
    }

    [Fact]
    public void DerivedDotNetTestCommandCarriesTheExclusion()
    {
        var command = "dotnet test" + GateTestCategoryFilter.DotNetSuffix;

        Assert.Equal("dotnet test --filter 'Category!=MachineBound&Category!=LiveCli'", command);
    }

    [Fact]
    public void SkipReasonNamesBothFamiliesAndWhereToRunThem()
    {
        Assert.Contains(GateTestCategoryFilter.MachineBoundCategory, GateTestCategoryFilter.SkipReason, StringComparison.Ordinal);
        Assert.Contains(GateTestCategoryFilter.LiveCliCategory, GateTestCategoryFilter.SkipReason, StringComparison.Ordinal);
        Assert.Contains("quota", GateTestCategoryFilter.SkipReason, StringComparison.OrdinalIgnoreCase);
    }
}
