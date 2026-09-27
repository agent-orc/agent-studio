namespace AgentStudio.Pipeline;

public sealed record BatchGatePilotWave(
    string BatchId, int EligibleDeliveredMembers, int FullSuiteRuns,
    int AdmittedMembers, int ConstructionEjections, bool CandidateGreen,
    double HostOverloadMinutes, double ComparableBaselineHostOverloadMinutes,
    double QueueDrainMinutes, double ComparableBaselineQueueDrainMinutes,
    IReadOnlyList<double> GateLatencyMinutes, decimal GateCost,
    int StaleAttemptPasses = 0, int MissingShaMappings = 0,
    int UntestedPublishShas = 0, int FalseCompletedCards = 0);

public sealed record BatchGatePilotReport(
    int Waves, int EligibleDeliveredMembers, int FullSuiteRuns,
    double FullSuiteRunsPerEligibleMember, double MeanBatchSize,
    double HostOverloadMinutes, double BaselineHostOverloadMinutes,
    double HostOverloadReduction, double QueueDrainMinutes,
    double BaselineQueueDrainMinutes, double QueueDrainImprovement,
    double MedianGateLatencyMinutes, double P95GateLatencyMinutes,
    double BatchGreenRate, double ConstructionEjectionRate,
    decimal TotalGateCost, int StaleAttemptPasses,
    int MissingShaMappings, int UntestedPublishShas,
    int FalseCompletedCards, bool CorrectnessFloorMet)
{
    public static BatchGatePilotReport Calculate(IReadOnlyList<BatchGatePilotWave> waves)
    {
        var eligible = waves.Sum(x => x.EligibleDeliveredMembers);
        var runs = waves.Sum(x => x.FullSuiteRuns);
        var admitted = waves.Sum(x => x.AdmittedMembers);
        var ejected = waves.Sum(x => x.ConstructionEjections);
        var overload = waves.Sum(x => x.HostOverloadMinutes);
        var baselineOverload = waves.Sum(x => x.ComparableBaselineHostOverloadMinutes);
        var drain = waves.Sum(x => x.QueueDrainMinutes);
        var baselineDrain = waves.Sum(x => x.ComparableBaselineQueueDrainMinutes);
        var latencies = waves.SelectMany(x => x.GateLatencyMinutes).Order().ToArray();
        var stale = waves.Sum(x => x.StaleAttemptPasses);
        var mapping = waves.Sum(x => x.MissingShaMappings);
        var untested = waves.Sum(x => x.UntestedPublishShas);
        var completed = waves.Sum(x => x.FalseCompletedCards);
        return new BatchGatePilotReport(
            waves.Count, eligible, runs,
            Ratio(runs, eligible), Ratio(admitted, waves.Count),
            overload, baselineOverload,
            baselineOverload > 0 ? 1 - overload / baselineOverload : double.NaN,
            drain, baselineDrain,
            baselineDrain > 0 ? 1 - drain / baselineDrain : double.NaN,
            Percentile(latencies, .5), Percentile(latencies, .95),
            Ratio(waves.Count(x => x.CandidateGreen), waves.Count),
            Ratio(ejected, admitted + ejected),
            waves.Sum(x => x.GateCost), stale, mapping, untested, completed,
            stale == 0 && mapping == 0 && untested == 0 && completed == 0);
    }

    public bool MeetsPilotTargets => CorrectnessFloorMet
        && FullSuiteRunsPerEligibleMember <= .40
        && MeanBatchSize >= 3
        && HostOverloadReduction >= .50
        && QueueDrainImprovement >= .50
        && MedianGateLatencyMinutes <= 15
        && P95GateLatencyMinutes <= 30
        && BatchGreenRate >= .80
        && ConstructionEjectionRate <= .10;

    private static double Ratio(double numerator, double denominator)
        => denominator > 0 ? numerator / denominator : double.NaN;

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
