namespace AgentStudio.Review;

/// <summary>
/// Deterministic half of the closing Result assessment (AGT-3046). It
/// classifies the outcome from the evidence bundle and names the contradictions
/// that are provable from the records alone. The model adds explanation and
/// may catch more; this policy guarantees that a provable contradiction is never
/// dropped and is the whole answer when the model's block is malformed.
/// </summary>
public static class SummaryAssessmentPolicy
{
    private static readonly string[] IntegratedClasses =
    [
        IntegrationRecordClasses.IntegratedVerified,
        IntegrationRecordClasses.IntegratedHistorical,
        IntegrationRecordClasses.CuratedMapping,
    ];

    private static readonly string[] UnfinishedResults = ["Failed", "Blocked", "NeedsInput", "Partial"];

    public static SummaryAssessment Assess(SummaryEvidenceBundle bundle, DateTime generatedAt)
    {
        var facts = Facts.From(bundle);
        var (outcome, reason) = Classify(bundle, facts);
        return new SummaryAssessment
        {
            Outcome = outcome,
            OutcomeReason = reason,
            PolicyOutcome = outcome,
            Source = SummaryAssessmentSources.Policy,
            GeneratedAt = generatedAt,
            Inconsistencies = DetectInconsistencies(bundle, facts),
        };
    }

    private static (string Outcome, string Reason) Classify(SummaryEvidenceBundle bundle, Facts facts)
    {
        var result = bundle.StatusHeader?.Result;
        var delivered = facts.LaneCompleted
            || facts.Integrated.Count > 0
            || string.Equals(bundle.CompletionClaim?.Basis, CompletionClaimBases.IntegratedDelivery, StringComparison.Ordinal);
        if (delivered)
        {
            if (facts.OperatorMergeSha is { } merge && facts.EffectiveCommits.Count == 0)
                return (SummaryOutcomes.DeliveredViaOperatorMerge,
                    $"No card commit carries the work; operator merge `{Short(merge)}`{OnBranch(facts.MergeBranch)} delivered it.");
            if (facts.LaneCompleted && bundle.CompletionClaim is { } claim)
                return (SummaryOutcomes.Delivered, $"Completed on `{claim.Basis}`.");
            return (SummaryOutcomes.Delivered, facts.Integrated.Count > 0
                ? $"Integration record `{facts.Integrated[0].Classification}` covers the delivery."
                : "The card reached the completed lane.");
        }

        if (Is(result, "NoOp"))
            return (SummaryOutcomes.NoOp, "The run reported no change to make.");
        if (Is(result, "NeedsInput") || Is(result, "Blocked") || Is(result, "Partial")
            || bundle.Lane == TaskStates.Escalated
            || string.Equals(bundle.OrchestratorVerdict, "escalate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bundle.SightReviewVerdict, "awaiting-sight-review", StringComparison.Ordinal))
            return (SummaryOutcomes.ParkedOnDecision, result is null
                ? $"The card waits in `{bundle.Lane}` for an operator decision."
                : $"The run ended with `{result}` and waits for an operator decision.");
        if (Is(result, "Failed") || facts.FailedSteps.Any(s => s.StepId == PipelineCatalogue.CoreAgentRunStepId))
            return (SummaryOutcomes.Failed, result is null ? "The core agent run failed." : $"The run ended with `{result}`.");
        if (facts.FailedSteps.Count > 0 && !Is(result, "Success"))
            return (SummaryOutcomes.Failed, $"Pipeline step `{facts.FailedSteps[0].StepId}` failed.");
        return (SummaryOutcomes.Delivered, "The run completed; delivery is not yet integrated.");
    }

    private static List<SummaryInconsistency> DetectInconsistencies(SummaryEvidenceBundle bundle, Facts facts)
    {
        var found = new List<SummaryInconsistency>();
        var header = bundle.StatusHeader;
        var salvage = facts.Salvage;
        var mergeClause = facts.OperatorMergeSha is { } m
            ? $"operator merge `{Short(m)}`{OnBranch(facts.MergeBranch)}"
            : null;
        var salvageClause = salvage?.CommitSha is { } s ? $" (salvage commit `{Short(s)}`)" : "";

        if (header?.Files is > 0 && facts.EffectiveCommits.Count == 0 && !bundle.CodeActivityDetected)
        {
            found.Add(new SummaryInconsistency
            {
                Kind = SummaryInconsistencyKinds.FilesVersusNoCode,
                SignalA = $"status.md header: Files: {header.Files}",
                SignalB = "commit chain is empty and no code activity was detected (\"no code change\")",
                Explanation = mergeClause is not null
                    ? $"The run's changes did not land as card commits. {Capitalize(mergeClause)} carried them{salvageClause}. "
                      + "The file count describes that work; \"no code change\" is a commit-attribution gap, not lost work."
                    : salvage?.CommitSha is { } only
                        ? $"The changed files exist only on salvage commit `{Short(only)}`{OnBranch(salvage.Branch)}. "
                          + "No card commit or integration record carries them."
                        : "The summary counted changed files that no card commit carries. "
                          + "The count may describe uncommitted or discarded work.",
                WorkMayBeLost = mergeClause is null,
            });
        }

        var unfinishedResult = header?.Result is { } r && UnfinishedResults.Contains(r, StringComparer.OrdinalIgnoreCase) ? r : null;
        if (facts.LaneCompleted && (facts.FailedSteps.Count > 0 || unfinishedResult is not null))
        {
            var failure = facts.FailedSteps.Count > 0
                ? "Pipeline failure: " + string.Join("; ", facts.FailedSteps.Select(st =>
                    st.Reason is null ? $"`{st.StepId}` failed" : $"`{st.StepId}` failed ({st.Reason})"))
                : $"Result: {unfinishedResult}";
            var claim = bundle.CompletionClaim;
            found.Add(new SummaryInconsistency
            {
                Kind = SummaryInconsistencyKinds.FailureVersusCompleted,
                SignalA = failure,
                SignalB = claim is null ? $"lane `{bundle.Lane}`" : $"lane `{bundle.Lane}` with completion claim `{claim.Basis}`",
                Explanation = mergeClause is not null
                    ? $"The automated run did not finish delivery; the card was completed afterwards through {mergeClause}{salvageClause}. "
                      + "The failure describes the automated path, not the delivered work."
                    : claim is not null
                        ? $"The card was completed on `{claim.Basis}`{(string.IsNullOrWhiteSpace(claim.Evidence) ? "" : $": {claim.Evidence}")}. "
                          + "The failed step was not re-run, so its header stays."
                        : "The card sits in the completed lane without a completion claim or integration record to explain the failure.",
                WorkMayBeLost = mergeClause is null && claim is null && facts.Integrated.Count == 0,
            });
        }

        if (bundle.Commits.Count == 0 && facts.Integrated.Count > 0)
        {
            var record = facts.Integrated[0];
            var target = record.IntegrationSha ?? record.CommitShas.FirstOrDefault() ?? record.SourceSha;
            found.Add(new SummaryInconsistency
            {
                Kind = SummaryInconsistencyKinds.EmptyCommitsVersusIntegrated,
                SignalA = "commit chain is empty",
                SignalB = $"integration record `{record.Classification}`{(target is null ? "" : $" for `{Short(target)}`")}{OnBranch(record.IntegrationBranch)}",
                Explanation = (target is null
                        ? "The integration record names no commit the card attributes. "
                        : $"`{Short(target)}` reached the branch outside the card's commit chain{(mergeClause is null ? "" : " as an operator merge")}{salvageClause}. ")
                    + "The delivery is integrated; the card's own commit list was never filled.",
                WorkMayBeLost = false,
            });
        }

        if (salvage?.CommitSha is { } salvageSha
            && facts.Integrated.Count == 0
            && facts.EffectiveCommits.Count == 0
            && facts.OperatorMergeSha is null
            && !string.Equals(bundle.CompletionClaim?.Basis, CompletionClaimBases.DeliverableWithoutCode, StringComparison.Ordinal))
        {
            found.Add(new SummaryInconsistency
            {
                Kind = SummaryInconsistencyKinds.SalvageNotIntegrated,
                SignalA = $"salvage commit `{Short(salvageSha)}`{OnBranch(salvage.Branch)}",
                SignalB = "no card commit and no integration record",
                Explanation = "The run's work was saved to a salvage branch when it ended"
                    + (salvage.RunStatus is null ? "" : $" (run status `{salvage.RunStatus}`)")
                    + ". Nothing on the card integrates it, so it is not on the delivery branch.",
                WorkMayBeLost = true,
            });
        }

        return found;
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    internal static string Short(string sha) => sha.Length > 9 && sha.All(Uri.IsHexDigit) ? sha[..9] : sha;

    private static string OnBranch(string? branch) => string.IsNullOrWhiteSpace(branch) ? "" : $" on `{branch}`";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private sealed record Facts(
        bool LaneCompleted,
        List<SummaryCommitEvidence> EffectiveCommits,
        List<SummaryIntegrationEvidence> Integrated,
        List<SummaryStepEvidence> FailedSteps,
        SummarySalvageEvidence? Salvage,
        string? OperatorMergeSha,
        string? MergeBranch)
    {
        public static Facts From(SummaryEvidenceBundle bundle)
        {
            var effective = bundle.Commits.Where(c => !c.Superseded).ToList();
            var integrated = bundle.IntegrationRecords
                .Where(r => IntegratedClasses.Contains(r.Classification, StringComparer.Ordinal))
                .ToList();
            var failed = bundle.Pipeline?.Steps.Where(s => s.Status == "failed").ToList() ?? [];
            var salvage = bundle.SalvageRefs.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.CommitSha))
                ?? bundle.SalvageRefs.LastOrDefault();

            // An operator merge is an integrated commit the card's own chain
            // does not attribute: the curated or verified record names it, or
            // the completion claim proved containment through it.
            string? merge = null;
            string? branch = null;
            foreach (var record in integrated)
            {
                var candidate = new[] { record.IntegrationSha }
                    .Concat(record.CommitShas)
                    .FirstOrDefault(sha => !string.IsNullOrWhiteSpace(sha) && !Attributed(effective, sha!));
                if (candidate is null) continue;
                merge = candidate;
                branch = record.IntegrationBranch;
                break;
            }
            if (merge is null
                && bundle.CompletionClaim?.CommitSha is { Length: > 0 } claimSha
                && !Attributed(effective, claimSha))
            {
                merge = claimSha;
            }

            return new Facts(
                bundle.Lane == TaskStates.Completed,
                effective,
                integrated,
                failed,
                salvage,
                merge,
                branch);
        }

        private static bool Attributed(List<SummaryCommitEvidence> commits, string sha)
            => commits.Any(c => c.Sha.Length > 0
                && (c.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase)
                    || sha.StartsWith(c.Sha, StringComparison.OrdinalIgnoreCase)));
    }
}
