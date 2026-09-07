using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Pipeline;

/// <summary>
/// Commits the job-folder evidence in the workspace repository at an
/// orchestrator run boundary. This is deliberately separate from
/// <see cref="GitService"/>: source-code commits happen in the watched project
/// repository, while these commits snapshot task artifacts in TaskRepository.
/// </summary>
public sealed class WorkspaceArtifactCommitService
{
    private const string CommitterName = "agent-orchestrator";
    private const string CommitterEmail = "agent-orchestrator@local";

    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceArtifactCommitService> _logger;
    private readonly WorkspaceArtifactPushQueue? _pushQueue;
    private readonly int _indexLockRetryAttempts;
    private readonly int _indexLockRetryBackoffMs;
    private readonly long _maximumFileBytes;

    // Per-repo serialization gate. The punctual job-folder commits and the
    // debounced Transition-Committer's evidence batches both mutate the same
    // workspace repo's index; without a shared lock two `git commit` calls can
    // collide on `.git/index.lock`. Keyed by resolved git root so distinct
    // repos never contend. Static so the single DI singleton and any test
    // instance pointed at the same repo share the gate.
    private static readonly ConcurrentDictionary<string, object> RepoGates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static object RepositoryGate(string gitRoot) =>
        RepoGates.GetOrAdd(gitRoot, _ => new object());

    public WorkspaceArtifactCommitService(
        IConfiguration configuration,
        ILogger<WorkspaceArtifactCommitService> logger,
        WorkspaceArtifactPushQueue? pushQueue = null)
    {
        _configuration = configuration;
        _logger = logger;
        _pushQueue = pushQueue;
        _indexLockRetryAttempts = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceEvidence:IndexLockRetryAttempts") ?? 5, 1, 50);
        _indexLockRetryBackoffMs = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceEvidence:IndexLockRetryBackoffMs") ?? 100, 0, 5000);
        _maximumFileBytes = Math.Clamp(
            configuration.GetValue<long?>("WorkspaceArtifacts:MaximumFileBytes") ?? 50L * 1024 * 1024,
            1L * 1024 * 1024,
            95L * 1024 * 1024);
    }

    public WorkspaceArtifactCommitResult TryCommitRunBoundary(
        string? workspaceRoot,
        string jobId,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath,
        ReviewDecisionKind verdict)
        => TryCommitJobFolder(
            workspaceRoot,
            jobId,
            beforeMoveFolderPath,
            afterMoveFolderPath,
            logLabel: $"verdict={NormalizeVerdict(verdict)}",
            failLabel: verdict.ToString(),
            planMessage: (gitRoot, pathspecs, afterFolder) =>
            {
                var runIndex = ResolveRunIndex(gitRoot, pathspecs, afterFolder);
                var steps = ResolveStepsTrailer(afterFolder);
                return new ArtifactCommitPlan(BuildCommitMessage(jobId, runIndex, verdict, steps), runIndex, steps);
            });

    /// <summary>
    /// Commits the job-folder evidence at an out-of-band completion boundary
    /// (<c>docs/concepts/out-of-band-task-completion.md</c> §3). Shares the
    /// add/diff/commit plumbing with <see cref="TryCommitRunBoundary"/>; only
    /// the commit message differs, recording the external source instead of an
    /// orchestrator verdict + run index.
    /// </summary>
    public WorkspaceArtifactCommitResult TryCommitExternalCompletion(
        string? workspaceRoot,
        string jobId,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath,
        string source)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim();
        return TryCommitJobFolder(
            workspaceRoot,
            jobId,
            beforeMoveFolderPath,
            afterMoveFolderPath,
            logLabel: $"external-completion source={normalizedSource}",
            failLabel: "external-completion",
            planMessage: (_, _, _) =>
                new ArtifactCommitPlan(BuildExternalCompletionMessage(jobId, normalizedSource), 0, null));
    }

    public WorkspaceArtifactCommitResult TryCommitArtifactUpload(
        string? workspaceRoot,
        string jobId,
        string jobFolderPath,
        IReadOnlyList<string> files)
        => TryCommitJobFolder(
            workspaceRoot,
            jobId,
            beforeMoveFolderPath: null,
            afterMoveFolderPath: jobFolderPath,
            logLabel: $"artifact-upload files={files.Count}",
            failLabel: "artifact-upload",
            planMessage: (_, _, _) =>
                new ArtifactCommitPlan(BuildArtifactUploadMessage(jobId, files), 0, null));

    /// <summary>
    /// Shared add/diff/commit core for the workspace evidence commits. The
    /// caller supplies the commit message (and the run-index/steps it wants
    /// echoed in the result) via <paramref name="planMessage"/>, which runs only
    /// after the tree is confirmed dirty, so the message builders never pay for
    /// a no-op commit.
    /// </summary>
    private WorkspaceArtifactCommitResult TryCommitJobFolder(
        string? workspaceRoot,
        string jobId,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath,
        string logLabel,
        string failLabel,
        Func<string, IReadOnlyList<string>, string, ArtifactCommitPlan> planMessage)
    {
        try
        {
            workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
                ? _configuration["TaskRepository"]
                : workspaceRoot;
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return WorkspaceArtifactCommitResult.Skipped("workspace-missing");

            var gitRoot = ResolveGitRoot(workspaceRoot);
            if (gitRoot == null)
                return WorkspaceArtifactCommitResult.Skipped("not-a-git-repo");

            var pathspecs = BuildPathspecs(gitRoot, beforeMoveFolderPath, afterMoveFolderPath);
            if (pathspecs.Count == 0)
                return WorkspaceArtifactCommitResult.Skipped("job-folder-outside-workspace");

            string? shortSha;
            ArtifactCommitPlan plan;
            // Serialize with the Transition-Committer's evidence batches (and
            // any concurrent job-folder commit) on this repo, with an
            // index.lock retry so a lost race with an external git process is
            // recovered rather than surfaced as a failure.
            lock (RepositoryGate(gitRoot))
            {
                var oversized = FindOversizedFiles(gitRoot, pathspecs);
                UnstageRefusedFiles(gitRoot, oversized);
                var refusedSpecs = BuildLiteralExcludePathspecs(oversized);
                var addArgs = new List<string> { "add", "-A", "--" };
                addArgs.AddRange(pathspecs);
                addArgs.AddRange(refusedSpecs);
                var add = RunGitRetryingIndexLock(gitRoot, addArgs);
                if (add.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);

                var changed = RunGit(gitRoot, ["diff", "--cached", "--quiet", "--", .. pathspecs, .. refusedSpecs]);
                if (changed.Code == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                if (changed.Code != 1)
                    return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

                var afterFolder = !string.IsNullOrWhiteSpace(afterMoveFolderPath)
                    ? afterMoveFolderPath!
                    : beforeMoveFolderPath ?? string.Empty;
                plan = planMessage(gitRoot, pathspecs, afterFolder);

                var commitArgs = new List<string>
                {
                    "-c", $"user.name={CommitterName}",
                    "-c", $"user.email={CommitterEmail}",
                    "commit", "-F", "-", "--"
                };
                commitArgs.AddRange(pathspecs);
                commitArgs.AddRange(refusedSpecs);
                var commit = RunGitRetryingIndexLock(gitRoot, commitArgs, plan.Message);
                if (commit.Code != 0)
                {
                    if (commit.ErrorText.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                        return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);
                }

                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }
            _logger.LogInformation(
                "workspace-artifact-commit jobId={JobId} {LogLabel} runIndex={RunIndex} sha={Sha} paths={Paths}",
                jobId, logLabel, plan.RunIndex, shortSha ?? "", string.Join(",", pathspecs));
            if (WorkspaceAutoPushEnabled() && _pushQueue != null)
            {
                var enqueued = _pushQueue.Enqueue(new WorkspaceArtifactPushRequest(gitRoot, jobId));
                if (!enqueued)
                    _logger.LogWarning("workspace-artifact-push enqueue failed jobId={JobId} repo={Repo}", jobId, gitRoot);
            }
            return WorkspaceArtifactCommitResult.Committed(shortSha, plan.RunIndex, plan.Steps ?? "none");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "workspace-artifact-commit failed for {JobId} ({Label})",
                jobId, failLabel);
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    internal static string BuildExternalCompletionMessage(string jobId, string source)
    {
        var normalizedJob = string.IsNullOrWhiteSpace(jobId) ? "job" : jobId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim();
        return
            $"chore(workspace): record external completion for {normalizedJob}\n\n" +
            $"Completed-Externally-By: {normalizedSource}\n";
    }

    private bool WorkspaceAutoPushEnabled() =>
        _configuration.GetValue<bool?>("WorkspaceArtifacts:AutoPushEnabled") ?? true;

    internal static string BuildArtifactUploadMessage(string jobId, IReadOnlyList<string> files)
    {
        var normalizedJob = string.IsNullOrWhiteSpace(jobId) ? "job" : jobId.Trim();
        var normalizedFiles = files.Count == 0
            ? "none"
            : string.Join(",", files.Select(f => string.IsNullOrWhiteSpace(f) ? "unknown" : f.Trim()));
        return
            $"chore(workspace): record uploaded artifacts for {normalizedJob}\n\n" +
            $"Artifact-Upload-Files: {normalizedFiles}\n";
    }

    /// <summary>Message plan produced once the tree is known dirty: the commit body plus the run-index/steps echoed in the result.</summary>
    private sealed record ArtifactCommitPlan(string Message, int RunIndex, string? Steps);

    internal static string BuildCommitMessage(
        string jobId,
        int runIndex,
        ReviewDecisionKind verdict,
        string steps)
    {
        var normalizedJob = string.IsNullOrWhiteSpace(jobId) ? "job" : jobId.Trim();
        var normalizedSteps = string.IsNullOrWhiteSpace(steps) ? "none" : steps.Trim();
        return
            $"chore(workspace): record run artifacts for {normalizedJob}\n\n" +
            $"Run-Index: {runIndex.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Verdict: {NormalizeVerdict(verdict)}\n" +
            $"Steps: {normalizedSteps}\n";
    }

    internal static string ResolveStepsTrailer(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return "none";
        var path = Path.Combine(jobFolderPath, "pipeline-execution.json");
        if (!File.Exists(path)) return "none";

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("steps", out var stepsEl)
                || stepsEl.ValueKind != JsonValueKind.Array)
            {
                return "none";
            }

            var steps = new List<string>();
            foreach (var step in stepsEl.EnumerateArray())
            {
                var id = GetString(step, "stepId");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var verdict = GetString(step, "verdict");
                var status = ResolveTerminalStatusToken(step);
                var value = string.IsNullOrWhiteSpace(verdict) ? status : verdict;
                if (string.IsNullOrWhiteSpace(value)) continue;
                steps.Add($"{id}={NormalizeToken(value)}");
            }

            return steps.Count == 0 ? "none" : string.Join(",", steps);
        }
        catch
        {
            return "unreadable";
        }
    }

    internal static int ResolveRunIndexFromSessionEvents(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return 0;
        var path = Path.Combine(jobFolderPath, "logs", "session-events.jsonl");
        if (!File.Exists(path)) return 0;

        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) count++;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "WorkspaceArtifactCommitService: Tolerate torn JSONL lines like TaskSessionLog does.");
                // Tolerate torn JSONL lines like TaskSessionLog does.
            }
        }
        return count;
    }

    internal static int ResolveRunIndexFromPipelineAttempt(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return 0;
        var path = Path.Combine(jobFolderPath, "pipeline-execution.json");
        if (!File.Exists(path)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("attempt", out var attemptEl)
                && attemptEl.ValueKind == JsonValueKind.Number
                && attemptEl.TryGetInt32(out var attempt)
                && attempt > 0)
            {
                return attempt;
            }

            if (doc.RootElement.TryGetProperty("previousAttempts", out var previousEl)
                && previousEl.ValueKind == JsonValueKind.Array)
            {
                return previousEl.GetArrayLength() + 1;
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private int ResolveRunIndex(string gitRoot, IReadOnlyList<string> pathspecs, string afterFolder)
    {
        var fromPipeline = ResolveRunIndexFromPipelineAttempt(afterFolder);
        if (fromPipeline > 0) return fromPipeline;

        var fromEvents = ResolveRunIndexFromSessionEvents(afterFolder);
        if (fromEvents > 0) return fromEvents;

        var logArgs = new List<string> { "log", "--format=%B%x00", "--" };
        logArgs.AddRange(pathspecs);
        var log = RunGit(gitRoot, logArgs);
        if (log.Code != 0 || string.IsNullOrWhiteSpace(log.Out)) return 1;

        var max = 0;
        foreach (var raw in log.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("Run-Index:", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(line["Run-Index:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    max = Math.Max(max, n);
            }
        }
        return max + 1;
    }

    /// <summary>
    /// Resolves the git top-level for a workspace/watch path, falling back to
    /// the configured <c>TaskRepository</c> when none is supplied. Used by the
    /// Transition-Committer to bucket enqueued transitions by repo without a
    /// second copy of the git-root plumbing.
    /// </summary>
    public string? ResolveWorkspaceGitRoot(string? workspaceRoot)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? _configuration["TaskRepository"]
            : workspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;
        return ResolveGitRoot(root);
    }

    /// <summary>
    /// Debounced evidence commit for the Transition-Committer: stages the data
    /// paths of the workspace repo touched by a batch of lane transitions
    /// (<paramref name="watchPaths"/>, each scoped to its relative folder under
    /// the git root) minus <paramref name="excludeGlobs"/>, and commits them
    /// with <paramref name="message"/>. Shares the committer identity, git
    /// plumbing, per-repo lock and index.lock retry with the punctual
    /// job-folder commits above — no parallel implementation. Push is left to
    /// the caller (the batcher enqueues onto the existing push queue when the
    /// <c>WorkspaceEvidence:Push</c> switch is on), so this method never
    /// touches the network. Never throws: every failure is returned as a
    /// <see cref="WorkspaceArtifactCommitResult.Failed"/> so the caller — and
    /// ultimately the transition that produced the evidence — is never broken.
    /// </summary>
    public WorkspaceArtifactCommitResult TryCommitEvidence(
        string gitRoot,
        IReadOnlyList<string> watchPaths,
        IReadOnlyList<string> excludeGlobs,
        string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gitRoot) || !Directory.Exists(gitRoot))
                return WorkspaceArtifactCommitResult.Skipped("workspace-missing");

            var pathspecs = new List<string>();
            foreach (var wp in watchPaths)
                AddPathspec(pathspecs, gitRoot, wp);
            pathspecs = pathspecs
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (pathspecs.Count == 0)
                return WorkspaceArtifactCommitResult.Skipped("no-data-paths");

            // Excludes are enforced in two places, and BOTH are required:
            //   1. `git reset` unstages them from the index (below). This alone
            //      handles untracked scratch, and keeps the index clean so the
            //      diff-check is precise.
            //   2. `:(exclude)` magic pathspecs on the `git commit` (here). This
            //      is the load-bearing half for already-TRACKED files: passing
            //      pathspecs to `git commit` triggers partial-commit (`--only`)
            //      semantics that record the WORKING-TREE content of every listed
            //      tracked path and disregard the index — so a reset-unstaged but
            //      tracked file (e.g. a project's ~1 MB `.orchestrator/` runtime
            //      churn) would otherwise still be committed. The exclude pathspec
            //      removes it from the partial commit's file set.
            // The excludes are deliberately NOT put on `git add`/`git diff`: a
            // no-slash exclude glob (e.g. `*.tmp`) makes `git add` stage nothing
            // at all (a git pathspec quirk), which would silently disable the
            // whole evidence commit. `git commit` does not share that quirk.
            var resetSpecs = NormalizeExcludeGlobs(excludeGlobs);
            var excludeCommitSpecs = BuildExcludePathspecs(excludeGlobs);

            string? shortSha;
            lock (RepositoryGate(gitRoot))
            {
                var oversized = FindOversizedFiles(gitRoot, pathspecs);
                UnstageRefusedFiles(gitRoot, oversized);
                var refusedSpecs = BuildLiteralExcludePathspecs(oversized);
                // Stage the data paths (git add already honours the workspace
                // repo's .gitignore), then unstage the exclude globs from the
                // index.
                var addArgs = new List<string> { "add", "-A", "--" };
                addArgs.AddRange(pathspecs);
                addArgs.AddRange(refusedSpecs);
                var add = RunGitRetryingIndexLock(gitRoot, addArgs);
                if (add.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);

                if (resetSpecs.Count > 0)
                {
                    var resetArgs = new List<string> { "reset", "-q", "--" };
                    resetArgs.AddRange(resetSpecs);
                    var reset = RunGit(gitRoot, resetArgs);
                    if (reset.Code != 0)
                        _logger.LogDebug("workspace-evidence exclude reset non-zero root={Root} error={Error}", gitRoot, reset.ErrorText);
                }

                var diffArgs = new List<string> { "diff", "--cached", "--quiet", "--" };
                diffArgs.AddRange(pathspecs);
                diffArgs.AddRange(refusedSpecs);
                var changed = RunGit(gitRoot, diffArgs);
                if (changed.Code == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                if (changed.Code != 1)
                    return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

                var commitArgs = new List<string>
                {
                    "-c", $"user.name={CommitterName}",
                    "-c", $"user.email={CommitterEmail}",
                    "commit", "-F", "-", "--"
                };
                commitArgs.AddRange(pathspecs);
                commitArgs.AddRange(excludeCommitSpecs);
                commitArgs.AddRange(refusedSpecs);
                var commit = RunGitRetryingIndexLock(gitRoot, commitArgs, message);
                if (commit.Code != 0)
                {
                    if (commit.ErrorText.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                        return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);
                }

                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }

            _logger.LogInformation(
                "workspace-evidence-commit gitRoot={Root} sha={Sha} paths={Paths}",
                gitRoot, shortSha ?? "", string.Join(",", pathspecs));
            return WorkspaceArtifactCommitResult.Committed(shortSha, 0, "evidence");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-evidence-commit failed for {Root}", gitRoot);
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    /// <summary>
    /// Hourly safety-net commit for tracked workspace files that changed outside
    /// a task transition. Runtime-only path families stay excluded. Any staged
    /// removals created while untracking those families are deliberately kept,
    /// so one sweep makes the runtime classification durable.
    /// </summary>
    public WorkspaceArtifactCommitResult TryCommitTrackedSweep(string? workspaceRoot)
    {
        try
        {
            var gitRoot = ResolveWorkspaceGitRoot(workspaceRoot);
            if (gitRoot == null)
                return WorkspaceArtifactCommitResult.Skipped("not-a-git-repo");

            string? shortSha;
            lock (RepositoryGate(gitRoot))
            {
                var modified = RunGit(gitRoot, ["diff", "--name-only", "--diff-filter=ACMRTUXB"]);
                if (modified.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-diff", modified.ErrorText);

                var candidates = modified.Out
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(path => !IsRuntimeOnlyPath(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var oversized = FindOversizedFiles(gitRoot, candidates);
                UnstageRefusedFiles(gitRoot, oversized);

                if (candidates.Count > 0)
                {
                    var addArgs = new List<string> { "add", "-u", "--" };
                    addArgs.AddRange(candidates);
                    addArgs.AddRange(BuildLiteralExcludePathspecs(oversized));
                    var add = RunGitRetryingIndexLock(gitRoot, addArgs);
                    if (add.Code != 0)
                        return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);
                }

                var changed = RunGit(gitRoot, ["diff", "--cached", "--quiet"]);
                if (changed.Code == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                if (changed.Code != 1)
                    return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

                var commit = RunGitRetryingIndexLock(gitRoot,
                    [
                        "-c", $"user.name={CommitterName}",
                        "-c", $"user.email={CommitterEmail}",
                        "commit", "-m", "chore(workspace): sweep tracked workspace drift"
                    ]);
                if (commit.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);

                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }

            _logger.LogInformation("workspace-tracked-sweep-commit gitRoot={Root} sha={Sha}", gitRoot, shortSha ?? "");
            if (WorkspaceAutoPushEnabled() && _pushQueue != null
                && !_pushQueue.Enqueue(new WorkspaceArtifactPushRequest(gitRoot, "workspace-tracked-sweep")))
            {
                _logger.LogWarning("workspace-artifact-push enqueue failed jobId=workspace-tracked-sweep repo={Repo}", gitRoot);
            }
            return WorkspaceArtifactCommitResult.Committed(shortSha, 0, "tracked-sweep");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace tracked sweep failed for {Root}", workspaceRoot);
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    private List<string> FindOversizedFiles(string gitRoot, IReadOnlyList<string> pathspecs)
    {
        var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pathspec in pathspecs)
        {
            if (string.IsNullOrWhiteSpace(pathspec) || pathspec.StartsWith(':')) continue;
            var full = Path.GetFullPath(Path.Combine(gitRoot, pathspec.Replace('/', Path.DirectorySeparatorChar)));
            IEnumerable<string> files;
            if (File.Exists(full))
                files = [full];
            else if (Directory.Exists(full))
                files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories);
            else
                continue;

            foreach (var file in files)
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    if (size <= _maximumFileBytes) continue;
                    var relative = Path.GetRelativePath(gitRoot, file).Replace('\\', '/');
                    if (!refused.Add(relative)) continue;
                    _logger.LogWarning(
                        "workspace-artifact-file-refused path={Path} bytes={Bytes} maximumBytes={MaximumBytes}",
                        relative, size, _maximumFileBytes);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "workspace-artifact size check skipped unreadable path={Path}", file);
                }
            }
        }
        return refused.ToList();
    }

    private void UnstageRefusedFiles(string gitRoot, IReadOnlyList<string> refused)
    {
        if (refused.Count == 0) return;
        var resetArgs = new List<string> { "reset", "-q", "--" };
        resetArgs.AddRange(refused);
        var reset = RunGit(gitRoot, resetArgs);
        if (reset.Code != 0)
            _logger.LogWarning("workspace-artifact refused-file reset failed root={Root} error={Error}", gitRoot, reset.ErrorText);
    }

    private static List<string> BuildLiteralExcludePathspecs(IReadOnlyList<string> paths) =>
        paths.Select(path => $":(exclude,top,literal){path}").ToList();

    private static bool IsRuntimeOnlyPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("logs/bus/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".metadata/attempt-authority", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalize exclude globs into git reset pathspecs (default magic,
    /// where <c>*</c> also matches <c>/</c>), dropping blanks.</summary>
    private static List<string> NormalizeExcludeGlobs(IReadOnlyList<string> excludeGlobs)
    {
        var specs = new List<string>();
        if (excludeGlobs == null) return specs;
        foreach (var glob in excludeGlobs)
        {
            if (string.IsNullOrWhiteSpace(glob)) continue;
            specs.Add(glob.Trim());
        }
        return specs;
    }

    /// <summary>Turn exclude globs into <c>:(exclude)</c> magic pathspecs (default
    /// magic, where <c>*</c> also matches <c>/</c>, so a glob matches at any depth
    /// under the staged data paths) for the partial-commit's file set. Blanks are
    /// dropped; a glob already written as an exclude pathspec is passed through.</summary>
    private static List<string> BuildExcludePathspecs(IReadOnlyList<string> excludeGlobs)
    {
        var specs = new List<string>();
        if (excludeGlobs == null) return specs;
        foreach (var glob in excludeGlobs)
        {
            if (string.IsNullOrWhiteSpace(glob)) continue;
            var trimmed = glob.Trim();
            specs.Add(trimmed.StartsWith(":", StringComparison.Ordinal) ? trimmed : $":(exclude){trimmed}");
        }
        return specs;
    }

    private GitProcessResult RunGitRetryingIndexLock(string gitRoot, IReadOnlyList<string> args, string? stdin = null)
        => RunWithIndexLockRetry(
            () => RunGit(gitRoot, args, stdin),
            _indexLockRetryAttempts,
            attempt => Thread.Sleep(_indexLockRetryBackoffMs * attempt));

    /// <summary>
    /// Retry a git invocation while it fails on <c>.git/index.lock</c>
    /// contention. Pure over an injected <paramref name="run"/> so the retry
    /// contract is unit-testable without real git or real time (the debounce
    /// path uses virtual time; this one uses a no-op backoff).
    /// </summary>
    internal static GitProcessResult RunWithIndexLockRetry(
        Func<GitProcessResult> run,
        int attempts,
        Action<int>? backoff)
    {
        var result = run();
        var max = Math.Max(1, attempts);
        for (var attempt = 1; attempt < max && IsIndexLockContention(result.Code, result.ErrorText); attempt++)
        {
            backoff?.Invoke(attempt);
            result = run();
        }
        return result;
    }

    internal static bool IsIndexLockContention(int code, string errorText)
    {
        if (code == 0 || string.IsNullOrEmpty(errorText)) return false;
        return errorText.Contains("index.lock", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Another git process", StringComparison.OrdinalIgnoreCase)
            || (errorText.Contains("Unable to create", StringComparison.OrdinalIgnoreCase)
                && errorText.Contains(".lock", StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveGitRoot(string workspaceRoot)
    {
        var result = RunGit(workspaceRoot, ["rev-parse", "--show-toplevel"]);
        return result.Code == 0 && !string.IsNullOrWhiteSpace(result.Out)
            ? Path.GetFullPath(result.Out.Trim())
            : null;
    }

    private static List<string> BuildPathspecs(
        string gitRoot,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath)
    {
        var result = new List<string>();
        AddPathspec(result, gitRoot, beforeMoveFolderPath);
        AddPathspec(result, gitRoot, afterMoveFolderPath);
        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddPathspec(List<string> result, string gitRoot, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var full = Path.GetFullPath(folderPath);
        var rel = Path.GetRelativePath(gitRoot, full);
        if (string.IsNullOrWhiteSpace(rel)
            || rel == "."
            || rel.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(rel))
        {
            return;
        }
        result.Add(rel.Replace('\\', '/'));
    }

    private static string NormalizeVerdict(ReviewDecisionKind verdict) => verdict switch
    {
        ReviewDecisionKind.AcceptAsDone => "accept",
        ReviewDecisionKind.Reissue => "reissue",
        ReviewDecisionKind.Escalate => "escalate",
        ReviewDecisionKind.Skipped => "skipped",
        _ => verdict.ToString().ToLowerInvariant(),
    };

    private static string NormalizeToken(string value) =>
        value.Trim().Replace(' ', '-').ToLowerInvariant();

    private static string? ResolveTerminalStatusToken(JsonElement step)
    {
        if (!step.TryGetProperty("status", out var el)) return null;

        PipelineStepStatus? status = null;
        string? raw = null;
        if (el.ValueKind == JsonValueKind.String)
        {
            raw = el.GetString();
            if (!string.IsNullOrWhiteSpace(raw)
                && Enum.TryParse<PipelineStepStatus>(raw, ignoreCase: true, out var parsed))
            {
                status = parsed;
            }
        }
        else if (el.ValueKind == JsonValueKind.Number
                 && el.TryGetInt32(out var numeric)
                 && Enum.IsDefined(typeof(PipelineStepStatus), numeric))
        {
            status = (PipelineStepStatus)numeric;
        }

        return status switch
        {
            PipelineStepStatus.Passed or PipelineStepStatus.Failed or PipelineStepStatus.Skipped
                or PipelineStepStatus.NotApplicable => status.Value.ToString(),
            null when !string.IsNullOrWhiteSpace(raw) => raw,
            _ => null,
        };
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static GitProcessResult RunGit(string cwd, IReadOnlyList<string> args, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return new GitProcessResult(stdout, stderr, p.ExitCode);
    }

    internal sealed record GitProcessResult(string Out, string Err, int Code)
    {
        public string ErrorText => string.IsNullOrWhiteSpace(Err) ? Out.Trim() : Err.Trim();
    }
}

public sealed record WorkspaceArtifactCommitResult(
    bool Success,
    bool DidCommit,
    string? Sha,
    int? RunIndex,
    string? Steps,
    string? Error)
{
    public static WorkspaceArtifactCommitResult Committed(string? sha, int runIndex, string steps) =>
        new(true, true, sha, runIndex, steps, null);

    public static WorkspaceArtifactCommitResult Skipped(string reason) =>
        new(true, false, null, null, null, reason);

    public static WorkspaceArtifactCommitResult Failed(string phase, string error) =>
        new(false, false, null, null, null, $"{phase}: {error}");
}
