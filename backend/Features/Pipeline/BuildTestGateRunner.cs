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
    /// <summary>
    /// A verify command's own toolchain/bundler crashed before it reached test
    /// discovery (e.g. vite's case-insensitive-filesystem probe throwing while
    /// loading its config, or a relative worker missing from a package under
    /// node_modules), or its gate-run budget expired without a red test.
    /// Never a product failure; see CAC-18, WEB-19, and AGT-2872.
    /// </summary>
    Environment,
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

    /// <summary>
    /// AGT-2843: where the caller's resolved <c>timeout</c> (the gate-run
    /// budget passed to <see cref="IBuildTestGateRunner.RunAsync"/>) came from
    /// - e.g. an explicit override, a configured key, a project override, or
    /// the <see cref="GateRunBudgetPolicy"/> default - surfaced on
    /// <c>build_test_gate_started</c> so an operator can see why a gate has
    /// the budget it has. Purely diagnostic.
    /// </summary>
    public string? TimeoutBudgetSource { get; init; }

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
    public GateResourceEvidence? Resources { get; init; }
    public GateBudgetExtensionEvidence? BudgetExtension { get; init; }
    public bool FailedTestsObserved { get; init; }
    public IReadOnlyList<GateSlowTest> SlowTests { get; init; } = [];
    public long OriginalBudgetMs { get; init; }

    /// <summary>
    /// <c>preparation</c>, <c>verification</c>, or (AGT-2853)
    /// <c>flaky-rerun</c> for the one targeted re-run of a red test step. The
    /// re-run is deliberately its own phase: it repeats a command the plan
    /// already contains, so the positional coverage audit must not count it as
    /// another planned command.
    /// </summary>
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

public sealed record BuildTestGateDependencyCacheDecision(
    string RepositoryKey,
    bool Restored,
    long? AgeSeconds,
    long SizeBytes,
    bool Evicted,
    string? EvictionReason,
    bool ReranFromScratch,
    bool SavedVerified);

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
    public BuildTestGateDependencyCacheDecision? DependencyCacheDecision { get; init; }
    public BuildTestGateBudgetEvidence? ViolatedBudget { get; init; }
    public TestSelectionAudit? TestSelection { get; init; }
    public IReadOnlyList<BuildTestGateFinding> Findings { get; init; } = [];

    /// <summary>
    /// AGT-2853: a red test step spent its one targeted re-run
    /// (<see cref="GateFlakyRerunPolicy"/>). Same field name and meaning as the
    /// remote review executor's <c>ReviewCommandEvidenceDto.RetryPerformed</c>.
    /// </summary>
    public bool RetryPerformed { get; init; }

    /// <summary>
    /// AGT-2853: the exact test names that failed in the full run and passed on
    /// the targeted re-run. The gate is green with these recorded rather than
    /// silently absorbing them. Same field name as the remote review executor's
    /// <c>ReviewCommandEvidenceDto.FlakyQuarantinedFailures</c>.
    /// </summary>
    public IReadOnlyList<string> FlakyQuarantinedFailures { get; init; } = [];

    /// <summary>
    /// The shared classification both surfaces write for a re-run-cleared
    /// failure, or null when this gate quarantined nothing.
    /// </summary>
    public string? FlakyClassification => FlakyQuarantinedFailures.Count > 0
        ? ReviewFlakyQuarantine.Classification
        : null;

    public ProjectPreparationManifest? PreparationManifest { get; init; }
    public IReadOnlyList<ProjectDefinitionIssue> ProjectDefinitionIssues { get; init; } = [];
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

    /// <summary>Phase stamped on the AGT-2853 targeted re-run of a red test step.</summary>
    internal const string FlakyRerunPhase = "flaky-rerun";
    public const int MaxFailureExcerptChars = 2_000;
    internal const string DependencyCacheDirectoryName = ".dependency-cache";

    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    internal static readonly string MachineGateLockPath = Path.Combine(
        Path.GetTempPath(), "agentstudio-build-test-gate.lock");
    internal static readonly string ReviewWorkspaceRoot = Path.Combine(
        Path.GetTempPath(), "agentstudio-review-gates");
    internal static readonly string NpmCachePath = Path.Combine(
        Path.GetTempPath(), "agentstudio-dependency-cache", "npm");
    internal static readonly string PreparationCacheRoot = Path.Combine(
        Path.GetTempPath(), "agentstudio-preparation-cache");

    /// <summary>
    /// Product cache root for this runner. Only a hermetic test overrides it, so
    /// its published entries and per-run folders stay inside the test's own
    /// temporary directory.
    /// </summary>
    private readonly string _preparationCacheRoot = PreparationCacheRoot;

    private static readonly Regex SafeSha = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VolatileHex = new(
        "\\b[0-9a-fA-F]{7,64}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VolatileNumber = new(
        "\\b\\d+\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(
        "\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MissingRelativeModuleFromNodeModules = new(
        "cannot find module\\s+['\"]\\.{1,2}[\\\\/][^'\"]+['\"][\\s\\S]{0,8192}" +
        "require stack:[\\s\\S]{0,8192}node_modules[\\\\/]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PreparationRunNuGetPath = new(
        "agentstudio-preparation-cache[\\\\/]\\.runs[\\\\/][^\\s'\"\\\\/]+" +
        "[\\\\/]nuget[\\\\/]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
    private readonly Func<int, IGateProcessResources> _resourceFactory = pid => new GateProcessResources(pid);

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
        BuildTestMachineGateMode machineGateMode,
        string? preparationCacheRoot = null,
        Func<int, IGateProcessResources>? resourceFactory = null)
        : this(logger)
    {
        _machineGateMode = machineGateMode;
        if (resourceFactory is not null) _resourceFactory = resourceFactory;
        if (!string.IsNullOrWhiteSpace(preparationCacheRoot))
            _preparationCacheRoot = preparationCacheRoot;
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
            && requestedLevel != TestExecutionLevels.CompileOnly
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
            "build_test_gate_started gate_run_id={GateRunId} gate_id={GateId} started_at_utc={StartedAtUtc:o} repository={Repository} expected_sha={ExpectedSha} attempt_chain_id={AttemptChainId} executor={Executor} budget_limit_ms={BudgetLimitMs} budget_source={BudgetSource}",
            gateRunId, request.GateId, startedAt, repositoryPath,
            request.ExpectedSha ?? "missing", request.AttemptChainId ?? "missing", request.Executor,
            (long)timeout.TotalMilliseconds, request.TimeoutBudgetSource ?? "unspecified");

        MachineGateLease? machineLease = null;
        ExactWorkspaceLease? workspaceLease = null;
        ProjectPreparationResult? projectPreparation = null;
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
                var preparationManifestPath = PreparationManifestPath(
                    repositoryPath, _preparationCacheRoot);
                projectPreparation = await ProjectPreparationExecutor.RunAsync(
                    workspace!,
                    _preparationCacheRoot,
                    preparationManifestPath,
                    testedSha,
                    message => _logger.LogInformation("{ProjectPreparationMessage}", message),
                    timeout,
                    ct).ConfigureAwait(false);
                if (projectPreparation.Configured && !projectPreparation.Succeeded)
                {
                    var gateFailure = projectPreparation.FailureKind is PreparationFailureKind.Command
                        or PreparationFailureKind.Definition
                        ? BuildTestGateFailureKind.Code
                        : BuildTestGateFailureKind.Environment;
                    completed = WithFailure(new BuildTestGateResult(
                        BuildTestGateVerdict.Fail,
                        projectPreparation.ExitCode,
                        projectPreparation.Manifest?.DurationMs ?? 0,
                        projectPreparation.Output,
                        projectPreparation.FailureReason ?? "project preparation failed",
                        false,
                        false)
                    {
                        PreparationManifest = projectPreparation.Manifest,
                        ProjectDefinitionIssues = projectPreparation.DefinitionIssues,
                    }, gateFailure);
                }
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
                    IReadOnlyList<GatePreparationCommand> preparation = projectPreparation?.Configured == true
                        ? []
                        : GatePreparationPlanner.Plan(workspace!, profile, commands);
                    completed = commands.Count == 0
                        ? Skipped($"no verify commands apply to the changed files ({plan.Source}); level={staged.Audit.Level}")
                            with
                        {
                            TestSelection = staged.Audit,
                        }
                        : await RunCommandsAsync(
                            workspace!, preparation, commands, plan.Source, mode, timeout,
                            [], projectPreparation, ct)
                            .ConfigureAwait(false);
                    if (completed.FailureKind == BuildTestGateFailureKind.Environment
                        && IsPreparationCacheNuGetFailure(completed.Output + "\n" + completed.Reason))
                    {
                        foreach (var message in ProjectPreparationExecutor.EvictPublishedBlocks(
                                     projectPreparation,
                                     "nuget",
                                     "gate-environment-failure",
                                     item => _logger.LogWarning("{ProjectPreparationMessage}", item)))
                        {
                            completed = completed with
                            {
                                Output = AppendOutput(completed.Output, "# " + message),
                            };
                        }
                    }
                    var completedAudit = CompleteAudit(staged.Audit, commands, completed.Processes);
                    completed = completed with
                    {
                        TestSelection = completedAudit,
                        Reason = CoverageReason(completed.Reason, completedAudit),
                        PreparationManifest = projectPreparation?.Manifest,
                        ProjectDefinitionIssues = projectPreparation?.DefinitionIssues ?? [],
                    };
                }
            }

            if (workspaceLease is not null)
            {
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
                        Reason = completed.FailureKind == BuildTestGateFailureKind.Code
                            ? completed.Reason + "; " + cleanupReason : cleanupReason,
                        ViolatedBudget = completed.ViolatedBudget ?? cleanupError.ViolatedBudget,
                    }, completed.FailureKind == BuildTestGateFailureKind.Code
                        ? BuildTestGateFailureKind.Code : cleanupError.FailureKind);
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
                PreparationManifest = completed.PreparationManifest ?? projectPreparation?.Manifest,
                ProjectDefinitionIssues = completed.ProjectDefinitionIssues.Count > 0
                    ? completed.ProjectDefinitionIssues
                    : projectPreparation?.DefinitionIssues ?? [],
            };
            return completed;
        }
        finally
        {
            if (workspaceLease is not null)
            {
                await workspaceLease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
            }
            // The gate owns the preparation's per-run cache folder for exactly as
            // long as its verify commands need it. Releasing it here - after the
            // last command, on every exit path - keeps the published immutable
            // entries as the only long-lived cache state.
            ProjectPreparationExecutor.ReleaseRunRoot(projectPreparation);
            var completedAt = completed?.GateCompletedAtUtc ?? DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "build_test_gate_completed gate_run_id={GateRunId} gate_id={GateId} completed_at_utc={CompletedAtUtc:o} repository={Repository} expected_sha={ExpectedSha} tested_sha={TestedSha} attempt_chain_id={AttemptChainId} executor={Executor} workspace={Workspace} verdict={Verdict} exit={ExitCode} signal={Signal} failure_kind={FailureKind} failure_fingerprint={FailureFingerprint} violated_budget={ViolatedBudget} budget_limit_ms={BudgetLimitMs} budget_consumed_ms={BudgetConsumedMs} collision={CollisionDetected} queue_wait_ms={QueueWaitMs} self_healed={SelfHealed} dependency_cache={DependencyCacheDecision}",
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
                completed?.SelfHealed ?? selfHealed,
                DependencyCacheDecisionSummary(completed?.DependencyCacheDecision));
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

    internal static string PreparationManifestPath(string repositoryPath)
        => PreparationManifestPath(repositoryPath, PreparationCacheRoot);

    private static string PreparationManifestPath(string repositoryPath, string cacheRoot)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryPath).ToUpperInvariant())))
            .ToLowerInvariant()[..24];
        return Path.Combine(cacheRoot, "manifests", key, "latest.json");
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
        ProjectPreparationResult? projectPreparation,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var budget = new GateContentionBudget(timeout);
        var output = new RingOutput(MaxOutputLines);
        var evidence = new List<BuildTestGateProcessEvidence>();
        var findings = new List<BuildTestGateFinding>();
        var dependencyCache = new List<BuildTestGateDependencyCacheEvidence>();
        output.AppendLine($"# verify plan: {planSource} ({commands.Count} command(s))");
        foreach (var message in cacheRestoreMessages) output.AppendLine($"# {message}");
        var ranBackend = false;
        var ranFrontend = false;
        var flakyQuarantined = new List<string>();
        var retryPerformed = false;

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
                budget,
                elapsedBefore,
                output,
                ct,
                phase: "preparation",
                projectPreparation).ConfigureAwait(false);
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
                budget,
                elapsedBefore,
                output,
                ct,
                phase: "verification",
                projectPreparation)
                .ConfigureAwait(false);
            evidence.Add(process);
            if (process.ExitCode != 0 || process.TimedOut || process.Cancelled || process.LaunchError is not null)
            {
                var kind = ClassifyFailure(process);
                if (process.TimedOut && findings.Count > 0) kind = BuildTestGateFailureKind.Code;
                if (kind == BuildTestGateFailureKind.Code && !command.BlocksWorkPackage && !process.TimedOut)
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

                // AGT-2853: one targeted re-run of exactly the failed tests, on
                // the same build, charged to the same gate-run budget. A green
                // re-run keeps the gate green and records the names as flaky; a
                // second red leaves the original verdict untouched.
                var rerun = GateFlakyRerunPolicy.Decide(
                    command.Kind,
                    kind,
                    command.Command,
                    $"{process.StandardOutput}\n{process.StandardError}",
                    Remaining(budget.Limit, sw.Elapsed, allowExhausted: true));
                output.AppendLine(
                    $"# flaky re-run decision: {rerun.Reason} " +
                    $"failed={(rerun.FailedTests.Count == 0 ? "none" : string.Join(", ", rerun.FailedTests))}");
                if (rerun.ShouldRerun)
                {
                    retryPerformed = true;
                    var rerunElapsedBefore = sw.Elapsed;
                    var rerunProcess = await RunShellAsync(
                        workingDirectory,
                        rerun.Command!,
                        command.Shell,
                        budget,
                        rerunElapsedBefore,
                        output,
                        ct,
                        phase: FlakyRerunPhase,
                        projectPreparation)
                        .ConfigureAwait(false);
                    evidence.Add(rerunProcess);
                    if (CompletedNormally(rerunProcess) && rerunProcess.ExitCode == 0)
                    {
                        flakyQuarantined.AddRange(rerun.FailedTests);
                        output.AppendLine(
                            $"# {ReviewFlakyQuarantine.Classification}: the targeted re-run of " +
                            $"{string.Join(", ", rerun.FailedTests)} passed; recorded as flaky, gate not blocked");
                        continue;
                    }
                    output.AppendLine(
                        "# the targeted re-run failed again; the original red is the verdict");
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
                    RetryPerformed = retryPerformed,
                    FlakyQuarantinedFailures = flakyQuarantined,
                }, kind);
            }
        }

        sw.Stop();
        var passedReason = findings.Count == 0
            ? $"verify gate passed ({planSource})"
            : $"work-package gate passed with {findings.Count} separate non-blocking finding(s)";
        return new BuildTestGateResult(
            findings.Count == 0 ? BuildTestGateVerdict.Ok : BuildTestGateVerdict.Warn,
            0, sw.ElapsedMilliseconds, output.Text,
            FlakyReason(passedReason, flakyQuarantined),
            ranBackend, ranFrontend)
        {
            Processes = evidence,
            Findings = findings,
            DependencyCache = dependencyCache,
            RetryPerformed = retryPerformed,
            FlakyQuarantinedFailures = flakyQuarantined,
        };
    }

    /// <summary>
    /// Names the quarantined tests in the gate's own one-line reason so the
    /// flake is visible wherever that reason is read, not only in the log body.
    /// </summary>
    internal static string FlakyReason(string reason, IReadOnlyList<string> flakyQuarantined)
        => flakyQuarantined.Count == 0
            ? reason
            : $"{reason}; {ReviewFlakyQuarantine.Classification}: " +
              $"{string.Join(", ", flakyQuarantined)} failed once and passed on the targeted re-run";

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
            .Where(process => string.Equals(process.Phase, "verification", StringComparison.OrdinalIgnoreCase))
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
        if (process.FailedTestsObserved && process.TimedOut) commandDescription += "; failed tests were observed before cutoff";
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
        GateContentionBudget budget,
        TimeSpan elapsedBefore,
        RingOutput output,
        CancellationToken ct,
        string phase,
        ProjectPreparationResult? projectPreparation)
    {
        var executableCommand = command;
        string? reportDirectory = null;
        string? reportPrefix = null;
        if (GateFlakyRerunPolicy.IsTargetableDotNetTest(command))
        {
            // Use the existing OS temp folder with a unique prefix. This avoids
            // creating and structurally deleting a directory from the pipeline
            // feature while still isolating parallel gate reports.
            reportDirectory = Path.GetTempPath();
            reportPrefix = "agentstudio-gate-" + Guid.NewGuid().ToString("N");
            executableCommand += $" --logger \"trx;LogFilePrefix={reportPrefix}\" --logger \"console;verbosity=normal\" --results-directory \"{reportDirectory.Replace('\\', '/')}\"";
        }
        // Windows: cmd.exe /c cannot carry the composed command intact (the
        // backslash-escaped inner quotes of the appended `--logger "trx;..."`
        // and `--logger "console;verbosity=normal"` arguments reach dotnet as
        // literal characters and MSBuild fails with MSB4177/MSB1006, AGT-2912),
        // so platform commands run through Git Bash there, exactly like
        // explicit build-profile commands already do.
        var (fileName, args) = shell == VerifyCommandShell.Bash || OperatingSystem.IsWindows()
            ? (BashExecutable.Path, (IReadOnlyList<string>)["-lc", executableCommand])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", executableCommand]);
        return RunProcessAsync(
            workingDirectory, command, fileName, args,
            budget, elapsedBefore, output, ct, phase, projectPreparation, reportDirectory, reportPrefix);
    }

    private async Task<BuildTestGateProcessEvidence> RunProcessAsync(
        string workingDirectory,
        string command,
        string fileName,
        IReadOnlyList<string> args,
        GateContentionBudget budget,
        TimeSpan elapsedBefore,
        RingOutput output,
        CancellationToken ct,
        string phase,
        ProjectPreparationResult? projectPreparation,
        string? reportDirectory,
        string? reportPrefix)
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
        // AGT-2820: gate builds are one-shot. A reused MSBuild node outlives the
        // gate that started it, is reparented to init when the backend or the
        // worker goes away, and accumulates - 26 such nodes held 2963 MB on one
        // host, the oldest idle for eight days.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        // Repository preparation restored this gate's dependencies into its own
        // per-run cache folders. A verify command that does not see them resolves
        // against a package folder the restore never wrote to and fails with
        // NETSDK1064 on `--no-restore` (TE-52), so the preparation binding is the
        // last layer and overrides the gate's own dependency cache.
        PreparationCacheEnvironment.Apply(psi, projectPreparation);
        // The Studio backend starts the gate, so the child inherits the Studio's
        // own listener configuration. A verify command that boots an ASP.NET Core
        // host then reads the Studio's URL as its own and fails on a machine that
        // runs the Studio while passing everywhere else (AGT-2840). Nothing a gate
        // command does needs an inherited listener, so the boundary is unconditional
        // and runs after every layer that builds this environment.
        HostListenerEnvironment.RemoveFrom(psi.Environment);
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
            var timings = new GateTestTiming();
            using var resources = _resourceFactory(process.Id);
            var processClock = Stopwatch.StartNew();
            var stdoutTask = ReadTestOutputAsync(process.StandardOutput, timings);
            var stderrTask = ReadTestOutputAsync(process.StandardError, timings);
            var measurement = resources.Sample();
            var timedOut = false;
            var cancelled = false;
            try
            {
                var exit = process.WaitForExitAsync(bounded.Token);
                while (!exit.IsCompleted)
                {
                    var remaining = budget.Limit - elapsedBefore - processClock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        measurement = resources.Sample();
                        if (phase != "preparation" && budget.TryExtend(measurement, timings.Failed))
                        {
                            output.AppendLine($"# contention budget extended once: {budget.Extension}");
                            continue;
                        }
                        bounded.Cancel();
                        break;
                    }
                    using var sampleWait = CancellationTokenSource.CreateLinkedTokenSource(bounded.Token);
                    var delay = Task.Delay(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5), sampleWait.Token);
                    await Task.WhenAny(exit, delay).ConfigureAwait(false);
                    await sampleWait.CancelAsync().ConfigureAwait(false);
                    measurement = resources.Sample();
                    bounded.Token.ThrowIfCancellationRequested();
                }
                await exit.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = ct.IsCancellationRequested;
                timedOut = !cancelled;
                measurement = resources.Sample();
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: process tree kill"); }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: process exit after kill"); }
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            measurement = resources.Sample();
            if (reportDirectory is not null && reportPrefix is not null)
            {
                try
                {
                    foreach (var report in Directory.EnumerateFiles(
                                 reportDirectory, reportPrefix + "*.trx", SearchOption.TopDirectoryOnly))
                    {
                        timings.ReadTrx(report);
                        try { File.Delete(report); }
                        catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: test report cleanup"); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { SilentCatch.Note(ex, "BuildTestGateRunner: test report enumeration"); }
            }
            budget.FailedTestsObserved |= timings.Failed;
            output.AppendBlock("stdout", stdout);
            output.AppendBlock("stderr", stderr);
            var completedAt = DateTimeOffset.UtcNow;
            BuildTestGateBudgetEvidence? violatedBudget = null;
            if (timedOut)
            {
                var consumed = elapsedBefore + (completedAt - startedAt);
                violatedBudget = NewBudgetEvidence("gate-run", budget.Limit, consumed, phase);
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
                Resources = measurement,
                BudgetExtension = budget.Extension,
                OriginalBudgetMs = (long)budget.OriginalLimit.TotalMilliseconds,
                FailedTestsObserved = timings.Failed,
                SlowTests = timings.Slowest,
            };
        }
    }

    private static async Task<string> ReadTestOutputAsync(StreamReader reader, GateTestTiming timings)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            text.AppendLine(line);
            timings.Observe(line);
        }
        return text.ToString();
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
        if (process.FailedTestsObserved || GateTestTiming.HasFailedTests(process.StandardOutput + "\n" + process.StandardError))
            return BuildTestGateFailureKind.Code;
        if (process.LaunchError is not null) return BuildTestGateFailureKind.ProcessLaunch;
        if (process.Cancelled) return BuildTestGateFailureKind.Cancellation;
        if (process.TimedOut) return process.ViolatedBudget?.Name == "gate-run"
            ? BuildTestGateFailureKind.Environment
            : BuildTestGateFailureKind.Timeout;
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
        // A genuine toolchain/bundler startup crash is one exemption:
        // it is an unambiguous signature that the process never reached test
        // discovery, so it cannot be a completed process reporting its own
        // product result the way a logged lock string can (CAC-18). The other is
        // a missing NuGet package inside this gate's private preparation-cache
        // run directory: that path is executor-owned and cannot be changed by
        // the delivery, so the torn-cache signature is equally narrow.
        if (CompletedNormally(process)
            && !IsGenuineBuildOutputLock(evidence)
            && classified != BuildTestGateFailureKind.Environment)
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

    /// <summary>
    /// Narrow, high-confidence signatures of a bundler/toolchain crash that
    /// happened before any test could run, e.g. vite's case-insensitive-FS probe
    /// throwing while loading its config or Angular requiring a missing relative
    /// worker from a corrupted or torn node_modules tree (CAC-18, WEB-19). Kept
    /// deliberately specific: a broad heuristic here would
    /// repeat the AGT-2110 mistake of misclassifying genuine product failures.
    /// </summary>
    private static bool IsGenuineToolchainStartupCrash(string evidence)
        => evidence.Contains("testCaseInsensitiveFS", StringComparison.OrdinalIgnoreCase)
           || evidence.Contains("vite/dist/node/chunks/config.js", StringComparison.OrdinalIgnoreCase)
           || MissingRelativeModuleFromNodeModules.IsMatch(evidence)
           || (evidence.Contains("javascript-transformer-worker", StringComparison.OrdinalIgnoreCase)
               && evidence.Contains("node_modules/@angular/build", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A high-confidence torn NuGet cache signature. The path must point into
    /// this gate's own <c>agentstudio-preparation-cache/.runs/&lt;run&gt;/nuget</c>
    /// directory; the same NuGet diagnostic for any repository or host path is
    /// deliberately not exempted from the completed-process Code rule.
    /// </summary>
    internal static bool IsPreparationCacheNuGetFailure(string? evidence)
    {
        var value = evidence ?? string.Empty;
        var cachePath = PreparationRunNuGetPath.Match(value);
        if (!cachePath.Success) return false;
        var nu1101 = value.IndexOf("NU1101", StringComparison.OrdinalIgnoreCase);
        if (nu1101 >= 0 && Math.Abs(cachePath.Index - nu1101) <= 8_192) return true;

        var missing = value.LastIndexOf(
            "Could not find file",
            cachePath.Index,
            StringComparison.OrdinalIgnoreCase);
        if (missing < 0 || cachePath.Index - missing > 8_192) return false;
        var package = value.IndexOf(".nupkg", cachePath.Index, StringComparison.OrdinalIgnoreCase);
        return package >= cachePath.Index && package - cachePath.Index <= 8_192;
    }

    internal static BuildTestGateFailureKind ClassifyFailure(string? text)
    {
        var value = text ?? string.Empty;
        if (IsPreparationCacheNuGetFailure(value))
            return BuildTestGateFailureKind.Environment;
        if (IsGenuineToolchainStartupCrash(value))
            return BuildTestGateFailureKind.Environment;
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
        GateDependencyCacheSession? session,
        bool reranFromScratch)
    {
        if (session is null) return result;
        // Only a fully green gate can create the positive integrity marker.
        // Any failure, including an ordinary Code failure, removes the tree
        // instead of making the next run trust dependencies from a red build.
        var messages = result.Verdict == BuildTestGateVerdict.Ok
            ? session.Save()
            : session.Evict(result.FailureKind == BuildTestGateFailureKind.Environment
                ? "gate-environment-failure"
                : "gate-build-failure");
        var output = result.Output;
        foreach (var message in messages)
            output = AppendOutput(output, $"# {message}");
        var decision = session.Decision(reranFromScratch);
        var cacheSummary = DependencyCacheDecisionSummary(decision);
        return result with
        {
            Output = output,
            Reason = result.Reason + "; dependency cache: " + cacheSummary,
            DependencyCacheDecision = decision,
        };
    }

    private static bool ShouldRetryFromScratch(BuildTestGateResult result)
    {
        var failedVerification = result.Processes.LastOrDefault(process =>
            string.Equals(process.Phase, "verification", StringComparison.OrdinalIgnoreCase)
            && (process.ExitCode != 0
                || process.TimedOut
                || process.Cancelled
                || process.LaunchError is not null));
        return failedVerification is not null
               && !failedVerification.TimedOut
               && !failedVerification.Cancelled
               && failedVerification.LaunchError is null;
    }

    private static BuildTestGateResult CombineFromScratchRetry(
        BuildTestGateResult cachedTreeResult,
        BuildTestGateResult cleanTreeResult)
    {
        var reason = cleanTreeResult.Verdict == BuildTestGateVerdict.Ok
            ? cleanTreeResult.Reason + " after dependency cache eviction and one from-scratch rerun"
            : cleanTreeResult.Reason +
              "; cached-tree failure was evicted and the one from-scratch rerun also failed";
        var combined = cleanTreeResult with
        {
            DurationMs = cachedTreeResult.DurationMs + cleanTreeResult.DurationMs,
            Output = BoundOutput(
                cachedTreeResult.Output,
                "# dependency-cache evicted, rerun from scratch",
                cleanTreeResult.Output),
            Reason = reason,
            RanBackendBuild = cachedTreeResult.RanBackendBuild || cleanTreeResult.RanBackendBuild,
            RanFrontendBuild = cachedTreeResult.RanFrontendBuild || cleanTreeResult.RanFrontendBuild,
            Processes = cachedTreeResult.Processes.Concat(cleanTreeResult.Processes).ToArray(),
            Findings = cachedTreeResult.Findings.Concat(cleanTreeResult.Findings).ToArray(),
            DependencyCache = cachedTreeResult.DependencyCache.Concat(cleanTreeResult.DependencyCache).ToArray(),
            FailureFingerprint = null,
        };
        return cleanTreeResult.FailureKind == BuildTestGateFailureKind.None
            ? combined
            : WithFailure(combined, cleanTreeResult.FailureKind);
    }

    private static string BoundOutput(params string[] blocks)
    {
        var output = new RingOutput(MaxOutputLines);
        foreach (var block in blocks)
        {
            using var reader = new StringReader(block ?? string.Empty);
            while (reader.ReadLine() is { } line) output.AppendLine(line);
        }
        return output.Text;
    }

    internal static string DependencyCacheDecisionSummary(
        BuildTestGateDependencyCacheDecision? decision)
    {
        if (decision is null) return "not-used";
        return $"repository={decision.RepositoryKey} " +
               $"restored={(decision.Restored ? "yes" : "no")} " +
               $"ageSeconds={decision.AgeSeconds?.ToString() ?? "n/a"} " +
               $"sizeBytes={decision.SizeBytes} " +
               $"evicted={(decision.Evicted ? "yes" : "no")} " +
               $"evictionReason={decision.EvictionReason ?? "n/a"} " +
               $"rerunFromScratch={(decision.ReranFromScratch ? "yes" : "no")} " +
               $"savedVerified={(decision.SavedVerified ? "yes" : "no")}";
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

    /// <param name="allowExhausted">
    /// AGT-2853: the flaky re-run decision must be able to see a spent budget as
    /// spent. Every other caller starts a process and keeps the 1 ms floor, so a
    /// zero budget still produces a real timeout rather than an argument error.
    /// </param>
    private static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed, bool allowExhausted = false)
    {
        var remaining = timeout - elapsed;
        if (remaining > TimeSpan.Zero) return remaining;
        return allowExhausted ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1);
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
