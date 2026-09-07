using System.Globalization;

namespace AgentStudio.Watcher;

/// <summary>
/// Thresholds the five detector rules read. Every value is clamped where it is
/// parsed from configuration, so a typo cannot turn a detector into a firehose
/// or switch it off silently.
/// </summary>
public sealed record WatcherDetectorOptions
{
    /// <summary>Occurrences of one fingerprint that make it a repetition.</summary>
    public int RepetitionThreshold { get; init; } = 3;

    /// <summary>
    /// Distinct cards sharing one failure fingerprint that make it a repetition
    /// even below the occurrence threshold. Two cards of one project blocked by
    /// the same integration failure is the QS-102 shape.
    /// </summary>
    public int RepetitionCardThreshold { get; init; } = 2;

    /// <summary>How far past its cadence a signal may be before it counts as silent.</summary>
    public double SilenceCadenceFactor { get; init; } = 1.0;

    /// <summary>Age a validation error must reach before hygiene reports it.</summary>
    public TimeSpan HygieneGracePeriod { get; init; } = TimeSpan.FromDays(1);

    /// <summary>How long after a tool version change a dependent failure still counts as drift.</summary>
    public TimeSpan DriftWindow { get; init; } = TimeSpan.FromDays(30);

    public static WatcherDetectorOptions Default { get; } = new();
}

/// <summary>
/// The five W1 detector rules of dossier section 10.2, as pure functions over a
/// <see cref="WatcherSweepInput"/>. No model is used here: detection is
/// bookkeeping, which is exactly the reading the dossier records after the
/// evening of 6 September 2026.
/// </summary>
public static class WatcherDetectors
{
    /// <summary>
    /// Rule texts quoted into every finding so a proposal can cite why it
    /// exists. They are the dossier's own wording.
    /// </summary>
    public static class Rules
    {
        public const string Repetition =
            "The same failure fingerprint recurs N times within a window with no state change between occurrences.";
        public const string RepetitionAcrossCards =
            "An identical failure fingerprint appears on two or more cards of one project.";
        public const string Contradiction =
            "Two projections disagree: artifacts on disk versus a projected count, a Pass review versus a non-integrated delivery, a wait reason versus the underlying capability state.";
        public const string Silence =
            "An expected signal stops: runner snapshot, probe, heartbeat, integration after review Pass, rail run.";
        public const string Drift =
            "An installed tool changed and a dependent probe or parser failed afterwards.";
        public const string Hygiene =
            "Validation errors the product already computes are older than a grace period.";
    }

    /// <summary>
    /// Run every rule over one sweep. Findings come back ordered by class then
    /// fingerprint so a replay produces a stable sequence.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> Detect(
        WatcherSweepInput input,
        WatcherDetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var opts = options ?? WatcherDetectorOptions.Default;
        var findings = new List<WatcherFinding>();
        // Drift runs first because it is the more specific reading of the same
        // failures: "this broke after the toolchain changed" explains more than
        // "this keeps breaking". Whatever drift claims is withheld from the
        // repetition rule so one gate failure does not become two cases.
        var drift = DetectDrift(input, opts);
        findings.AddRange(drift);
        findings.AddRange(DetectRepetition(input, opts, DriftConsumedFailures(input, opts)));
        findings.AddRange(DetectContradiction(input));
        findings.AddRange(DetectSilence(input, opts));
        findings.AddRange(DetectHygiene(input, opts));
        return findings
            .OrderBy(finding => finding.DetectorClass, StringComparer.Ordinal)
            .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Repetition: group failures by source and normalised fingerprint, keep
    /// only the occurrences since the last state change, and report when the
    /// count or the affected-card spread crosses its threshold.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> DetectRepetition(
        WatcherSweepInput input,
        WatcherDetectorOptions options,
        IReadOnlySet<(string Source, string Fingerprint)>? excluded = null)
    {
        var findings = new List<WatcherFinding>();
        var groups = input.Failures
            .GroupBy(signal => (signal.Source, signal.Fingerprint), TupleComparer)
            .Where(group => excluded is null || !excluded.Contains(group.Key))
            .OrderBy(group => group.Key.Source, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Fingerprint, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(signal => signal.ObservedAtUtc).ToList();
            // "No state change between occurrences": a changed state restarts
            // the run, so only the tail since the last change is a repetition.
            var lastChange = ordered.FindLastIndex(signal => signal.StateChanged);
            var run = lastChange < 0 ? ordered : ordered[lastChange..];
            if (run.Count == 0) continue;

            var cards = DistinctCards(run.SelectMany(signal => signal.AffectedCards));
            var acrossCards = cards.Count >= options.RepetitionCardThreshold;
            if (run.Count < options.RepetitionThreshold && !acrossCards) continue;

            var first = run[0];
            var last = run[^1];
            var project = SingleProject(run.Select(signal => signal.Project));
            var evidence = new List<WatcherEvidenceItem>
            {
                new("Failure fingerprint", first.Fingerprint, first.Source),
                new("Occurrences without a state change", Count(run.Count), first.Source),
                new("First occurrence", Stamp(first.ObservedAtUtc), first.Source),
                new("Last occurrence", Stamp(last.ObservedAtUtc), first.Source),
                new("Subjects", Join(run.Select(signal => signal.Subject)), first.Source),
                cards.Count == 0
                    ? WatcherEvidenceItem.Missing("Affected cards", first.Source)
                    : new WatcherEvidenceItem("Affected cards", Join(cards), first.Source),
            };
            evidence.AddRange(run.SelectMany(signal => signal.Evidence));

            findings.Add(new WatcherFinding
            {
                Fingerprint = WatcherIdentity.Fingerprint(
                    WatcherDetectorClasses.Repetition, first.Source, first.Fingerprint),
                DetectorClass = WatcherDetectorClasses.Repetition,
                DetectorRule = acrossCards && run.Count < options.RepetitionThreshold
                    ? Rules.RepetitionAcrossCards
                    : Rules.Repetition,
                Title = acrossCards
                    ? $"{first.Summary} on {cards.Count} cards"
                    : $"{first.Summary} repeated {run.Count} times",
                Project = project,
                FirstSeenAtUtc = first.ObservedAtUtc,
                LastSeenAtUtc = last.ObservedAtUtc,
                Occurrences = run.Count,
                AffectedCards = cards,
                Evidence = evidence,
                // The same fingerprint repeating is a count, not a diagnosis.
                // Why it repeats is what a strong call may be asked to explain.
                UncertainCause = true,
            });
        }

        return findings;
    }

    /// <summary>
    /// Contradiction: one signal already carries both disagreeing values, so
    /// the rule is a pass-through that refuses to pick a winner.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> DetectContradiction(WatcherSweepInput input)
    {
        var findings = new List<WatcherFinding>();
        foreach (var signal in input.Contradictions
                     .Where(signal => !string.Equals(signal.LeftValue, signal.RightValue, StringComparison.Ordinal))
                     .OrderBy(signal => signal.Source, StringComparer.Ordinal)
                     .ThenBy(signal => signal.Subject, StringComparer.Ordinal))
        {
            var evidence = new List<WatcherEvidenceItem>
            {
                new(signal.LeftSource, signal.LeftValue, signal.LeftSource),
                new(signal.RightSource, signal.RightValue, signal.RightSource),
                new("Subject", signal.Subject, signal.Source),
                new("Observed", Stamp(signal.ObservedAtUtc), signal.Source),
            };
            evidence.AddRange(signal.Evidence);

            findings.Add(new WatcherFinding
            {
                Fingerprint = WatcherIdentity.Fingerprint(
                    WatcherDetectorClasses.Contradiction, signal.Source, signal.Subject),
                DetectorClass = WatcherDetectorClasses.Contradiction,
                DetectorRule = Rules.Contradiction,
                Title = signal.Summary,
                Project = signal.Project,
                FirstSeenAtUtc = signal.ObservedAtUtc,
                LastSeenAtUtc = signal.ObservedAtUtc,
                Occurrences = 1,
                AffectedCards = DistinctCards(signal.AffectedCards),
                Evidence = evidence,
                // No single source is authoritative when two projections
                // disagree, which is the dossier's default for strong analysis.
                UncertainCause = true,
            });
        }

        return findings;
    }

    /// <summary>
    /// Silence: an expected signal is overdue past its own cadence. The
    /// producer owns the cadence; the Watcher only compares it against now.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> DetectSilence(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        var findings = new List<WatcherFinding>();
        foreach (var signal in input.Presence
                     .OrderBy(signal => signal.Source, StringComparer.Ordinal)
                     .ThenBy(signal => signal.Subject, StringComparer.Ordinal))
        {
            if (signal.ExpectedCadence <= TimeSpan.Zero) continue;
            var deadline = signal.ExpectedCadence * Math.Max(0.1, options.SilenceCadenceFactor);
            var age = signal.LastSeenAtUtc is null
                ? TimeSpan.MaxValue
                : input.NowUtc - signal.LastSeenAtUtc.Value;
            if (age <= deadline) continue;

            var evidence = new List<WatcherEvidenceItem>
            {
                signal.LastSeenAtUtc is null
                    ? WatcherEvidenceItem.Missing("Last seen", signal.Source)
                    : new WatcherEvidenceItem("Last seen", Stamp(signal.LastSeenAtUtc.Value), signal.Source),
                new("Expected cadence", Duration(signal.ExpectedCadence), signal.Source),
                new("Age", signal.LastSeenAtUtc is null ? "never seen" : Duration(age), signal.Source),
                new("Subject", signal.Subject, signal.Source),
            };
            evidence.AddRange(signal.Evidence);

            findings.Add(new WatcherFinding
            {
                Fingerprint = WatcherIdentity.Fingerprint(
                    WatcherDetectorClasses.Silence, signal.Source, signal.Subject),
                DetectorClass = WatcherDetectorClasses.Silence,
                DetectorRule = Rules.Silence,
                Title = signal.Summary,
                Project = signal.Project,
                FirstSeenAtUtc = signal.LastSeenAtUtc ?? signal.ObservedAtUtc,
                LastSeenAtUtc = signal.ObservedAtUtc,
                Occurrences = 1,
                AffectedCards = DistinctCards(signal.AffectedCards),
                Evidence = evidence,
                // An absent signal names the gap, not the reason for it.
                UncertainCause = true,
            });
        }

        return findings;
    }

    /// <summary>
    /// Drift: a tool version changed and a failure that declares a dependency
    /// on that tool followed within the drift window.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> DetectDrift(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        var findings = new List<WatcherFinding>();
        foreach (var version in input.ToolVersions
                     .Where(signal => signal.Changed)
                     .OrderBy(signal => signal.Source, StringComparer.Ordinal)
                     .ThenBy(signal => signal.Subject, StringComparer.Ordinal))
        {
            var dependents = input.Failures
                .Where(failure => string.Equals(failure.DependsOnTool, version.Subject, StringComparison.Ordinal))
                .Where(failure => failure.ObservedAtUtc >= version.ChangedAtUtc
                                  && failure.ObservedAtUtc - version.ChangedAtUtc <= options.DriftWindow)
                .OrderBy(failure => failure.ObservedAtUtc)
                .ToList();
            if (dependents.Count == 0) continue;

            var first = dependents[0];
            var last = dependents[^1];
            var cards = DistinctCards(dependents.SelectMany(failure => failure.AffectedCards));
            var evidence = new List<WatcherEvidenceItem>
            {
                new("Tool", version.Subject, version.Source),
                new("Previous version", version.PreviousVersion ?? "(unknown)", version.Source),
                new("Current version", version.CurrentVersion, version.Source),
                new("Changed at", Stamp(version.ChangedAtUtc), version.Source),
                new("First dependent failure", Stamp(first.ObservedAtUtc), first.Source),
                new("Dependent failure fingerprint", first.Fingerprint, first.Source),
                new("Dependent failures", Count(dependents.Count), first.Source),
                new("Last dependent failure", Stamp(last.ObservedAtUtc), last.Source),
            };
            evidence.AddRange(version.Evidence);
            evidence.AddRange(dependents.SelectMany(failure => failure.Evidence));

            findings.Add(new WatcherFinding
            {
                Fingerprint = WatcherIdentity.Fingerprint(
                    WatcherDetectorClasses.Drift, version.Source, version.Subject, first.Fingerprint),
                DetectorClass = WatcherDetectorClasses.Drift,
                DetectorRule = Rules.Drift,
                Title = $"{first.Summary} after {version.Subject} changed to {version.CurrentVersion}",
                Project = version.Project ?? SingleProject(dependents.Select(failure => failure.Project)),
                FirstSeenAtUtc = version.ChangedAtUtc,
                LastSeenAtUtc = last.ObservedAtUtc,
                Occurrences = dependents.Count,
                AffectedCards = cards,
                Evidence = evidence,
                // The correlation is mechanical; whether the change caused the
                // failure is exactly the question a bounded analysis answers.
                UncertainCause = true,
            });
        }

        return findings;
    }

    /// <summary>
    /// Hygiene: validation errors the product already computes, grouped per
    /// producing validator, reported once they outlive the grace period.
    /// </summary>
    public static IReadOnlyList<WatcherFinding> DetectHygiene(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        var findings = new List<WatcherFinding>();
        var groups = input.Validations
            .Where(signal => input.NowUtc - signal.FirstSeenAtUtc > options.HygieneGracePeriod)
            .GroupBy(signal => (signal.Source, signal.Project), SourceProjectComparer)
            .OrderBy(group => group.Key.Source, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(signal => signal.FirstSeenAtUtc).ToList();
            var first = ordered[0];
            var evidence = new List<WatcherEvidenceItem>
            {
                new("Validator", first.Source, first.Source),
                new("Errors past the grace period", Count(ordered.Count), first.Source),
                new("Oldest error first seen", Stamp(first.FirstSeenAtUtc), first.Source),
                new("Grace period", Duration(options.HygieneGracePeriod), "watcher-policy"),
            };
            evidence.AddRange(ordered.Take(20).Select(signal =>
                new WatcherEvidenceItem(signal.Subject, signal.Message, signal.Source)));
            if (ordered.Count > 20)
            {
                evidence.Add(new WatcherEvidenceItem(
                    "Truncated", $"{ordered.Count - 20} further errors omitted from the pack", first.Source));
            }
            evidence.AddRange(ordered.SelectMany(signal => signal.Evidence));

            findings.Add(new WatcherFinding
            {
                Fingerprint = WatcherIdentity.Fingerprint(
                    WatcherDetectorClasses.Hygiene, first.Source, group.Key.Project),
                DetectorClass = WatcherDetectorClasses.Hygiene,
                DetectorRule = Rules.Hygiene,
                Title = $"{ordered.Count} unresolved {first.Source} validation errors older than {Duration(options.HygieneGracePeriod)}",
                Project = group.Key.Project,
                FirstSeenAtUtc = first.FirstSeenAtUtc,
                LastSeenAtUtc = ordered.Max(signal => signal.ObservedAtUtc),
                Occurrences = ordered.Count,
                AffectedCards = [],
                Evidence = evidence,
                // The validator already stated the cause in its own message.
                UncertainCause = false,
            });
        }

        return findings;
    }

    /// <summary>
    /// Failure groups a drift finding already explains. Repetition skips these
    /// so a toolchain change and the failures it caused stay one case.
    /// </summary>
    private static HashSet<(string Source, string Fingerprint)> DriftConsumedFailures(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        var consumed = new HashSet<(string Source, string Fingerprint)>();
        foreach (var version in input.ToolVersions.Where(signal => signal.Changed))
        {
            foreach (var failure in input.Failures.Where(failure =>
                         string.Equals(failure.DependsOnTool, version.Subject, StringComparison.Ordinal)
                         && failure.ObservedAtUtc >= version.ChangedAtUtc
                         && failure.ObservedAtUtc - version.ChangedAtUtc <= options.DriftWindow))
            {
                consumed.Add((failure.Source, failure.Fingerprint));
            }
        }
        return consumed;
    }

    private static readonly IEqualityComparer<(string Source, string Fingerprint)> TupleComparer =
        EqualityComparer<(string Source, string Fingerprint)>.Default;

    private static readonly IEqualityComparer<(string Source, string? Project)> SourceProjectComparer =
        EqualityComparer<(string Source, string? Project)>.Default;

    private static List<string> DistinctCards(IEnumerable<string> cards) => cards
        .Where(card => !string.IsNullOrWhiteSpace(card))
        .Select(card => card.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(card => card, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// A finding stays workspace-wide unless every contributing signal names
    /// the same project. Guessing one project for a cross-project pattern
    /// would hide it from the other projects it affects.
    /// </summary>
    private static string? SingleProject(IEnumerable<string?> projects)
    {
        var distinct = projects
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static string Stamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Join(IEnumerable<string> values) => string.Join(", ", values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Duration(TimeSpan value)
    {
        if (value >= TimeSpan.FromDays(1))
            return $"{value.TotalDays:0.#} d";
        if (value >= TimeSpan.FromHours(1))
            return $"{value.TotalHours:0.#} h";
        if (value >= TimeSpan.FromMinutes(1))
            return $"{value.TotalMinutes:0.#} min";
        return $"{value.TotalSeconds:0.#} s";
    }
}
