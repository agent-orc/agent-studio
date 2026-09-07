using System.Globalization;

namespace AgentStudio.Watcher;

/// <summary>Stable rule ids. One rule owns one fingerprint shape and one report wording.</summary>
public static class WatcherDetectorRules
{
    /// <summary>Repetition: the same probe error on N consecutive cycles.</summary>
    public const string ProbeErrorRepeat = "probe-error-repeat";

    /// <summary>Repetition: more than K review attempts on one subject SHA without a state change.</summary>
    public const string ReviewAttemptRepeat = "review-attempt-repeat";

    /// <summary>Repetition: one integration failure fingerprint across two or more cards of a project.</summary>
    public const string IntegrationFailureRepeat = "integration-failure-repeat";

    /// <summary>Contradiction: a completion whose Git evidence carries no delivery.</summary>
    public const string EmptyCompletion = "empty-completion";

    /// <summary>Contradiction: an artifact count and its projected count disagree.</summary>
    public const string ProjectionCountMismatch = "projection-count-mismatch";

    /// <summary>Silence: no runner capability snapshot while Ready cards target that runner.</summary>
    public const string RunnerSnapshotSilence = "runner-snapshot-silence";

    /// <summary>Drift: a tool version changed and a dependent failure followed.</summary>
    public const string ToolDriftDependentFailure = "tool-drift-dependent-failure";

    /// <summary>Hygiene: a validation error older than the grace period.</summary>
    public const string ValidationErrorAged = "validation-error-aged";

    public static readonly string[] All =
    [
        ProbeErrorRepeat,
        ReviewAttemptRepeat,
        IntegrationFailureRepeat,
        EmptyCompletion,
        ProjectionCountMismatch,
        RunnerSnapshotSilence,
        ToolDriftDependentFailure,
        ValidationErrorAged,
    ];
}

/// <summary>
/// The five detector classes of §10.2 as one pure function over a normalized
/// sweep input. No I/O, no clock, no model: the sweep's <c>NowUtc</c> is the
/// only time source and every threshold is producer-owned.
/// </summary>
/// <remarks>
/// Findings come back deterministically ordered so a replay of the same input
/// produces the same sequence of case ids.
/// </remarks>
public static class WatcherDetectors
{
    public static IReadOnlyList<WatcherFinding> Detect(
        WatcherSweepInput input,
        WatcherDetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = options ?? WatcherDetectorOptions.Default;

        var findings = new List<WatcherFinding>();
        findings.AddRange(DetectProbeRepetition(input, settings));
        findings.AddRange(DetectReviewAttemptRepetition(input, settings));
        findings.AddRange(DetectIntegrationFailureRepetition(input, settings));
        findings.AddRange(DetectEmptyCompletion(input));
        findings.AddRange(DetectProjectionMismatch(input));
        findings.AddRange(DetectRunnerSnapshotSilence(input));
        findings.AddRange(DetectToolDrift(input, settings));
        findings.AddRange(DetectAgedValidationErrors(input, settings));

        return findings
            .OrderBy(finding => finding.DetectorClass, StringComparer.Ordinal)
            .ThenBy(finding => finding.DetectorRule, StringComparer.Ordinal)
            .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToList();
    }

    // ---------------------------------------------------------------- repetition

    /// <summary>
    /// A probe that keeps failing with the same text is the cheapest finding in
    /// the catalogue. Only the trailing run of failures counts: a healthy cycle
    /// in between means the fault recovered and started again, which is a new
    /// occurrence rather than an unbroken repetition.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectProbeRepetition(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        foreach (var group in input.Probes
                     .GroupBy(probe => probe.CliType, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var cycles = group.OrderBy(probe => probe.ObservedAtUtc).ToList();
            var trailing = new List<WatcherProbeSignal>();
            string? signature = null;

            for (var index = cycles.Count - 1; index >= 0; index--)
            {
                var cycle = cycles[index];
                if (!cycle.Failed) break;
                var normalized = WatcherFingerprint.NormalizeText(cycle.Error);
                signature ??= normalized;
                if (!string.Equals(signature, normalized, StringComparison.Ordinal)) break;
                trailing.Add(cycle);
            }

            if (signature is null || trailing.Count < options.ProbeRepeatThreshold) continue;

            trailing.Reverse();
            var first = trailing[0];
            var last = trailing[^1];
            var stale = last.ProbeFailedAtUtc ?? last.ObservedAtUtc;
            var evidence = new List<WatcherEvidenceItem>
            {
                new()
                {
                    Label = "probe-error",
                    Source = $"quota-cache.json#{group.Key}",
                    Value = last.Error,
                    ObservedAtUtc = stale,
                },
                new()
                {
                    Label = "consecutive-failed-cycles",
                    Source = "quota probe cycles",
                    Value = trailing.Count.ToString(CultureInfo.InvariantCulture),
                },
                new()
                {
                    Label = "stale-for",
                    Source = "first failed cycle in the run",
                    Value = Duration(input.NowUtc - (first.ProbeFailedAtUtc ?? first.ObservedAtUtc)),
                    ObservedAtUtc = first.ProbeFailedAtUtc ?? first.ObservedAtUtc,
                },
            };
            if (last.CliVersion is not null)
            {
                evidence.Add(new WatcherEvidenceItem
                {
                    Label = "cli-version",
                    Source = $"probe report for {group.Key}",
                    Value = last.CliVersion,
                });
            }

            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Repetition,
                DetectorRule = WatcherDetectorRules.ProbeErrorRepeat,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.ProbeErrorRepeat, group.Key.ToLowerInvariant(), signature),
                Title = $"{group.Key} quota probe has failed identically for {Duration(input.NowUtc - (first.ProbeFailedAtUtc ?? first.ObservedAtUtc))}",
                Summary = $"{trailing.Count} consecutive probe cycles returned the same error, so the reported quota for {group.Key} is stale.",
                Occurrences = trailing.Count,
                FirstSeenAtUtc = first.ProbeFailedAtUtc ?? first.ObservedAtUtc,
                LastSeenAtUtc = stale,
                Evidence = evidence,
                TaskType = TaskTypes.Bug,
            };
        }
    }

    /// <summary>
    /// Review attempts that keep re-running the same subject SHA without moving
    /// the card are work the product paid for twice. The signal is the count
    /// plus the unchanged state, never a single attempt.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectReviewAttemptRepetition(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        foreach (var attempt in input.ReviewAttempts
                     .Where(attempt => attempt.Attempts > options.ReviewAttemptThreshold && !attempt.StateChanged)
                     .OrderBy(attempt => attempt.TaskKey, StringComparer.OrdinalIgnoreCase))
        {
            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Repetition,
                DetectorRule = WatcherDetectorRules.ReviewAttemptRepeat,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.ReviewAttemptRepeat, attempt.Project, attempt.TaskKey, attempt.SubjectSha),
                Project = attempt.Project,
                Title = $"{attempt.TaskKey} ran {attempt.Attempts} reviews on one subject SHA without a state change",
                Summary = $"Every attempt on {Short(attempt.SubjectSha)} returned {attempt.Verdict ?? "the same verdict"} and the card never left its lane. The review loop is unbounded for this subject.",
                Occurrences = attempt.Attempts,
                FirstSeenAtUtc = attempt.FirstAttemptAtUtc,
                LastSeenAtUtc = attempt.LastAttemptAtUtc,
                AffectedCards = [attempt.TaskKey],
                Evidence =
                [
                    new WatcherEvidenceItem
                    {
                        Label = "review-attempts",
                        Source = $"review_attempts for {attempt.TaskKey}",
                        Value = attempt.Attempts.ToString(CultureInfo.InvariantCulture),
                        ObservedAtUtc = attempt.LastAttemptAtUtc,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "peak-attempts-per-day",
                        Source = $"review_attempts for {attempt.TaskKey}",
                        Value = attempt.PeakAttemptsPerDay.ToString(CultureInfo.InvariantCulture),
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "subject-sha",
                        Source = "review subject",
                        Value = attempt.SubjectSha,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "verdict",
                        Source = "review reports",
                        Value = attempt.AllSameVerdict
                            ? $"{attempt.Verdict ?? "unknown"} on every attempt"
                            : "mixed verdicts",
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "integration-state-change",
                        Source = "task lane and integration status",
                        Value = "none across the whole attempt window",
                    },
                ],
                TaskType = TaskTypes.Bug,
            };
        }
    }

    /// <summary>
    /// One broken substrate blocking many cards must be reported once, against
    /// the substrate, not once per card. That is the whole point of grouping by
    /// failure fingerprint before counting cards.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectIntegrationFailureRepetition(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        foreach (var group in input.IntegrationFailures
                     .GroupBy(failure => (failure.Project, failure.FailureFingerprint))
                     .OrderBy(group => group.Key.Project, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key.FailureFingerprint, StringComparer.Ordinal))
        {
            var cards = group
                .Select(failure => failure.TaskKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cards.Count < options.IntegrationFailureCardThreshold) continue;

            var first = group.Min(failure => failure.ObservedAtUtc);
            var last = group.Max(failure => failure.ObservedAtUtc);
            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Repetition,
                DetectorRule = WatcherDetectorRules.IntegrationFailureRepeat,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.IntegrationFailureRepeat, group.Key.Project, group.Key.FailureFingerprint),
                Project = group.Key.Project,
                Title = $"{cards.Count} reviewed deliveries in {group.Key.Project} share one integration failure",
                Summary = $"The same integration failure ({group.Key.FailureFingerprint}) has blocked {cards.Count} cards since {first:yyyy-MM-dd HH:mm} UTC. The fault is in the integration checkout, not in the cards.",
                Occurrences = group.Count(),
                FirstSeenAtUtc = first,
                LastSeenAtUtc = last,
                AffectedCards = cards,
                Evidence =
                [
                    new WatcherEvidenceItem
                    {
                        Label = "failure-fingerprint",
                        Source = $"integration outcomes in {group.Key.Project}",
                        Value = group.Key.FailureFingerprint,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "failure-message",
                        Source = "integration result Error",
                        Value = group.First().Message,
                        ObservedAtUtc = last,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "blocked-cards",
                        Source = "integration outcomes",
                        Value = string.Join(", ", cards),
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "blocked-for",
                        Source = "first blocked delivery",
                        Value = Duration(input.NowUtc - first),
                        ObservedAtUtc = first,
                    },
                ],
                TaskType = TaskTypes.Bug,
            };
        }
    }

    // -------------------------------------------------------------- contradiction

    /// <summary>
    /// A completion is a claim about delivered work. When the result SHA equals
    /// the base or nothing was attributed, the claim and the Git evidence
    /// disagree and the card must not read as delivered.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectEmptyCompletion(WatcherSweepInput input)
    {
        foreach (var completion in input.Completions
                     .Where(IsEmptyDelivery)
                     .OrderBy(completion => completion.TaskKey, StringComparer.OrdinalIgnoreCase))
        {
            var reason = string.Equals(completion.ResultSha, completion.BaseSha, StringComparison.OrdinalIgnoreCase)
                ? "the result SHA equals the base SHA"
                : "no commits were attributed";

            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Contradiction,
                DetectorRule = WatcherDetectorRules.EmptyCompletion,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.EmptyCompletion, completion.Project, completion.TaskKey),
                Project = completion.Project,
                Title = $"{completion.TaskKey} completed with no delivered work",
                Summary = $"The card was recorded as completed but {reason}"
                          + (completion.PrecedingTypedOutcome is null
                              ? "."
                              : $", and the run before it ended as {completion.PrecedingTypedOutcome}."),
                Occurrences = 1,
                FirstSeenAtUtc = completion.CompletedAtUtc,
                LastSeenAtUtc = completion.CompletedAtUtc,
                AffectedCards = [completion.TaskKey],
                Evidence =
                [
                    new WatcherEvidenceItem
                    {
                        Label = "preceding-typed-outcome",
                        Source = $"execution outcome for {completion.TaskKey}",
                        Value = completion.PrecedingTypedOutcome,
                        Missing = completion.PrecedingTypedOutcome is null,
                        ObservedAtUtc = completion.CompletedAtUtc,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "completion-source",
                        Source = "completion record",
                        Value = completion.ExternalCompletion ? "external completion" : "run loop",
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "baseSha",
                        Source = "result envelope",
                        Value = completion.BaseSha,
                        Missing = completion.BaseSha is null,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "resultSha",
                        Source = "result envelope",
                        Value = completion.ResultSha,
                        Missing = completion.ResultSha is null,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "attributed-commits",
                        Source = "task commit attribution",
                        Value = completion.AttributedCommits.ToString(CultureInfo.InvariantCulture),
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "landed-lane",
                        Source = "task state",
                        Value = completion.LandedState,
                        Missing = completion.LandedState is null,
                    },
                ],
                TaskType = TaskTypes.Bug,
            };
        }
    }

    /// <summary>
    /// A completion counts as empty only when there is something to compare
    /// against. A missing result SHA is an envelope problem owned elsewhere,
    /// not a contradiction the Watcher can prove from these two facts.
    /// </summary>
    private static bool IsEmptyDelivery(WatcherCompletionSignal completion)
    {
        if (completion.AttributedCommits == 0) return true;
        return !string.IsNullOrWhiteSpace(completion.ResultSha)
               && !string.IsNullOrWhiteSpace(completion.BaseSha)
               && string.Equals(completion.ResultSha, completion.BaseSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two counts of the same thing that disagree. The detector reports both
    /// values with their sources and does not decide which one is right.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectProjectionMismatch(WatcherSweepInput input)
    {
        foreach (var contradiction in input.Projections
                     .Where(row => row.ArtifactCount != row.ProjectedCount)
                     .OrderBy(row => row.TaskKey, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.Subject, StringComparer.Ordinal))
        {
            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Contradiction,
                DetectorRule = WatcherDetectorRules.ProjectionCountMismatch,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.ProjectionCountMismatch,
                    contradiction.Project,
                    contradiction.TaskKey,
                    contradiction.Subject),
                Project = contradiction.Project,
                Title = $"{contradiction.TaskKey} shows {contradiction.ProjectedCount} {contradiction.Subject} while {contradiction.ArtifactCount} artifacts exist",
                Summary = $"{contradiction.ArtifactSource} holds {contradiction.ArtifactCount} artifacts, {contradiction.ProjectionSource} projects {contradiction.ProjectedCount}. The operator sees the projected value.",
                Occurrences = 1,
                FirstSeenAtUtc = contradiction.ObservedAtUtc,
                LastSeenAtUtc = contradiction.ObservedAtUtc,
                AffectedCards = [contradiction.TaskKey],
                Evidence =
                [
                    new WatcherEvidenceItem
                    {
                        Label = "artifact-count",
                        Source = contradiction.ArtifactSource,
                        Value = contradiction.ArtifactCount.ToString(CultureInfo.InvariantCulture),
                        ObservedAtUtc = contradiction.ObservedAtUtc,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "projected-count",
                        Source = contradiction.ProjectionSource,
                        Value = contradiction.ProjectedCount.ToString(CultureInfo.InvariantCulture),
                        ObservedAtUtc = contradiction.ObservedAtUtc,
                    },
                ],
                TaskType = TaskTypes.Bug,
            };
        }
    }

    // -------------------------------------------------------------------- silence

    /// <summary>
    /// A quiet runner is only a problem when something waits for it. Ready
    /// cards targeting the runner are what turn an absent snapshot into a
    /// finding, and they are also the impact statement in the report.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectRunnerSnapshotSilence(WatcherSweepInput input)
    {
        foreach (var snapshot in input.CapabilitySnapshots
                     .Where(row => row.ReadyCardsTargeting.Count > 0)
                     .OrderBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.RunnerId, StringComparer.OrdinalIgnoreCase))
        {
            var cadence = snapshot.ExpectedCadence > TimeSpan.Zero
                ? snapshot.ExpectedCadence
                : TimeSpan.FromMinutes(5);
            var lastSeen = snapshot.LastSnapshotAtUtc;
            var silentFor = lastSeen is null ? (TimeSpan?)null : input.NowUtc - lastSeen.Value;
            if (silentFor is not null && silentFor <= cadence) continue;

            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Silence,
                DetectorRule = WatcherDetectorRules.RunnerSnapshotSilence,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.RunnerSnapshotSilence, snapshot.Project, snapshot.RunnerId),
                Project = snapshot.Project,
                Title = silentFor is null
                    ? $"Runner {snapshot.RunnerId} has never reported a capability snapshot while {snapshot.ReadyCardsTargeting.Count} Ready cards target it"
                    : $"Runner {snapshot.RunnerId} has been silent for {Duration(silentFor.Value)} while {snapshot.ReadyCardsTargeting.Count} Ready cards target it",
                Summary = $"The expected snapshot cadence is {Duration(cadence)}. Ready cards in {snapshot.Project} cannot start, and the lane reason shown to the operator is not derived from this capability state.",
                Occurrences = 1,
                FirstSeenAtUtc = lastSeen ?? input.NowUtc,
                LastSeenAtUtc = input.NowUtc,
                AffectedCards = snapshot.ReadyCardsTargeting,
                Evidence =
                [
                    new WatcherEvidenceItem
                    {
                        Label = "last-capability-snapshot",
                        Source = $"runner_capabilities for {snapshot.RunnerId}",
                        Value = lastSeen?.ToString("O", CultureInfo.InvariantCulture),
                        Missing = lastSeen is null,
                        ObservedAtUtc = lastSeen,
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "expected-cadence",
                        Source = "runner advertisement",
                        Value = Duration(cadence),
                    },
                    new WatcherEvidenceItem
                    {
                        Label = "ready-cards-targeting",
                        Source = $"Ready lane of {snapshot.Project}",
                        Value = string.Join(", ", snapshot.ReadyCardsTargeting),
                    },
                ],
                TaskType = TaskTypes.Bug,
            };
        }
    }

    // ---------------------------------------------------------------------- drift

    /// <summary>
    /// A version change alone is not a finding and a failure alone belongs to
    /// its own class. Drift is the pair: the change came first and the
    /// dependent failure started inside the window after it.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectToolDrift(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        foreach (var failure in input.DependentFailures
                     .OrderBy(row => row.Tool, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.FailureFingerprint, StringComparer.Ordinal))
        {
            var change = input.ToolVersions
                .Where(version => string.Equals(version.Tool, failure.Tool, StringComparison.OrdinalIgnoreCase))
                .Where(version => version.ChangedAtUtc <= failure.FirstFailureAtUtc)
                .Where(version => failure.FirstFailureAtUtc - version.ChangedAtUtc <= options.DriftWindow)
                .OrderByDescending(version => version.ChangedAtUtc)
                .FirstOrDefault();
            if (change is null) continue;

            var evidence = new List<WatcherEvidenceItem>
            {
                new()
                {
                    Label = "tool-version-change",
                    Source = $"version tracker for {failure.Tool}",
                    Value = $"{change.PreviousVersion ?? "unknown"} to {change.NewVersion}",
                    ObservedAtUtc = change.ChangedAtUtc,
                },
                new()
                {
                    Label = "first-failure-after-change",
                    Source = $"failures attributed to {failure.Tool}",
                    Value = failure.Message,
                    ObservedAtUtc = failure.FirstFailureAtUtc,
                },
                new()
                {
                    Label = "failure-occurrences",
                    Source = $"failures attributed to {failure.Tool}",
                    Value = failure.Occurrences.ToString(CultureInfo.InvariantCulture),
                    ObservedAtUtc = failure.LastFailureAtUtc,
                },
            };
            evidence.AddRange(failure.Evidence);

            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Drift,
                DetectorRule = WatcherDetectorRules.ToolDriftDependentFailure,
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.ToolDriftDependentFailure,
                    failure.Project,
                    failure.Tool.ToLowerInvariant(),
                    failure.FailureFingerprint),
                Project = failure.Project,
                Title = $"{failure.Tool} failed {failure.Occurrences} times since it changed to {change.NewVersion}",
                Summary = $"{failure.Tool} moved from {change.PreviousVersion ?? "an unknown version"} to {change.NewVersion} on {change.ChangedAtUtc:yyyy-MM-dd HH:mm} UTC, and the first dependent failure followed {Duration(failure.FirstFailureAtUtc - change.ChangedAtUtc)} later.",
                Occurrences = failure.Occurrences,
                FirstSeenAtUtc = failure.FirstFailureAtUtc,
                LastSeenAtUtc = failure.LastFailureAtUtc,
                AffectedCards = failure.AffectedCards,
                Evidence = evidence,
                TaskType = TaskTypes.Bug,
            };
        }
    }

    // -------------------------------------------------------------------- hygiene

    /// <summary>
    /// Validation errors the product already computes on every list. The only
    /// judgement the detector adds is age: below the grace period this is
    /// someone mid-edit, above it nobody is coming back.
    /// </summary>
    private static IEnumerable<WatcherFinding> DetectAgedValidationErrors(
        WatcherSweepInput input,
        WatcherDetectorOptions options)
    {
        foreach (var group in input.ValidationErrors
                     .Where(error => input.NowUtc - error.FirstSeenAtUtc > options.HygieneGrace)
                     .GroupBy(error => error.Project, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var errors = group
                .OrderBy(error => error.Subject, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var first = errors.Min(error => error.FirstSeenAtUtc);
            var scope = group.Key ?? "the workspace";

            yield return new WatcherFinding
            {
                DetectorClass = WatcherDetectorClasses.Hygiene,
                DetectorRule = WatcherDetectorRules.ValidationErrorAged,
                // Deliberately keyed on the scope, not on the individual
                // subjects: one repair card for fifteen broken descriptors is
                // the useful unit of work, and the set churns as they are fixed.
                Fingerprint = WatcherFingerprint.Compute(
                    WatcherDetectorRules.ValidationErrorAged, group.Key),
                Project = group.Key,
                Title = $"{errors.Count} descriptor validation errors in {scope} are older than {Duration(options.HygieneGrace)}",
                Summary = $"The catalogue recomputes these errors on every list and they have stood since {first:yyyy-MM-dd HH:mm} UTC. Each one hides its entry from the catalogue.",
                Occurrences = errors.Count,
                FirstSeenAtUtc = first,
                LastSeenAtUtc = errors.Max(error => error.FirstSeenAtUtc),
                Evidence = errors
                    .Take(WatcherDefaults.MaxEvidenceItems)
                    .Select(error => new WatcherEvidenceItem
                    {
                        Label = "validation-error",
                        Source = error.Subject,
                        Value = error.Message,
                        ObservedAtUtc = error.FirstSeenAtUtc,
                    })
                    .ToList(),
                TaskType = TaskTypes.Chore,
            };
        }
    }

    // --------------------------------------------------------------------- shared

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "an unknown SHA"
            : sha.Length <= 12 ? sha
                : sha[..12];

    /// <summary>Coarse, stable duration wording. Report text must not churn between sweeps over a few seconds.</summary>
    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h";
        return $"{(int)span.TotalDays} d";
    }
}
