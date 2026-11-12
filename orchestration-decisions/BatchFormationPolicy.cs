namespace AgentStudio.OrchestrationDecisions;

/// <summary>A queued card as the planner sees it: its task key and changed paths, null when its diff is unavailable.</summary>
public sealed record BatchPlannerCard(string TaskKey, IReadOnlyList<string>? Paths);

/// <summary>
/// One formed batch of a scope and base that has not entered a gate.
/// <paramref name="MemberKeys"/> lists every member of its closed manifest;
/// <paramref name="QueuedSinceUtc"/> is its flight queue place (AGT-3710).
/// </summary>
public sealed record BatchRepackWaiting(
    string BatchId, IReadOnlyList<string> MemberKeys, DateTimeOffset ClosedAtUtc,
    DateTimeOffset QueuedSinceUtc);

/// <summary>
/// Facts of one repack pass over the formed batches of one scope and base.
/// <paramref name="PendingMembers"/> are the cards of waiting members whose
/// queued record still exists; <paramref name="Eligible"/> are the queued
/// cards that may join a waiting batch, in enqueue order.
/// </summary>
public sealed record BatchRepackFacts(
    IReadOnlyList<BatchRepackWaiting> Waiting, IReadOnlyList<BatchPlannerCard> PendingMembers,
    IReadOnlyList<BatchPlannerCard> Eligible, int MinimumSize, int MaximumSize, string Overlap);

public enum BatchRepackAction
{
    /// <summary>Nothing to change.</summary>
    None,

    /// <summary>Keep every batch; record why the batches outside the selection wait.</summary>
    Annotate,

    /// <summary>Close one successor with <see cref="BatchRepackDecision.SelectedKeys"/> that replaces <see cref="BatchRepackDecision.Retiring"/>.</summary>
    Replace,
}

/// <summary>
/// The repack plan of one scope and base. <paramref name="Candidates"/> are
/// the cards the selection considered, waiting members first.
/// <paramref name="Annotations"/> maps a formed batch that keeps waiting to
/// its visible reason. A replacement keeps the queue place of the oldest
/// batch it retires (<paramref name="QueuedSinceUtc"/>).
/// </summary>
public sealed record BatchRepackDecision(
    BatchRepackAction Action, IReadOnlyList<string> Retiring, IReadOnlyList<string> SelectedKeys,
    IReadOnlyList<BatchPlannerCard> Candidates, IReadOnlyDictionary<string, string> Annotations,
    DateTimeOffset? QueuedSinceUtc);

public enum BatchIntentKind
{
    /// <summary>A new batch closes over queued members.</summary>
    Formation,

    /// <summary>A successor closes and retires the formed batches it replaces.</summary>
    Replacement,
}

/// <summary>One pending generation: the queued review attempt of one task's run.</summary>
public sealed record BatchPendingGeneration(string TaskKey, string RunAttempt, string? ReviewAttemptId);

/// <summary>
/// A pending generation a durable batch or an open intent already owns.
/// <paramref name="OwnerId"/> is a batch ID, or the successor batch ID of an
/// open intent.
/// </summary>
public sealed record BatchGenerationOwner(BatchPendingGeneration Generation, string OwnerId);

/// <summary>
/// A batch an intent retires. <paramref name="Phase"/> is its phase record,
/// null when it has none; <paramref name="Assembling"/> is true while a flight
/// of this host assembles it. <paramref name="RetiredByOwnFlight"/> marks the
/// flight that holds the batch's coordinator lease and replaces its own batch
/// (a re-formation on a moved base, a documentation successor); such a batch
/// may be past the formed phase.
/// </summary>
public sealed record BatchRetiringFact(string BatchId, BatchPhase? Phase, bool Assembling,
    bool RetiredByOwnFlight = false);

/// <summary>
/// Facts of one intent the planner wants to commit. <paramref name="Owners"/>
/// are the generations every non-terminal batch and every open intent of the
/// project owns.
/// </summary>
public sealed record BatchIntentFacts(
    BatchIntentKind Kind, string SuccessorBatchId, IReadOnlyList<BatchPendingGeneration> Members,
    IReadOnlyList<BatchRetiringFact> Retiring, IReadOnlyList<BatchGenerationOwner> Owners);

/// <summary>An accepted intent, or the refusal and the generations another owner holds.</summary>
public sealed record BatchIntentDecision(
    bool Accepted, string? Refusal, IReadOnlyList<BatchPendingGeneration> Conflicts);

/// <summary>
/// Facts of an open intent at the start of a tick. <paramref name="SuccessorClosed"/>
/// is true once the successor manifest exists; <paramref name="SuccessorPhase"/>
/// is its phase, null without a phase record.
/// </summary>
public sealed record BatchIntentRecoveryFacts(
    bool SuccessorClosed, BatchPhase? SuccessorPhase, IReadOnlyList<BatchRetiringFact> Retiring);

public enum BatchIntentStep
{
    /// <summary>The successor is not durable: nothing was retired, close the intent as discarded.</summary>
    Discard,

    /// <summary>The successor is durable: claim its members, retire the formed predecessors, close the intent as applied.</summary>
    ClaimAndRetire,

    /// <summary>A predecessor left the formed phase first: retire the successor, return its members, close the intent as rolled back.</summary>
    RollBack,
}

/// <summary>
/// AGT-3568: the formation planner's decisions (step 02 of the AGT-3552
/// split). The pilot collects facts; this rule chooses repack membership,
/// commits each formation or replacement as one durable intent that owns its
/// pending generations, and decides how an interrupted intent completes.
/// A replacement retires its predecessors only after its successor is durable.
/// </summary>
public static class BatchFormationPolicy
{
    public const string GenerationOwned = "generation-owned";
    public const string PredecessorNotFormed = "predecessor-not-formed";
    public const string PredecessorAssembling = "predecessor-assembling";
    public const string PredecessorSettled = "predecessor-settled";
    public const string NoMembers = "no-members";

    /// <summary>
    /// The largest set of cards, in the given order, of at most
    /// <paramref name="maximumSize"/> cards. Under <c>separate</c> overlap no
    /// two chosen cards share a changed file.
    /// </summary>
    public static IReadOnlyList<BatchPlannerCard> LargestCompatible(IReadOnlyList<BatchPlannerCard> ordered,
        int maximumSize, string overlap)
    {
        if (overlap != "separate") return ordered.Take(maximumSize).ToArray();
        var best = new List<BatchPlannerCard>();
        var chosen = new List<BatchPlannerCard>();
        Search(0);
        return best;

        void Search(int index)
        {
            if (chosen.Count > best.Count) best = chosen.ToList();
            if (best.Count == maximumSize || index == ordered.Count
                || chosen.Count + Math.Min(maximumSize - chosen.Count,
                    ordered.Count - index) <= best.Count) return;
            var card = ordered[index];
            if (chosen.All(other => ConflictingFiles(card, other).Count == 0))
            {
                chosen.Add(card);
                Search(index + 1);
                chosen.RemoveAt(chosen.Count - 1);
            }
            Search(index + 1);
        }
    }

    public static IReadOnlyList<string> ConflictingFiles(BatchPlannerCard left, BatchPlannerCard right)
    {
        // An unavailable diff must not silently qualify for separate overlap.
        if (left.Paths is null || right.Paths is null) return ["changed files unavailable"];
        return left.Paths.Intersect(right.Paths, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Repack of the formed batches of one scope and base. The waiting batches
    /// are read largest first, then oldest; their pending members come first,
    /// then the eligible queued cards. When the largest compatible selection is
    /// already one formed batch, the others are only annotated. Otherwise one
    /// successor replaces every waiting batch that loses a member to it.
    /// </summary>
    public static BatchRepackDecision Repack(BatchRepackFacts facts)
    {
        var none = new BatchRepackDecision(BatchRepackAction.None, [], [], [],
            new Dictionary<string, string>(StringComparer.Ordinal), null);
        var waiting = facts.Waiting.OrderByDescending(batch => batch.MemberKeys.Count)
            .ThenBy(batch => batch.ClosedAtUtc)
            .ThenBy(batch => batch.BatchId, StringComparer.Ordinal).ToArray();
        if (waiting.Length == 0 || waiting.Length == 1 && facts.Eligible.Count == 0) return none;
        var owned = waiting.SelectMany(batch => batch.MemberKeys).ToHashSet(StringComparer.Ordinal);
        var pending = facts.PendingMembers.DistinctBy(card => card.TaskKey, StringComparer.Ordinal)
            .ToDictionary(card => card.TaskKey, StringComparer.Ordinal);
        var candidates = waiting.SelectMany(batch => batch.MemberKeys)
            .Where(pending.ContainsKey).Select(key => pending[key])
            .Concat(facts.Eligible.Where(card => !owned.Contains(card.TaskKey)))
            .DistinctBy(card => card.TaskKey, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return none;
        var selected = LargestCompatible(candidates, facts.MaximumSize, facts.Overlap);
        var selectedKeys = selected.Select(card => card.TaskKey).ToHashSet(StringComparer.Ordinal);
        var moving = waiting.Where(batch => batch.MemberKeys.Any(selectedKeys.Contains)).ToArray();
        var alreadyFormed = moving.Length == 1
            && facts.Eligible.All(card => !selectedKeys.Contains(card.TaskKey))
            && moving[0].MemberKeys.ToHashSet(StringComparer.Ordinal).SetEquals(selectedKeys);
        if (alreadyFormed)
            return none with
            {
                Action = BatchRepackAction.Annotate, Candidates = candidates,
                Annotations = Annotations(waiting.Where(batch => batch.BatchId != moving[0].BatchId),
                    pending, selected, facts),
            };
        if (selected.Count < facts.MinimumSize || moving.Length == 0) return none;
        return new BatchRepackDecision(BatchRepackAction.Replace,
            moving.Select(batch => batch.BatchId).ToArray(),
            selected.Select(card => card.TaskKey).ToArray(), candidates,
            Annotations(waiting.Except(moving), pending, selected, facts),
            moving.Min(batch => batch.QueuedSinceUtc));
    }

    /// <summary>
    /// Why a candidate stayed out of a repack successor: a separate-overlap
    /// file conflict with the selection, or the maximum size.
    /// </summary>
    public static (bool Overlap, string Detail) RepackExclusion(BatchPlannerCard card,
        IReadOnlyList<BatchPlannerCard> selected, int maximumSize, string overlap)
    {
        var conflicts = selected.SelectMany(member => ConflictingFiles(card, member))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return conflicts.Length == 0 || overlap != "separate"
            ? (false, $"maximumSize {maximumSize}")
            : (true, "overlap separate: " + string.Join(", ", conflicts));
    }

    /// <summary>
    /// Commit check of one intent. Each pending generation has exactly one
    /// owner: a generation that a batch or an open intent other than the
    /// retired predecessors holds refuses the intent. A replacement retires
    /// only formed batches no flight assembles.
    /// </summary>
    public static BatchIntentDecision Decide(BatchIntentFacts facts)
    {
        if (facts.Members.Count == 0) return Refuse(NoMembers, []);
        if (facts.Retiring.Any(batch => batch.RetiredByOwnFlight
                && (batch.Phase is null or BatchPhase.Unknown || BatchPhaseTransitionPolicy.IsTerminal(batch.Phase.Value))))
            return Refuse(PredecessorSettled, []);
        if (facts.Retiring.Any(batch => !batch.RetiredByOwnFlight && batch.Phase != BatchPhase.Formed))
            return Refuse(PredecessorNotFormed, []);
        if (facts.Retiring.Any(batch => !batch.RetiredByOwnFlight && batch.Assembling))
            return Refuse(PredecessorAssembling, []);
        var retiring = facts.Retiring.Select(batch => batch.BatchId).ToHashSet(StringComparer.Ordinal);
        var conflicts = facts.Members.Where(member => facts.Owners.Any(owner =>
                owner.OwnerId != facts.SuccessorBatchId && !retiring.Contains(owner.OwnerId)
                && SameGeneration(owner.Generation, member)))
            .ToArray();
        return conflicts.Length > 0 ? Refuse(GenerationOwned, conflicts) : new(true, null, []);
    }

    /// <summary>
    /// The step that completes an intent a crash or a refusal left open. A
    /// successor that is not durable retired nothing, so the intent is
    /// discarded. A durable successor claims its members and retires its
    /// predecessors, unless a predecessor left the formed phase first: then the
    /// predecessor keeps its members and the successor is rolled back.
    /// </summary>
    public static BatchIntentStep Recover(BatchIntentRecoveryFacts facts)
    {
        if (!facts.SuccessorClosed || facts.SuccessorPhase is BatchPhase.Abandoned)
            return BatchIntentStep.Discard;
        var successorFormed = facts.SuccessorPhase is null or BatchPhase.Unknown or BatchPhase.Formed;
        if (successorFormed && facts.Retiring.Any(OvertookIntent))
            return BatchIntentStep.RollBack;
        return BatchIntentStep.ClaimAndRetire;
    }

    /// <summary>
    /// A predecessor that moved on before the intent retired it: a formed batch
    /// that started its flight, or the batch of a dead own flight that published.
    /// </summary>
    private static bool OvertookIntent(BatchRetiringFact batch)
        => batch.RetiredByOwnFlight
            ? batch.Phase == BatchPhase.Published
            : batch.Phase is not (null or BatchPhase.Formed or BatchPhase.Abandoned) || batch.Assembling;

    /// <summary>A predecessor may be retired only once its successor has a durable phase record.</summary>
    public static bool MayRetirePredecessors(BatchPhase? successorPhase)
        => successorPhase is { } phase && phase is not (BatchPhase.Unknown or BatchPhase.Abandoned);

    /// <summary>
    /// One task is in at most one batch at a time, so two generations of one
    /// task conflict as well; the newer review supersedes the older record.
    /// </summary>
    private static bool SameGeneration(BatchPendingGeneration left, BatchPendingGeneration right)
        => string.Equals(left.TaskKey, right.TaskKey, StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string> Annotations(IEnumerable<BatchRepackWaiting> batches,
        IReadOnlyDictionary<string, BatchPlannerCard> pending, IReadOnlyList<BatchPlannerCard> selected,
        BatchRepackFacts facts)
        => batches.ToDictionary(batch => batch.BatchId, batch =>
        {
            var conflicts = batch.MemberKeys.Where(pending.ContainsKey)
                .SelectMany(key => selected.SelectMany(chosen => ConflictingFiles(pending[key], chosen)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return facts.Overlap == "separate" && conflicts.Length > 0
                ? "new batch: overlap separate, files " + string.Join(", ", conflicts)
                : $"new batch: maximumSize {facts.MaximumSize}";
        }, StringComparer.Ordinal);

    private static BatchIntentDecision Refuse(string refusal, IReadOnlyList<BatchPendingGeneration> conflicts)
        => new(false, refusal, conflicts);
}
