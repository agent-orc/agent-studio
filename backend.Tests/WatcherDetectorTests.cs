using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The five detector classes of the Watcher dossier section 10.2, replayed
/// against the 2026-09-06 fixture matrix and against the negative cases that
/// must stay quiet.
/// </summary>
public sealed class WatcherDetectorTests
{
    [Fact]
    public void MergedSweep_ProducesOneFindingPerDossierRow()
    {
        var findings = WatcherDetectors.Detect(WatcherFixtureMatrix.MergedSweep());

        Assert.Equal(WatcherFixtureMatrix.All.Count, findings.Count);
        Assert.Equal(8, findings.Count);
        Assert.Equal(
            findings.Count,
            findings.Select(finding => finding.Fingerprint).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MergedSweep_CoversAllFiveDetectorClasses()
    {
        var findings = WatcherDetectors.Detect(WatcherFixtureMatrix.MergedSweep());

        var classes = findings
            .Select(finding => finding.DetectorClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(WatcherDetectorClasses.All.OrderBy(name => name, StringComparer.Ordinal), classes);
    }

    public static TheoryData<string> FixtureIds()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in WatcherFixtureMatrix.All) data.Add(fixture.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void EachFixture_ProducesExactlyItsOwnFinding(string fixtureId)
    {
        var fixture = WatcherFixtureMatrix.ById(fixtureId);

        var findings = WatcherDetectors.Detect(fixture.Sweep());

        var finding = Assert.Single(findings);
        Assert.Equal(fixture.ExpectedClass, finding.DetectorClass);
        Assert.Equal(fixture.ExpectedRule, finding.DetectorRule);
        Assert.NotEmpty(finding.Title);
        Assert.NotEmpty(finding.Summary);
        Assert.NotEmpty(finding.Evidence);
        Assert.StartsWith(fixture.ExpectedRule + ":", finding.Fingerprint, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void EachFixture_ProducesAStableFingerprintAcrossSweeps(string fixtureId)
    {
        var fixture = WatcherFixtureMatrix.ById(fixtureId);

        var first = WatcherDetectors.Detect(fixture.Sweep()).Single();
        // A later sweep sees the same signals through a moved clock. Only the
        // age wording may change; identity must not.
        var second = WatcherDetectors.Detect(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddHours(6))).Single();

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void EmptySweep_ProducesNoFindings()
    {
        var findings = WatcherDetectors.Detect(WatcherSweepInput.Empty(WatcherFixtureMatrix.NowUtc));

        Assert.Empty(findings);
    }

    // ---------------------------------------------------------------- repetition

    [Fact]
    public void ProbeRepetition_IgnoresAFailureRunShorterThanTheThreshold()
    {
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Probes =
            [
                Probe("claude", "19:00", "stub instead of a usage report"),
                Probe("claude", "20:00", "stub instead of a usage report"),
            ],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void ProbeRepetition_RestartsCountingAfterAHealthyCycle()
    {
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Probes =
            [
                Probe("claude", "13:00", "stub instead of a usage report"),
                Probe("claude", "14:00", "stub instead of a usage report"),
                Probe("claude", "15:00", null),
                Probe("claude", "16:00", "stub instead of a usage report"),
                Probe("claude", "17:00", "stub instead of a usage report"),
            ],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void ProbeRepetition_TreatsAChangedErrorAsANewRun()
    {
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Probes =
            [
                Probe("claude", "13:00", "stub instead of a usage report"),
                Probe("claude", "14:00", "stub instead of a usage report"),
                Probe("claude", "15:00", "authentication expired"),
            ],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void ProbeRepetition_IgnoresVolatileNumbersInsideOneError()
    {
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Probes =
            [
                Probe("claude", "13:00", "stub instead of a usage report (exit 0, 41 bytes)"),
                Probe("claude", "14:00", "stub instead of a usage report (exit 0, 39 bytes)"),
                Probe("claude", "15:00", "stub instead of a usage report (exit 0, 44 bytes)"),
            ],
        };

        var finding = Assert.Single(WatcherDetectors.Detect(input));
        Assert.Equal(3, finding.Occurrences);
    }

    [Fact]
    public void IntegrationFailureRepetition_NeedsMoreThanOneAffectedCard()
    {
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            IntegrationFailures =
            [
                new()
                {
                    Project = "quality-suite",
                    TaskKey = "QS-81",
                    FailureFingerprint = "refusing-to-fast-forward-dirty",
                    Message = "dirty integration checkout",
                    ObservedAtUtc = WatcherFixtureMatrix.NowUtc.AddDays(-2),
                },
            ],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void ReviewAttemptRepetition_StaysQuietWhenTheStateActuallyMoved()
    {
        var attempt = WatcherFixtureMatrix.ById("review-attempt-repeat").Signals.ReviewAttempts[0];

        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            ReviewAttempts = [attempt with { StateChanged = true }],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    // -------------------------------------------------------------- contradiction

    [Fact]
    public void EmptyCompletion_IgnoresADeliveryThatMovedTheHead()
    {
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Completions =
            [
                new()
                {
                    Project = "agent-taskboard",
                    TaskKey = "AGT-2709",
                    CompletedAtUtc = WatcherFixtureMatrix.NowUtc.AddHours(-4),
                    BaseSha = "4c1d9a7f2b6e5081c3a9d47f10be2c8845ff3a91",
                    ResultSha = "77b0e5c9013a4f2286dd415ec0f9a3b26d18ee40",
                    AttributedCommits = 3,
                },
            ],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void ProjectionMismatch_StaysQuietWhenBothSourcesAgree()
    {
        var contradiction = WatcherFixtureMatrix.ById("projection-count-mismatch").Signals.Projections[0];

        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Projections = [contradiction with { ProjectedCount = contradiction.ArtifactCount }],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    // -------------------------------------------------------------------- silence

    [Fact]
    public void RunnerSilence_IsNotAProblemWithoutDependentReadyCards()
    {
        var snapshot = WatcherFixtureMatrix.ById("runner-snapshot-silence").Signals.CapabilitySnapshots[0];

        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            CapabilitySnapshots = [snapshot with { ReadyCardsTargeting = [] }],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void RunnerSilence_IsNotAProblemInsideTheAdvertisedCadence()
    {
        var snapshot = WatcherFixtureMatrix.ById("runner-snapshot-silence").Signals.CapabilitySnapshots[0];

        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            CapabilitySnapshots =
            [
                snapshot with { LastSnapshotAtUtc = WatcherFixtureMatrix.NowUtc.AddMinutes(-2) },
            ],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void RunnerSilence_ReportsARunnerThatNeverAdvertised()
    {
        var snapshot = WatcherFixtureMatrix.ById("runner-snapshot-silence").Signals.CapabilitySnapshots[0];

        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            CapabilitySnapshots = [snapshot with { LastSnapshotAtUtc = null }],
        };

        var finding = Assert.Single(WatcherDetectors.Detect(input));
        Assert.Contains("never reported", finding.Title, StringComparison.Ordinal);
        Assert.Contains(finding.Evidence, item => item is { Label: "last-capability-snapshot", Missing: true });
    }

    // ---------------------------------------------------------------------- drift

    [Fact]
    public void Drift_NeedsAVersionChangeBeforeTheFailure()
    {
        var fixture = WatcherFixtureMatrix.ById("gate-cache-drift").Signals;
        var change = fixture.ToolVersions[0];

        var input = fixture with
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            // The toolchain changed after the failures had already started, so
            // the failure cannot be attributed to the change.
            ToolVersions = [change with { ChangedAtUtc = WatcherFixtureMatrix.NowUtc.AddHours(-1) }],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void Drift_IgnoresAVersionChangeOlderThanTheWindow()
    {
        var fixture = WatcherFixtureMatrix.ById("gate-cache-drift").Signals;
        var change = fixture.ToolVersions[0];

        var input = fixture with
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            ToolVersions = [change with { ChangedAtUtc = WatcherFixtureMatrix.NowUtc.AddDays(-120) }],
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void Drift_CarriesTheSourceEvidenceOfTheDependentFailure()
    {
        var finding = Assert.Single(
            WatcherDetectors.Detect(WatcherFixtureMatrix.ById("gate-cache-drift").Sweep()));

        Assert.Contains(finding.Evidence, item => item.Label == "tool-version-change");
        Assert.Contains(
            finding.Evidence,
            item => item.Label == "dependency-cache" && item.Value!.Contains("no install marker", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------- hygiene

    [Fact]
    public void Hygiene_StaysQuietInsideTheGracePeriod()
    {
        var errors = WatcherFixtureMatrix.ById("validation-error-aged").Signals.ValidationErrors;

        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            ValidationErrors = errors
                .Select(error => error with { FirstSeenAtUtc = WatcherFixtureMatrix.NowUtc.AddHours(-2) })
                .ToList(),
        };

        Assert.Empty(WatcherDetectors.Detect(input));
    }

    [Fact]
    public void Hygiene_GroupsOneScopeIntoOneRepairCase()
    {
        var finding = Assert.Single(
            WatcherDetectors.Detect(WatcherFixtureMatrix.ById("validation-error-aged").Sweep()));

        Assert.Equal(15, finding.Occurrences);
        Assert.Equal(TaskTypes.Chore, finding.TaskType);
    }

    private static WatcherProbeSignal Probe(string cli, string clock, string? error)
    {
        var at = DateTime.Parse(
            $"2026-09-06T{clock}:00Z",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        return new WatcherProbeSignal
        {
            CliType = cli,
            ObservedAtUtc = at,
            ProbeFailedAtUtc = error is null ? null : at,
            Error = error,
        };
    }
}
