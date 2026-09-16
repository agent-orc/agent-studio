namespace AgentStudio.Pipeline;

/// <summary>
/// One deterministic gate's observed state for an integration branch (AGT-2819).
/// <para>
/// <paramref name="MergeBaseSha"/> is set only when the gate actually ran on the
/// merge base, which a review does whenever the same gate failed on the
/// delivery. That is the only observation that can name a commit on the branch.
/// A gate that simply passed on the delivery is reported with a null merge base:
/// it clears a stale red flag without pretending to have measured the branch.
/// </para>
/// </summary>
public sealed record IntegrationBranchGateObservation(
    string Branch,
    string StepId,
    string Command,
    bool Red,
    string? MergeBaseSha,
    DateTime ObservedAtUtc);

/// <summary>
/// Persisted health of one <c>(branch, step)</c> pair. Keeping the last green
/// merge-base commit is what turns a bare "it is red" into "it turned red
/// between these two commits".
/// </summary>
public sealed record IntegrationBranchGateRecord(
    string Branch,
    string StepId,
    bool Red,
    string? Command = null,
    string? RedSinceSha = null,
    DateTime? RedSinceUtc = null,
    string? LastGreenSha = null,
    DateTime? LastGreenUtc = null,
    DateTime? ObservedAtUtc = null);

/// <summary>What one observation did to a gate's recorded health.</summary>
public enum IntegrationBranchGateTransition
{
    /// <summary>Green before, green now.</summary>
    StillGreen,

    /// <summary>Green (or never observed) before, red now. The alerting transition.</summary>
    TurnedRed,

    /// <summary>Red before, red now. Already reported, so it stays quiet.</summary>
    StillRed,

    /// <summary>Red before, green now.</summary>
    Recovered,
}

/// <summary>
/// Whether this observation is news, what to say about it, and the health row to
/// persist.
/// </summary>
public sealed record IntegrationBranchGateDecision(
    IntegrationBranchGateTransition Transition,
    bool ShouldAlert,
    IntegrationBranchGateRecord Next,
    string Summary);

/// <summary>
/// Pure transition policy for integration-branch gate health (AGT-2819).
/// <para>
/// The component-size gate had been red on <c>develop</c> since 2026-08-19 and
/// nothing said so; it was visible only as a <c>ProductFailure</c> on unrelated
/// cards. Every review now runs each deterministic gate on the merge base before
/// attributing its failure, so the measurement exists. This policy turns that
/// measurement into exactly one operator alert per red transition instead of one
/// per card that passes through the branch.
/// </para>
/// </summary>
public static class IntegrationBranchGateHealthPolicy
{
    public static IntegrationBranchGateDecision Decide(
        IntegrationBranchGateRecord? previous,
        IntegrationBranchGateObservation observation)
    {
        var wasRed = previous?.Red == true;
        var transition = (wasRed, observation.Red) switch
        {
            (false, true) => IntegrationBranchGateTransition.TurnedRed,
            (true, true) => IntegrationBranchGateTransition.StillRed,
            (true, false) => IntegrationBranchGateTransition.Recovered,
            _ => IntegrationBranchGateTransition.StillGreen,
        };

        // Only a merge-base run proves something about the branch, so only that
        // moves the named commits. A plain green delivery run clears the flag.
        var measured = !string.IsNullOrWhiteSpace(observation.MergeBaseSha);
        var next = new IntegrationBranchGateRecord(
            observation.Branch,
            observation.StepId,
            observation.Red,
            observation.Command,
            RedSinceSha: observation.Red
                ? wasRed
                    ? previous!.RedSinceSha ?? observation.MergeBaseSha
                    : observation.MergeBaseSha
                : null,
            RedSinceUtc: observation.Red
                ? wasRed ? previous!.RedSinceUtc ?? observation.ObservedAtUtc : observation.ObservedAtUtc
                : null,
            LastGreenSha: observation.Red || !measured
                ? previous?.LastGreenSha
                : observation.MergeBaseSha,
            LastGreenUtc: observation.Red || !measured
                ? previous?.LastGreenUtc
                : observation.ObservedAtUtc,
            ObservedAtUtc: observation.ObservedAtUtc);

        return new IntegrationBranchGateDecision(
            transition,
            ShouldAlert: transition == IntegrationBranchGateTransition.TurnedRed,
            next,
            Describe(transition, previous, next, observation));
    }

    /// <summary>
    /// The operator-facing sentence. When a green merge-base commit is known the
    /// alert names the range the breaking commit lies in; it never claims a
    /// single culprit the measurement cannot prove.
    /// </summary>
    private static string Describe(
        IntegrationBranchGateTransition transition,
        IntegrationBranchGateRecord? previous,
        IntegrationBranchGateRecord next,
        IntegrationBranchGateObservation observation)
    {
        var step = $"{observation.StepId} (`{observation.Command}`)";
        return transition switch
        {
            IntegrationBranchGateTransition.TurnedRed when previous?.LastGreenSha is { Length: > 0 } green =>
                $"Gate {step} is red on {observation.Branch} at {Short(observation.MergeBaseSha)}. "
                + $"It was last measured green at {Short(green)}, so a commit in "
                + $"{Short(green)}..{Short(observation.MergeBaseSha)} turned it red.",
            IntegrationBranchGateTransition.TurnedRed =>
                $"Gate {step} is red on {observation.Branch} at {Short(observation.MergeBaseSha)}. "
                + "No earlier green measurement exists, so that is the earliest commit it is known red at.",
            IntegrationBranchGateTransition.StillRed =>
                $"Gate {step} is still red on {observation.Branch}, red since "
                + $"{Short(next.RedSinceSha)} and observed again at {Short(observation.MergeBaseSha)}.",
            IntegrationBranchGateTransition.Recovered =>
                $"Gate {step} is green again on {observation.Branch}.",
            _ => $"Gate {step} is green on {observation.Branch}.",
        };
    }

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "(unmeasured)" : sha.Length <= 12 ? sha : sha[..12];
}
