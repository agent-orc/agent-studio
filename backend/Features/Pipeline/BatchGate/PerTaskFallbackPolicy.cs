using AgentStudio.Git;

namespace AgentStudio.Pipeline;

/// <summary>What the per-task batch fallback gate tests (AGT-3434).</summary>
public enum PerTaskFallbackSubjectKind
{
    /// <summary>The delivery merged onto the current integration tip.</summary>
    MergedTree,

    /// <summary>The delivery on the base it was written on, the rule before AGT-3434.</summary>
    OwnBase,

    /// <summary>The delivery conflicts with the tip: no gate, the agent merges it.</summary>
    MergeRound,
}

/// <param name="TestedSha">Commit the gate tests; the result SHA for <see cref="PerTaskFallbackSubjectKind.OwnBase"/>.</param>
/// <param name="BaseSha">Integration tip of a merged candidate.</param>
/// <param name="TreeSha">Tree of a merged candidate, compared by the publish step.</param>
public sealed record PerTaskFallbackSubject(
    PerTaskFallbackSubjectKind Kind,
    string? TestedSha,
    string? BaseSha,
    string? TreeSha,
    string Reason,
    IReadOnlyList<string> ConflictedFiles)
{
    public bool Merged => Kind == PerTaskFallbackSubjectKind.MergedTree;
}

/// <summary>
/// Chooses the tree the per-task batch fallback gate tests. Before AGT-3434 it
/// tested the delivery on the base it was written on: a stale base could fail
/// on tests the tip had already fixed, and the merge path then ran its own
/// gate on the merged tree. The gate now tests the delivery merged onto the
/// current integration tip. Two cases keep the old subject: the tip cannot be
/// fetched or merged for a reason other than a conflict, and a merged
/// candidate that failed on a tip whose own verdict is red. The second keeps
/// what the old base protected: a red integration branch does not escalate a
/// delivery that is green on its own.
/// </summary>
public static class PerTaskFallbackPolicy
{
    /// <param name="resultSha">Immutable delivery SHA of the review generation.</param>
    /// <param name="ownBaseRequested">The previous merged candidate failed on a red tip.</param>
    /// <param name="integrationTip">Fetched integration tip; null when it could not be fetched.</param>
    /// <param name="merge">The delivery merged onto <paramref name="integrationTip"/>.</param>
    /// <param name="candidateTree">Tree of the merged candidate; null when unreadable.</param>
    public static PerTaskFallbackSubject Choose(string resultSha, bool ownBaseRequested,
        string? integrationTip, BatchReplayResult? merge, string? candidateTree)
    {
        if (ownBaseRequested)
            return OwnBase(resultSha,
                "the merged candidate failed on an integration tip that is red on its own");
        if (integrationTip is null)
            return OwnBase(resultSha, "the integration tip could not be fetched");
        if (merge is null)
            return OwnBase(resultSha, "the delivery was not merged onto the integration tip");
        if (merge.ConflictedFiles.Count > 0)
            return new(PerTaskFallbackSubjectKind.MergeRound, null, integrationTip, null,
                $"the delivery conflicts with the integration tip {Short(integrationTip)} in "
                + string.Join(", ", merge.ConflictedFiles),
                merge.ConflictedFiles);
        if (!merge.Success || string.IsNullOrWhiteSpace(merge.TipSha))
            return OwnBase(resultSha,
                $"the delivery could not be merged onto {Short(integrationTip)}: {merge.Error ?? "no candidate"}");
        if (string.IsNullOrWhiteSpace(candidateTree))
            return OwnBase(resultSha, "the merged candidate tree could not be read");
        return new(PerTaskFallbackSubjectKind.MergedTree, merge.TipSha, integrationTip, candidateTree,
            $"the delivery merged onto the integration tip {Short(integrationTip)}", []);
    }

    /// <summary>
    /// A deterministic red of a merged candidate is retested on the delivery's
    /// own base only when the integration tip is red on its own. A delivery
    /// already based on the tip is its own candidate; a retest would repeat it.
    /// </summary>
    public static bool RetestOnOwnBase(PerTaskFallbackSubject subject, string resultSha,
        BuildTestGateResult gate, string? tipOutcome)
        => subject.Merged
            && !string.Equals(subject.TestedSha, resultSha, StringComparison.OrdinalIgnoreCase)
            && gate.Verdict == BuildTestGateVerdict.Fail
            && gate.FailureKind == BuildTestGateFailureKind.Code
            && tipOutcome == "fail";

    private static PerTaskFallbackSubject OwnBase(string resultSha, string reason)
        => new(PerTaskFallbackSubjectKind.OwnBase, resultSha, null, null, reason, []);

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
