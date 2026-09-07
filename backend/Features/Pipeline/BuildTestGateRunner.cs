using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public enum BuildTestGateVerdict
{
    Skipped,
    Ok,
    Warn,
    Fail,
    NotApplicable,
}

public enum BuildTestGateFailureKind
{
    None,
    Code,
    Lock,
    Timeout,
    OutOfMemory,
    ProcessLaunch,
    Cancellation,
    MissingSource,
    ReviewModel,
}

public sealed record BuildTestGateRequest(
    string RepositoryPath,
    string? ExpectedSha,
    string Executor,
    bool RequireExactSubject = true)
{
    public string GateId { get; init; } = PipelineCatalogue.BuildTestGateStepId;
    public string? Project { get; init; }
    public string? WatchPath { get; init; }
    public string? JobId { get; init; }
    public string? AttemptChainId { get; init; }
    public string? SubjectRef { get; init; }
    public string Lane { get; init; } = TaskStates.AutoReview;
    public string? RequiredTestLevel { get; init; }
    public TestExecutionPolicy? TestExecution { get; init; }
    public string? JobFolderPath { get; init; }

    public Action? OnMachineGateWaiting { get; init; }
    public Action? OnMachineGateAcquired { get; init; }

    /// <summary>
    /// Budget for the true infrastructure operations that MUST be quick regardless
    /// of how long a verify run takes: materializing the exact-subject worktree
    /// (fetch + <c>worktree add</c>), reading HEAD, and tearing the worktree down.
    /// </summary>
    public TimeSpan InfrastructureTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Budget for WAITING in the machine-gate queue for the one running gate ahead
    /// to finish. This is deliberately separate from <see cref="InfrastructureTimeout"/>:
    /// the machine lock is held for a gate's entire build+test run (15-25 min in
    /// production), so a queued card must be willing to wait roughly one full run,
    /// not the short infra-op budget. Budgeting the queue wait against the infra SLA
    /// made every card queued behind a running gate escalate with a spurious
    /// "Timeout persisted" after 120 s (AGT-2182, 21.07.). When unset the runner
    /// derives run-timeout + infra-timeout.
    /// </summary>
    public TimeSpan? QueueWaitTimeout { get; init; }
}

public sealed record BuildTestGateProcessEvidence
{
    /// <summary><c>preparation</c> or <c>verification</c>.</summary>
    public string Phase { get; init; } = "verification";
    public string Command { get; init; } = "";
    public string FileName { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int? ExitCode { get; init; }
    public string? TerminationSignal { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public string? LaunchError { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public BuildTestGateBudgetEvidence? ViolatedBudget { get; init; }
}

public sealed record BuildTestGateBudgetEvidence(
    string Name,
    long LimitMs,
    long ConsumedMs,
    string Phase);

public sealed record BuildTestGateDependencyCacheEvidence(
    string WorkingSubdir,
    string State,
    string Reason,
    string LockHash,
    IReadOnlyList<string> Lockfiles,
    bool InstallRan);

public sealed record BuildTestGateFinding(
    string Kind,
    string Scope,
    string Command,
    string Reason,
    int? ExitCode,
    string Evidence);

public sealed record BuildTestGateResult(
    BuildTestGateVerdict Verdict,
    int? ExitCode,
    long DurationMs,
    string Output,
    string Reason,
    bool RanBackendBuild,
    bool RanFrontendBuild)
{
    public string? GateRunId { get; init; }
    public DateTimeOffset? GateStartedAtUtc { get; init; }
    public DateTimeOffset? GateCompletedAtUtc { get; init; }
    public long GateQueueWaitMs { get; init; }
    public bool GateCollisionDetected { get; init; }
    public bool SelfHealed { get; init; }
    public string GateId { get; init; } = PipelineCatalogue.BuildTestGateStepId;
    public string? Repository { get; init; }
    public string? ExpectedSha { get; init; }
    public string? TestedSha { get; init; }
    public string? AttemptChainId { get; init; }
    public string? Executor { get; init; }
    public string? Workspace { get; init; }
    public string? TerminationSignal { get; init; }
    public BuildTestGateFailureKind FailureKind { get; init; }
    public string? FailureFingerprint { get; init; }
    public IReadOnlyList<BuildTestGateProcessEvidence> Processes { get; init; } = [];
    public IReadOnlyList<BuildTestGateDependencyCacheEvidence> DependencyCache { get; init; } = [];
    public BuildTestGateBudgetEvidence? ViolatedBudget { get; init; }
    public TestSelectionAudit? TestSelection { get; init; }
    public IReadOnlyList<BuildTestGateFinding> Findings { get; init; } = [];
    public bool IsInfrastructureFailure => FailureKind is not BuildTestGateFailureKind.None
        and not BuildTestGateFailureKind.Code;
}

public interface IBuildTestGateRunner
{
    Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles,
        BuildProfile? profile,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct);
}

internal enum BuildTestMachineGateMode
{
    Shared,
    BypassForHermeticTest,
}

/// <summary>
/// Runs deterministic verification against one exact Git subject. Real command
/// loops are serialized by one machine-wide lock without reducing coding slots.
/// The Task Server checkout only supplies Git objects and is never a command
/// workspace.
/// </summary>
public sealed class BuildTestGateRunner : IBuildTestGateRunner
{
    public const int MaxOutputLines = 300;
    public const int MaxFailureExcerptChars = 2_000;
    internal const string DependencyCacheDirectoryName = ".dependency-cache";

    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    internal static readonly string MachineGateLockPath = Path.Combine(
        Path.GetTempPath(), "agentstudio-build-test-gate.lock");
    internal static readonly string ReviewWorkspaceRoot = Path.Combine(
        Path.GetTempPath(), "agentstudio-review-gates");
    internal static readonly string NpmCachePath = Path.Combine(
        Path.GetTempPath(), "agentstudio-dependency-cache", "npm");

    private static readonly Regex SafeSha = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VolatileHex = new(
        "\\b[0-9a-fA-F]{7,64}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VolatileNumber = new(
        "\\b\\d+\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(
        "\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] CodeExtensions =
    [
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets",
        ".ts", ".html", ".scss", ".css", ".json", ".mjs", ".js",
    ];

    private readonly ILogger<BuildTestGateRunner> _logger;
    private readonly ILoadThrottleGate? _loadThrottle;
    private readonly ITestSelectionAdvisor? _testSelectionAdvisor;
    private readonly IPipelineHealthSensor? _health;
    private readonly BuildTestMachineGateMode _machineGateMode;

    public BuildTestGateRunner(
        ILogger<BuildTestGateRunner> logger,
        ILoadThrottleGate? loadThrottle = null,
        ITestSelectionAdvisor? testSelectionAdvisor = null,
        IPipelineHealthSensor? health = null)
    {
        _logger = logger;
        _loadThrottle = loadThrottle;
        _testSelectionAdvisor = testSelectionAdvisor;
        _health = health;
        _machineGateMode = BuildTestMachineGateMode.Shared;
    }

    internal BuildTestGateRunner(
        ILogger<BuildTestGateRunner> logger,
        BuildTestMachineGateMode machineGateMode)
        : this(logger)
    {
        _machineGateMode = machineGateMode;
    }

    public async Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles,
        BuildProfile? profile,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (mode == PostStepMode.Off) return Skipped("mode=off");
        var requestedLevel = TestSelectionPlanner.ResolveLevel(
            request.TestExecution, request.Lane, request.RequiredTestLevel);
        var hasContinuousBaseline = request.TestExecution?.ContinuousCommands?
            .Any(command => !string.IsNullOrWhiteSpace(command)) == true;
        if (changedFiles is { Count: > 0 }
            && !HasCodeDiff(changedFiles)
            && requestedLevel != TestExecutionLevels.Full
            && requestedLevel != TestExecutionLevels.BuildOnly
            && !hasContinuousBaseline)
            return Skipped("no code diff");

        var repositoryPath = Path.GetFullPath(request.RepositoryPath);
        var gateRunId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var infrastructureTimeout = request.InfrastructureTimeout > TimeSpan.Zero
            ? request.InfrastructureTimeout
            : TimeSpan.FromMinutes(2);
        var queueWaitTimeout = ResolveQueueWaitTimeout(
            request.QueueWaitTimeout, timeout, infrastructureTimeout);
        _logger.LogInformation(
            "build_test_gate_started gate_run_id={GateRunId} gate_id={GateId} started_at_utc={StartedAtUtc:o} repository={Repository} expected_sha={ExpectedSha} attempt_chain_id={AttemptChainId} executor={Executor}",
            gateRunId, request.GateId, startedAt, repositoryPath,
            request.ExpectedSha ?? "missing", request.AttemptChainId ?? "missing", request.Executor);

        MachineGateLease? machineLease = null;
        ExactWorkspaceLease? workspaceLease = null;
        GateDependencyCacheSession? dependencyCache = null;
        var dependencyCacheSaved = false;
        BuildTestGateResult? completed = null;
        string? workspace = null;
        string? testedSha = null;
        var selfHealed = false;
        long fallbackQueueWaitMs = 0;
        var fallbackCollision = false;
        DateTime? acquiredAtUtc = null;
        try
        {
            if (_loadThrottle is not null)
            {
                await _loadThrottle.WaitUntilReadyAsync(
                    $"build-test-gate:{Path.GetFileName(repositoryPath)}", ct).ConfigureAwait(false);
            }

            if (completed is null)
            {
                if (_machineGateMode == BuildTestMachineGateMode.BypassForHermeticTest)
                {
                    request.OnMachineGateAcquired?.Invoke();
                    acquiredAtUtc = DateTime.UtcNow;
                }
                else
                {
                    var acquisition = await AcquireMachineGateAsync(
                        queueWaitTimeout, request.OnMachineGateWaiting, ct).ConfigureAwait(false);
                    fallbackQueueWaitMs = acquisition.QueueWaitMs;
                    fallbackCollision = acquisition.CollisionDetected;
                    if (acquisition.Lease is null)
                    {
                        completed = InfrastructureFailure(
                            BuildTestGateFailureKind.Timeout,
                            acquisition.Reason,
                            acquisition.Reason,
                            acquisition.ViolatedBudget);
                    }
                    else
                    {
                        machineLease = acquisition.Lease;
                        request.OnMachineGateAcquired?.Invoke();
                        acquiredAtUtc = DateTime.UtcNow;
                        _logger.LogInformation(
                            "build_test_gate_acquired gate_run_id={GateRunId} repository={Repository} collision={CollisionDetected} queue_wait_ms={QueueWaitMs}",
                            gateRunId, repositoryPath, machineLease.CollisionDetected, machineLease.QueueWaitMs);
                        if (HasHealthContext(request))
                        {
                            ReportGateAcquired(new PipelineGateContext(
                                gateRunId,
                                request.Project!,
                                request.WatchPath!,
                                request.JobId!,
                                acquiredAtUtc.Value));
                        }
                    }
                }
            }
            if (completed is null && request.RequireExactSubject)
            {
                var prepared = await PrepareExactWorkspaceAsync(
                    repositoryPath, request.ExpectedSha, request.SubjectRef, gateRunId,
                    infrastructureTimeout, ct).ConfigureAwait(false);
                if (prepared.Lease is null)
                {
                    completed = InfrastructureFailure(
                        prepared.FailureKind,
                        prepared.Reason,
                        prepared.Output,
                        prepared.ViolatedBudget);
                }
                else
                {
                    workspaceLease = prepared.Lease;
                    workspace = workspaceLease.Path;
                    testedSha = workspaceLease.TestedSha;
                    selfHealed = prepared.SelfHealed;
                }
            }
            else if (completed is null && !Directory.Exists(repositoryPath))
            {
                completed = InfrastructureFailure(
                    BuildTestGateFailureKind.MissingSource,
                    $"repository not found: {repositoryPath}", string.Empty);
            }
            else if (completed is null)
            {
                workspace = repositoryPath;
                testedSha = await ReadHeadShaAsync(repositoryPath, infrastructureTimeout, ct).ConfigureAwait(false);
            }

            if (completed is null)
            {
                var plan = VerifyCommandPlanner.Plan(workspace!, profile);
                if (plan.IsEmpty)
                {
                    _logger.LogInformation(
                        "BuildTestGateRunner: no verify commands derivable for {Repo}; gate runs without a build check",
                        workspace);
                    completed = NotApplicable("no verify commands derivable");
                }
                else
                {
                    var staged = TestSelectionPlanner.Plan(
                        workspace!, plan, changedFiles, request.TestExecution,
                        request.Lane, request.RequiredTestLevel);
                    if (_testSelectionAdvisor is not null
                        && staged.Audit.Level == TestExecutionLevels.WorkPackage
                        && staged.Audit.Candidates.Count > 0)
                    {
                        var advice = await _testSelectionAdvisor.AdviseAsync(
                            staged.Audit, request.TestExecution, workspace!,
                            request.Project, request.JobId, request.JobFolderPath, ct).ConfigureAwait(false);
                        if (advice is not null)
                        {
                            staged = TestSelectionPlanner.Plan(
                                workspace!, plan, changedFiles, request.TestExecution,
                                request.Lane, request.RequiredTestLevel, advice);
                        }
                    }
                    var commands = staged.Commands.Where(c => ShouldRunForChange(c, changedFiles)).ToList();
                    var preparation = GatePreparationPlanner.Plan(workspace!, profile, commands);
                    IReadOnlyList<string> cacheRestoreMessages = [];
                    if (workspaceLease is not null)
                    {
                        dependencyCache = GateDependencyCacheSession.Create(
                            ReviewWorkspaceRoot,
                            repositoryPath,
                            workspace!,
                            preparation,
                            commands,
                            _logger);
                        cacheRestoreMessages = dependencyCache.Restore();
                    }
                    completed = commands.Count == 0
                        ? Skipped($"no verify commands apply to the changed files ({plan.Source}); level={staged.Audit.Level}")
                            with
                        {
                            TestSelection = staged.Audit,
                        }
                        : await RunCommandsAsync(
                            workspace!, preparation, commands, plan.Source, mode, timeout,
                            cacheRestoreMessages, ct)
                            .ConfigureAwait(false);
                    var completedAudit = CompleteAudit(staged.Audit, commands, completed.Processes);
                    completed = completed with
                    {
                        TestSelection = completedAudit,
                        Reason = CoverageReason(completed.Reason, completedAudit),
                    };
                }
            }

            if (workspaceLease is not null)
            {
                completed = SaveDependencyCache(completed!, dependencyCache);
                dependencyCacheSaved = true;
                var cleanupError = await workspaceLease.RemoveAsync(
                    infrastructureTimeout, CancellationToken.None).ConfigureAwait(false);
                workspaceLease = null;
                if (cleanupError is not null)
                {
                    var cleanupReason = BudgetFailureReason(
                        "exact review workspace cleanup",
                        cleanupError.ViolatedBudget,
                        cleanupError.Evidence);
                    completed = WithFailure(completed with
                    {
                        Verdict = BuildTestGateVerdict.Fail,
                        ExitCode = null,
                        DurationMs = Math.Max(
                            completed.DurationMs,
                            (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds),
                        Output = AppendOutput(completed.Output, cleanupError.Evidence),
                        Reason = cleanupReason,
                        ViolatedBudget = cleanupError.ViolatedBudget,
                    }, cleanupError.FailureKind);
                }
            }

            completed = completed with
            {
                GateRunId = gateRunId,
                GateId = request.GateId,
                GateStartedAtUtc = startedAt,
                GateCompletedAtUtc = DateTimeOffset.UtcNow,
                GateQueueWaitMs = machineLease?.QueueWaitMs ?? fallbackQueueWaitMs,
                GateCollisionDetected = machineLease?.CollisionDetected ?? fallbackCollision,
                SelfHealed = selfHealed,
                Repository = repositoryPath,
                ExpectedSha = request.ExpectedSha,
                TestedSha = testedSha,
                AttemptChainId = request.AttemptChainId,
                Executor = request.Executor,
                Workspace = workspace,
            };
            return completed;
        }
        finally
        {
            if (workspaceLease is not null)
            {
                if (!dependencyCacheSaved)
                {
                    if (completed is not null)
                        completed = SaveDependencyCache(completed, dependencyCache);
                    else
                        dependencyCache?.Save();
                }
                await workspaceLease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
            }
            var completedAt = completed?.GateCompletedAtUtc ?? DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "build_test_gate_completed gate_run_id={GateRunId} gate_id={GateId} completed_at_utc={CompletedAtUtc:o} repository={Repository} expected_sha={ExpectedSha} tested_sha={TestedSha} attempt_chain_id={AttemptChainId} executor={Executor} workspace={Workspace} verdict={Verdict} exit={ExitCode} signal={Signal} failure_kind={FailureKind} failure_fingerprint={FailureFingerprint} violated_budget={ViolatedBudget} budget_limit_ms={BudgetLimitMs} budget_consumed_ms={BudgetConsumedMs} collision={CollisionDetected} queue_wait_ms={QueueWaitMs} self_healed={SelfHealed}",
                gateRunId, request.GateId, completedAt, repositoryPath,
                request.ExpectedSha ?? "missing", completed?.TestedSha ?? testedSha ?? "missing",
                request.AttemptChainId ?? "missing", request.Executor,
                completed?.Workspace ?? workspace ?? "missing", completed?.Verdict.ToString() ?? "interrupted",
                completed?.ExitCode?.ToString() ?? "n/a", completed?.TerminationSignal ?? "n/a",
                completed?.FailureKind.ToString() ?? BuildTestGateFailureKind.Cancellation.ToString(),
                completed?.FailureFingerprint ?? "none",
                completed?.ViolatedBudget?.Name ?? "none",
                completed?.ViolatedBudget?.LimitMs ?? 0,
                completed?.ViolatedBudget?.ConsumedMs ?? 0,
                machineLease?.CollisionDetected ?? false, machineLease?.QueueWaitMs ?? 0,
                completed?.SelfHealed ?? selfHealed);
            machineLease?.Dispose();
            machineLease = null;
            if (acquiredAtUtc.HasValue && HasHealthContext(request))
            {
                ReportGateCompleted(new PipelineGateCompletion(
                    gateRunId,
                    request.Project!,
                    request.WatchPath!,
                    request.JobId!,
                    completedAt.UtcDateTime,
                    completed?.FailureFingerprint));
            }
        }
    }

    private static bool HasHealthContext(BuildTestGateRequest request)
        => !string.IsNullOrWhiteSpace(request.Project)
           && !string.IsNullOrWhiteSpace(request.WatchPath)
           && !string.IsNullOrWhiteSpace(request.JobId);

    private void ReportGateAcquired(PipelineGateContext gate)
    {
        try
        {
            _health?.GateAcquired(gate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "build_test_gate_health_observer_failed phase=acquired gate_run_id={GateRunId} project={Project} job_id={JobId}",
                gate.GateRunId,
                gate.Project,
                gate.JobId);
        }
    }

    private void ReportGateCompleted(PipelineGateCompletion completion)
    {
        try
        {
            _health?.GateCompleted(completion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "build_test_gate_health_observer_failed phase=completed gate_run_id={GateRunId} project={Project} job_id={JobId}",
                completion.GateRunId,
                completion.Project,
                completion.JobId);
        }
    }

    private async Task<BuildTestGateResult> RunCommandsAsync(
        string repositoryPath,
        IReadOnlyList<GatePreparationCommand> preparation,
        IReadOnlyList<VerifyCommand> commands,
        string planSource,
        PostStepMode mode,
        TimeSpan timeout,
        IReadOnlyList<string> cacheRestoreMessages,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var output = new RingOutput(MaxOutputLines);
        var evidence = new List<BuildTestGateProcessEvidence>();
        var findings = new List<BuildTestGateFinding>();
        var dependencyCache = new List<BuildTestGateDependencyCacheEvidence>();
        output.AppendLine($"# verify plan: {planSource} ({commands.Count} command(s))");
        foreach (var message in cacheRestoreMessages) output.AppendLine($"# {message}");
        var ranBackend = false;
        var ranFrontend = false;

        foreach (var command in preparation)
        {
            var workingDirectory = ResolveWorkingDirectory(repositoryPath, command.WorkingSubdir);
            if (!Directory.Exists(workingDirectory))
            {
                return WithFailure(new BuildTestGateResult(
                    BuildTestGateVerdict.Fail, null, sw.ElapsedMilliseconds, output.Text,
                    $"dependency preparation directory is missing: {workingDirectory}",
                    ranBackend, ranFrontend)
                {
                    Processes = evidence,
                    DependencyCache = dependencyCache,
                }, BuildTestGateFailureKind.MissingSource);
            }

            output.AppendLine($"# dependency preparation: {workingDirectory}");
            var decisions = command.DependencyScopes
                .Select(scope =>
                {
                    var installRoot = ResolveWorkingDirectory(repositoryPath, scope.WorkingSubdir);
                    return new DependencyPreparationDecision(
                        scope,
                        installRoot,
                        DependencyPreparationState.Evaluate(
                            installRoot,
                            new ReviewDependencyScopeDto(
                                scope.WorkingSubdir,
                                scope.Lockfiles)));
                })
                .ToArray();
            var installNeeded = decisions.Length == 0
                || decisions.Any(item => item.Decision.State != "hit");
            if (!installNeeded)
            {
                foreach (var item in decisions)
                {
                    dependencyCache.Add(ToCacheEvidence(item, installRan: false));
                    output.AppendLine(
                        $"# dependency-cache hit scope={DisplayScope(item.Scope.WorkingSubdir)} " +
                        $"reason={item.Decision.Reason} lockHash={item.Decision.LockHash}");
                }
                continue;
            }

            var elapsedBefore = sw.Elapsed;
            var process = await RunShellAsync(
                workingDirectory,
                command.Command,
                command.Shell,
                Remaining(timeout, elapsedBefore),
                timeout,
                elapsedBefore,
                output,
                ct,
                phase: "preparation").ConfigureAwait(false);
            evidence.Add(process);
            if (process.ExitCode != 0 || process.TimedOut || process.Cancelled || process.LaunchError is not null)
            {
                sw.Stop();
                var kind = ClassifyFailure(process);
                var verdict = kind == BuildTestGateFailureKind.Code && mode != PostStepMode.Fail
                    ? BuildTestGateVerdict.Warn
                    : BuildTestGateVerdict.Fail;
                var reason = FailureReason($"dependency preparation `{command.Command}`", process);
                return WithFailure(new BuildTestGateResult(
                    verdict, process.ExitCode, sw.ElapsedMilliseconds, output.Text,
                    reason, ranBackend, ranFrontend)
                {
                    Processes = evidence,
                    Findings = findings,
                    DependencyCache = decisions
                        .Select(item => ToCacheEvidence(item, installRan: true))
                        .Concat(dependencyCache)
                        .ToArray(),
                    TerminationSignal = process.TerminationSignal,
                    ViolatedBudget = process.ViolatedBudget,
                }, kind);
            }
            foreach (var item in decisions)
            {
                if (!string.IsNullOrWhiteSpace(item.Decision.LockHash))
                    DependencyPreparationState.Stamp(item.InstallRoot, item.Decision.LockHash);
                dependencyCache.Add(ToCacheEvidence(item, installRan: true));
                output.AppendLine(
                    $"# dependency-cache miss scope={DisplayScope(item.Scope.WorkingSubdir)} " +
                    $"reason={item.Decision.Reason} installRan=true lockHash={item.Decision.LockHash}");
            }
        }

        foreach (var command in commands)
        {
            var workingDirectory = ResolveWorkingDirectory(repositoryPath, command);
            if (!Directory.Exists(workingDirectory))
            {
                return WithFailure(new BuildTestGateResult(
                    BuildTestGateVerdict.Fail, null, sw.ElapsedMilliseconds, output.Text,
                    $"verify command directory is missing: {workingDirectory}", ranBackend, ranFrontend)
                {
                    Processes = evidence,
                    DependencyCache = dependencyCache,
                }, BuildTestGateFailureKind.MissingSource);
            }

            if (command.Ecosystem == VerifyEcosystem.Node) ranFrontend = true;
            else ranBackend = true;
            output.AppendLine($"# working directory: {workingDirectory}");

            var elapsedBefore = sw.Elapsed;
            var process = await RunShellAsync(
                workingDirectory,
                command.Command,
                command.Shell,
                Remaining(timeout, elapsedBefore),
                timeout,
                elapsedBefore,
                output,
                ct,
                phase: "verification")
                .ConfigureAwait(false);
            evidence.Add(process);
            if (process.ExitCode != 0 || process.TimedOut || process.Cancelled || process.LaunchError is not null)
            {
                var kind = ClassifyFailure(process);
                if (kind == BuildTestGateFailureKind.Code && !command.BlocksWorkPackage)
                {
                    findings.Add(new BuildTestGateFinding(
                        "out-of-work-package-test-failure",
                        command.TestScope,
                        command.Command,
                        $"{Describe(command)} failed outside the selected work package",
                        process.ExitCode,
                        LastEvidence(process)));
                    output.AppendLine("# non-blocking finding: continuous test failure recorded separately");
                    continue;
                }
                sw.Stop();
                var verdict = kind == BuildTestGateFailureKind.Code && mode != PostStepMode.Fail
                    ? BuildTestGateVerdict.Warn
                    : BuildTestGateVerdict.Fail;
                var reason = FailureReason(Describe(command), process);
                return WithFailure(new BuildTestGateResult(
                    verdict, process.ExitCode, sw.ElapsedMilliseconds, output.Text,
                    reason, ranBackend, ranFrontend)
                {
                    Processes = evidence,
                    Findings = findings,
                    DependencyCache = dependencyCache,
                    TerminationSignal = process.TerminationSignal,
                    ViolatedBudget = process.ViolatedBudget,
                }, kind);
            }
        }

        sw.Stop();
        return new BuildTestGateResult(
            findings.Count == 0 ? BuildTestGateVerdict.Ok : BuildTestGateVerdict.Warn,
            0, sw.ElapsedMilliseconds, output.Text,
            findings.Count == 0
                ? $"verify gate passed ({planSource})"
                : $"work-package gate passed with {findings.Count} separate non-blocking finding(s)",
            ranBackend, ranFrontend)
        {
            Processes = evidence,
            Findings = findings,
            DependencyCache = dependencyCache,
        };
    }

    private sealed record DependencyPreparationDecision(
        GateDependencyScope Scope,
        string InstallRoot,
        ReviewDependencyCacheEvidenceDto Decision);

    private static BuildTestGateDependencyCacheEvidence ToCacheEvidence(
        DependencyPreparationDecision item,
        bool installRan)
        => new(
            DisplayScope(item.Scope.WorkingSubdir),
            item.Decision.State,
            item.Decision.Reason,
            item.Decision.LockHash,
            item.Decision.Lockfiles,
            installRan);

    private static string DisplayScope(string workingSubdir)
        => string.IsNullOrWhiteSpace(workingSubdir) ? "." : workingSubdir;

    private static string CoverageReason(string reason, TestSelectionAudit audit)
    {
        var omitted = audit.OmittedTestCommands.Count;
        return $"{reason}; test-level={audit.Level}; selected={audit.SelectedCommands.Count}; " +
               (audit.FullSuiteRan
                   ? audit.FullSuiteRequired ? "full-suite=required-and-run" : "full-suite=run-conservatively"
                   : $"full-suite=not-run; omitted={omitted}");
    }

    private static TestSelectionAudit CompleteAudit(
        TestSelectionAudit audit,
        IReadOnlyList<VerifyCommand> commands,
        IReadOnlyList<BuildTestGateProcessEvidence> processes)
    {
        if (audit.Level != TestExecutionLevels.Full) return audit;

        // Evidence is appended once per attempted command and commands execute
        // sequentially. A failure can stop the loop, so only the matching prefix
        // is known to have run. An empty declared test inventory is complete
        // after the remaining verify commands finish successfully; the verdict
        // still guards that case at the pre-main boundary.
        var verificationProcesses = processes
            .Where(process => !string.Equals(process.Phase, "preparation", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var attemptedCount = Math.Min(commands.Count, verificationProcesses.Length);
        var allTestsAttempted = commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Kind == VerifyCommandKind.Test)
            .All(item => item.index < attemptedCount
                && verificationProcesses[item.index].LaunchError is null);
        var notRun = commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Kind == VerifyCommandKind.Test
                && (item.index >= attemptedCount || verificationProcesses[item.index].LaunchError is not null))
            .Select(item => TestSelectionPlanner.Describe(item.command));
        return audit with
        {
            FullSuiteRan = allTestsAttempted,
            OmittedTestCommands = audit.OmittedTestCommands
                .Concat(notRun)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static string LastEvidence(BuildTestGateProcessEvidence process)
    {
        var text = string.Join('\n', process.StandardOutput, process.StandardError);
        var lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(40));
    }

    private static string FailureReason(
        string commandDescription,
        BuildTestGateProcessEvidence process,
        string suffix = "")
    {
        var excerpt = FailureOutputExcerpt(process);
        if (process.ViolatedBudget is not null)
            return BudgetFailureReason(
                commandDescription + suffix,
                process.ViolatedBudget,
                excerpt);
        return $"{commandDescription} exit {process.ExitCode?.ToString() ?? "n/a"}{suffix}" +
               (string.IsNullOrWhiteSpace(excerpt) ? string.Empty : $"; output: {excerpt}");
    }

    private static BuildTestGateBudgetEvidence NewBudgetEvidence(
        string name,
        TimeSpan limit,
        TimeSpan consumed,
        string phase)
        => new(
            name,
            Math.Max(1, (long)Math.Round(limit.TotalMilliseconds)),
            Math.Max(0, (long)Math.Round(consumed.TotalMilliseconds)),
            phase);

    private static string BudgetFailureReason(
        string operation,
        BuildTestGateBudgetEvidence? budget,
        string? detail = null)
    {
        var prefix = budget is null
            ? $"{operation} failed"
            : $"{operation} violated {budget.Name} budget " +
              $"(limit={budget.LimitMs}ms, consumed={budget.ConsumedMs}ms, phase={budget.Phase})";
        if (string.IsNullOrWhiteSpace(detail)) return prefix;
        var normalized = Whitespace.Replace(detail, " ").Trim();
        if (normalized.Length > 900) normalized = "..." + normalized[^900..];
        return $"{prefix}; evidence: {normalized}";
    }

    /// <summary>
    /// Returns a single-line, bounded stdout/stderr excerpt suitable for the
    /// durable gate reason stored in <c>pipeline-execution.json</c>. The detailed
    /// streams remain available in process evidence and the gate log.
    /// </summary>
    internal static string FailureOutputExcerpt(BuildTestGateProcessEvidence process)
    {
        var parts = new List<string>();
        Append("stderr", process.StandardError);
        Append("stdout", process.StandardOutput);
        Append("launch", process.LaunchError);
        var excerpt = string.Join(" | ", parts);
        return excerpt.Length <= MaxFailureExcerptChars
            ? excerpt
            : excerpt[..(MaxFailureExcerptChars - 3)].TrimEnd() + "...";

        void Append(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var normalized = Whitespace.Replace(value, " ").Trim();
            const int perStreamLimit = 900;
            if (normalized.Length > perStreamLimit)
                normalized = "..." + normalized[^perStreamLimit..];
            parts.Add($"{label}: {normalized}");
        }
    }

    /// <summary>
    /// The budget for WAITING in the machine-gate queue for the one running gate
    /// ahead to finish. The machine lock is held for a gate's entire build+test run
    /// (15-25 min in production), so a queued card must be willing to wait roughly
    /// one full run - NOT the short infra-op SLA. Budgeting the queue wait against
    /// the infra SLA made every card queued behind a running gate escalate as
    /// "Timeout persisted" after 120 s (AGT-2182, 21.07.). An explicit
    /// <paramref name="configured"/> value wins; otherwise derive run-timeout plus
    /// infra-timeout so one full run ahead is tolerated.
    /// </summary>
    internal static TimeSpan ResolveQueueWaitTimeout(
        TimeSpan? configured,
        TimeSpan runTimeout,
        TimeSpan infrastructureTimeout)
    {
        if (configured is { } value && value > TimeSpan.Zero)
            return value;
        var run = runTimeout > TimeSpan.Zero ? runTimeout : TimeSpan.Zero;
        var infra = infrastructureTimeout > TimeSpan.Zero ? infrastructureTimeout : TimeSpan.Zero;
        var derived = run + infra;
        return derived > TimeSpan.Zero ? derived : TimeSpan.FromMinutes(2);
    }

    private static async Task<MachineGateAcquisition> AcquireMachineGateAsync(
        TimeSpan queueWaitTimeout,
        Action? onWaiting,
        CancellationToken ct)
    {
        var wait = Stopwatch.StartNew();
        var collision = !await ProcessGate.WaitAsync(0, ct).ConfigureAwait(false);
        if (collision) onWaiting?.Invoke();
        var ownsProcessGate = !collision;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(queueWaitTimeout);
        try
        {
            if (!ownsProcessGate)
            {
                await ProcessGate.WaitAsync(bounded.Token).ConfigureAwait(false);
                ownsProcessGate = true;
            }

            while (true)
            {
                bounded.Token.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        MachineGateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        OperatingSystem.IsWindows() ? FileShare.None : FileShare.ReadWrite,
                        bufferSize: 1, FileOptions.None);
                    if (!OperatingSystem.IsWindows() && !NativeFileLock.TryAcquireExclusive(stream))
                    {
                        stream.Dispose();
                        if (!collision) onWaiting?.Invoke();
                        collision = true;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), bounded.Token).ConfigureAwait(false);
                        continue;
                    }

                    wait.Stop();
                    return MachineGateAcquisition.Acquired(
                        new MachineGateLease(stream, wait.ElapsedMilliseconds, collision));
                }
                catch (IOException)
                {
                    collision = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), bounded.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (ownsProcessGate) ProcessGate.Release();
            wait.Stop();
            return MachineGateAcquisition.TimedOut(wait.ElapsedMilliseconds, collision,
                BudgetFailureReason(
                    "machine build/test gate queue wait",
                    NewBudgetEvidence(
                        "machine-gate-queue",
                        queueWaitTimeout,
                        wait.Elapsed,
                        "queue")),
                queueWaitTimeout);
        }
        catch
        {
            if (ownsProcessGate) ProcessGate.Release();
            throw;
        }
    }

    private static class NativeFileLock
    {
        private const int LockExclusive = 2;
        private const int LockNonBlocking = 4;

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int Flock(int fileDescriptor, int operation);

        public static bool TryAcquireExclusive(FileStream stream)
        {
            if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                    LockExclusive | LockNonBlocking) == 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            if (error is 4 or 11 or 35) return false;
            throw new InvalidOperationException(
                $"Could not acquire the build/test machine lock (flock errno {error}).");
        }
    }

    private sealed record MachineGateAcquisition(
        MachineGateLease? Lease,
        long QueueWaitMs,
        bool CollisionDetected,
        string Reason,
        BuildTestGateBudgetEvidence? ViolatedBudget)
    {
        public static MachineGateAcquisition Acquired(MachineGateLease lease)
            => new(lease, lease.QueueWaitMs, lease.CollisionDetected, string.Empty, null);

        public static MachineGateAcquisition TimedOut(
            long waitMs,
            bool collision,
            string reason,
            TimeSpan? limit = null)
            => new(
                null,
                waitMs,
                collision,
                reason,
                NewBudgetEvidence(
                    "machine-gate-queue",
                    limit ?? TimeSpan.FromMilliseconds(Math.Max(1, waitMs)),
                    TimeSpan.FromMilliseconds(waitMs),
                    "queue"));
    }

    private sealed class MachineGateLease : IDisposable
    {
        private readonly FileStream _stream;
        private bool _disposed;

        public MachineGateLease(FileStream stream, long queueWaitMs, bool collisionDetected)
        {
            _stream = stream;
            QueueWaitMs = queueWaitMs;
            CollisionDetected = collisionDetected;
        }

        public long QueueWaitMs { get; }
        public bool CollisionDetected { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream.Dispose(); }
            finally { ProcessGate.Release(); }
        }
    }

    private async Task<WorkspacePreparation> PrepareExactWorkspaceAsync(
        string repositoryPath,
        string? expectedSha,
        string? subjectRef,
        string gateRunId,
        TimeSpan infrastructureTimeout,
        CancellationToken ct)
    {
        if (!Directory.Exists(repositoryPath))
            return WorkspacePreparation.Failed(BuildTestGateFailureKind.MissingSource,
                $"repository not found: {repositoryPath}");
        if (string.IsNullOrWhiteSpace(expectedSha) || !SafeSha.IsMatch(expectedSha))
            return WorkspacePreparation.Failed(BuildTestGateFailureKind.MissingSource,
                "exact review subject SHA is missing or invalid");

        var stopwatch = Stopwatch.StartNew();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(infrastructureTimeout);
        try
        {
            Directory.CreateDirectory(ReviewWorkspaceRoot);
            var selfHealed = false;
            var available = await RunGitAsync(
                repositoryPath, ["cat-file", "-e", expectedSha + "^{commit}"],
                Remaining(infrastructureTimeout, stopwatch.Elapsed), bounded.Token)
                .ConfigureAwait(false);
            if (available.ExitCode != 0)
            {
                var fetchTargets = SubjectFetchTargets(expectedSha, subjectRef);
                var fetchTarget = fetchTargets[0];
                var fetch = await RunGitAsync(
                    repositoryPath, ["fetch", "--no-tags", "origin", fetchTarget!],
                    Remaining(infrastructureTimeout, stopwatch.Elapsed), bounded.Token)
                    .ConfigureAwait(false);
                available = await RunGitAsync(
                    repositoryPath, ["cat-file", "-e", expectedSha + "^{commit}"],
                    Remaining(infrastructureTimeout, stopwatch.Elapsed), bounded.Token)
                    .ConfigureAwait(false);
                if (available.ExitCode != 0)
                {
                    // A named subject can disappear after the result envelope is
                    // accepted. Ask origin for the immutable object id as the
                    // only fallback. Never mirror every origin branch into the
                    // Task Server checkout: result and quarantine namespaces can
                    // contain thousands of refs and are indexed by attempt
                    // authority already.
                    var shaFetch = fetchTargets.Count == 1
                        ? fetch
                        : await RunGitAsync(
                            repositoryPath,
                            ["fetch", "--no-tags", "origin", fetchTargets[1]],
                            Remaining(infrastructureTimeout, stopwatch.Elapsed),
                            bounded.Token).ConfigureAwait(false);
                    available = await RunGitAsync(
                        repositoryPath, ["cat-file", "-e", expectedSha + "^{commit}"],
                        Remaining(infrastructureTimeout, stopwatch.Elapsed), bounded.Token)
                        .ConfigureAwait(false);
                    if (available.ExitCode != 0)
                    {
                        var fetchEvidence =
                            $"targeted fetch:\n{fetch.StandardOutput}\n{fetch.StandardError}\n" +
                            $"immutable SHA fetch:\n{shaFetch.StandardOutput}\n{shaFetch.StandardError}\n" +
                            $"subject probe:\n{available.StandardOutput}\n{available.StandardError}";
                        return WorkspacePreparation.Failed(
                            ClassifyInfrastructureOrMissing(fetchEvidence),
                            "exact review subject could not be fetched",
                            fetchEvidence,
                            FirstInfrastructureBudget(
                                infrastructureTimeout,
                                stopwatch.Elapsed,
                                "materialization",
                                fetch,
                                shaFetch,
                                available));
                    }

                    selfHealed = true;
                    _logger.LogInformation(
                        "build_test_gate_subject_self_healed repository={Repository} expected_sha={ExpectedSha} self_healed={SelfHealed}",
                        repositoryPath, expectedSha, true);
                }
            }

            var workspace = Path.Combine(ReviewWorkspaceRoot, gateRunId);
            var add = await RunGitAsync(
                repositoryPath, ["worktree", "add", "--detach", workspace, expectedSha],
                Remaining(infrastructureTimeout, stopwatch.Elapsed), bounded.Token)
                .ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                var addEvidence = add.StandardOutput + "\n" + add.StandardError;
                return WorkspacePreparation.Failed(
                    ClassifyInfrastructureOrMissing(addEvidence),
                    "exact review workspace could not be created",
                    addEvidence,
                    FirstInfrastructureBudget(
                        infrastructureTimeout,
                        stopwatch.Elapsed,
                        "materialization",
                        add));
            }

            var lease = new ExactWorkspaceLease(repositoryPath, workspace, "missing", _logger);
            string? testedSha;
            try
            {
                testedSha = await ReadHeadShaAsync(
                        workspace,
                        Remaining(infrastructureTimeout, stopwatch.Elapsed),
                        bounded.Token)
                    .ConfigureAwait(false);
                lease.SetTestedSha(testedSha ?? "missing");
            }
            catch
            {
                await lease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
                throw;
            }
            if (!string.Equals(expectedSha, testedSha, StringComparison.OrdinalIgnoreCase))
            {
                await lease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
                return WorkspacePreparation.Failed(BuildTestGateFailureKind.MissingSource,
                    $"exact review subject mismatch: expected {expectedSha}, tested {testedSha ?? "missing"}");
            }

            _logger.LogInformation(
                "build_test_gate_workspace_ready repository={Repository} expected_sha={ExpectedSha} tested_sha={TestedSha} workspace={Workspace} self_healed={SelfHealed}",
                repositoryPath, expectedSha, testedSha, workspace, selfHealed);
            return WorkspacePreparation.Ready(lease, selfHealed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            var budget = NewBudgetEvidence(
                "workspace-materialization",
                infrastructureTimeout,
                stopwatch.Elapsed,
                "materialization");
            return WorkspacePreparation.Failed(BuildTestGateFailureKind.Timeout,
                BudgetFailureReason("exact review subject materialization", budget),
                violatedBudget: budget);
        }
    }

    internal static IReadOnlyList<string> SubjectFetchTargets(string expectedSha, string? subjectRef)
    {
        var targets = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(subjectRef)) targets.Add(subjectRef.Trim());
        if (targets.Count == 0 || !string.Equals(targets[0], expectedSha, StringComparison.OrdinalIgnoreCase))
            targets.Add(expectedSha);
        return targets;
    }

    private static BuildTestGateBudgetEvidence? FirstInfrastructureBudget(
        TimeSpan limit,
        TimeSpan consumed,
        string phase,
        params GitCommandResult[] commands)
        => commands.Any(command => command.FailureKind == GitProcessFailureKind.TimedOut)
            ? NewBudgetEvidence($"workspace-{phase}", limit, consumed, phase)
            : null;

    private static BuildTestGateFailureKind ClassifyInfrastructureOrMissing(string evidence)
    {
        var classified = ClassifyFailure(evidence);
        return classified == BuildTestGateFailureKind.None
            ? BuildTestGateFailureKind.MissingSource
            : classified;
    }

    private static async Task<string?> ReadHeadShaAsync(
        string path,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        var result = await RunGitAsync(path, ["rev-parse", "HEAD"], timeout, bounded.Token)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
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
        return new GitCommandResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.FailureKind);
    }

    private sealed record GitCommandResult(
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        GitProcessFailureKind FailureKind);

    private sealed record WorkspacePreparation(
        ExactWorkspaceLease? Lease,
        BuildTestGateFailureKind FailureKind,
        string Reason,
        string Output,
        bool SelfHealed,
        BuildTestGateBudgetEvidence? ViolatedBudget)
    {
        public static WorkspacePreparation Ready(ExactWorkspaceLease lease, bool selfHealed)
            => new(lease, BuildTestGateFailureKind.None, string.Empty, string.Empty, selfHealed, null);

        public static WorkspacePreparation Failed(
            BuildTestGateFailureKind kind,
            string reason,
            string output = "",
            BuildTestGateBudgetEvidence? violatedBudget = null)
            => new(null, kind, reason, output, false, violatedBudget);
    }

    private sealed record WorkspaceCleanupError(
        BuildTestGateFailureKind FailureKind,
        string Evidence,
        BuildTestGateBudgetEvidence? ViolatedBudget);

    private sealed class ExactWorkspaceLease
    {
        private readonly string _repositoryPath;
        private readonly ILogger _logger;
        private bool _removed;

        public ExactWorkspaceLease(string repositoryPath, string path, string testedSha, ILogger logger)
        {
            _repositoryPath = repositoryPath;
            Path = path;
            TestedSha = testedSha;
            _logger = logger;
        }

        public string Path { get; }
        public string TestedSha { get; private set; }

        public void SetTestedSha(string testedSha)
            => TestedSha = testedSha;

        public async Task<WorkspaceCleanupError?> RemoveAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_removed) return null;
            var stopwatch = Stopwatch.StartNew();
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            var evidence = new StringBuilder();
            try
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    var remove = await RunGitAsync(
                        _repositoryPath,
                        ["worktree", "remove", "--force", Path],
                        Remaining(timeout, stopwatch.Elapsed),
                        bounded.Token)
                        .ConfigureAwait(false);
                    if (remove.ExitCode == 0)
                    {
                        _removed = true;
                        return null;
                    }
                    evidence.AppendLine($"cleanup attempt {attempt}: {remove.StandardOutput} {remove.StandardError}".Trim());
                    if (remove.FailureKind == GitProcessFailureKind.TimedOut)
                    {
                        stopwatch.Stop();
                        var budget = NewBudgetEvidence(
                            "workspace-cleanup", timeout, stopwatch.Elapsed, "cleanup");
                        return new WorkspaceCleanupError(
                            BuildTestGateFailureKind.Timeout,
                            evidence.ToString().Trim(),
                            budget);
                    }

                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                stopwatch.Stop();
                var budget = NewBudgetEvidence(
                    "workspace-cleanup", timeout, stopwatch.Elapsed, "cleanup");
                return new WorkspaceCleanupError(
                    BuildTestGateFailureKind.Timeout,
                    evidence.Append("cleanup infrastructure SLA expired").ToString(),
                    budget);
            }
            return new WorkspaceCleanupError(
                ClassifyInfrastructureOrMissing(evidence.ToString()),
                evidence.ToString().Trim(),
                null);
        }

        public async Task RemoveBestEffortAsync(TimeSpan timeout)
        {
            try
            {
                var error = await RemoveAsync(timeout, CancellationToken.None).ConfigureAwait(false);
                if (error is not null)
                {
                    _logger.LogWarning(
                        "BuildTestGateRunner: exact workspace cleanup failed for {Workspace}: {Error}",
                        Path, error.Evidence);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "BuildTestGateRunner: exact workspace cleanup threw for {Workspace}", Path);
            }
        }
    }

    private Task<BuildTestGateProcessEvidence> RunShellAsync(
        string workingDirectory,
        string command,
        VerifyCommandShell shell,
        TimeSpan processTimeout,
        TimeSpan budgetLimit,
        TimeSpan elapsedBefore,
        RingOutput output,
        CancellationToken ct,
        string phase)
    {
        var (fileName, args) = shell == VerifyCommandShell.Bash
            ? (BashExecutable.Path, (IReadOnlyList<string>)["-lc", command])
            : OperatingSystem.IsWindows()
                ? ("cmd.exe", (IReadOnlyList<string>)["/c", command])
                : ("/bin/sh", (IReadOnlyList<string>)["-c", command]);
        return RunProcessAsync(
            workingDirectory, command, fileName, args, processTimeout,
            budgetLimit, elapsedBefore, output, ct, phase);
    }

    private async Task<BuildTestGateProcessEvidence> RunProcessAsync(
        string workingDirectory,
        string command,
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan processTimeout,
        TimeSpan budgetLimit,
        TimeSpan elapsedBefore,
        RingOutput output,
        CancellationToken ct,
        string phase)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["NPM_CONFIG_CACHE"] = NpmCachePath;
        output.AppendLine($"> {fileName} {string.Join(' ', args)}");

        Process? process;
        try
        {
            Directory.CreateDirectory(NpmCachePath);
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuildTestGateRunner: Process.Start failed for {FileName}", fileName);
            output.AppendLine(ex.Message);
            return NewProcessEvidence(startedAt, command, fileName, args, workingDirectory, phase) with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LaunchError = ex.Message,
            };
        }
        if (process is null)
        {
            const string error = "Process.Start returned null";
            output.AppendLine(error);
            return NewProcessEvidence(startedAt, command, fileName, args, workingDirectory, phase) with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LaunchError = error,
            };
        }

        using (process)
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(processTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = ct.IsCancellationRequested;
                timedOut = !cancelled;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: process tree kill"); }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: process exit after kill"); }
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            output.AppendBlock("stdout", stdout);
            output.AppendBlock("stderr", stderr);
            var completedAt = DateTimeOffset.UtcNow;
            BuildTestGateBudgetEvidence? violatedBudget = null;
            if (timedOut)
            {
                var consumed = elapsedBefore + (completedAt - startedAt);
                violatedBudget = NewBudgetEvidence("gate-run", budgetLimit, consumed, phase);
                output.AppendLine(
                    $"{fileName} violated gate-run budget " +
                    $"limit={violatedBudget.LimitMs}ms consumed={violatedBudget.ConsumedMs}ms phase={phase}");
            }
            if (cancelled) output.AppendLine($"{fileName} was cancelled");
            int? exitCode = process.HasExited ? process.ExitCode : null;
            var signal = ResolveTerminationSignal(exitCode, timedOut, cancelled);
            return NewProcessEvidence(startedAt, command, fileName, args, workingDirectory, phase) with
            {
                CompletedAtUtc = completedAt,
                ExitCode = exitCode,
                TerminationSignal = signal,
                TimedOut = timedOut,
                Cancelled = cancelled,
                StandardOutput = stdout,
                StandardError = stderr,
                ViolatedBudget = violatedBudget,
            };
        }
    }

    private static BuildTestGateProcessEvidence NewProcessEvidence(
        DateTimeOffset startedAt,
        string command,
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        string phase)
        => new()
        {
            Phase = phase,
            Command = command,
            FileName = fileName,
            Arguments = args.ToArray(),
            WorkingDirectory = workingDirectory,
            StartedAtUtc = startedAt,
            CompletedAtUtc = startedAt,
        };

    private static string? ResolveTerminationSignal(int? exitCode, bool timedOut, bool cancelled)
    {
        if (timedOut) return "timeout";
        if (cancelled) return "cancellation";
        if (OperatingSystem.IsWindows() || exitCode is null || exitCode < 128) return null;
        return exitCode switch
        {
            137 => "SIGKILL",
            143 => "SIGTERM",
            _ => "signal-" + (exitCode - 128),
        };
    }

    internal static BuildTestGateFailureKind ClassifyFailure(BuildTestGateProcessEvidence process)
    {
        if (process.LaunchError is not null) return BuildTestGateFailureKind.ProcessLaunch;
        if (process.Cancelled) return BuildTestGateFailureKind.Cancellation;
        if (process.TimedOut) return BuildTestGateFailureKind.Timeout;
        if (process.ExitCode == 137 || string.Equals(process.TerminationSignal, "SIGKILL", StringComparison.Ordinal))
            return BuildTestGateFailureKind.OutOfMemory;
        var evidence = process.StandardError + "\n" + process.StandardOutput;
        var classified = ClassifyFailure(evidence);
        if (classified == BuildTestGateFailureKind.None)
            return BuildTestGateFailureKind.Code;
        // A verify command that ran to completion and returned an exit code was NOT
        // prevented from running by the host: whatever lock / OOM / timeout string it
        // printed is its own reported result - e.g. a test that logs an
        // IOException "... because it is being used by another process" on its temp
        // DB files (AGT-2110, 21.07.). Treating such a DETERMINISTIC test failure as
        // review infrastructure poisoned the environmental-retry budget: the same
        // 15-25 min build+test was re-run twice more, each time holding the machine
        // gate and starving every queued card, before escalating "Lock persisted".
        // Only a genuine MSBuild build-output lock (MSB3026/MSB3027) is a real,
        // retryable host fault; every other string from a completed process is a
        // code/test defect that must flow through the normal reissue path instead.
        if (CompletedNormally(process) && !IsGenuineBuildOutputLock(evidence))
            return BuildTestGateFailureKind.Code;
        return classified;
    }

    private static bool CompletedNormally(BuildTestGateProcessEvidence process)
        => process.LaunchError is null
           && !process.TimedOut
           && !process.Cancelled
           && process.ExitCode is not null
           && process.ExitCode != 137;

    private static bool IsGenuineBuildOutputLock(string evidence)
        => evidence.Contains("MSB3026", StringComparison.OrdinalIgnoreCase)
           || evidence.Contains("MSB3027", StringComparison.OrdinalIgnoreCase);

    internal static BuildTestGateFailureKind ClassifyFailure(string? text)
    {
        var value = text ?? string.Empty;
        if (ContainsAny(value,
                "being used by another process", "file is locked", "cannot access the file",
                "resource temporarily unavailable", "sharing violation", "MSB3026", "MSB3027"))
            return BuildTestGateFailureKind.Lock;
        if (ContainsAny(value,
                "out of memory", "outofmemoryexception", "cannot allocate memory", "heap limit"))
            return BuildTestGateFailureKind.OutOfMemory;
        if (ContainsAny(value,
                "timed out after", "deadline exceeded", "operation exceeded its time limit"))
            return BuildTestGateFailureKind.Timeout;
        if (ContainsAny(value,
                "process.start failed", "process.start returned null", "failed to start process",
                "executable file not found"))
            return BuildTestGateFailureKind.ProcessLaunch;
        if (ContainsAny(value, "operation was cancelled", "operation was canceled", "operationcanceledexception"))
            return BuildTestGateFailureKind.Cancellation;
        if (ContainsAny(value,
                "repository not found", "missing source", "bad object", "not a git repository",
                "unknown revision", "not a valid object name", "couldn't find remote ref"))
            return BuildTestGateFailureKind.MissingSource;
        if (ContainsAny(value, "review model", "model not found", "invalid model", "no parseable verdict"))
            return BuildTestGateFailureKind.ReviewModel;
        return BuildTestGateFailureKind.None;
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    internal static string Fingerprint(BuildTestGateFailureKind kind, string evidence)
    {
        var normalized = VolatileNumber.Replace(
            VolatileHex.Replace(evidence.ToLowerInvariant(), "<sha>"), "<n>");
        normalized = Whitespace.Replace(normalized, " ").Trim();
        if (normalized.Length > 8_192) normalized = normalized[^8_192..];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..16];
        return $"{kind.ToString().ToLowerInvariant()}:{hash}";
    }

    private static BuildTestGateResult Skipped(string reason)
        => new(BuildTestGateVerdict.Skipped, null, 0, string.Empty, reason, false, false);

    private static BuildTestGateResult NotApplicable(string reason)
        => new(BuildTestGateVerdict.NotApplicable, null, 0, string.Empty, reason, false, false);

    private static BuildTestGateResult InfrastructureFailure(
        BuildTestGateFailureKind kind,
        string reason,
        string output,
        BuildTestGateBudgetEvidence? violatedBudget = null)
    {
        var honestReason = violatedBudget is null
            || reason.Contains($"{violatedBudget.Name} budget", StringComparison.OrdinalIgnoreCase)
                ? reason
                : BudgetFailureReason(reason, violatedBudget, output);
        return WithFailure(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 0, output, honestReason, false, false)
        {
            ViolatedBudget = violatedBudget,
        }, kind);
    }

    private static BuildTestGateResult SaveDependencyCache(
        BuildTestGateResult result,
        GateDependencyCacheSession? session)
    {
        if (session is null) return result;
        var output = result.Output;
        foreach (var message in session.Save())
            output = AppendOutput(output, $"# {message}");
        return result with { Output = output };
    }

    private static string AppendOutput(string current, string? addition)
    {
        if (string.IsNullOrWhiteSpace(addition)) return current;
        if (string.IsNullOrWhiteSpace(current)) return addition.Trim();
        return current.TrimEnd() + Environment.NewLine + addition.Trim();
    }

    private static BuildTestGateResult WithFailure(
        BuildTestGateResult result,
        BuildTestGateFailureKind kind)
    {
        var processEvidence = string.Join("\n", result.Processes.Select(p =>
            $"{p.Command}\nexit={p.ExitCode}\nsignal={p.TerminationSignal}\nlaunch={p.LaunchError}" +
            $"\nbudget={p.ViolatedBudget?.Name}\n{p.StandardOutput}\n{p.StandardError}"));
        return result with
        {
            FailureKind = kind,
            FailureFingerprint = Fingerprint(kind, result.Reason + "\n" + result.Output + "\n" + processEvidence),
            TerminationSignal = result.TerminationSignal
                ?? (result.ExitCode is null ? kind.ToString().ToLowerInvariant() : null),
        };
    }

    internal static bool ShouldRunForChange(VerifyCommand command, IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null) return true;
        // Staged test selection has already applied diff, ownership, Test Hub,
        // and optional model evidence. Re-applying the legacy package-prefix
        // filter here would silently discard cross-package tests selected from
        // history or by the adviser. It would also make an explicit full run
        // smaller than the declared suite.
        if (command.Kind == VerifyCommandKind.Test) return true;
        if (command.Ecosystem != VerifyEcosystem.Node || string.IsNullOrEmpty(command.WorkingSubdir))
            return true;
        var prefix = command.WorkingSubdir.Replace('\\', '/').TrimEnd('/') + "/";
        return changedFiles.Any(file =>
            file.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(VerifyCommand command)
    {
        var location = string.IsNullOrEmpty(command.WorkingSubdir) ? string.Empty : $" ({command.WorkingSubdir})";
        return $"`{command.Command}`{location}";
    }

    internal static string ResolveWorkingDirectory(string repositoryPath, VerifyCommand command)
        => ResolveWorkingDirectory(repositoryPath, command.WorkingSubdir);

    internal static string ResolveWorkingDirectory(string repositoryPath, string workingSubdir)
    {
        var repositoryRoot = Path.GetFullPath(repositoryPath);
        return string.IsNullOrEmpty(workingSubdir)
            ? repositoryRoot
            : Path.GetFullPath(Path.Combine(repositoryRoot, workingSubdir));
    }

    // Retained as a diagnostic compatibility helper. Admission itself is now
    // machine-wide, but callers can still compare linked checkout identities.
    internal static string ResolveAdmissionKey(string repositoryPath)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var commonGitDirectory = ReadOnlyGitRefFingerprint.ResolveCommonDirectory(canonical);
        return commonGitDirectory is null
            ? canonical
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonGitDirectory));
    }

    internal static bool HasCodeDiff(IReadOnlyList<string> changedFiles)
        => changedFiles.Any(IsCodePath);

    private static bool IsCodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith(".orchestrator/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(normalized);
        return CodeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private sealed class RingOutput
    {
        private readonly int _capacity;
        private readonly Queue<string> _lines = new();
        private readonly object _lock = new();

        public RingOutput(int capacity) => _capacity = capacity;

        public string Text
        {
            get
            {
                lock (_lock) return string.Join(Environment.NewLine, _lines);
            }
        }

        public void AppendBlock(string stream, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line) AppendLine($"[{stream}] {line}");
        }

        public void AppendLine(string? line)
        {
            if (line is null) return;
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity) _lines.Dequeue();
            }
        }
    }
}
