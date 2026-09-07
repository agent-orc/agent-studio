using Xunit;

namespace AgentStudio.Tests.Watcher;

/// <summary>
/// Direct matrix tests for the five detector classes. Each class is checked
/// both ways: the case that must fire and the neighbouring case that must stay
/// silent, because a detector that fires on healthy waiting is worse than no
/// detector at all.
/// </summary>
public sealed class WatcherDetectorMatrixTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 19, 30, 0, DateTimeKind.Utc);
    private static readonly WatcherDetectorThresholds Thresholds = WatcherDetectorThresholds.Defaults();

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(9, true)]
    public void RepetitionFiresOnlyAtOrAboveTheThreshold(int occurrences, bool expected)
    {
        var observation = Observation() with
        {
            Repetitions = [Repetition(occurrences, "state-unchanged")],
        };

        Assert.Equal(expected, WatcherDetectors.Repetition(observation, Thresholds).Any());
    }

    [Fact]
    public void RepetitionCountsOnlyOccurrencesInsideTheWindow()
    {
        var stale = new WatcherRepetitionSignal(
            "fp", "probe", "message",
            [Now.AddDays(-40), Now.AddDays(-39), Now.AddDays(-38)],
            "unchanged", [], null);

        var observation = Observation() with { Repetitions = [stale] };

        Assert.Empty(WatcherDetectors.Repetition(observation, Thresholds));
    }

    [Fact]
    public void ADifferentStateTokenIsADifferentFingerprint()
    {
        var observation = Observation() with
        {
            Repetitions = [Repetition(3, "state-a"), Repetition(3, "state-b")],
        };

        var fingerprints = WatcherDetectors.Repetition(observation, Thresholds)
            .Select(f => f.Fingerprint)
            .ToList();

        Assert.Equal(2, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ContradictionFiresOnlyWhenTheTwoSourcesDisagree()
    {
        var agree = Pair("7", "7");
        var disagree = Pair("7", "0");

        Assert.Empty(WatcherDetectors.Contradiction(Observation() with { Projections = [agree] }));
        var finding = Assert.Single(WatcherDetectors.Contradiction(Observation() with { Projections = [disagree] }));

        // Both values travel with their sources; no side is declared the winner.
        Assert.Contains(finding.Evidence, e => e.Value == "7");
        Assert.Contains(finding.Evidence, e => e.Value == "0");
    }

    [Fact]
    public void ContradictionFiresWhenOneSideIsMissingEntirely()
    {
        var finding = Assert.Single(
            WatcherDetectors.Contradiction(Observation() with { Projections = [Pair("7", null)] }));

        Assert.Contains("nothing", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SilenceIgnoresASignalInsideItsCadence()
    {
        var healthy = new WatcherExpectedSignal(
            "runner snapshot", Now.AddMinutes(-5), TimeSpan.FromMinutes(5), [], "AGT");

        Assert.Empty(WatcherDetectors.Silence(Observation() with { ExpectedSignals = [healthy] }, Thresholds));
    }

    [Fact]
    public void SilenceFiresBeyondTheCadenceMultiplier()
    {
        var late = new WatcherExpectedSignal(
            "runner snapshot", Now.AddMinutes(-60), TimeSpan.FromMinutes(5), [], "AGT");

        Assert.Single(WatcherDetectors.Silence(Observation() with { ExpectedSignals = [late] }, Thresholds));
    }

    [Fact]
    public void ANeverSeenSignalIsSilenceNotHealthyWaiting()
    {
        var never = new WatcherExpectedSignal(
            "runner snapshot", null, TimeSpan.FromMinutes(5), [], "AGT");

        var finding = Assert.Single(
            WatcherDetectors.Silence(Observation() with { ExpectedSignals = [never] }, Thresholds));

        Assert.Contains("never seen", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DriftNeedsBothAVersionChangeAndADependentFailure()
    {
        var changeOnly = Change(failureAt: null);
        var changeThenFailure = Change(failureAt: Now.AddDays(-1));

        Assert.Empty(WatcherDetectors.Drift(Observation() with { ToolChanges = [changeOnly] }, Thresholds));
        Assert.Single(WatcherDetectors.Drift(Observation() with { ToolChanges = [changeThenFailure] }, Thresholds));
    }

    [Fact]
    public void DriftIgnoresAFailureThatPrecededTheVersionChange()
    {
        var before = Change(failureAt: Now.AddDays(-10));

        Assert.Empty(WatcherDetectors.Drift(Observation() with { ToolChanges = [before] }, Thresholds));
    }

    [Fact]
    public void HygieneRespectsTheGracePeriod()
    {
        var fresh = new WatcherValidationError("a/workbench.json", "bad key", Now.AddHours(-1), "AGT");
        var aged = new WatcherValidationError("a/workbench.json", "bad key", Now.AddDays(-3), "AGT");

        Assert.Empty(WatcherDetectors.Hygiene(Observation() with { ValidationErrors = [fresh] }, Thresholds));
        Assert.Single(WatcherDetectors.Hygiene(Observation() with { ValidationErrors = [aged] }, Thresholds));
    }

    [Fact]
    public void HygieneRollsUpOneMessageAcrossManySubjects()
    {
        var errors = Enumerable.Range(1, 15)
            .Select(i => new WatcherValidationError($"d-{i}/workbench.json", "bad key", Now.AddDays(-3), "AGT"))
            .ToList();

        var finding = Assert.Single(WatcherDetectors.Hygiene(Observation() with { ValidationErrors = errors }, Thresholds));

        Assert.Contains(finding.Evidence, e => e.Label == "subjectCount" && e.Value == "15");
    }

    [Fact]
    public void HygieneKeepsDifferentMessagesApart()
    {
        List<WatcherValidationError> errors =
        [
            new("a/workbench.json", "bad key", Now.AddDays(-3), "AGT"),
            new("b/workbench.json", "missing entrypoint", Now.AddDays(-3), "AGT"),
        ];

        Assert.Equal(2, WatcherDetectors.Hygiene(Observation() with { ValidationErrors = errors }, Thresholds).Count());
    }

    [Fact]
    public void NormalizeCollapsesVolatileIdsButKeepsTheFaultText()
    {
        var first = WatcherFingerprint.Normalize("Gate 9f2c1ab3d4e5f607 failed after 42 seconds");
        var second = WatcherFingerprint.Normalize("Gate 1122334455667788 failed after 7 seconds");

        Assert.Equal(first, second);
        Assert.Contains("gate", first, StringComparison.Ordinal);
        Assert.Contains("failed after", first, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeKeepsGenuinelyDifferentFaultsApart()
    {
        Assert.NotEqual(
            WatcherFingerprint.Normalize("merge conflict in develop"),
            WatcherFingerprint.Normalize("dirty worktree in develop"));
    }

    [Fact]
    public void PartBoundariesCannotBeForgedByConcatenation()
    {
        Assert.NotEqual(WatcherFingerprint.Of("ab", "c"), WatcherFingerprint.Of("a", "bc"));
    }

    private static WatcherObservation Observation() => WatcherObservation.Empty(Now);

    private static WatcherRepetitionSignal Repetition(int occurrences, string stateToken)
        => new(
            "fp", "probe", "probe failed",
            [.. Enumerable.Range(0, occurrences).Select(i => Now.AddHours(-i))],
            stateToken, [], null);

    private static WatcherProjectionPair Pair(string? left, string? right)
        => new("round count", "artifacts", left, "projection", right, [], "AGT");

    private static WatcherToolVersionChange Change(DateTime? failureAt)
        => new("gate toolchain", "10.0.100", "10.0.301", Now.AddDays(-2), failureAt, "startup error", [], "AGT");
}
