using System.Collections.Concurrent;

namespace AgentStudio.Docs;

/// <summary>Outcome of a start request (started, busy, or bad project).</summary>
public sealed record WikiGradingStartResult(bool Started, string? Error, WikiGradingRunStatus? Status);

/// <summary>
/// Orchestrates a global wiki-grading maintenance run (AGT-2051): enumerate every
/// wiki page, grade each with the chosen model via the <see cref="IWikiPageGrader"/>
/// seam, and write the verdict into the page's companion sidecar. A project has
/// at most one run in flight; the run is fire-and-forget on the server with an
/// in-memory status registry the UI polls.
///
/// <para>Design guarantees from the concept: progress is visible
/// (<c>processed / total</c>), the run is <b>abortable</b> mid-flight, it is
/// <b>idempotent</b> (a page whose content fingerprint and model are unchanged is
/// skipped unless the operator forces a re-grade), and pages are graded
/// <b>sequentially with pacing</b> so a run is batched rather than a parallel
/// storm. Spend is recorded by the one-shot rail (the production grader), so
/// quota stays visible.</para>
/// </summary>
public sealed class WikiGradingService
{
    private static readonly TimeSpan PagePacingDelay = TimeSpan.FromMilliseconds(200);
    private const int RecentTail = 8;

    private readonly ProjectDocsService _docs;
    private readonly IWikiPageGrader _grader;
    private readonly WikiCompanionStore _companions;
    private readonly ILogger<WikiGradingService> _logger;
    private readonly GitService? _git;
    private readonly ManagedRepositoryMutationService? _repositoryMutations;

    private readonly ConcurrentDictionary<string, RunHandle> _runs =
        new(StringComparer.OrdinalIgnoreCase);

    public WikiGradingService(
        ProjectDocsService docs,
        IWikiPageGrader grader,
        WikiCompanionStore companions,
        ILogger<WikiGradingService> logger,
        GitService? git = null,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _docs = docs;
        _grader = grader;
        _companions = companions;
        _logger = logger;
        _git = git;
        _repositoryMutations = repositoryMutations;
    }

    /// <summary>
    /// Start a background grading run for a project. Returns a busy result (with
    /// the live status) when one is already running, or a bad-project result when
    /// the project has no docs/ tree.
    /// </summary>
    public WikiGradingStartResult Start(string project, WikiGradingRunRequest req)
    {
        var overview = _docs.GetWikiOverview(project);
        if (overview == null || !overview.Exists)
            return new WikiGradingStartResult(false, $"No docs/ wiki for project '{project}'.", null);

        if (_runs.TryGetValue(project, out var existing) && existing.SnapshotState() == WikiGradingRunState.Running)
            return new WikiGradingStartResult(false, "A grading run is already in progress.", Snapshot(project, existing));

        var handle = new RunHandle(NewRunId(), Normalize(req), new CancellationTokenSource());
        _runs[project] = handle;
        handle.Task = Task.Run(() => ExecuteAsync(project, handle));
        return new WikiGradingStartResult(true, null, Snapshot(project, handle));
    }

    /// <summary>Latest run status for a project (running or finished), or null when
    /// no run has ever been started for it.</summary>
    public WikiGradingRunStatus? GetStatus(string project)
        => _runs.TryGetValue(project, out var handle) ? Snapshot(project, handle) : null;

    /// <summary>Requests cancellation of an in-flight run. Returns false when there
    /// is nothing running to abort.</summary>
    public bool Abort(string project)
    {
        if (!_runs.TryGetValue(project, out var handle) || handle.SnapshotState() != WikiGradingRunState.Running)
            return false;
        try { handle.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>
    /// Run a grading pass synchronously to completion. Used by the end-to-end
    /// probe and unit tests so they can assert the written companions without a
    /// polling loop. Honours the supplied cancellation token for abort coverage.
    /// </summary>
    public async Task<WikiGradingRunStatus> RunToCompletionAsync(
        string project, WikiGradingRunRequest req, CancellationToken ct = default)
    {
        var handle = new RunHandle(NewRunId(), Normalize(req), CancellationTokenSource.CreateLinkedTokenSource(ct));
        _runs[project] = handle;
        await ExecuteAsync(project, handle).ConfigureAwait(false);
        return Snapshot(project, handle);
    }

    private async Task ExecuteAsync(string project, RunHandle handle)
    {
        var ct = handle.Cts.Token;
        try
        {
            var overview = _docs.GetWikiOverview(project);
            if (overview == null || !overview.Exists)
            {
                handle.Finish(WikiGradingRunState.Failed, "No docs/ wiki for this project.");
                return;
            }

            var wikiDir = Path.GetFullPath(overview.BaseDir);
            var pages = overview.Files.ToList();
            if (handle.Request.Limit > 0)
                pages = pages.Take(handle.Request.Limit).ToList();

            handle.SetTotal(pages.Count);

            var first = true;
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                // Batching / no parallel storm: sequential, with a short breath
                // between pages so a large tree does not hammer the rail.
                if (!first) await Task.Delay(PagePacingDelay, ct).ConfigureAwait(false);
                first = false;

                handle.SetCurrent(page.RelPath);
                var result = await GradeOneAsync(project, wikiDir, page, handle, ct).ConfigureAwait(false);
                handle.Record(result);
            }

            handle.Finish(WikiGradingRunState.Completed, null);
            if (handle.SnapshotGraded() > 0)
                _docs.InvalidateWikiContent(project);
            _logger.LogInformation(
                "Wiki grading run {RunId} for {Project} completed: {Graded} graded, {Skipped} skipped, {Failed} failed, {Critical} critical.",
                handle.RunId, project, handle.SnapshotGraded(), handle.SnapshotSkipped(), handle.SnapshotFailed(), handle.SnapshotCritical());
        }
        catch (OperationCanceledException)
        {
            handle.Finish(WikiGradingRunState.Aborted, null);
            _logger.LogInformation("Wiki grading run {RunId} for {Project} aborted.", handle.RunId, project);
        }
        catch (Exception ex)
        {
            handle.Finish(WikiGradingRunState.Failed, ex.Message);
            _logger.LogWarning(ex, "Wiki grading run {RunId} for {Project} failed.", handle.RunId, project);
        }
        finally
        {
            handle.ClearCurrent();
        }
    }

    private async Task<WikiPageGradeResult> GradeOneAsync(
        string project, string wikiDir, WikiFileEntry page, RunHandle handle, CancellationToken ct)
    {
        var fullPath = Path.Combine(wikiDir, page.RelPath.Replace('/', Path.DirectorySeparatorChar));
        string content;
        try
        {
            if (!FilesystemPathBoundary.IsRegularFileUnder(wikiDir, fullPath))
                return new WikiPageGradeResult(page.RelPath, "unknown", WikiGradeOutcome.Failed,
                    "read rejected: page is outside the wiki root or is a link");
            content = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            return new WikiPageGradeResult(page.RelPath, "unknown", WikiGradeOutcome.Failed, $"read failed: {ex.Message}");
        }

        var hash = WikiCompanionStore.HashContent(content);
        var companionAbs = WikiCompanionStore.CompanionPathFor(wikiDir, page.RelPath);
        if (!FilesystemPathBoundary.IsPathUnderWithoutLinks(wikiDir, companionAbs))
            return new WikiPageGradeResult(page.RelPath, "unknown", WikiGradeOutcome.Failed,
                "companion path is outside the wiki root or is a link");

        // Idempotency: an unchanged page graded by the same model is skipped
        // unless the operator forces a full re-grade.
        if (!handle.Request.Force)
        {
            var stored = _companions.ReadGrading(companionAbs);
            if (stored is { Hash: { } h } && string.Equals(h, hash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(stored.Model, handle.Request.Model, StringComparison.OrdinalIgnoreCase))
            {
                return new WikiPageGradeResult(page.RelPath, stored.Grade ?? "unknown", WikiGradeOutcome.Skipped, null);
            }
        }

        var input = new WikiPageGradeInput(project, page.RelPath, page.Title, content, hash);
        var verdict = await _grader.GradeAsync(input, handle.Request, ct).ConfigureAwait(false);
        if (!verdict.Ok)
            return new WikiPageGradeResult(page.RelPath, verdict.Grade, WikiGradeOutcome.Failed, verdict.Error);

        try
        {
            var repoRoot = _git?.ResolveRepoRootForProject(project);
            if (_repositoryMutations != null && !string.IsNullOrWhiteSpace(repoRoot))
            {
                var companionRel = Path.GetRelativePath(repoRoot, companionAbs).Replace('\\', '/');
                var mutation = _repositoryMutations.Execute(
                    project,
                    repoRoot,
                    $"wiki-grading-{handle.RunId}-{page.RelPath}",
                    $"chore(wiki): grade {page.RelPath}",
                    [companionRel],
                    () => _companions.WriteGrading(
                        wikiDir, page.RelPath, page.Title, content, verdict,
                        handle.Request, handle.RunId, DateTime.UtcNow));
                if (!mutation.Success)
                {
                    return new WikiPageGradeResult(
                        page.RelPath,
                        verdict.Grade,
                        WikiGradeOutcome.Failed,
                        $"companion persistence failed: {mutation.Error}");
                }
            }
            else
            {
                _companions.WriteGrading(
                    wikiDir, page.RelPath, page.Title, content, verdict,
                    handle.Request, handle.RunId, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            return new WikiPageGradeResult(page.RelPath, verdict.Grade, WikiGradeOutcome.Failed, $"companion write failed: {ex.Message}");
        }

        return new WikiPageGradeResult(page.RelPath, verdict.Grade, WikiGradeOutcome.Graded, null);
    }

    private static WikiGradingRunRequest Normalize(WikiGradingRunRequest req)
    {
        var cli = string.IsNullOrWhiteSpace(req.CliType) ? WikiMaintenanceModelService.DefaultCli : req.CliType.Trim();
        var model = string.IsNullOrWhiteSpace(req.Model) ? WikiMaintenanceModelService.DefaultModel : req.Model.Trim();
        var level = string.IsNullOrWhiteSpace(req.ThinkingLevel) ? null : req.ThinkingLevel!.Trim();
        return req with { CliType = cli, Model = model, ThinkingLevel = level, Limit = Math.Max(0, req.Limit) };
    }

    private WikiGradingRunStatus Snapshot(string project, RunHandle handle) => handle.ToStatus(project);

    private static string NewRunId() =>
        "wg-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// Mutable per-run state. All counter mutation and snapshot reads happen under
    /// <see cref="_gate"/> so the polling status endpoint sees a consistent view.
    /// </summary>
    private sealed class RunHandle
    {
        private readonly object _gate = new();
        private readonly List<WikiGradingRunItem> _recent = new();

        public string RunId { get; }
        public WikiGradingRunRequest Request { get; }
        public CancellationTokenSource Cts { get; }
        public Task? Task { get; set; }

        private WikiGradingRunState _state = WikiGradingRunState.Running;
        private int _total, _processed, _graded, _skipped, _failed, _critical;
        private string? _current;
        private readonly string _startedAt = DateTime.UtcNow.ToString("o");
        private string? _completedAt;
        private string? _error;

        public RunHandle(string runId, WikiGradingRunRequest request, CancellationTokenSource cts)
        {
            RunId = runId;
            Request = request;
            Cts = cts;
        }

        public WikiGradingRunState SnapshotState() { lock (_gate) return _state; }
        public int SnapshotGraded() { lock (_gate) return _graded; }
        public int SnapshotSkipped() { lock (_gate) return _skipped; }
        public int SnapshotFailed() { lock (_gate) return _failed; }
        public int SnapshotCritical() { lock (_gate) return _critical; }

        public void SetTotal(int total) { lock (_gate) _total = total; }
        public void SetCurrent(string rel) { lock (_gate) _current = rel; }
        public void ClearCurrent() { lock (_gate) _current = null; }

        public void Record(WikiPageGradeResult result)
        {
            lock (_gate)
            {
                _processed++;
                switch (result.Outcome)
                {
                    case WikiGradeOutcome.Graded: _graded++; break;
                    case WikiGradeOutcome.Skipped: _skipped++; break;
                    case WikiGradeOutcome.Failed: _failed++; break;
                }
                if (result.Outcome != WikiGradeOutcome.Failed
                    && (string.Equals(result.Grade, "C", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(result.Grade, "D", StringComparison.OrdinalIgnoreCase)))
                {
                    _critical++;
                }
                _recent.Add(new WikiGradingRunItem(result.RelPath, result.Grade, result.Outcome.ToString()));
                if (_recent.Count > RecentTail) _recent.RemoveAt(0);
            }
        }

        public void Finish(WikiGradingRunState state, string? error)
        {
            lock (_gate)
            {
                _state = state;
                _completedAt = DateTime.UtcNow.ToString("o");
                if (!string.IsNullOrWhiteSpace(error)) _error = error;
                _current = null;
            }
        }

        public WikiGradingRunStatus ToStatus(string project)
        {
            lock (_gate)
            {
                return new WikiGradingRunStatus(
                    ProjectName: project,
                    RunId: RunId,
                    State: _state,
                    CliType: Request.CliType,
                    Model: Request.Model,
                    ThinkingLevel: Request.ThinkingLevel,
                    Force: Request.Force,
                    Total: _total,
                    Processed: _processed,
                    Graded: _graded,
                    Skipped: _skipped,
                    Failed: _failed,
                    Critical: _critical,
                    CurrentRelPath: _current,
                    StartedAtUtc: _startedAt,
                    CompletedAtUtc: _completedAt,
                    Error: _error,
                    Recent: _recent.ToList());
            }
        }
    }
}
