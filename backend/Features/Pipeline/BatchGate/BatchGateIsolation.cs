namespace AgentStudio.Pipeline;

public enum BatchGateFailureClass
{
    Pass,
    InfrastructureRed,
    DeterministicSuiteRed,
    FlakyRed,
    Superseded,
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
        CancellationToken ct,
        Func<BatchGateSubject, bool>? current = null,
        Func<IReadOnlyList<BatchGateSubject>, BatchGateProbeResult, CancellationToken, Task<bool>>? publishGreen = null)
    {
        if (maximumTotalDiagnosticRuns < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalDiagnosticRuns));
        var survivors = orderedMembers.ToList();
        var ejected = new List<string>();
        var diagnostics = new List<BatchGateDiagnostic>();
        BatchGateProbeResult? survivorVerdict = null;
        while (survivors.Count > 0)
        {
            var beforePrune = survivors.Count;
            Prune(survivors);
            if (survivors.Count == 0) break;
            var cohort = survivors.ToList();
            var cohortRed = beforePrune == survivors.Count;
            var perCycle = BatchGatePolicy.DiagnosticRunsPerCycle(cohort.Count);
            var cycleUsed = 0;
            while (cohort.Count > 1 && cycleUsed < perCycle
                   && diagnostics.Count < maximumTotalDiagnosticRuns)
            {
                ct.ThrowIfCancellationRequested();
                var beforeCohortPrune = cohort.Count;
                Prune(cohort);
                if (cohort.Count != beforeCohortPrune) cohortRed = false;
                if (cohort.Count == 0) break;
                var first = cohort.Take(Math.Max(1, cohort.Count / 2)).ToList();
                var result = await probe(first, ct).ConfigureAwait(false);
                if (result.Classification == BatchGateFailureClass.Superseded)
                {
                    Prune(survivors);
                    Prune(cohort);
                    cohortRed = false;
                    continue;
                }
                diagnostics.Add(new BatchGateDiagnostic(
                    first.Select(x => x.TaskKey).ToArray(), result));
                cycleUsed++;
                if (result.Classification == BatchGateFailureClass.DeterministicSuiteRed)
                {
                    cohort = first;
                    cohortRed = true;
                }
                else if (result.Classification == BatchGateFailureClass.Pass)
                {
                    if (publishGreen is not null && await publishGreen(first, result, ct).ConfigureAwait(false))
                        survivors.RemoveAll(member => first.Any(green => green.TaskKey == member.TaskKey));
                    cohort = cohort.Skip(first.Count).ToList();
                    // With three members, test the complementary pair as one
                    // child before narrowing it. Its verdict remains useful if
                    // either member changes delivery during the probe.
                    if (orderedMembers.Count == 3 && cohort.Count == 2
                        && diagnostics.Count < maximumTotalDiagnosticRuns)
                    {
                        var complement = await probe(cohort, ct).ConfigureAwait(false);
                        if (complement.Classification == BatchGateFailureClass.Superseded)
                        {
                            Prune(survivors);
                            Prune(cohort);
                            cohortRed = false;
                        }
                        else
                        {
                            diagnostics.Add(new BatchGateDiagnostic(
                                cohort.Select(member => member.TaskKey).ToArray(), complement));
                            if (complement.Classification == BatchGateFailureClass.Pass)
                            {
                                if (publishGreen is not null)
                                    await publishGreen(cohort, complement, ct).ConfigureAwait(false);
                                return new(ejected, cohort.Select(member => member.TaskKey).ToArray(),
                                    [], diagnostics, complement);
                            }
                            if (complement.Classification != BatchGateFailureClass.DeterministicSuiteRed)
                                return Unresolved();
                            cohortRed = true;
                        }
                    }
                }
                else
                    return Unresolved();
            }
            Prune(cohort);
            if (cohort.Count == 0) continue;
            if (cohort.Count != 1)
                return Unresolved();
            if (!cohortRed)
            {
                var result = await probe(cohort, ct).ConfigureAwait(false);
                if (result.Classification == BatchGateFailureClass.Superseded)
                {
                    Prune(survivors);
                    continue;
                }
                diagnostics.Add(new BatchGateDiagnostic(
                    cohort.Select(member => member.TaskKey).ToArray(), result));
                if (result.Classification == BatchGateFailureClass.Pass)
                {
                    if (publishGreen is not null)
                        await publishGreen(cohort, result, ct).ConfigureAwait(false);
                    return new(ejected, cohort.Select(member => member.TaskKey).ToArray(),
                        [], diagnostics, result);
                }
                if (result.Classification != BatchGateFailureClass.DeterministicSuiteRed)
                    return Unresolved();
            }
            ejected.Add(cohort[0].TaskKey);
            survivors.RemoveAll(x => x.TaskKey == cohort[0].TaskKey);
            if (survivors.Count == 0)
                return new(ejected, [], [], diagnostics, null);
            ct.ThrowIfCancellationRequested();
            survivorVerdict = await probe(survivors, ct).ConfigureAwait(false);
            if (survivorVerdict.Classification == BatchGateFailureClass.Superseded)
            {
                Prune(survivors);
                continue;
            }
            // The final survivor run is evidence, not a halving diagnostic.
            if (survivorVerdict.Classification == BatchGateFailureClass.Pass)
            {
                if (publishGreen is not null)
                    await publishGreen(survivors, survivorVerdict, ct).ConfigureAwait(false);
                return new(ejected, survivors.Select(x => x.TaskKey).ToArray(),
                    [], diagnostics, survivorVerdict);
            }
            if (survivorVerdict.Classification != BatchGateFailureClass.DeterministicSuiteRed)
                return Unresolved();
        }
        return Unresolved();

        BatchGateIsolationResult Unresolved() => new(
            ejected, survivors.Select(x => x.TaskKey).ToArray(),
            survivors.Select(x => x.TaskKey).ToArray(), diagnostics,
            survivorVerdict);

        void Prune(List<BatchGateSubject> members)
        {
            if (current is not null)
                members.RemoveAll(member => !current(member));
        }
    }
}
