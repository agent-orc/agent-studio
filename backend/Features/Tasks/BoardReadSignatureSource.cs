using System.Globalization;

using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>
/// Collects everything that can change the body of a board read
/// (<c>GET /api/tasks/</c>, <c>GET /api/tasks/grouped</c>) into one cache
/// token. <see cref="BoardReadValidator"/> turns that token into the entity
/// tag; this class owns the question the tag has to answer correctly:
/// <i>could this response differ from the one the client already holds?</i>
///
/// <para><b>Completeness is the whole contract.</b> A part that is missing here
/// does not merely cost performance - it serves a client a
/// <c>304 Not Modified</c> for a board that did move. So every input the two
/// endpoints read is represented, and where an input has no version of its own,
/// the value it produces is folded directly:</para>
/// <list type="bullet">
/// <item><description><b>Task index</b> - <c>TaskScannerService.SnapshotGeneration</c>.
/// Covers <c>task.json</c> semantics, lane membership, folder structure, and
/// everything the lane sort orders by.</description></item>
/// <item><description><b>Generated sidecars</b> -
/// <see cref="TaskSidecarGeneration"/>. The task index deliberately ignores
/// them, but the response projects them (pipeline execution record, step prompt
/// log, spawn ledger, planning closure, concept dossier, token
/// receipts).</description></item>
/// <item><description><b>Git-derived state</b> -
/// <see cref="TaskListGitProjectionCache.Generation"/> plus the published
/// freshness stamp, which the grouped body carries as
/// <c>gitStateAt</c>/<c>stale</c>.</description></item>
/// <item><description><b>Project settings</b> -
/// <see cref="ProjectSettingsService.Version"/>. Lane sort strategies reorder
/// every lane; pipeline and benchmark settings change what the live-status and
/// better-candidate projections say.</description></item>
/// <item><description><b>Review decision journals</b> - the per-project
/// <c>logs/decisions/{project}.jsonl</c> stamp. These live outside every watch
/// path, so no watcher event covers them.</description></item>
/// <item><description><b>In-memory runtime state</b> - the runtime fold below.
/// CLI executions, run-activity facts, auto-loop snapshots, summariser state,
/// runner badges, quota fallbacks, queue positions and execution locations
/// exist only in process memory and leave no file trace at all.</description></item>
/// <item><description><b>Request shape</b> - route, caller-visible task set and
/// query variant, so two clients with different project access or different
/// query parameters never share a tag.</description></item>
/// </list>
///
/// <para><b>Deliberate gaps, and why they are safe.</b> Two projected values
/// still read a clock rather than a fact. The better-candidate evidence age is
/// quantised to the UTC day at the source, so the UTC date is folded in and the
/// age advances exactly once a day. The execution-location freshness verdict
/// (a 75 s heartbeat window and a 3 min activity window, which decide
/// <c>connectionState</c> and the recovering state) cannot be derived from a
/// fact alone; a coarse tick is folded in while any card sits in the Progress
/// lane, which is the only lane those branches apply to. An idle board has no
/// Progress card and so does not pay that tick at all.</para>
///
/// <para><b>Upper bound on how long a tag holds.</b> The task index publishes a
/// new generation on every snapshot publish, including the safety-TTL rescan
/// that finds nothing changed (<c>TaskIndexCache:SafetyTtlSeconds</c>, default 30 s).
/// A validator therefore never outlives that window, which is the same bound
/// every other reader of the index already lives with: it caps the board poll
/// at one full response per TTL instead of one per poll, and it doubles as a
/// backstop should the filesystem watcher ever stop delivering events.</para>
/// </summary>
public sealed class BoardReadSignatureSource(
    TaskScannerService scanner,
    TaskSidecarGeneration sidecars,
    TaskListGitProjectionCache gitProjection,
    ProjectSettingsService projectSettings,
    AutoReviewPostProcessingQueue reviewQueue,
    IConfiguration configuration,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Resolution of the clock tick folded in while a Progress card is on the
    /// board. Comfortably below the 75 s heartbeat window that is the shortest
    /// time threshold any projected field uses, so a freshness verdict can
    /// never be more than one tick behind the truth.
    /// </summary>
    internal static readonly TimeSpan ProgressClockTick = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds the cache token for one board read. <paramref name="jobs"/> is the
    /// caller-visible task set the endpoint has already scanned and filtered -
    /// passing it in keeps the signature aligned with the exact set the response
    /// is built from, including the project-access filter.
    /// </summary>
    public string Compose(
        string route,
        string variant,
        IReadOnlyList<TaskInfo> jobs,
        GitProjectionFreshness freshness,
        CliRouter router,
        TaskRunnerService runners)
    {
        var anyProgress = false;
        for (var i = 0; i < jobs.Count && !anyProgress; i++)
            anyProgress = jobs[i].State == TaskStates.Progress;

        return BoardReadValidator.Compose(
            route,
            variant,
            Part("tasks", scanner.SnapshotGeneration),
            Part("sidecars", sidecars.Generation),
            Part("git", gitProjection.Generation),
            Part("gitStateAt", freshness.GitStateAt?.UtcDateTime.Ticks ?? -1),
            Part("gitStale", freshness.Stale ? 1 : 0),
            Part("settings", projectSettings.Version),
            Part("decisions", DecisionJournalFold(jobs)),
            Part("runtime", RuntimeFold(jobs, router, runners)),
            Part("day", DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).DayNumber),
            Part("tick", anyProgress ? ProgressTick() : -1),
            Part("count", jobs.Count));
    }

    private static string Part(string name, long value)
        => name + "=" + value.ToString(CultureInfo.InvariantCulture);

    private long ProgressTick()
        => _time.GetUtcNow().UtcDateTime.Ticks / ProgressClockTick.Ticks;

    /// <summary>
    /// Folds the append-only review decision journals the board reads for its
    /// orchestrator-verdict chips. One (length, write time) probe per project on
    /// the board, mirroring how the journal reader itself validates its cached
    /// index. A verdict normally also moves its card to another lane, but the
    /// journal is the field's actual source and lives outside every watch path,
    /// so it is probed rather than assumed.
    /// </summary>
    private long DecisionJournalFold(IReadOnlyList<TaskInfo> jobs)
    {
        var workspace = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hash = new HashCode();
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;
            if (!seen.Add(job.ProjectName)) continue;
            hash.Add(job.ProjectName);
            try
            {
                var file = new FileInfo(ReviewDecisionLog.DecisionsFile(workspace!, job.ProjectName));
                hash.Add(file.Exists ? file.Length : -1L);
                hash.Add(file.Exists ? file.LastWriteTimeUtc.Ticks : 0L);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable journal contributes nothing to the response
                // either - the reader swallows the same failures - so fold a
                // constant rather than failing the read.
                SilentCatch.Note(ex, "BoardReadSignatureSource: decision journal probe");
                hash.Add(0L);
            }
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Folds the in-memory runtime overlay the board projects onto each card.
    /// Nothing here touches disk: every value is an O(1) read out of a runner or
    /// CLI registry, so the fold stays cheap enough to run before the endpoint
    /// decides whether the response is even needed.
    ///
    /// <para>The lane gates mirror <c>TaskEndpointHelpers.WithRuntime</c>
    /// exactly. When a new runtime field is projected onto a card, it belongs
    /// here too - a field that is projected but not folded is a stale card
    /// behind a 304.</para>
    /// </summary>
    private long RuntimeFold(IReadOnlyList<TaskInfo> jobs, CliRouter router, TaskRunnerService runners)
    {
        var hash = new HashCode();

        // Runner queues decide the "position n in queue" chip on Ready cards and
        // the runner mode pill, and they move without any file changing.
        foreach (var (name, status) in runners.GetStatus().Projects.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(name);
            hash.Add(status.Mode);
            hash.Add(status.ActiveJobId);
            hash.Add(status.QueuedJobIds.Count);
            foreach (var queued in status.QueuedJobIds) hash.Add(queued);
        }

        foreach (var job in jobs)
        {
            hash.Add(job.TaskKey);
            hash.Add(job.State);

            var progress = job.State == TaskStates.Progress;
            var execution = progress ? router.Get(job.CliType).GetExecution(job.TaskKey) : null;
            hash.Add(execution);
            hash.Add(runners.GetStuckLoopStateForJob(job.Id, job.ProjectName));
            hash.Add(runners.SummaryService.GetState(job.TaskKey));

            TaskRunActivity? activity = null;
            if (progress)
            {
                // The outcome issue the endpoint passes only decorates the
                // projection with a message that comes from task.json, which the
                // task index generation already covers; the kind - the part the
                // execution location reads back - does not depend on it.
                activity = TaskRunActivityClassifier.Classify(
                    runners.GetRunActivityForJob(job.Id, job.ProjectName), execution, outcomeIssue: null);
                hash.Add(activity);
                hash.Add(runners.ResolveRunnerBadge(job.TaskKey));
                hash.Add(runners.GetQuotaFallbackForJob(job.Id, job.ProjectName));
            }
            if (job.State == TaskStates.AutoReview)
            {
                hash.Add(reviewQueue.PositionOf(job.ProjectName, job.Id));
            }

            // Folded as the projected value rather than as its inputs: the run
            // lease registry and client identity store have no version of their
            // own, and the freshness verdict is derived rather than stored.
            hash.Add(runners.ResolveExecutionLocation(job, execution, activity));
        }

        return hash.ToHashCode();
    }
}
