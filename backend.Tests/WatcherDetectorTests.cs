using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix tests for the five W1 detector rules. Detection is pure, so
/// every case here is a table of signals in and findings out with no host,
/// no filesystem, and no model.
/// </summary>
public class WatcherDetectorTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);

    private static WatcherFailureSignal Failure(
        string fingerprint,
        int minutesAgo,
        string subject = "subject",
        string? project = null,
        string[]? cards = null,
        bool stateChanged = false,
        string? dependsOnTool = null) =>
        new("test-source", subject, fingerprint, Now.AddMinutes(-minutesAgo), "Something failed")
        {
            Project = project,
            AffectedCards = cards ?? [],
            StateChanged = stateChanged,
            DependsOnTool = dependsOnTool,
        };

    [Theory]
    // Below the occurrence threshold and below the card spread: not a repetition.
    [InlineData(2, 1, false)]
    // At the occurrence threshold: a repetition.
    [InlineData(3, 1, true)]
    [InlineData(9, 1, true)]
    // Below the occurrence threshold but across two cards: still a repetition,
    // which is the QS-102 shape from the dossier.
    [InlineData(2, 2, true)]
    public void Repetition_FiresOnOccurrenceCountOrCardSpread(int occurrences, int cardCount, bool expected)
    {
        var cards = Enumerable.Range(0, cardCount).Select(index => $"CARD-{index}").ToArray();
        var failures = Enumerable.Range(0, occurrences)
            .Select(index => Failure("same-fingerprint", 60 - index, cards: [cards[index % cardCount]]))
            .ToList();

        var findings = WatcherDetectors.Detect(
            new WatcherSweepInput { NowUtc = Now, Failures = failures });

        Assert.Equal(expected, findings.Any(f => f.DetectorClass == WatcherDetectorClasses.Repetition));
    }

    [Fact]
    public void Repetition_CountsOnlyOccurrencesSinceTheLastStateChange()
    {
        // Four occurrences, but the third one changed state. Only the two after
        // it count, which is below the threshold of three.
        var failures = new List<WatcherFailureSignal>
        {
            Failure("fp", 60),
            Failure("fp", 50),
            Failure("fp", 40, stateChanged: true),
            Failure("fp", 30),
        };

        var findings = WatcherDetectors.DetectRepetition(
            new WatcherSweepInput { NowUtc = Now, Failures = failures },
            WatcherDetectorOptions.Default);

        Assert.Empty(findings);
    }

    [Fact]
    public void Repetition_StaysWorkspaceWideWhenSignalsSpanProjects()
    {
        var failures = new List<WatcherFailureSignal>
        {
            Failure("fp", 60, project: "Alpha"),
            Failure("fp", 50, project: "Beta"),
            Failure("fp", 40, project: "Alpha"),
        };

        var finding = Assert.Single(WatcherDetectors.DetectRepetition(
            new WatcherSweepInput { NowUtc = Now, Failures = failures },
            WatcherDetectorOptions.Default));

        Assert.Null(finding.Project);
    }

    [Fact]
    public void Repetition_KeepsTheProjectWhenEverySignalNamesTheSameOne()
    {
        var failures = Enumerable.Range(0, 3)
            .Select(index => Failure("fp", 60 - index, project: "Alpha"))
            .ToList();

        var finding = Assert.Single(WatcherDetectors.DetectRepetition(
            new WatcherSweepInput { NowUtc = Now, Failures = failures },
            WatcherDetectorOptions.Default));

        Assert.Equal("Alpha", finding.Project);
    }

    [Fact]
    public void Contradiction_ReportsBothValuesAndPicksNoWinner()
    {
        var signal = new WatcherContradictionSignal(
            "review-projection", "AGT-1", "artifacts", "7 reports", "banner", "0 rounds",
            Now.AddHours(-1), "Banner and artifacts disagree");

        var finding = Assert.Single(WatcherDetectors.DetectContradiction(
            new WatcherSweepInput { NowUtc = Now, Contradictions = [signal] }));

        Assert.Equal(WatcherDetectorClasses.Contradiction, finding.DetectorClass);
        Assert.Contains(finding.Evidence, item => item.Value == "7 reports" && item.Source == "artifacts");
        Assert.Contains(finding.Evidence, item => item.Value == "0 rounds" && item.Source == "banner");
    }

    [Fact]
    public void Contradiction_IgnoresAgreeingProjections()
    {
        var signal = new WatcherContradictionSignal(
            "review-projection", "AGT-1", "artifacts", "7", "banner", "7",
            Now.AddHours(-1), "These agree");

        Assert.Empty(WatcherDetectors.DetectContradiction(
            new WatcherSweepInput { NowUtc = Now, Contradictions = [signal] }));
    }

    [Theory]
    // Fresh inside its cadence: healthy waiting is not a problem.
    [InlineData(2, 5, false)]
    // Past its cadence: silent.
    [InlineData(9, 5, true)]
    public void Silence_ComparesTheProducerCadenceAgainstNow(int ageMinutes, int cadenceMinutes, bool expected)
    {
        var signal = new WatcherPresenceSignal(
            "runner-capability", "runner:one", Now.AddMinutes(-ageMinutes),
            TimeSpan.FromMinutes(cadenceMinutes), Now, "Runner snapshot");

        var findings = WatcherDetectors.DetectSilence(
            new WatcherSweepInput { NowUtc = Now, Presence = [signal] },
            WatcherDetectorOptions.Default);

        Assert.Equal(expected, findings.Count == 1);
    }

    [Fact]
    public void Silence_RecordsANeverSeenSignalAsMissingEvidence()
    {
        var signal = new WatcherPresenceSignal(
            "runner-capability", "runner:one", null, TimeSpan.FromMinutes(5), Now, "Runner snapshot");

        var finding = Assert.Single(WatcherDetectors.DetectSilence(
            new WatcherSweepInput { NowUtc = Now, Presence = [signal] },
            WatcherDetectorOptions.Default));

        Assert.Contains(finding.Evidence, item => item.Label == "Last seen" && !item.Available);
    }

    [Fact]
    public void Drift_NeedsBothAVersionChangeAndADependentFailureAfterIt()
    {
        var changedAt = Now.AddDays(-2);
        var version = new WatcherToolVersionSignal("gate", "toolchain", "v1", "v2", changedAt);
        var before = Failure("boom", 60 * 24 * 3, dependsOnTool: "toolchain");
        var after = Failure("boom", 60, dependsOnTool: "toolchain");

        Assert.Empty(WatcherDetectors.DetectDrift(
            new WatcherSweepInput { NowUtc = Now, ToolVersions = [version], Failures = [before] },
            WatcherDetectorOptions.Default));

        var finding = Assert.Single(WatcherDetectors.DetectDrift(
            new WatcherSweepInput { NowUtc = Now, ToolVersions = [version], Failures = [after] },
            WatcherDetectorOptions.Default));
        Assert.Equal(WatcherDetectorClasses.Drift, finding.DetectorClass);
    }

    [Fact]
    public void Drift_IgnoresAToolThatDidNotChange()
    {
        var version = new WatcherToolVersionSignal("gate", "toolchain", "v1", "v1", Now.AddDays(-2));
        var failure = Failure("boom", 60, dependsOnTool: "toolchain");

        Assert.Empty(WatcherDetectors.DetectDrift(
            new WatcherSweepInput { NowUtc = Now, ToolVersions = [version], Failures = [failure] },
            WatcherDetectorOptions.Default));
    }

    [Fact]
    public void Drift_WithholdsItsFailuresFromTheRepetitionRule()
    {
        // Eight identical gate failures after a toolchain change would satisfy
        // the repetition rule too. The more specific reading wins so the
        // operator sees one case, not two.
        var changedAt = Now.AddDays(-10);
        var version = new WatcherToolVersionSignal("gate", "toolchain", "v1", "v2", changedAt);
        var failures = Enumerable.Range(0, 8)
            .Select(index => Failure("boom", 60 * 24 * (9 - index), dependsOnTool: "toolchain"))
            .ToList();

        var findings = WatcherDetectors.Detect(
            new WatcherSweepInput { NowUtc = Now, ToolVersions = [version], Failures = failures });

        var finding = Assert.Single(findings);
        Assert.Equal(WatcherDetectorClasses.Drift, finding.DetectorClass);
    }

    [Theory]
    // Inside the grace period: the product is still allowed to fix itself.
    [InlineData(0.5, false)]
    // Past it: hygiene reports it.
    [InlineData(3, true)]
    public void Hygiene_ReportsOnlyValidationErrorsPastTheGracePeriod(double ageDays, bool expected)
    {
        var signal = new WatcherValidationSignal(
            "dossier-descriptor", "a/workbench.json", "missing status", Now.AddDays(-ageDays), Now);

        var findings = WatcherDetectors.DetectHygiene(
            new WatcherSweepInput { NowUtc = Now, Validations = [signal] },
            WatcherDetectorOptions.Default);

        Assert.Equal(expected, findings.Count == 1);
    }

    [Fact]
    public void Hygiene_GroupsOneValidatorIntoOneCaseAndBoundsThePack()
    {
        var validations = Enumerable.Range(0, 40)
            .Select(index => new WatcherValidationSignal(
                "dossier-descriptor", $"doc-{index}/workbench.json", "missing status", Now.AddDays(-3), Now))
            .ToList();

        var finding = Assert.Single(WatcherDetectors.DetectHygiene(
            new WatcherSweepInput { NowUtc = Now, Validations = validations },
            WatcherDetectorOptions.Default));

        Assert.Equal(40, finding.Occurrences);
        Assert.Contains(finding.Evidence, item => item.Label == "Truncated");
    }

    [Fact]
    public void Hygiene_DoesNotDeclareUncertaintyBecauseTheValidatorAlreadyNamedTheCause()
    {
        var signal = new WatcherValidationSignal(
            "dossier-descriptor", "a/workbench.json", "missing status", Now.AddDays(-3), Now);

        var finding = Assert.Single(WatcherDetectors.DetectHygiene(
            new WatcherSweepInput { NowUtc = Now, Validations = [signal] },
            WatcherDetectorOptions.Default));

        Assert.False(finding.UncertainCause);
    }

    [Fact]
    public void Detect_IsStableAcrossRepeatedRuns()
    {
        var input = WatcherFixtureMatrix.Combined();

        var first = WatcherDetectors.Detect(input).Select(f => f.Fingerprint).ToList();
        var second = WatcherDetectors.Detect(input).Select(f => f.Fingerprint).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Detect_OnAnEmptySweepFindsNothing()
    {
        Assert.Empty(WatcherDetectors.Detect(new WatcherSweepInput { NowUtc = Now }));
    }
}
