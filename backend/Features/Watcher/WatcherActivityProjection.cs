namespace AgentStudio.Watcher;

/// <summary>One Activity row derived from a case or a proposal.</summary>
public sealed record WatcherActivityEntry(
    DateTime At,
    string Kind,
    string Topic,
    string Summary,
    string Reasoning,
    string? Project);

/// <summary>
/// Maps Watcher state onto the existing Activity entry grid. Dossier section 4a
/// is explicit that no new feed kind is introduced: the stable kinds carry
/// Watcher topics, and Problem and Decision remain filters over the same
/// chronological stream.
/// </summary>
public static class WatcherActivityProjection
{
    /// <summary>
    /// Case state to Activity kind, per the section 4 visibility table.
    /// <c>alert</c> presents as Problem, <c>decision</c> as Decision,
    /// <c>observation</c> as a quiet resolved finding.
    /// </summary>
    public static string KindFor(WatcherCaseState state) => state switch
    {
        WatcherCaseState.Open => OrchestratorLogKinds.Alert,
        WatcherCaseState.DecisionRequired => OrchestratorLogKinds.Decision,
        WatcherCaseState.ActionRunning => OrchestratorLogKinds.Action,
        WatcherCaseState.Resolved => OrchestratorLogKinds.Observation,
        WatcherCaseState.GaveUp => OrchestratorLogKinds.Alert,
        _ => OrchestratorLogKinds.Observation,
    };

    /// <summary>
    /// A newly opened or reopened case. Identical checks that find no new
    /// evidence update counters on the case and must not produce a row, which
    /// is the section 4 noise rule.
    /// </summary>
    public static WatcherActivityEntry FindingRaised(WatcherCase watcherCase)
        => new(
            At: watcherCase.LastSeenAt,
            Kind: KindFor(watcherCase.State),
            Topic: WatcherBusTopics.FindingRaised,
            Summary: watcherCase.Summary,
            Reasoning: Reasoning(watcherCase),
            Project: watcherCase.Project);

    /// <summary>
    /// A proposal waiting for an operator answer. This is the operator's entry
    /// point into review mode and stays pending until an attributable decision
    /// lands.
    /// </summary>
    public static WatcherActivityEntry DecisionRequired(WatcherCase watcherCase, WatcherProposal proposal)
        => new(
            At: proposal.CreatedAt,
            Kind: OrchestratorLogKinds.Decision,
            Topic: WatcherBusTopics.DecisionRequired,
            Summary: proposal.IsComment
                ? $"Watcher proposes a note on {proposal.CommentOnCard}: {proposal.CardDraft.Title}"
                : $"Watcher proposes a ticket: {proposal.CardDraft.Title}",
            Reasoning: string.Join(
                " ",
                [
                    Reasoning(watcherCase),
                    $"Recommended route {proposal.Recommendation.Model}/{proposal.Recommendation.ThinkingLevel}.",
                    "Approve, edit, merge, or reject. The proposal does not enter Ready by itself.",
                ]),
            Project: watcherCase.Project);

    /// <summary>A case that reached a terminal. Settled history renders quietly.</summary>
    public static WatcherActivityEntry Resolved(WatcherCase watcherCase)
        => new(
            At: watcherCase.LastSeenAt,
            Kind: OrchestratorLogKinds.Observation,
            Topic: WatcherBusTopics.AnalysisComplete,
            Summary: $"Watcher case resolved: {watcherCase.Summary}",
            Reasoning: $"Terminal reason {watcherCase.TerminalReason ?? "unspecified"}. {Reasoning(watcherCase)}",
            Project: watcherCase.Project);

    private static string Reasoning(WatcherCase watcherCase)
        => $"Case {watcherCase.CaseId}, class {watcherCase.DetectorClass.ToString().ToLowerInvariant()}, "
           + $"fingerprint {watcherCase.Fingerprint}, {watcherCase.Occurrences} occurrences across "
           + $"{watcherCase.SweepCount} sweeps, evidence digest {watcherCase.EvidencePackDigest}.";
}
