namespace AgentStudio.Tasks;

/// <summary>
/// Monotonic counter over every file event inside a watched task folder,
/// including the ones <see cref="TaskWatcherService.ShouldNotifyIndexChange"/>
/// deliberately drops.
///
/// <para>AGT-2703 needs a validator for the board reads, and
/// <see cref="TaskIndexCache.Generation"/> alone is not one. That generation
/// only advances for <c>task.json</c> semantics and task-folder structure,
/// which is exactly right for the task index but leaves out every generated
/// sidecar the board response also projects: the pipeline execution record,
/// the step prompt log, the spawn ledger, the planning-closure declaration, the
/// concept dossier, and the token receipts. All of them live inside a watch
/// path, so the raw <see cref="TaskWatcherService.OnPathChanged"/> stream sees
/// each write - this counter turns that stream into a single cheap number the
/// board ETag can fold in.</para>
///
/// <para>The counter is deliberately coarse. It does not care which file moved
/// or whether the change is even visible on the board; any write inside a watch
/// path advances it. Being conservative is the point: a spurious advance costs
/// one full response, while a missed one would serve a client a
/// <c>304 Not Modified</c> for a board that did move. The idle board this
/// optimisation targets produces no file events at all, so the counter stands
/// still exactly when it matters.</para>
/// </summary>
public sealed class TaskSidecarGeneration
{
    private long _generation;

    /// <summary>Current value. Never decreases; wraps only after 2^63 events.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Subscribes to the raw watcher stream. Called once during host start-up,
    /// next to the other watcher bindings in <c>Program.cs</c>; without it the
    /// counter exists but never moves, and sidecar writes would hide behind a
    /// validator that never changes.
    /// </summary>
    public void Attach(TaskWatcherService watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        watcher.OnPathChanged += _ => Advance();
    }

    /// <summary>
    /// Advances the counter. Public so mutation paths that write a sidecar
    /// without going through the filesystem watcher (and tests) can move it
    /// explicitly.
    /// </summary>
    public void Advance() => Interlocked.Increment(ref _generation);
}
