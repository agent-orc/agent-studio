using System.Diagnostics;
using System.Collections.Concurrent;
using AgentStudio.State;
using AgentStudio.Supervisor;

namespace AgentStudio.Pipeline;

/// <summary>
/// Pushes workspace artifacts and other bounded platform-owned repository
/// commits off the run path.
/// Failures are visible and retried with bounded exponential backoff. Exact-SHA
/// obligations survive restart and are swept without requiring another commit.
/// </summary>
public sealed class WorkspaceArtifactPushWorker : BackgroundService
{
    private readonly WorkspaceArtifactPushQueue _queue;
    private readonly HashSet<(string Repository, string Branch, string Sha)> _attempted = [];
    private readonly ILogger<WorkspaceArtifactPushWorker> _logger;
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _bus;
    private readonly SupervisorAdvisoryStore? _advisories;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _sweepInterval;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _catchUpTimeout;
    private readonly int _backlogWarningCommits;
    private readonly long _backlogWarningBytes;
    private readonly string? _workspaceRoot;
    private readonly bool _autoPushEnabled;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<PushGitResult>> _runGit;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryPushGates =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null,
        SupervisorAdvisoryStore? advisories = null)
        : this(queue, logger, configuration, bus, advisories, TimeProvider.System, null)
    {
    }

    internal WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus,
        SupervisorAdvisoryStore? advisories,
        TimeProvider time,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<PushGitResult>>? runGit)
    {
        _queue = queue;
        _logger = logger;
        _configuration = configuration;
        _bus = bus;
        _advisories = advisories;
        _time = time;
        _baseBackoff = TimeSpan.FromSeconds(Math.Max(0,
            configuration.GetValue<int?>("WorkspaceArtifacts:PushRetrySeconds") ?? 30));
        _defaultTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:PushTimeoutSeconds") ?? 30, 1, 3600));
        _catchUpTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:CatchUpPushTimeoutSeconds") ?? 600, 30, 3600));
        _backlogWarningCommits = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:BacklogWarningCommits") ?? 50, 1, 100_000);
        _backlogWarningBytes = Math.Max(1,
            configuration.GetValue<long?>("WorkspaceArtifacts:BacklogWarningBytes") ?? 512L * 1024 * 1024);
        _workspaceRoot = configuration["TaskRepository"];
        _autoPushEnabled = configuration.GetValue<bool?>("WorkspaceArtifacts:AutoPushEnabled") ?? true;
        _sweepInterval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:PushOutboxSweepSeconds") ?? 60, 1, 3600));
        _runGit = runGit ?? RunGitAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Start the timer before replay so a long failed startup push cannot
            // postpone discovery indefinitely. Both paths run on this single reader.
            using var timer = new PeriodicTimer(_sweepInterval, _time);
            var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            await SweepPendingAsync(stoppingToken).ConfigureAwait(false);
            var catchUp = StartupCatchUpRequest();
            if (catchUp != null)
                await ProcessSafelyAsync(catchUp, stoppingToken).ConfigureAwait(false);
            var available = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await Task.WhenAny(tick, available).ConfigureAwait(false) == tick)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    await SweepPendingAsync(stoppingToken).ConfigureAwait(false);
                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    if (!await available.ConfigureAwait(false)) break;
                    if (_queue.Reader.TryRead(out var request))
                    {
                        // A startup/periodic replay may already have acknowledged
                        // this channel notification. Failed receipts wait for the sweep.
                        if (request.Sha == null || (_queue.IsPending(request) && !_attempted.Contains(
                            (request.RepositoryRoot, request.TargetBranch, request.Sha))))
                            await ProcessSafelyAsync(request, stoppingToken).ConfigureAwait(false);
                    }
                    available = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "WorkspaceArtifactPushWorker: shutdown; pending exact-SHA obligations remain in the outbox.");
        }
    }

    internal async Task SweepPendingAsync(CancellationToken ct)
    {
        _attempted.Clear();
        try
        {
            foreach (var intent in _queue.PendingIntents())
            {
                ct.ThrowIfCancellationRequested();
                try { await RecoverIntentAsync(intent, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "workspace-artifact-push intent recovery failed repo={Repo}; intent retained",
                        intent.Request.RepositoryRoot);
                }
            }
            foreach (var request in _queue.PendingRequests())
            {
                ct.ThrowIfCancellationRequested();
                await ProcessSafelyAsync(request, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "workspace-artifact-push outbox sweep failed; pending receipts retained");
        }
    }

    private async Task ProcessSafelyAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        if (request.Sha != null)
            _attempted.Add((request.RepositoryRoot, request.TargetBranch, request.Sha));
        try
        {
            await ProcessAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Includes acknowledgement failure after a successful remote push.
            // The retained receipt will prove containment on the next sweep.
            _logger.LogError(ex, "workspace-artifact-push interrupted repo={Repo} branch={Branch} sha={Sha}; receipt retained",
                request.RepositoryRoot, request.TargetBranch, request.Sha);
        }
    }

    internal WorkspaceArtifactPushRequest? StartupCatchUpRequest() =>
        _autoPushEnabled && !string.IsNullOrWhiteSpace(_workspaceRoot) && Directory.Exists(_workspaceRoot)
            && !AgentStudio.Tasks.TaskArtifactsSourceSetting.IsExportedWorkspacePath(_configuration, _workspaceRoot)
            ? new WorkspaceArtifactPushRequest(_workspaceRoot, "workspace-startup-catch-up")
            : null;

    internal async Task<bool> ProcessAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        // With TaskArtifacts:Source = task-server the workspace repository is the
        // Task Server's export (AGT-3320): its export job pushes it, the monolith never does.
        if (AgentStudio.Tasks.TaskArtifactsSourceSetting.IsExportedWorkspacePath(_configuration, request.RepositoryRoot))
        {
            _logger.LogDebug(
                "workspace-artifact-push-skipped repo={Repo} reason={Reason}",
                request.RepositoryRoot, AgentStudio.Tasks.TaskArtifactsSourceSetting.ExportSkipReason);
            return true;
        }
        var repository = Path.GetFullPath(request.RepositoryRoot);
        var gate = RepositoryPushGates.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            request = request with { RepositoryRoot = repository };
            var pushed = await ProcessSingleFlightAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (pushed) _queue.Acknowledge(request);
            return pushed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ProcessSingleFlightAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        // Local tracking refs can be stale after an ambiguous push or a remote
        // rewrite. Only an observation of origin can discharge an exact-SHA receipt.
        if (request.Sha != null && await RemoteContainsAsync(request, ct).ConfigureAwait(false))
            return true;
        var backlog = await MeasureBacklogAsync(request, ct).ConfigureAwait(false);
        if (backlog.AheadCount >= _backlogWarningCommits || backlog.EstimatedBytes >= _backlogWarningBytes)
        {
            _logger.LogWarning(
                "workspace-artifact-push-backlog repo={Repo} branch={Branch} ahead={AheadCount} estimatedBytes={EstimatedBytes}",
                request.RepositoryRoot, request.TargetBranch, backlog.AheadCount, backlog.EstimatedBytes);
        }
        if (request.Sha == null && backlog.AheadMeasured && backlog.AheadCount == 0)
        {
            _logger.LogDebug(
                "workspace-artifact-push-skipped repo={Repo} branch={Branch} reason=already-current",
                request.RepositoryRoot, request.TargetBranch);
            return true;
        }

        var timeout = _defaultTimeout;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await _runGit(request.RepositoryRoot,
                ["push", "origin", $"{request.Sha ?? "HEAD"}:refs/heads/{request.TargetBranch}"], timeout, ct);
            if (result.Code == 0)
            {
                _logger.LogInformation(
                    "workspace-artifact-push-succeeded jobId={JobId} repo={Repo} attempt={Attempt}",
                    request.JobId, request.RepositoryRoot, attempt);
                return true;
            }

            _logger.LogWarning(
                "workspace-artifact-push-failed jobId={JobId} repo={Repo} attempt={Attempt} timeoutSeconds={TimeoutSeconds} failureKind={FailureKind} error={Error}",
                request.JobId, request.RepositoryRoot, attempt, timeout.TotalSeconds, result.FailureKind, result.Error);
            if (result.FailureKind == GitProcessFailureKind.TimedOut)
                timeout = _catchUpTimeout;
            if (attempt < 3)
                await Task.Delay(TimeSpan.FromTicks(_baseBackoff.Ticks * (1L << (attempt - 1))), _time, ct);
            else
                await RaiseFinalAdvisoryAsync(request, backlog, result.Error, attempt, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task RecoverIntentAsync(WorkspaceArtifactPushIntent intent, CancellationToken ct)
    {
        var request = intent.Request;
        if (AgentStudio.Tasks.TaskArtifactsSourceSetting.IsExportedWorkspacePath(_configuration, request.RepositoryRoot))
            return;
        var result = await _runGit(request.RepositoryRoot,
            ["log", "--all", "--reflog", "--format=%H", "--fixed-strings", $"--grep={intent.Marker}"],
            _defaultTimeout, ct).ConfigureAwait(false);
        if (result.Code != 0) return;
        var matches = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // An intent without a matching commit may still be in its write path.
        // Retain it. An ambiguous marker never authorizes a branch-tip push.
        if (matches.Length != 1 || matches[0].Length is not (40 or 64) || !matches[0].All(Uri.IsHexDigit))
            return;
        if (_queue.Enqueue(request with { Sha = matches[0] }))
            _queue.CompleteIntent(intent);
    }

    private async Task<bool> RemoteContainsAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        var branchRef = $"refs/heads/{request.TargetBranch}";
        var remote = await _runGit(request.RepositoryRoot,
            ["ls-remote", "--heads", "origin", branchRef], _defaultTimeout, ct).ConfigureAwait(false);
        if (remote.Code != 0) return false;
        var fields = remote.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || fields[1] != branchRef) return false;
        var tip = fields[0];
        if (tip.Equals(request.Sha, StringComparison.OrdinalIgnoreCase)) return true;
        if (tip.Length is not (40 or 64) || !tip.All(Uri.IsHexDigit)) return false;

        // Fetch objects by the observed remote tip without moving local branches,
        // tracking refs or FETCH_HEAD. Never substitute local HEAD for the receipt.
        var fetch = await _runGit(request.RepositoryRoot,
            ["fetch", "--no-tags", "--no-write-fetch-head", "origin", tip], _defaultTimeout, ct).ConfigureAwait(false);
        if (fetch.Code != 0) return false;
        var contains = await _runGit(request.RepositoryRoot,
            ["merge-base", "--is-ancestor", request.Sha!, tip], _defaultTimeout, ct).ConfigureAwait(false);
        return contains.Code == 0;
    }

    private async Task<WorkspacePushBacklog> MeasureBacklogAsync(
        WorkspaceArtifactPushRequest request,
        CancellationToken ct)
    {
        var remoteRef = $"refs/remotes/origin/{request.TargetBranch}";
        var tip = request.Sha ?? "HEAD";
        var aheadResult = await _runGit(request.RepositoryRoot,
            ["rev-list", "--count", $"{remoteRef}..{tip}"], _defaultTimeout, ct).ConfigureAwait(false);
        var bytesResult = await _runGit(request.RepositoryRoot,
            ["rev-list", "--disk-usage", "--objects", $"{remoteRef}..{tip}"], _defaultTimeout, ct).ConfigureAwait(false);

        _ = int.TryParse(aheadResult.Output.Trim(), out var ahead);
        _ = long.TryParse(bytesResult.Output.Trim(), out var bytes);
        return new WorkspacePushBacklog(ahead, bytes, aheadResult.Code == 0, bytesResult.Code == 0);
    }

    private async Task RaiseFinalAdvisoryAsync(
        WorkspaceArtifactPushRequest request,
        WorkspacePushBacklog backlog,
        string error,
        int attempts,
        CancellationToken ct)
    {
        var project = string.IsNullOrWhiteSpace(request.Project) ? "workspace" : request.Project!;
        var advisory = new SupervisorAdvisory(
            _time.GetUtcNow().UtcDateTime,
            project,
            SupervisorSeverity.Warn,
            SupervisorSource.HardCheck,
            "workspace-repository-push-failed",
            $"Workspace repository push failed after {attempts} attempts. Repository: {request.RepositoryRoot}; branch: {request.TargetBranch}; ahead: {backlog.AheadCount}; error: {error}",
            request.JobId);

        if (_advisories != null)
        {
            try
            {
                await _advisories.AppendAsync(request.RepositoryRoot, project, advisory, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "workspace-artifact-push advisory persistence failed repo={Repo}", request.RepositoryRoot);
            }
        }

        if (_bus != null)
        {
            await _bus.EmitManagedRepoPushFailureAsync(
                request.Project, request.JobId, request.RepositoryRoot, request.TargetBranch,
                "failed", error, attempts, ct).ConfigureAwait(false);
            if (_advisories != null)
                await _bus.EmitAdvisoryAsync(advisory, ct).ConfigureAwait(false);
        }
    }

    private static async Task<PushGitResult> RunGitAsync(
        string cwd, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var result = await GitProcessBoundary.RunAsync(
            psi,
            stdin: null,
            timeout,
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new PushGitResult(
            result.ExitCode,
            result.StandardOutput,
            string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim(),
            result.FailureKind);
    }

    internal sealed record PushGitResult(int Code, string Output, string Error, GitProcessFailureKind FailureKind);
    internal sealed record WorkspacePushBacklog(
        int AheadCount,
        long EstimatedBytes,
        bool AheadMeasured,
        bool BytesMeasured);
}
