namespace AgentStudio.Watcher;

/// <summary>The card a proposal created, as the task API reported it back.</summary>
public sealed record WatcherCardCreation(string TaskId, string TaskKey);

/// <summary>
/// The only boundary through which the Watcher touches task state. Everything
/// it is allowed to do in W1 and W2 is here: read which cards are open, create
/// one card in the proposal state, append one comment, and - once an operator
/// has approved - move that same card to Ready.
/// </summary>
/// <remarks>
/// Keeping the surface this narrow is what makes "no task or Git mutation
/// outside proposal creation and comments" checkable rather than aspirational.
/// The architecture test asserts the sweep reaches task state only through this
/// interface.
/// </remarks>
public interface IWatcherTaskGateway
{
    /// <summary>
    /// Of the given keys, the ones that exist and are not in a terminal lane.
    /// Used both to name overlapping work in a draft and to decide whether a
    /// fingerprint gets a comment instead of a new card.
    /// </summary>
    IReadOnlyList<string> OpenCards(string? project, IReadOnlyCollection<string> taskKeys);

    /// <summary>
    /// Create the drafted card in the proposal state: lane
    /// <c>1-preparation</c>, tag <c>watcher-proposal</c>, references to the
    /// case and the overlapping cards. Returns null when the project is unknown
    /// or the task API refused.
    /// </summary>
    WatcherCardCreation? CreateProposalCard(
        string project,
        WatcherCardDraft draft,
        WatcherModelRecommendation recommendation,
        string caseId);

    /// <summary>
    /// Append one Watcher comment to an existing card. This is the §10.3 rule:
    /// a fingerprint that already has an open card gets a comment, not a second
    /// card.
    /// </summary>
    bool AppendComment(string project, string taskKey, string caseId, string summary, string body);

    /// <summary>
    /// Promote an approved proposal card: drop the proposal tag, apply the
    /// recommended model, and move it to Ready. Only ever called from an
    /// attributable operator decision.
    /// </summary>
    Task<bool> PromoteToReadyAsync(
        string project,
        string taskId,
        WatcherModelRecommendation recommendation,
        string decidedBy,
        CancellationToken ct);
}
