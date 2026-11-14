using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Pipeline;

/// <summary>One pending generation an intent claims for its successor.</summary>
public sealed record BatchGateIntentMember(string TaskKey, string RunAttempt, string ReviewAttemptId);

/// <summary>
/// A batch a replacement retires: the terminal state it writes, the phase
/// record version it was decided on, and whether the retired batch's waiting
/// flight requests a successor tick when it exits.
/// </summary>
public sealed record BatchGateIntentRetirement(BatchGateState State, long ExpectedVersion,
    bool RetiredByOwnFlight = false, bool RequestSuccessorTick = false);

/// <summary>
/// AGT-3568: one durable formation or replacement decision of the planner.
/// It is written before the successor manifest closes; the successor's first
/// phase record (AGT-3563) is its commit point, and predecessors retire only
/// after it. <paramref name="Route"/> names the deciding route for the audit.
/// </summary>
public sealed record BatchGateIntent(
    string IntentId, BatchIntentKind Kind, string Route, DateTimeOffset CreatedAtUtc,
    BatchGateManifest Successor, IReadOnlyList<BatchGateIntentMember> Members,
    IReadOnlyList<BatchGateIntentRetirement> Retiring);

/// <summary>How an intent ended: <c>applied</c>, <c>discarded</c> or <c>rolled-back</c>.</summary>
public sealed record BatchGateIntentOutcome(string IntentId, string Outcome, string? Reason, DateTimeOffset AtUtc);

/// <summary>A durable request to tick once the flight of <paramref name="BatchId"/> exits.</summary>
public sealed record BatchGateSuccessorTick(string Project, string BatchId, string Reason, DateTimeOffset AtUtc);

public sealed partial class BatchGateStore
{
    public const string IntentApplied = "applied";
    public const string IntentDiscarded = "discarded";
    public const string IntentRolledBack = "rolled-back";

    private string PlannerDirectory(string project) => Path.Combine(_root, "planner", SafeName(project));

    private static string IntentPath(string directory, string intentId)
        => Path.Combine(directory, "intents", SafeName(intentId) + ".json");

    private static string IntentOutcomePath(string directory, string intentId)
        => Path.Combine(directory, "intents", SafeName(intentId) + ".outcome.json");

    /// <summary>
    /// Runs <paramref name="action"/> as the only planner of the project
    /// across processes. A planner decides, writes and applies its intent
    /// under this lock, so another planner never sees a live intent half
    /// applied; an open intent it finds belongs to a host that died.
    /// </summary>
    public T WithPlannerLock<T>(string project, Func<T> action)
    {
        using var guard = ExclusiveGuard(PlannerDirectory(project), "planner.lock");
        return action();
    }

    /// <summary>
    /// The commit check of <paramref name="intent"/> against every pending
    /// generation a recoverable batch or an open intent of the project already
    /// owns. Call it under <see cref="WithPlannerLock{T}"/>.
    /// </summary>
    public BatchIntentDecision DecideIntent(BatchGateIntent intent, Func<string, bool> assembling)
    {
        var project = intent.Successor.Scope.Project;
        var owners = ListRecoverableManifests()
            .Where(manifest => string.Equals(manifest.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .SelectMany(manifest => manifest.Members.Select(member => new BatchGenerationOwner(
                new BatchPendingGeneration(member.TaskKey, member.RunAttempt, null), manifest.BatchId)))
            .Concat(ListOpenIntents(project).Where(open => open.IntentId != intent.IntentId)
                .SelectMany(open => open.Members.Select(member => new BatchGenerationOwner(
                    new BatchPendingGeneration(member.TaskKey, member.RunAttempt, member.ReviewAttemptId),
                    open.Successor.BatchId))))
            .ToArray();
        return AgentStudio.Runner.MonolithTransitionAdapter.BatchIntent(new BatchIntentFacts(intent.Kind,
            intent.Successor.BatchId,
            intent.Members.Select(member => new BatchPendingGeneration(
                member.TaskKey, member.RunAttempt, member.ReviewAttemptId)).ToArray(),
            RetiringFacts(intent, assembling), owners));
    }

    public IReadOnlyList<BatchRetiringFact> RetiringFacts(BatchGateIntent intent, Func<string, bool> assembling)
        => intent.Retiring.Select(retiring => new BatchRetiringFact(retiring.State.BatchId,
            PhaseOrNull(retiring.State.BatchId), assembling(retiring.State.BatchId),
            retiring.RetiredByOwnFlight)).ToArray();

    /// <summary>
    /// Writes the intent with the phase record version of each predecessor it
    /// retires. Call it under <see cref="WithPlannerLock{T}"/> after an
    /// accepted <see cref="DecideIntent"/>.
    /// </summary>
    public BatchGateIntent OpenIntent(BatchGateIntent intent)
    {
        var opened = intent with
        {
            Retiring = intent.Retiring.Select(retiring => retiring with
            {
                ExpectedVersion = PhaseVersion(retiring.State.BatchId),
            }).ToArray(),
        };
        var directory = PlannerDirectory(intent.Successor.Scope.Project);
        Directory.CreateDirectory(Path.Combine(directory, "intents"));
        WriteOnce(IntentPath(directory, intent.IntentId), opened);
        return opened;
    }

    public void CloseIntent(BatchGateIntent intent, string outcome, string? reason, DateTimeOffset nowUtc)
    {
        var path = IntentOutcomePath(PlannerDirectory(intent.Successor.Scope.Project), intent.IntentId);
        if (!File.Exists(path)) WriteOnce(path, new BatchGateIntentOutcome(intent.IntentId, outcome, reason, nowUtc));
    }

    public BatchGateIntentOutcome? ReadIntentOutcome(BatchGateIntent intent)
    {
        var path = IntentOutcomePath(PlannerDirectory(intent.Successor.Scope.Project), intent.IntentId);
        return File.Exists(path) ? Read<BatchGateIntentOutcome>(path) : null;
    }

    /// <summary>Intents without an outcome, oldest first; <paramref name="project"/> null lists every project.</summary>
    public IReadOnlyList<BatchGateIntent> ListOpenIntents(string? project = null)
    {
        var root = project is null ? Path.Combine(_root, "planner") : PlannerDirectory(project);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "intents"
                && !path.EndsWith(".outcome.json", StringComparison.Ordinal)
                && !File.Exists(path[..^".json".Length] + ".outcome.json"))
            .Select(Read<BatchGateIntent>)
            .OrderBy(intent => intent.CreatedAtUtc).ThenBy(intent => intent.IntentId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool HasManifest(string batchId) => File.Exists(Path.Combine(BatchDirectory(batchId), "manifest.json"));

    /// <summary>The phase of a batch, null while its manifest has no state.</summary>
    public BatchPhase? PhaseOrNull(string batchId)
        => HasState(batchId) ? LatestState(batchId).Phase : null;

    /// <summary>The version the next compare-and-swap of the batch phase record compares with.</summary>
    public long PhaseVersion(string batchId)
        => Math.Max(ReadPhaseRecord(batchId)?.Version ?? 0, HighestHistoryNumber(BatchDirectory(batchId)));

    /// <summary>
    /// Records that the exit of the flight of <paramref name="batchId"/> runs
    /// a planning tick. The request outlives the process; a restarted host
    /// serves it with its first tick.
    /// </summary>
    public void RequestSuccessorTick(string project, string batchId, string reason, DateTimeOffset nowUtc)
    {
        var folder = Path.Combine(PlannerDirectory(project), "successor-ticks");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(batchId) + ".json");
        if (!File.Exists(path))
            try { WriteOnce(path, new BatchGateSuccessorTick(project, batchId, reason, nowUtc)); }
            catch (IOException ex) when (File.Exists(path))
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "A concurrent request asked for the same successor tick");
            }
    }

    /// <summary>True for exactly one caller once the successor tick of <paramref name="batchId"/> was requested.</summary>
    public bool TryConsumeSuccessorTick(string project, string batchId)
    {
        var path = Path.Combine(PlannerDirectory(project), "successor-ticks", SafeName(batchId) + ".json");
        if (!File.Exists(path)) return false;
        var claimed = path + "." + Guid.NewGuid().ToString("N") + ".consumed";
        try { File.Move(path, claimed); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "Another host consumed the successor tick first");
            return false;
        }
        File.Delete(claimed);
        return true;
    }

    public IReadOnlyList<BatchGateSuccessorTick> ListSuccessorTicks()
    {
        var root = Path.Combine(_root, "planner");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "successor-ticks")
            .Select(Read<BatchGateSuccessorTick>).ToArray();
    }
}
