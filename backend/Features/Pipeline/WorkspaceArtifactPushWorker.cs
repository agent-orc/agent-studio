using System.Diagnostics;
using System.Collections.Concurrent;
using AgentStudio.State;
using AgentStudio.Supervisor;

namespace AgentStudio.Pipeline;

/// <summary>
/// Pushes workspace artifacts and other bounded platform-owned repository
/// commits off the run path.
/// Failures are visible and retried with bounded exponential backoff; a later
/// commit also acts as a natural retry for all still-ahead commits.
/// </summary>
public sealed class WorkspaceArtifactPushWorker : BackgroundService
{
    private readonly WorkspaceArtifactPushQueue _queue;
    private readonly ILogger<WorkspaceArtifactPushWorker> _logger;
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _bus;
    private readonly SupervisorAdvisoryStore? _advisories;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _catchUpTimeout;
    private readonly int _backlogWarningCommits;
    private readonly long _backlogWarningBytes;
    private readonly string? _workspaceRoot;
    private readonly bool _autoPushEnabled;
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
        _runGit = runGit ?? RunGitAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var catchUp = StartupCatchUpRequest();
            if (catchUp != null)
                await ProcessAsync(catchUp, stoppingToken).ConfigureAwait(false);
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(request, stoppingToken);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "WorkspaceArtifactPushWorker: graceful shutdown; a later workspace commit retries pending history.");
        }
    }

    internal WorkspaceArtifactPushRequest? StartupCatchUpRequest() =>
        _autoPushEnabled && !string.IsNullOrWhiteSpace(_workspaceRoot) && Directory.Exists(_workspaceRoot)
            ? new WorkspaceArtifactPushRequest(_workspaceRoot, "workspace-startup-catch-up")
            : null;

    internal async Task<bool> ProcessAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        var repository = Path.GetFullPath(request.RepositoryRoot);
        var gate = RepositoryPushGates.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ProcessSingleFlightAsync(request with { RepositoryRoot = repository }, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ProcessSingleFlightAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        var backlog = await MeasureBacklogAsync(request, ct).ConfigureAwait(false);
        if (backlog.AheadCount >= _backlogWarningCommits || backlog.EstimatedBytes >= _backlogWarningBytes)
        {
            _logger.LogWarning(
                "workspace-artifact-push-backlog repo={Repo} branch={Branch} ahead={AheadCount} estimatedBytes={EstimatedBytes}",
                request.RepositoryRoot, request.TargetBranch, backlog.AheadCount, backlog.EstimatedBytes);
        }
        if (backlog.AheadMeasured && backlog.AheadCount == 0)
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
                await Task.Delay(TimeSpan.FromTicks(_baseBackoff.Ticks * (1L << (attempt - 1))), ct);
            else
                await RaiseFinalAdvisoryAsync(request, backlog, result.Error, attempt, ct).ConfigureAwait(false);
        }
        return false;
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
        var result = await GitNetworkProcessRunner.RunAsync(
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
