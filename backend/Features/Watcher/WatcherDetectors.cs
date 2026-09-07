namespace AgentStudio.Watcher;

/// <summary>
/// Tunable thresholds for <see cref="WatcherDetectors"/>. Threshold ownership
/// stays with the mechanical producer wherever one exists: a silence check
/// consumes the producer's declared cadence instead of inventing a timer, and
/// these values only decide when counting becomes a finding.
/// </summary>
public sealed record WatcherDetectorThresholds(
    int RepetitionThreshold,
    TimeSpan RepetitionWindow,
    double SilenceCadenceMultiplier,
    TimeSpan DriftFailureWindow,
    TimeSpan HygieneGracePeriod)
{
    public static WatcherDetectorThresholds Defaults() => new(
        RepetitionThreshold: 3,
        // Seven days because the 2026-09-06 findings ranged from an 8 h probe
        // loop to nine deliveries blocked across six days by one dirty
        // checkout. A narrower window drops the slow repetitions, which are the
        // ones that stayed invisible longest.
        RepetitionWindow: TimeSpan.FromDays(7),
        SilenceCadenceMultiplier: 3.0,
        DriftFailureWindow: TimeSpan.FromDays(2),
        HygieneGracePeriod: TimeSpan.FromDays(1));

    public static WatcherDetectorThresholds FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(WatcherDefaults.ConfigurationSection);
        var defaults = Defaults();
        return new WatcherDetectorThresholds(
            RepetitionThreshold: Math.Clamp(
                section.GetValue<int?>("RepetitionThreshold") ?? defaults.RepetitionThreshold, 2, 1000),
            RepetitionWindow: TimeSpan.FromHours(Math.Clamp(
                section.GetValue<int?>("RepetitionWindowHours") ?? (int)defaults.RepetitionWindow.TotalHours, 1, 24 * 30)),
            SilenceCadenceMultiplier: Math.Clamp(
                section.GetValue<double?>("SilenceCadenceMultiplier") ?? defaults.SilenceCadenceMultiplier, 1.5, 100),
            DriftFailureWindow: TimeSpan.FromHours(Math.Clamp(
                section.GetValue<int?>("DriftFailureWindowHours") ?? (int)defaults.DriftFailureWindow.TotalHours, 1, 24 * 30)),
            HygieneGracePeriod: TimeSpan.FromHours(Math.Clamp(
                section.GetValue<int?>("HygieneGracePeriodHours") ?? (int)defaults.HygieneGracePeriod.TotalHours, 1, 24 * 30)));
    }
}

/// <summary>
/// The five detector classes of dossier section 10.2 as pure functions over a
/// <see cref="WatcherObservation"/>. Nothing here reads the filesystem, calls a
/// model, or mutates state, so every rule is addressable by a direct matrix
/// test.
/// </summary>
/// <remarks>
/// The dossier's own reading of the 2026-09-06 evening is that six of eight
/// findings were repetition of an unchanged fingerprint or a contradiction
/// between two sources the server already had. These functions are therefore
/// deliberately bookkeeping and not intelligence: fingerprint, count, compare,
/// and stop when the count crosses a threshold.
/// </remarks>
public static class WatcherDetectors
{
    /// <summary>Human-readable rule text carried on every case and proposal for audit.</summary>
    public static class Rules
    {
        public const string Repetition =
            "Same failure fingerprint recurs at least N times inside the window with no state change between occurrences.";
        public const string Contradiction =
            "Two projections of one fact disagree; both values and their sources are recorded.";
        public const string Silence =
            "An expected signal is older than its declared cadence multiplied by the silence factor.";
        public const string Drift =
            "An installed tool version changed and a dependent probe or parser failed after the change.";
        public const string Hygiene =
            "A validation error the product already computes is older than the grace period.";
    }

    /// <summary>
    /// Repetition. The state token is the discriminator: occurrences only
    /// accumulate while nothing about the surrounding state changed, which is
    /// what separates "the same thing keeps happening" from "it is being
    /// retried and progressing".
    /// </summary>
    public static IEnumerable<WatcherFinding> Repetition(WatcherObservation observation, WatcherDetectorThresholds thresholds)
    {
        foreach (var signal in observation.Repetitions)
        {
            var inWindow = signal.Occurrences
                .Where(at => observation.CapturedAt - at <= thresholds.RepetitionWindow)
                .OrderBy(at => at)
                .ToList();
            if (inWindow.Count < thresholds.RepetitionThreshold) continue;

            yield return new WatcherFinding(
                ObservedAt: observation.CapturedAt,
                DetectorClass: WatcherDetectorClass.Repetition,
                Fingerprint: WatcherFingerprint.Of(signal.Source, signal.Fingerprint, signal.StateToken),
                DetectorRule: Rules.Repetition,
                Summary: $"{signal.Source} repeated {inWindow.Count} times without a state change: {Trim(signal.Message)}",
                AffectedCards: signal.AffectedCards,
                Evidence:
                [
                    new WatcherEvidenceItem("fingerprint", signal.Fingerprint, signal.Source),
                    new WatcherEvidenceItem("count", inWindow.Count.ToString(), signal.Source),
                    new WatcherEvidenceItem("firstOccurrence", Stamp(inWindow[0]), signal.Source),
                    new WatcherEvidenceItem("lastOccurrence", Stamp(inWindow[^1]), signal.Source),
                    new WatcherEvidenceItem("stateToken", signal.StateToken, signal.Source),
                    new WatcherEvidenceItem("message", signal.Message, signal.Source),
                ],
                Project: signal.Project);
        }
    }

    /// <summary>
    /// Contradiction. Both values travel with their sources because no single
    /// side is authoritative; the dossier routes this class to a human rather
    /// than letting the Watcher pick a winner.
    /// </summary>
    public static IEnumerable<WatcherFinding> Contradiction(WatcherObservation observation)
    {
        foreach (var pair in observation.Projections)
        {
            if (string.Equals(pair.LeftValue, pair.RightValue, StringComparison.Ordinal)) continue;

            yield return new WatcherFinding(
                ObservedAt: observation.CapturedAt,
                DetectorClass: WatcherDetectorClass.Contradiction,
                Fingerprint: WatcherFingerprint.Of(pair.Fact, pair.LeftSource, pair.RightSource),
                DetectorRule: Rules.Contradiction,
                Summary: $"{pair.Fact}: {pair.LeftSource} reports {Describe(pair.LeftValue)}, {pair.RightSource} reports {Describe(pair.RightValue)}.",
                AffectedCards: pair.AffectedCards,
                Evidence:
                [
                    new WatcherEvidenceItem("fact", pair.Fact),
                    new WatcherEvidenceItem(pair.LeftSource, pair.LeftValue, pair.LeftSource),
                    new WatcherEvidenceItem(pair.RightSource, pair.RightValue, pair.RightSource),
                ],
                Project: pair.Project);
        }
    }

    /// <summary>
    /// Silence. A never-seen signal is a finding as soon as it is declared,
    /// because "no snapshot has ever arrived" is exactly the runner-link case
    /// of 2026-09-06 and must not be read as healthy waiting.
    /// </summary>
    public static IEnumerable<WatcherFinding> Silence(WatcherObservation observation, WatcherDetectorThresholds thresholds)
    {
        foreach (var expected in observation.ExpectedSignals)
        {
            if (expected.ExpectedCadence <= TimeSpan.Zero) continue;
            var deadline = expected.ExpectedCadence * thresholds.SilenceCadenceMultiplier;
            var age = expected.LastSeenAt is null
                ? (TimeSpan?)null
                : observation.CapturedAt - expected.LastSeenAt.Value;
            if (age is not null && age <= deadline) continue;

            var ageText = age is null ? "never seen" : Duration(age.Value);
            yield return new WatcherFinding(
                ObservedAt: observation.CapturedAt,
                DetectorClass: WatcherDetectorClass.Silence,
                Fingerprint: WatcherFingerprint.Of(expected.SignalName, expected.Project),
                DetectorRule: Rules.Silence,
                Summary: $"{expected.SignalName} is {ageText} against an expected cadence of {Duration(expected.ExpectedCadence)}.",
                AffectedCards: expected.AffectedCards,
                Evidence:
                [
                    new WatcherEvidenceItem("signal", expected.SignalName),
                    new WatcherEvidenceItem("lastSeen", expected.LastSeenAt is null ? null : Stamp(expected.LastSeenAt.Value)),
                    new WatcherEvidenceItem("expectedCadence", Duration(expected.ExpectedCadence)),
                    new WatcherEvidenceItem("age", ageText),
                ],
                Project: expected.Project);
        }
    }

    /// <summary>
    /// Drift. A version change alone is not a finding; it becomes one only when
    /// a dependent probe or parser failed after the change and inside the
    /// window.
    /// </summary>
    public static IEnumerable<WatcherFinding> Drift(WatcherObservation observation, WatcherDetectorThresholds thresholds)
    {
        foreach (var change in observation.ToolChanges)
        {
            if (change.FirstDependentFailureAt is not { } failedAt) continue;
            if (failedAt < change.ChangedAt) continue;
            if (failedAt - change.ChangedAt > thresholds.DriftFailureWindow) continue;

            yield return new WatcherFinding(
                ObservedAt: observation.CapturedAt,
                DetectorClass: WatcherDetectorClass.Drift,
                Fingerprint: WatcherFingerprint.Of(change.Tool, change.PreviousVersion, change.CurrentVersion),
                DetectorRule: Rules.Drift,
                Summary: $"{change.Tool} changed from {change.PreviousVersion ?? "unknown"} to {change.CurrentVersion}; a dependent failure followed after {Duration(failedAt - change.ChangedAt)}.",
                AffectedCards: change.AffectedCards,
                Evidence:
                [
                    new WatcherEvidenceItem("tool", change.Tool),
                    new WatcherEvidenceItem("previousVersion", change.PreviousVersion),
                    new WatcherEvidenceItem("currentVersion", change.CurrentVersion),
                    new WatcherEvidenceItem("changedAt", Stamp(change.ChangedAt)),
                    new WatcherEvidenceItem("firstFailureAfterChange", Stamp(failedAt)),
                    new WatcherEvidenceItem("failure", change.DependentFailureMessage),
                ],
                Project: change.Project);
        }
    }

    /// <summary>
    /// Hygiene. Errors are rolled up per project and per normalized message so
    /// fifteen broken descriptors are one case with fifteen subjects rather
    /// than fifteen cases, per the section 5 rollup rule.
    /// </summary>
    public static IEnumerable<WatcherFinding> Hygiene(WatcherObservation observation, WatcherDetectorThresholds thresholds)
    {
        var aged = observation.ValidationErrors
            .Where(error => observation.CapturedAt - error.FirstObservedAt > thresholds.HygieneGracePeriod)
            .GroupBy(error => (error.Project, Message: WatcherFingerprint.Normalize(error.Message)))
            .OrderBy(group => group.Key.Project, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Message, StringComparer.Ordinal);

        foreach (var group in aged)
        {
            var subjects = group.Select(error => error.Subject).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var oldest = group.Min(error => error.FirstObservedAt);
            yield return new WatcherFinding(
                ObservedAt: observation.CapturedAt,
                DetectorClass: WatcherDetectorClass.Hygiene,
                Fingerprint: WatcherFingerprint.Of(group.Key.Project, group.Key.Message),
                DetectorRule: Rules.Hygiene,
                Summary: $"{subjects.Count} validation {(subjects.Count == 1 ? "error" : "errors")} older than {Duration(thresholds.HygieneGracePeriod)}: {Trim(group.First().Message)}",
                AffectedCards: [],
                Evidence:
                [
                    new WatcherEvidenceItem("validationMessage", group.First().Message),
                    new WatcherEvidenceItem("subjectCount", subjects.Count.ToString()),
                    new WatcherEvidenceItem("subjects", string.Join(", ", subjects)),
                    new WatcherEvidenceItem("oldestObservedAt", Stamp(oldest)),
                ],
                Project: group.Key.Project);
        }
    }

    /// <summary>
    /// Runs every class in a stable order. Ordering matters because the ledger
    /// folds findings in sequence and the sweep log reports counts per class.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> RunAll(WatcherObservation observation, WatcherDetectorThresholds thresholds)
    =>
    [
        .. Repetition(observation, thresholds),
        .. Contradiction(observation),
        .. Silence(observation, thresholds),
        .. Drift(observation, thresholds),
        .. Hygiene(observation, thresholds),
    ];

    private static string Describe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "nothing" : value;

    private static string Trim(string value)
        => value.Length <= 160 ? value : value[..160] + "...";

    private static string Stamp(DateTime at)
        => at.ToUniversalTime().ToString("u");

    private static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{span.TotalDays:0.#} d";
        if (span.TotalHours >= 1) return $"{span.TotalHours:0.#} h";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:0.#} min";
        return $"{span.TotalSeconds:0} s";
    }
}
