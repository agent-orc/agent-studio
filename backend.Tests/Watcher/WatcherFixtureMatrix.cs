namespace AgentStudio.Tests.Watcher;

/// <summary>
/// The eight findings the operator made by hand on the evening of 2026-09-06,
/// rebuilt as replayable signals (Watcher dossier AGT-W15 section 10.1).
/// </summary>
/// <remarks>
/// Each entry carries the exact signal the Task Server already held on that
/// evening, not a paraphrase of the conclusion. That is the point of the
/// matrix: if a detector needs information the server did not have, the fixture
/// cannot supply it and the gap becomes visible instead of being assumed away.
/// The hand-written cards named per row are the ground truth this slice is
/// measured against.
/// </remarks>
public static class WatcherFixtureMatrix
{
    /// <summary>The evening the operator worked as the Watcher by hand.</summary>
    public static readonly DateTime Evening = new(2026, 9, 6, 19, 30, 0, DateTimeKind.Utc);

    /// <summary>Number of findings in section 10.1. The acceptance criterion counts against this.</summary>
    public const int ExpectedFindings = 8;

    /// <summary>Cards the operator created by hand, per finding, for traceability.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> HandWrittenCards =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["quota-probe-stale"] = ["AGT-2705", "AGT-2706"],
            ["runner-snapshot-silent"] = ["AGT-2711", "AGT-2712"],
            ["crash-as-external-completion"] = ["AGT-2713"],
            ["review-attempts-without-integration"] = ["AGT-2717", "AGT-2720"],
            ["integration-checkout-dirty"] = ["QS-102"],
            ["gate-toolchain-drift"] = ["AGT-2720"],
            ["escalation-banner-contradiction"] = ["AGT-2714", "AGT-2717"],
            ["dossier-descriptor-hygiene"] = [],
        };

    /// <summary>
    /// Builds the full observation for one sweep at <paramref name="capturedAt"/>.
    /// Replaying it twice is what the persistence check needs, so the signal
    /// timestamps are absolute and do not drift with the sweep clock.
    /// </summary>
    public static WatcherObservation Observation(DateTime? capturedAt = null)
    {
        var at = capturedAt ?? Evening;
        return new WatcherObservation(
            CapturedAt: at,
            Repetitions:
            [
                QuotaProbeStale(),
                ReviewAttemptsWithoutIntegration(),
                IntegrationCheckoutDirty(),
            ],
            Projections:
            [
                CrashRecordedAsExternalCompletion(),
                EscalationBannerContradiction(),
            ],
            ExpectedSignals: [RunnerSnapshotSilent(at)],
            ToolChanges: [GateToolchainDrift()],
            ValidationErrors: [.. DossierDescriptorHygiene()]);
    }

    /// <summary>
    /// Finding 1. Claude launcher stub probe failed on every cycle for 8 h with
    /// a constant error text. Signal: <c>QuotaSnapshot.ProbeFailedAt</c> set on
    /// every cycle plus a constant <c>Error</c>.
    /// </summary>
    public static WatcherRepetitionSignal QuotaProbeStale()
        => new(
            Fingerprint: "claude:launcher-stub-missing",
            Source: "cli-quota-probe",
            Message: "Claude quota probe failed: launcher stub did not return a usage block.",
            Occurrences: [.. Cycles(Evening.AddHours(-8), TimeSpan.FromMinutes(30), 16)],
            // The probe never recovered, so the CLI version and last-good window
            // stayed identical across all sixteen cycles.
            StateToken: "cliVersion=1.4.2;lastGoodWindow=unchanged",
            AffectedCards: [],
            Project: null);

    /// <summary>
    /// Finding 4. 412 remote reviews on one card, 284 of them in one day, every
    /// one a Pass, and the delivery was never integrated. Signal: review
    /// attempts per task against an unchanged subject SHA and integration
    /// status.
    /// </summary>
    public static WatcherRepetitionSignal ReviewAttemptsWithoutIntegration()
        => new(
            Fingerprint: "review-pass-not-integrated:AGT-2717",
            Source: "remote-review-attempt",
            Message: "Review attempt returned Pass on subject 9f2c1ab while integration status stayed pending.",
            Occurrences: [.. Cycles(Evening.AddHours(-20), TimeSpan.FromMinutes(4), 284)],
            StateToken: "subjectSha=9f2c1ab;integrationStatus=pending",
            AffectedCards: ["AGT-2717"],
            Project: "AGT");

    /// <summary>
    /// Finding 5. Nine reviewed deliveries of one project blocked for six days
    /// by a single dirty integration checkout. Signal: the identical
    /// integration failure reason across cards of one project.
    /// </summary>
    public static WatcherRepetitionSignal IntegrationCheckoutDirty()
        => new(
            Fingerprint: "integration-error:dirty-worktree",
            Source: "integration-outcome",
            Message: "Integration working tree has uncommitted changes; refusing to fast-forward it from origin.",
            Occurrences: [.. Cycles(Evening.AddDays(-6), TimeSpan.FromHours(16), 9)],
            StateToken: "integrationBranch=develop;dirty=true",
            AffectedCards: ["QS-88", "QS-90", "QS-91", "QS-93", "QS-95", "QS-96", "QS-98", "QS-99", "QS-101"],
            Project: "QS");

    /// <summary>
    /// Finding 3. A CLI crash was recorded as an external completion into human
    /// review with an empty result. Signal: the completion's result SHA equals
    /// the base SHA and no commit was attributed.
    /// </summary>
    public static WatcherProjectionPair CrashRecordedAsExternalCompletion()
        => new(
            Fact: "AGT-2692 delivery carries product changes",
            LeftSource: "typed-run-outcome",
            LeftValue: "CliCrash",
            RightSource: "external-completion",
            RightValue: "Completed with resultSha == baseSha (4d1e77c) and 0 attributed commits",
            AffectedCards: ["AGT-2692"],
            Project: "AGT");

    /// <summary>
    /// Finding 7. The escalation banner read "0 rounds, grade not recorded" on a
    /// card that had seven review reports on disk. Signal: artifact count versus
    /// the projected count.
    /// </summary>
    public static WatcherProjectionPair EscalationBannerContradiction()
        => new(
            Fact: "AGT-2714 review round count",
            LeftSource: "remote-review-artifacts",
            LeftValue: "7 reports on disk (remote-review-grade-*.md)",
            RightSource: "escalation-banner-projection",
            RightValue: "0 rounds, grade not recorded",
            AffectedCards: ["AGT-2714"],
            Project: "AGT");

    /// <summary>
    /// Finding 2. The Ready lane reported "waiting for sign-in" while the runner
    /// link had been down for four days. Signal: no runner capability snapshot
    /// while Ready cards target that runner.
    /// </summary>
    public static WatcherExpectedSignal RunnerSnapshotSilent(DateTime capturedAt)
        => new(
            SignalName: "runner capability snapshot (linux-runner-01)",
            LastSeenAt: capturedAt.AddDays(-4),
            ExpectedCadence: TimeSpan.FromMinutes(5),
            AffectedCards: ["AGT-2699", "AGT-2701", "AGT-2704"],
            Project: "AGT");

    /// <summary>
    /// Finding 6. The pre-main gate had been failing before the first test for
    /// weeks because the dependency cache was corrupt. Signal: the gate
    /// toolchain version changed and the gate then failed at startup, before
    /// test discovery, on every run.
    /// </summary>
    public static WatcherToolVersionChange GateToolchainDrift()
        => new(
            Tool: "pre-main gate toolchain (dotnet sdk)",
            PreviousVersion: "10.0.100",
            CurrentVersion: "10.0.301",
            ChangedAt: Evening.AddDays(-19),
            FirstDependentFailureAt: Evening.AddDays(-19).AddHours(2),
            DependentFailureMessage:
                "Cache hit followed by a toolchain startup error before test discovery; cache entry has no install marker.",
            AffectedCards: ["AGT-2720"],
            Project: "AGT");

    /// <summary>
    /// Finding 8. Fifteen invalid dossier descriptors. Signal: catalogue
    /// validation errors the product already computes on every list. They share
    /// one validation message, so the rollup rule makes them one case with
    /// fifteen subjects.
    /// </summary>
    public static IEnumerable<WatcherValidationError> DossierDescriptorHygiene()
    {
        const string message = "key must use the project reference form 'PROJECT-W<number>'.";
        var firstObserved = Evening.AddDays(-3);
        for (var index = 1; index <= 15; index++)
        {
            yield return new WatcherValidationError(
                Subject: $"docs/concepts/dossier-{index:00}/workbench.json",
                Message: message,
                FirstObservedAt: firstObserved,
                Project: "AGT");
        }
    }

    private static IEnumerable<DateTime> Cycles(DateTime start, TimeSpan step, int count)
    {
        for (var index = 0; index < count; index++) yield return start + step * index;
    }
}
