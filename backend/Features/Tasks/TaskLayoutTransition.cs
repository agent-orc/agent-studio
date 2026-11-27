using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Lane change for the flat storage layout (F45 restscope) expressed as a pure
/// metadata + index mutation: it rewrites <c>task.json.state</c> and moves the
/// task's location between state buckets in <c>index/by-state.json</c>, with NO
/// <see cref="Directory.Move(string, string)"/>. The task's physical location
/// (<c>jobs/&lt;bucket&gt;/&lt;key&gt;</c>) is invariant under a lane change.
///
/// <para>
/// That invariance is the point of the flat layout and the acceptance
/// criterion "Lane-Wechsel = reine Metadata-/Index-Mutation, kein FS-Move": it
/// removes the cross-lane <c>Directory.Move</c> that produced the 409 conflicts
/// and zombie folders under the old lane-folder layout. <c>state</c> in
/// <c>task.json</c> is the authority; <c>by-state.json</c> is a derived cache, so
/// a crash between the field write and the index write self-heals on the next
/// <see cref="TaskLayoutIndex.Rebuild"/>.
/// </para>
///
/// <para>
/// Because it never constructs a lane-folder path and never calls
/// <c>Directory.Move</c> / <c>Directory.Delete</c> (the index write goes through
/// <c>File.Move</c>), it stays outside the TaskFolderAccessIsolation whitelist.
/// This type is pure apart from filesystem effects, so it is unit-tested
/// directly against a temp directory. Production wiring (deferred to the
/// supervised cutover behind the EnableNewLayout flag) calls it from
/// <c>TaskTransitionService.MoveAsync</c> in place of the lane-folder
/// <c>Directory.Move</c>.
/// </para>
/// </summary>
internal static class TaskLayoutTransition
{
    /// <summary>
    /// Moves the task identified by <paramref name="taskKey"/> into
    /// <paramref name="newState"/> by rewriting <c>task.json.state</c> and the
    /// <c>by-state</c> index. Returns a result describing the change (or a
    /// no-op / not-found outcome); the physical folder is never moved.
    /// </summary>
    /// <param name="projectRoot">Per-project root holding <c>jobs/</c> +
    /// <c>index/</c>.</param>
    /// <param name="taskKey">Stable task key (e.g. <c>ASS-617</c>), the
    /// <c>by-key</c> index key and the task's folder name.</param>
    /// <param name="newState">Target lane; must be a member of
    /// <see cref="TaskStates.All"/>.</param>
    /// <param name="logger">Structured-log sink.</param>
    public static TransitionResult ChangeState(
        string projectRoot, string taskKey, string newState, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(taskKey))
            return TransitionResult.NotFound(taskKey, newState);
        if (Array.IndexOf(TaskStates.All, newState) < 0)
            throw new ArgumentException($"Unknown task state '{newState}'.", nameof(newState));

        var byKey = TaskLayoutIndex.ReadByKey(projectRoot);
        if (!byKey.TryGetValue(taskKey, out var location) || string.IsNullOrWhiteSpace(location))
            return TransitionResult.NotFound(taskKey, newState);

        var jobDir = Path.Combine(
            TaskStorageLayout.JobsRoot(projectRoot),
            location.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(Path.Combine(jobDir, "task.json")))
            return TransitionResult.NotFound(taskKey, newState);

        var fromState = ReadState(jobDir);
        if (string.Equals(fromState, newState, StringComparison.Ordinal))
            return new TransitionResult(taskKey, location, fromState, newState, Changed: false);

        // 1. Authority: the lane lives in task.json.state. A failed write must
        // stop before the derived index or any caller's lane-change effects.
        try
        {
            TaskJsonFile.UpdateFieldOrThrow(jobDir, "state", newState);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Failed to persist authoritative state for task '{taskKey}'; the lane change was not applied.", ex);
        }

        // 2. Derived cache: move the location between state buckets. by-key
        // is unchanged because the physical location does not move. Pruning
        // the emptied state keeps the live index shape identical to a Rebuild.
        try
        {
            var byState = TaskLayoutIndex.ReadByState(projectRoot);
            foreach (var list in byState.Values)
                list.RemoveAll(l => string.Equals(l, location, StringComparison.Ordinal));
            foreach (var emptied in byState.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
                byState.Remove(emptied);
            if (!byState.TryGetValue(newState, out var dest))
                byState[newState] = dest = new List<string>();
            dest.Add(location);
            TaskLayoutIndex.WriteByState(projectRoot, byState, logger);
        }
        catch (Exception ex)
        {
            // Keep the successful authority write: the index is rebuildable.
            throw new IOException(
                $"The authoritative state was saved for task '{taskKey}', but the derived index update failed; rebuild the task index.", ex);
        }

        logger.LogInformation(
            "task-transition key={Key} from={From} to={To} location={Location} (metadata-only, no folder move)",
            taskKey, fromState, newState, location);

        return new TransitionResult(taskKey, location, fromState, newState, Changed: true);
    }

    private static string? ReadState(string jobDir)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(jobDir, "task.json")), TaskJsonFile.ReadOpts);
        return doc != null && doc.TryGetValue("state", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    public readonly record struct TransitionResult(
        string TaskKey, string? Location, string? FromState, string ToState, bool Changed)
    {
        public static TransitionResult NotFound(string taskKey, string toState) =>
            new(taskKey, null, null, toState, false);
    }
}
