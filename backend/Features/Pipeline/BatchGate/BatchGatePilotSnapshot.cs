namespace AgentStudio.Pipeline;

/// <summary>
/// Live pilot numbers. Missing comparable baseline values remain null.
/// <c>FullSuiteRuns</c> and <c>FullSuiteHostMinutes</c> count gate executions:
/// batch runs, base-verdict runs and every per-task gate, emergency fallbacks
/// included. The review-verification fields count the builds and selected tests
/// that batch-deferred reviews ran on their own (aspect
/// <c>review-verification</c>), candidate plus non-cached baseline workspaces.
/// They are counted from the deploy that introduced them onward; reviews
/// settled earlier have no fact and are not reconstructed from evidence.
/// <c>TotalVerificationHostMinutes</c> is the sum of both sides.
/// <c>BaseChecks</c> lists, per red candidate, what answered whether its base
/// was red and what that cost (AGT-3433); <c>BaseCheckMinutes</c> sums it.
/// A narrow base check counts in the host minutes, not as a full-suite run.
/// The per-task fallback fields (AGT-3434) count the cards that path settled,
/// the suites they ran (per-task gates plus publish gates), the mean per card,
/// and the publish steps that reused the per-task verdict instead of a suite.
/// </summary>
public sealed record BatchGatePilotSnapshot(
    string Project, int ClosedBatches, int EligibleDeliveredMembers,
    int FullSuiteRuns, double? FullSuiteRunsPerEligibleMember,
    double? MeanBatchSize, double HostOverloadMinutes,
    double? ComparableBaselineHostOverloadMinutes,
    double? HostOverloadReduction, double? MeanQueueDrainMinutes,
    double? ComparableBaselineQueueDrainMinutes,
    double? QueueDrainImprovement, double? MedianGateLatencyMinutes,
    double? P95GateLatencyMinutes, double? BatchGreenRate,
    double? ConstructionEjectionRate, double FullSuiteHostMinutes,
    decimal? GateCostUsd, int StaleAttemptPasses,
    int MissingShaMappings, int UntestedPublishShas,
    int FalseCompletedCards, bool CorrectnessFloorMet,
    int IntegrationBranchRedBatches = 0,
    PendingIntegrationPushWait? PendingPushWait = null,
    int ReviewVerificationRuns = 0,
    double ReviewVerificationHostMinutes = 0,
    double ReviewVerificationBaselineMinutes = 0,
    double TotalVerificationHostMinutes = 0,
    IReadOnlyList<BatchGateBaseCheckView>? BaseChecks = null,
    double BaseCheckMinutes = 0,
    int PerTaskFallbackCards = 0,
    int PerTaskFallbackSuiteRuns = 0,
    double? PerTaskFallbackSuitesPerCard = null,
    int PerTaskPublishVerdictReuses = 0,
    IReadOnlyList<BatchGateIsolationFinding>? IsolationFindings = null);

/// <summary>One red candidate's base answer: its classification, the source that answered, and the minutes it cost.</summary>
public sealed record BatchGateBaseCheckView(
    string BatchId, string BaseSha, string Classification, string AnsweredBy,
    double Minutes, DateTimeOffset RecordedAtUtc);

public static class BatchGatePilotSnapshotReader
{
    public static BatchGatePilotSnapshot Read(BatchGateStore store,
        string project, int falseCompletedCards = 0, int staleReleasedCards = 0)
    {
        var all = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.Ordinal))
            .ToArray();
        var primary = all.Where(item => item.ParentBatchId is null).ToArray();
        var executions = store.ListExecutions(project);
        var fullSuiteRuns = executions.Count(item => item.NarrowCheck is null);
        var baseChecks = new List<BatchGateBaseCheckView>();
        var eligible = primary.SelectMany(item => item.Members)
            .Select(item => item.TaskKey + "\n" + item.RunAttempt)
            .Distinct(StringComparer.Ordinal).Count();
        var queueDrain = new List<double>();
        var latencies = new List<double>();
        var missingMappings = 0;
        var untested = 0;
        var stale = 0;
        foreach (var manifest in all)
        {
            foreach (var answered in store.ListRuns(manifest.BatchId)
                         .Select(item => store.ReadVerdict(manifest.BatchId, item.BatchRunId))
                         .Where(item => item?.BaseCheck is not null))
                baseChecks.Add(new BatchGateBaseCheckView(manifest.BatchId, manifest.BaseSha,
                    answered!.Classification, answered.BaseCheck!, answered.BaseCheckMinutes ?? 0,
                    answered.CompletedAtUtc));
            var publication = store.ReadPublication(manifest.BatchId);
            if (publication is null) continue;
            var run = store.ListRuns(manifest.BatchId)
                .FirstOrDefault(item => item.BatchRunId == publication.BatchRunId);
            var verdict = store.ReadVerdict(manifest.BatchId, publication.BatchRunId);
            if (run is null || verdict is null
                || verdict.Outcome != "pass"
                || publication.TestedCandidateSha != run.CandidateSha
                || publication.VerifiedRemoteSha != run.CandidateSha
                || verdict.TestedCandidateSha != run.CandidateSha)
                untested++;
            if (manifest.Members.Count > 0)
                queueDrain.Add((publication.VerifiedAtUtc
                    - manifest.Members.Min(item => item.ReviewCompletedAtUtc)).TotalMinutes);
            foreach (var member in manifest.Members)
            {
                var replay = store.TryReadReplay(manifest.BatchId, member.TaskKey);
                if (replay is null)
                {
                    missingMappings++;
                    continue;
                }
                if (replay.Outcome != "admitted") continue;
                latencies.Add((publication.VerifiedAtUtc
                    - member.ReviewCompletedAtUtc).TotalMinutes);
                var memberRecord = store.TryReadMember(
                    manifest.BatchId, member.TaskKey, publication.BatchRunId);
                if (memberRecord is null || replay.Replacements.Count == 0
                    || !memberRecord.ReplacementShas.SequenceEqual(
                        replay.Replacements.Select(item => item.RebasedSha),
                        StringComparer.OrdinalIgnoreCase))
                    missingMappings++;
                if (memberRecord is not null
                    && (memberRecord.RunAttempt != member.RunAttempt
                        || memberRecord.DeliveryEpoch != member.DeliveryEpoch
                        || memberRecord.OriginalResultSha != member.ResultSha))
                    stale++;
            }
        }
        var attempted = primary.SelectMany(item => item.Members).Count();
        var ejected = primary.Sum(item => item.Members.Count(member =>
            store.TryReadReplay(item.BatchId, member.TaskKey)?.Outcome
                is "conflict" or "unattributable"));
        var green = primary.Count(item => store.ListRuns(item.BatchId).Any(run =>
            store.ReadVerdict(item.BatchId, run.BatchRunId)?.Outcome == "pass"));
        var withRun = primary.Count(item => store.ListRuns(item.BatchId).Count > 0);
        var sortedLatency = latencies.Order().ToArray();
        var overload = executions.Sum(item => item.OverloadMinutes);
        var hostMinutes = executions.Sum(item =>
            (item.CompletedAtUtc - item.StartedAtUtc).TotalMinutes);
        var reviewVerifications = store.ListReviewVerifications(project);
        var reviewMinutes = reviewVerifications.Sum(item =>
            item.CandidateMinutes + item.BaselineMinutes);
        var fallbackCards = store.ListFallbackSuites(project);
        var fallbackSuites = fallbackCards.Sum(item => item.Suites);
        return new BatchGatePilotSnapshot(
            project, primary.Length, eligible, fullSuiteRuns,
            Ratio(fullSuiteRuns, eligible), Ratio(attempted, primary.Length),
            overload, null, null,
            queueDrain.Count > 0 ? queueDrain.Average() : null,
            null, null, Percentile(sortedLatency, .5),
            Percentile(sortedLatency, .95), Ratio(green, withRun),
            Ratio(ejected, attempted), hostMinutes, null,
            stale + staleReleasedCards, missingMappings, untested, falseCompletedCards,
            stale + staleReleasedCards == 0 && missingMappings == 0 && untested == 0
            && falseCompletedCards == 0,
            all.Count(item => store.LatestState(item.BatchId) is
                { Phase: BatchPhase.Paused, Reason: "integration-branch-red" }),
            ReviewVerificationRuns: reviewVerifications.Count(item => item.CountedCommands > 0),
            ReviewVerificationHostMinutes: reviewMinutes,
            ReviewVerificationBaselineMinutes: reviewVerifications.Sum(item => item.BaselineMinutes),
            TotalVerificationHostMinutes: hostMinutes + reviewMinutes,
            BaseChecks: baseChecks.OrderByDescending(item => item.RecordedAtUtc).ToArray(),
            BaseCheckMinutes: baseChecks.Sum(item => item.Minutes),
            PerTaskFallbackCards: fallbackCards.Count,
            PerTaskFallbackSuiteRuns: fallbackSuites,
            PerTaskFallbackSuitesPerCard: Ratio(fallbackSuites, fallbackCards.Count),
            PerTaskPublishVerdictReuses: fallbackCards.Count(item => item.PublishVerdictReused),
            IsolationFindings: store.ListIsolationFindings(project));
    }

    private static double? Ratio(double numerator, double denominator)
        => denominator > 0 ? numerator / denominator : null;

    private static double? Percentile(double[] values, double percentile)
        => values.Length == 0 ? null
            : values[Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1,
                0, values.Length - 1)];
}
