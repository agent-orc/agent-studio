namespace AgentStudio.Pipeline;

/// <summary>Live pilot numbers. Missing comparable baseline values remain null.</summary>
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
    int FalseCompletedCards, bool CorrectnessFloorMet);

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
            store.TryReadReplay(item.BatchId, member.TaskKey)?.Outcome == "conflict"));
        var green = primary.Count(item => store.ListRuns(item.BatchId).Any(run =>
            store.ReadVerdict(item.BatchId, run.BatchRunId)?.Outcome == "pass"));
        var withRun = primary.Count(item => store.ListRuns(item.BatchId).Count > 0);
        var sortedLatency = latencies.Order().ToArray();
        var overload = executions.Sum(item => item.OverloadMinutes);
        var hostMinutes = executions.Sum(item =>
            (item.CompletedAtUtc - item.StartedAtUtc).TotalMinutes);
        return new BatchGatePilotSnapshot(
            project, primary.Length, eligible, executions.Count,
            Ratio(executions.Count, eligible), Ratio(attempted, primary.Length),
            overload, null, null,
            queueDrain.Count > 0 ? queueDrain.Average() : null,
            null, null, Percentile(sortedLatency, .5),
            Percentile(sortedLatency, .95), Ratio(green, withRun),
            Ratio(ejected, attempted), hostMinutes, null,
            stale + staleReleasedCards, missingMappings, untested, falseCompletedCards,
            stale + staleReleasedCards == 0 && missingMappings == 0 && untested == 0
            && falseCompletedCards == 0);
    }

    private static double? Ratio(double numerator, double denominator)
        => denominator > 0 ? numerator / denominator : null;

    private static double? Percentile(double[] values, double percentile)
        => values.Length == 0 ? null
            : values[Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1,
                0, values.Length - 1)];
}
