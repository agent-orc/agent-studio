namespace AgentStudio.Pipeline;

public enum ReleaseLineMergeAction
{
    /// <summary>The full suite passed on a merge whose base still carries the published release line.</summary>
    Publish,

    /// <summary>The release line moved during the gate: roll back and merge again onto the new tip.</summary>
    Retry,

    /// <summary>The release line kept moving and the attempt budget is spent: roll back and stop.</summary>
    StopMoving,

    /// <summary>The full suite did not pass on the merge result: roll back and publish nothing.</summary>
    Refuse,
}

public sealed record ReleaseLineMergeDecision(ReleaseLineMergeAction Action, string Reason);

/// <summary>
/// Pure decision after the pre-main full suite ran on the merge of a delivery
/// that was behind the release line (AGT-3435). Main only advances to the exact
/// merge result the suite passed, and only while that merge still contains the
/// published release line. A red verdict is never retried; a release line that
/// moved during the gate gets a bounded number of fresh merges.
/// </summary>
public static class ReleaseLineMergePolicy
{
    public const int MaxAttempts = 3;

    public static ReleaseLineMergeDecision Decide(
        bool gateGreen,
        bool releaseLineMoved,
        int attempt,
        int maxAttempts = MaxAttempts)
    {
        var limit = Math.Max(1, maxAttempts);
        if (!gateGreen)
            return new(
                ReleaseLineMergeAction.Refuse,
                "the full suite did not pass on the merge result");
        if (!releaseLineMoved)
            return new(
                ReleaseLineMergeAction.Publish,
                "the full suite passed on the merge result and the release line did not move");
        return attempt < limit
            ? new(
                ReleaseLineMergeAction.Retry,
                $"the release line moved during the full suite; merging again (attempt {attempt + 1}/{limit})")
            : new(
                ReleaseLineMergeAction.StopMoving,
                $"the release line moved during each of {limit} full-suite runs; stopped after {limit}/{limit} attempts");
    }
}
