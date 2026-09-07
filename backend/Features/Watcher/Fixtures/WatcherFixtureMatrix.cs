namespace AgentStudio.Watcher;

/// <summary>
/// One replayable finding from the evening of 6 September 2026, as recorded in
/// section 10.1 of the Watcher dossier.
/// </summary>
/// <param name="Id">Stable fixture handle used by tests and the replay endpoint.</param>
/// <param name="Finding">The dossier's own description of what a Watcher would have found.</param>
/// <param name="Signal">The signal the Task Server already held that evening.</param>
/// <param name="ExpectedDetectorClass">Which of the five classes must fire.</param>
/// <param name="ManualTickets">The cards the operator wrote by hand for this finding.</param>
/// <param name="Input">The signals a sweep would have collected.</param>
public sealed record WatcherFixture(
    string Id,
    string Finding,
    string Signal,
    string ExpectedDetectorClass,
    IReadOnlyList<string> ManualTickets,
    WatcherSweepInput Input);

/// <summary>
/// The W1 fixture set: the eight findings of section 10.1 turned into replayable
/// signal sets, one per row of that table.
/// </summary>
/// <remarks>
/// <para>
/// Each fixture carries the exact signal the dossier names in its "Signal that
/// was already there" column, so a replay proves the detector rule rather than
/// a hand-written expectation. Replaying <see cref="Combined"/> must produce
/// exactly eight findings, one per fixture, and must cover all five detector
/// classes.
/// </para>
/// <para>
/// Where the dossier does not name the cards a finding touched, the fixture
/// uses representative identifiers of the right shape. The cards the operator
/// wrote by hand are kept separately in <see cref="WatcherFixture.ManualTickets"/>
/// and travel into the evidence pack as manual precedent, never as if the
/// Watcher had found them.
/// </para>
/// </remarks>
public static class WatcherFixtureMatrix
{
    /// <summary>The sweep instant the fixtures are written against.</summary>
    public static readonly DateTime NowUtc = new(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<WatcherFixture> All { get; } =
    [
        QuotaProbesStale(),
        RunnerLinkSilent(),
        CrashRecordedAsEmptyCompletion(),
        ReviewAttemptsWithoutIntegration(),
        IntegrationCheckoutBlocksProject(),
        GateToolchainDrift(),
        EscalationBannerContradiction(),
        DossierDescriptorHygiene(),
    ];

    /// <summary>All fixture signals folded into one sweep, as a real cycle would see them.</summary>
    public static WatcherSweepInput Combined(DateTime? nowUtc = null)
    {
        var combined = new WatcherSweepInput { NowUtc = nowUtc ?? NowUtc };
        foreach (var fixture in All) combined = combined.Concat(fixture.Input);
        return combined with { NowUtc = nowUtc ?? NowUtc };
    }

    public static WatcherFixture ById(string id) =>
        All.FirstOrDefault(fixture => string.Equals(fixture.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown Watcher fixture '{id}'.");

    // Row 1. Quota probes stale for 8 h (Claude launcher stub) and 30 h (Codex
    // hook modal). Both surfaces share one fingerprint because the finding is
    // "the probe surface is stale", not "this CLI printed that string"; the
    // per-CLI error text stays in the evidence.
    private static WatcherFixture QuotaProbesStale()
    {
        const string fingerprint = "quota-probe-stale";
        var claude = Enumerable.Range(0, 4).Select(cycle => new WatcherFailureSignal(
            WatcherSignalSources.CliQuotaProbe,
            "claude:launcher-stub",
            fingerprint,
            NowUtc.AddHours(-8).AddHours(cycle * 2),
            "Quota probe has not returned a usable snapshot")
        {
            Evidence =
            [
                new WatcherEvidenceItem(
                    "claude probe error",
                    "launcher returned stub output; no quota block parsed",
                    WatcherSignalSources.CliQuotaProbe),
            ],
        });
        var codex = Enumerable.Range(0, 4).Select(cycle => new WatcherFailureSignal(
            WatcherSignalSources.CliQuotaProbe,
            "codex:hook-modal",
            fingerprint,
            NowUtc.AddHours(-30).AddHours(cycle * 7),
            "Quota probe has not returned a usable snapshot")
        {
            Evidence =
            [
                new WatcherEvidenceItem(
                    "codex probe error",
                    "hook modal blocked the probe; probeFailedAt set on every cycle",
                    WatcherSignalSources.CliQuotaProbe),
            ],
        });

        return new WatcherFixture(
            "quota-probes-stale",
            "Quota probes stale for 8 h (Claude launcher stub) and 30 h (Codex hook modal)",
            "probeFailedAt set on every cycle, error text constant",
            WatcherDetectorClasses.Repetition,
            ["AGT-2705", "AGT-2706"],
            new WatcherSweepInput { NowUtc = NowUtc, Failures = [.. claude, .. codex] });
    }

    // Row 2. The Ready lane reported "waiting for sign-in" while the runner link
    // had been down for four days. The presence signal exists only because Ready
    // cards target that runner; a runner nobody waits on is not a problem.
    private static WatcherFixture RunnerLinkSilent() => new(
        "runner-link-silent",
        "Ready lane reported \"waiting for sign-in\" while the runner link was down for four days",
        "No runner capability snapshot for more than 5 min while Ready cards target the runner",
        WatcherDetectorClasses.Silence,
        ["AGT-2711", "AGT-2712"],
        new WatcherSweepInput
        {
            NowUtc = NowUtc,
            Presence =
            [
                new WatcherPresenceSignal(
                    WatcherSignalSources.RunnerCapability,
                    "runner:linux-runner-01",
                    NowUtc.AddDays(-4),
                    TimeSpan.FromMinutes(5),
                    NowUtc,
                    "No runner capability snapshot for four days while Ready cards target this runner")
                {
                    Project = "Agent Studio",
                    AffectedCards = ["AGT-2698", "AGT-2701", "AGT-2703"],
                    Evidence =
                    [
                        new WatcherEvidenceItem(
                            "Ready lane wait reason",
                            "waiting for sign-in",
                            "task-projection"),
                        new WatcherEvidenceItem(
                            "Keeper task",
                            "disabled",
                            WatcherSignalSources.RunnerCapability),
                    ],
                },
            ],
        });

    // Row 3. A CLI crash was recorded as an external completion into human
    // review with an empty result: the completion SHA equals the base SHA.
    private static WatcherFixture CrashRecordedAsEmptyCompletion() => new(
        "crash-empty-completion",
        "Crash recorded as external completion into human review with an empty result",
        "typedOutcome=CliCrash followed by an external completion whose result SHA equals the base",
        WatcherDetectorClasses.Contradiction,
        ["AGT-2713"],
        new WatcherSweepInput
        {
            NowUtc = NowUtc,
            Contradictions =
            [
                new WatcherContradictionSignal(
                    WatcherSignalSources.TaskCompletion,
                    "AGT-2699",
                    "run outcome",
                    "typedOutcome=CliCrash",
                    "completion envelope",
                    "external completion, resultSha == baseSha (7c41b0e), 0 attributed commits",
                    NowUtc.AddHours(-5),
                    "A crashed run was projected as a completed delivery with no attributed commits")
                {
                    Project = "Agent Studio",
                    AffectedCards = ["AGT-2699"],
                    Evidence =
                    [
                        new WatcherEvidenceItem("Lane after completion", "5-human-review", "task-projection"),
                        new WatcherEvidenceItem("baseSha", "7c41b0e", "git"),
                        new WatcherEvidenceItem("resultSha", "7c41b0e", "completion envelope"),
                    ],
                },
            ],
        });

    // Row 4. 412 remote reviews on one card, 284 in one day, all Pass, never
    // integrated. The fixture replays the bounded window one sweep would see and
    // records the full historical counts as evidence.
    private static WatcherFixture ReviewAttemptsWithoutIntegration()
    {
        const string fingerprint = "review-pass-without-integration:subject-sha=4d19f7c";
        var attempts = Enumerable.Range(0, 12).Select(index => new WatcherFailureSignal(
            WatcherSignalSources.ReviewAttempt,
            "AGT-2688@4d19f7c",
            fingerprint,
            NowUtc.AddHours(-6).AddMinutes(index * 25),
            "Remote review passed on the same subject SHA without any state change")
        {
            Project = "Agent Studio",
            AffectedCards = ["AGT-2688"],
        }).ToList();
        attempts[0] = attempts[0] with
        {
            Evidence =
            [
                new WatcherEvidenceItem("Review attempts on this subject SHA", "412", WatcherSignalSources.ReviewAttempt),
                new WatcherEvidenceItem("Attempts in one day", "284", WatcherSignalSources.ReviewAttempt),
                new WatcherEvidenceItem("Review verdict", "Pass on every attempt", WatcherSignalSources.ReviewAttempt),
                new WatcherEvidenceItem("Integration status", "unchanged across all attempts", WatcherSignalSources.Integration),
                new WatcherEvidenceItem("Replayed window", "12 attempts in the last 6 h", "watcher-fixture"),
            ],
        };

        return new WatcherFixture(
            "review-attempts-without-integration",
            "412 remote reviews on one card, 284 in one day, all Pass, never integrated",
            "Review attempts per task per day; integration status unchanged across them",
            WatcherDetectorClasses.Repetition,
            ["AGT-2717", "AGT-2720"],
            new WatcherSweepInput { NowUtc = NowUtc, Failures = attempts });
    }

    // Row 5. Nine reviewed deliveries blocked by one dirty integration checkout
    // for six days. One fingerprint, nine cards: the cross-card spread is what
    // makes it a repetition below the occurrence threshold.
    private static WatcherFixture IntegrationCheckoutBlocksProject()
    {
        const string fingerprint = "refusing to fast-forward: dirty files in the integration checkout";
        var cards = new[]
        {
            "QS-0071", "QS-0072", "QS-0074", "QS-0075", "QS-0077",
            "QS-0079", "QS-0080", "QS-0083", "QS-0084",
        };
        var failures = cards.Select((card, index) => new WatcherFailureSignal(
            WatcherSignalSources.Integration,
            card,
            fingerprint,
            NowUtc.AddDays(-6).AddHours(index * 3),
            "Integration refuses to fast-forward because the checkout is dirty")
        {
            Project = "Quality Site",
            AffectedCards = [card],
            Evidence = index == 0
                ?
                [
                    new WatcherEvidenceItem(
                        "Integration checkout",
                        "/srv/agent/integration/quality-site",
                        WatcherSignalSources.Integration),
                    new WatcherEvidenceItem("Blocked since", "2026-08-31", WatcherSignalSources.Integration),
                ]
                : [],
        }).ToList();

        return new WatcherFixture(
            "integration-checkout-blocks-project",
            "Nine reviewed deliveries blocked by one dirty integration checkout for six days",
            "Same integration failure fingerprint across cards of one project",
            WatcherDetectorClasses.Repetition,
            ["QS-102"],
            new WatcherSweepInput { NowUtc = NowUtc, Failures = failures });
    }

    // Row 6. Pre-main gate failing before the first test for weeks because the
    // dependency cache entry was corrupt. The cache key changed and every gate
    // run that depends on it has failed since.
    private static WatcherFixture GateToolchainDrift()
    {
        const string tool = "gate-toolchain:pre-main-dependency-cache";
        var changedAt = NowUtc.AddDays(-19);
        var failures = Enumerable.Range(0, 8).Select(index => new WatcherFailureSignal(
            WatcherSignalSources.Gate,
            "pre-main",
            "gate-failed-before-test-discovery: toolchain startup error after cache hit",
            changedAt.AddDays(index * 2),
            "The pre-main gate fails before test discovery")
        {
            Project = "Agent Studio",
            DependsOnTool = tool,
            Evidence = index == 0
                ?
                [
                    new WatcherEvidenceItem(
                        "Gate transcript head",
                        "cache hit, then toolchain startup error before any test was discovered",
                        WatcherSignalSources.Gate),
                    new WatcherEvidenceItem("Install marker", "absent for this cache entry", WatcherSignalSources.Gate),
                ]
                : [],
        }).ToList();

        return new WatcherFixture(
            "gate-toolchain-drift",
            "Pre-main gate failing before the first test for weeks (corrupted dependency cache)",
            "Gate failure before test discovery after a cache hit; cache entry without install marker",
            WatcherDetectorClasses.Drift,
            ["AGT-2720"],
            new WatcherSweepInput
            {
                NowUtc = NowUtc,
                ToolVersions =
                [
                    new WatcherToolVersionSignal(
                        WatcherSignalSources.Gate,
                        tool,
                        "cache-key 2026-08-04-a1",
                        "cache-key 2026-08-18-c7",
                        changedAt)
                    {
                        Project = "Agent Studio",
                    },
                ],
                Failures = failures,
            });
    }

    // Row 7. The escalation banner said "0 rounds, grade not recorded" on a card
    // that carries seven remote review reports on disk.
    private static WatcherFixture EscalationBannerContradiction() => new(
        "escalation-banner-contradiction",
        "Escalation banner \"0 rounds, grade not recorded\" on a card with seven review reports",
        "Remote review artifacts present, banner projection empty",
        WatcherDetectorClasses.Contradiction,
        ["AGT-2714", "AGT-2717"],
        new WatcherSweepInput
        {
            NowUtc = NowUtc,
            Contradictions =
            [
                new WatcherContradictionSignal(
                    WatcherSignalSources.ReviewProjection,
                    "AGT-2688",
                    "review artifacts on disk",
                    "7 remote review reports",
                    "escalation banner projection",
                    "0 rounds, grade not recorded",
                    NowUtc.AddHours(-2),
                    "The escalation banner reports no review rounds for a card with seven review reports")
                {
                    Project = "Agent Studio",
                    AffectedCards = ["AGT-2688"],
                    Evidence =
                    [
                        new WatcherEvidenceItem(
                            "Artifact folder",
                            "results/review/",
                            "review artifacts on disk"),
                        new WatcherEvidenceItem("Projected rounds", "0", "escalation banner projection"),
                        new WatcherEvidenceItem("Projected grade", "not recorded", "escalation banner projection"),
                    ],
                },
            ],
        });

    // Row 8. Fifteen invalid dossier descriptors, already computed by the
    // catalogue validation on every list call and never acted on.
    private static WatcherFixture DossierDescriptorHygiene()
    {
        var firstSeen = NowUtc.AddDays(-3);
        var validations = Enumerable.Range(1, 15).Select(index => new WatcherValidationSignal(
            WatcherSignalSources.DossierDescriptor,
            $"docs/operations/dossier-{index:00}/workbench.json",
            index % 3 == 0
                ? "descriptor is missing the required 'status' field"
                : "descriptor references a slug that does not resolve to a document",
            firstSeen,
            NowUtc)).ToList();

        return new WatcherFixture(
            "dossier-descriptor-hygiene",
            "Fifteen invalid dossier descriptors",
            "Catalogue validation errors, already computed on every list",
            WatcherDetectorClasses.Hygiene,
            [],
            new WatcherSweepInput { NowUtc = NowUtc, Validations = validations });
    }
}
