namespace AgentStudio.Pipeline;

public enum BatchGateFailureClass
{
    Pass,
    InfrastructureRed,
    DeterministicSuiteRed,
    FlakyRed,
}

public sealed record BatchGateProbeResult(
    BatchGateFailureClass Classification, string TestedSha, string EvidencePath);

public sealed record BatchGateDiagnostic(
    IReadOnlyList<string> MemberKeys, BatchGateProbeResult Result);

public sealed record BatchGateIsolationResult(
    IReadOnlyList<string> EjectedKeys,
    IReadOnlyList<string> SurvivorKeys,
    IReadOnlyList<string> UnresolvedCohortKeys,
    IReadOnlyList<BatchGateDiagnostic> Diagnostics,
    BatchGateProbeResult? SurvivorVerdict);

/// <summary>
/// Ordered halving after a deterministic full-batch red. Each cycle is capped
/// at ceil(log2 n) diagnostic runs. Only a green survivor rerun is publishable;
/// unexpected failure classes and exhausted budgets leave a visible cohort.
/// </summary>
public static class BatchGateIsolation
{
    public static async Task<BatchGateIsolationResult> RunAsync(
        IReadOnlyList<BatchGateSubject> orderedMembers,
        Func<IReadOnlyList<BatchGateSubject>, CancellationToken, Task<BatchGateProbeResult>> probe,
        int maximumTotalDiagnosticRuns,
        CancellationToken ct)
    {
        if (maximumTotalDiagnosticRuns < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalDiagnosticRuns));
        var survivors = orderedMembers.ToList();
        var ejected = new List<string>();
        var diagnostics = new List<BatchGateDiagnostic>();
        BatchGateProbeResult? survivorVerdict = null;
        while (survivors.Count > 0)
        {
            var cohort = survivors.ToList();
            var perCycle = BatchGatePolicy.DiagnosticRunsPerCycle(cohort.Count);
            var cycleUsed = 0;
            while (cohort.Count > 1 && cycleUsed < perCycle
                   && diagnostics.Count < maximumTotalDiagnosticRuns)
            {
                ct.ThrowIfCancellationRequested();
                var first = cohort.Take((cohort.Count + 1) / 2).ToList();
                var result = await probe(first, ct).ConfigureAwait(false);
                diagnostics.Add(new BatchGateDiagnostic(
                    first.Select(x => x.TaskKey).ToArray(), result));
                cycleUsed++;
                if (result.Classification == BatchGateFailureClass.DeterministicSuiteRed)
                    cohort = first;
                else if (result.Classification == BatchGateFailureClass.Pass)
                    cohort = cohort.Skip(first.Count).ToList();
                else
                    return Unresolved();
            }
            if (cohort.Count != 1)
                return Unresolved();
            ejected.Add(cohort[0].TaskKey);
            survivors.RemoveAll(x => x.TaskKey == cohort[0].TaskKey);
            if (survivors.Count == 0)
                return new(ejected, [], [], diagnostics, null);
            ct.ThrowIfCancellationRequested();
            survivorVerdict = await probe(survivors, ct).ConfigureAwait(false);
            // The final survivor run is evidence, not a halving diagnostic.
            if (survivorVerdict.Classification == BatchGateFailureClass.Pass)
                return new(ejected, survivors.Select(x => x.TaskKey).ToArray(),
                    [], diagnostics, survivorVerdict);
            if (survivorVerdict.Classification != BatchGateFailureClass.DeterministicSuiteRed)
                return Unresolved();
        }
        return Unresolved();

        BatchGateIsolationResult Unresolved() => new(
            ejected, survivors.Select(x => x.TaskKey).ToArray(),
            survivors.Select(x => x.TaskKey).ToArray(), diagnostics,
            survivorVerdict);
    }
}
