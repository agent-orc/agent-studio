using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The fixture matrix is the W1 acceptance evidence: replaying the eight
/// findings of 6 September 2026 must produce eight cases, one per finding, in
/// the detector class the dossier assigns to it.
/// </summary>
public class WatcherFixtureMatrixTests
{
    [Fact]
    public void Matrix_CoversTheEightFindingsOfTheDossier()
    {
        Assert.Equal(8, WatcherFixtureMatrix.All.Count);
        Assert.Equal(
            WatcherFixtureMatrix.All.Count,
            WatcherFixtureMatrix.All.Select(fixture => fixture.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Matrix_ExercisesAllFiveDetectorClasses()
    {
        var classes = WatcherFixtureMatrix.All
            .Select(fixture => fixture.ExpectedDetectorClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);

        Assert.Equal(
            WatcherDetectorClasses.All.OrderBy(value => value, StringComparer.Ordinal),
            classes);
    }

    [Theory]
    [InlineData("quota-probes-stale")]
    [InlineData("runner-link-silent")]
    [InlineData("crash-empty-completion")]
    [InlineData("review-attempts-without-integration")]
    [InlineData("integration-checkout-blocks-project")]
    [InlineData("gate-toolchain-drift")]
    [InlineData("escalation-banner-contradiction")]
    [InlineData("dossier-descriptor-hygiene")]
    public void EachFixture_ProducesExactlyOneFindingInItsDeclaredClass(string id)
    {
        var fixture = WatcherFixtureMatrix.ById(id);

        var finding = Assert.Single(WatcherDetectors.Detect(fixture.Input));

        Assert.Equal(fixture.ExpectedDetectorClass, finding.DetectorClass);
        Assert.NotEmpty(finding.Evidence);
        Assert.NotEmpty(finding.DetectorRule);
        Assert.NotEmpty(finding.Title);
    }

    [Fact]
    public void CombinedReplay_ProducesEightDistinctCases()
    {
        var findings = WatcherDetectors.Detect(WatcherFixtureMatrix.Combined());

        Assert.Equal(8, findings.Count);
        Assert.Equal(8, findings.Select(f => f.Fingerprint).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CombinedReplay_MatchesTheClassOfEveryFixture()
    {
        var findings = WatcherDetectors.Detect(WatcherFixtureMatrix.Combined());

        var byClass = findings
            .GroupBy(finding => finding.DetectorClass, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var expected = WatcherFixtureMatrix.All
            .GroupBy(fixture => fixture.ExpectedDetectorClass, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Assert.Equal(expected, byClass);
    }

    [Fact]
    public void QuotaProbeFixture_FoldsBothCliSurfacesIntoOneCaseAndKeepsBothErrors()
    {
        var fixture = WatcherFixtureMatrix.ById("quota-probes-stale");

        var finding = Assert.Single(WatcherDetectors.Detect(fixture.Input));

        Assert.Null(finding.Project);
        Assert.Contains(finding.Evidence, item => item.Label == "claude probe error");
        Assert.Contains(finding.Evidence, item => item.Label == "codex probe error");
        var subjects = Assert.Single(finding.Evidence, item => item.Label == "Subjects");
        Assert.Contains("claude:launcher-stub", subjects.Value, StringComparison.Ordinal);
        Assert.Contains("codex:hook-modal", subjects.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationCheckoutFixture_NamesEveryBlockedCard()
    {
        var fixture = WatcherFixtureMatrix.ById("integration-checkout-blocks-project");

        var finding = Assert.Single(WatcherDetectors.Detect(fixture.Input));

        Assert.Equal(9, finding.AffectedCards.Count);
        Assert.Equal("Quality Site", finding.Project);
    }

    [Fact]
    public void GateFixture_IsReportedAsDriftAndNotAlsoAsRepetition()
    {
        var fixture = WatcherFixtureMatrix.ById("gate-toolchain-drift");

        var finding = Assert.Single(WatcherDetectors.Detect(fixture.Input));

        Assert.Equal(WatcherDetectorClasses.Drift, finding.DetectorClass);
        Assert.Contains(finding.Evidence, item => item.Label == "Previous version");
        Assert.Contains(finding.Evidence, item => item.Label == "Current version");
    }

    [Fact]
    public void EveryFixture_RecordsTheManualPrecedentSeparatelyFromItsSignals()
    {
        // The cards the operator wrote by hand are provenance, not something the
        // Watcher detected. They must never leak into the affected-card set.
        foreach (var fixture in WatcherFixtureMatrix.All)
        {
            var finding = Assert.Single(WatcherDetectors.Detect(fixture.Input));
            foreach (var manual in fixture.ManualTickets)
            {
                Assert.DoesNotContain(manual, finding.AffectedCards, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
