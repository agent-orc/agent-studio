using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Fresh, disposable exact-subject workspace for one fenced ReviewAttempt.
/// It never consults or reuses the coding checkout. All writable paths and
/// process namespaces are rooted under the attempt directory.
/// </summary>
public sealed class RemoteReviewWorkspace
{
    private const int TestFailureParserVersion = 3;
    internal const int ReviewMaterialMaximumFiles = 200;
    internal const int ReviewMaterialMaximumLines = 12_000;
    private readonly RunnerOptions _options;
    private readonly ReviewSubjectDto _subject;
    private readonly ReviewLeaseDto _lease;
    private readonly Action<string> _log;
    private readonly RemoteReviewAgentCommandRunner _agentCommands;
    private string? _initialTree;
    private string? _baselineSha;
    private string? _integrationHeadSha;
    private bool _baselineCachePruned;
    private bool _dirtyBefore;

    public RemoteReviewWorkspace(
        RunnerOptions options,
        ReviewSubjectDto subject,
        ReviewLeaseDto lease,
        Action<string> log)
    {
        _options = options;
        _subject = subject;
        _lease = lease;
        _log = log;
        var root = Path.GetFullPath(options.ReviewWorkDir);
        AttemptRoot = Path.Combine(root, SafeSegment(lease.ResourceNamespace));
        RepositoryPath = Path.Combine(AttemptRoot, "repository");
        ArtifactPath = Path.Combine(AttemptRoot, "artifacts");
        CachePath = Path.Combine(AttemptRoot, "cache");
        TempPath = Path.Combine(AttemptRoot, "tmp");
        HomePath = Path.Combine(AttemptRoot, "home");
        BaselineCacheRoot = Path.Combine(root, ".baseline-cache");
        DependencyCacheRoot = Path.Combine(root, ".dependency-cache");
        _agentCommands = new RemoteReviewAgentCommandRunner(
            options,
            lease,
            RepositoryPath,
            ArtifactPath,
            HomePath,
            log);
    }

    public string AttemptRoot { get; }
    public string RepositoryPath { get; }
    public string ArtifactPath { get; }
    public string CachePath { get; }
    public string TempPath { get; }
    public string HomePath { get; }
    public string BaselineCacheRoot { get; }
    public string DependencyCacheRoot { get; }

    public IReadOnlyDictionary<string, string?> ProcessEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8",
            ["LC_ALL"] = Environment.GetEnvironmentVariable("LC_ALL") ?? "C.UTF-8",
            ["SSL_CERT_FILE"] = Environment.GetEnvironmentVariable("SSL_CERT_FILE"),
            ["SSL_CERT_DIR"] = Environment.GetEnvironmentVariable("SSL_CERT_DIR"),
            ["HOME"] = HomePath,
            ["TMPDIR"] = TempPath,
            ["TMP"] = TempPath,
            ["TEMP"] = TempPath,
            ["XDG_CACHE_HOME"] = CachePath,
            ["NUGET_PACKAGES"] = Path.Combine(CachePath, "nuget"),
            ["npm_config_cache"] = Path.Combine(CachePath, "npm"),
            ["PIP_CACHE_DIR"] = Path.Combine(CachePath, "pip"),
            ["CARGO_HOME"] = Path.Combine(CachePath, "cargo"),
            ["GRADLE_USER_HOME"] = Path.Combine(CachePath, "gradle"),
            ["DOTNET_CLI_HOME"] = Path.Combine(CachePath, "dotnet"),
            // AGT-2820: a reused MSBuild node outlives the build that created it
            // and is reparented to init when its review worker goes away. The
            // frozen plan already passes -nodeReuse:false; this covers the
            // MSBuild invocations a preparation script nests underneath it.
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["COMPOSE_PROJECT_NAME"] = _lease.ResourceNamespace,
            ["AGENT_REVIEW_NAMESPACE"] = _lease.ResourceNamespace,
            ["AGENT_REVIEW_DATABASE_NAMESPACE"] = _lease.ResourceNamespace,
            ["AGENT_REVIEW_PORT_BASE"] = _lease.PortBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PORT"] = _lease.PortBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        if (OperatingSystem.IsWindows())
        {
            // The hermetic set above is Linux-shaped. A Windows process tree is
            // not functional without the OS plumbing variables (powershell.exe
            // and most tools hard-require SystemRoot; cmd shims need ComSpec and
            // PATHEXT), so on a Windows host they pass through - they carry no
            // credentials and no per-project state.
            foreach (var name in new[] { "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) environment[name] = value;
            }
        }
        foreach (var name in _options.ReviewCredentialEnvironment)
        {
            if (!SafeEnvironmentName(name))
                throw new InvalidOperationException($"Invalid review credential environment name '{name}'.");
            environment[name] = Environment.GetEnvironmentVariable(name);
        }
        // AGT-2831, applied last on purpose: TMPDIR does not fence the .NET
        // build servers. VBCSCompiler and the reusable MSBuild nodes listen on
        // host-global /tmp sockets, so concurrent attempts otherwise share one
        // compiler server that keeps the working directory of whichever attempt
        // started it. No later configuration may weaken the fence.
        ReviewBuildServerIsolation.ApplyTo(environment, TempPath);
        return environment;
    }

    public async Task<ReviewWorkspaceProofDto> PrepareAsync(
        TaskServerClient client,
        CancellationToken ct)
    {
        if (Directory.Exists(AttemptRoot))
            throw new ReviewInfrastructureException(
                "DirtyBefore",
                $"Review attempt workspace already exists: {AttemptRoot}");
        Directory.CreateDirectory(AttemptRoot);
        Directory.CreateDirectory(ArtifactPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(HomePath);
        Directory.CreateDirectory(BaselineCacheRoot);
        Directory.CreateDirectory(DependencyCacheRoot);

        if (!string.IsNullOrWhiteSpace(_subject.RepositoryUrl))
            await MaterializeGitAsync(_subject.RepositoryUrl!, ct);
        else if (!string.IsNullOrWhiteSpace(_subject.SourceBundleArtifactId))
            await MaterializeBundleAsync(client, ct);
        else
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                "Review subject has neither an immutable result ref nor a source bundle.");

        var repositoryId = !string.IsNullOrWhiteSpace(_subject.RepositoryUrl)
            ? RepositoryIdentityContract.FromUrl(_subject.RepositoryUrl)
            : _subject.RepositoryId;
        if (!string.Equals(repositoryId, _subject.RepositoryId, StringComparison.Ordinal))
            throw new ReviewInfrastructureException(
                "RepositoryMismatch",
                $"Materialized repository identity '{repositoryId}' does not match '{_subject.RepositoryId}'.");

        var head = await GitValueAsync("rev-parse", "HEAD", ct);
        if (!string.Equals(head, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
            throw new ReviewInfrastructureException(
                "ShaMismatch",
                $"Materialized HEAD '{head}' does not match expected Result-SHA '{_subject.ExpectedResultSha}'.");
        _initialTree = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
        _dirtyBefore = (await GitValueAsync("status", "--porcelain", "--untracked-files=all", ct)).Length > 0;
        if (_dirtyBefore)
            throw new ReviewInfrastructureException("DirtyBefore", "Fresh review workspace is dirty before review.");
        _log($"review subject ready repository={repositoryId} head={head} tree={_initialTree} dirty=false");
        return Proof(repositoryId!, head, _initialTree, false);
    }

    /// <summary>
    /// Reconstructs the in-memory proof state in a detached review worker after
    /// the daemon prepared the exact-subject workspace. The worker refuses to
    /// start commands unless the persisted workspace still proves the same
    /// immutable subject and a clean tree.
    /// </summary>
    internal async Task<ReviewWorkspaceProofDto> AdoptPreparedAsync(CancellationToken ct)
    {
        if (!Directory.Exists(RepositoryPath))
            throw new ReviewInfrastructureException(
                "PreparedWorkspaceMissing",
                $"Prepared review repository is missing: {RepositoryPath}");

        var head = await GitValueAsync("rev-parse", "HEAD", ct);
        if (!string.Equals(head, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
            throw new ReviewInfrastructureException(
                "ShaMismatch",
                $"Prepared review HEAD '{head}' does not match expected Result-SHA '{_subject.ExpectedResultSha}'.");
        _initialTree = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
        _dirtyBefore = (await GitValueAsync("status", "--porcelain", "--untracked-files=all", ct)).Length > 0;
        if (_dirtyBefore)
            throw new ReviewInfrastructureException(
                "DirtyBefore",
                "Prepared review workspace became dirty before the detached worker adopted it.");
        return Proof(_subject.RepositoryId, head, _initialTree, false);
    }

    public Task<ReviewExecutionEvidence> ExecutePlanAsync(CancellationToken ct)
        => ExecutePlanAsync(ct, resume: null, checkpoint: null);

    internal async Task<ReviewExecutionEvidence> ExecutePlanAsync(
        CancellationToken ct,
        ReviewExecutionCheckpoint? resume,
        Func<ReviewExecutionCheckpoint, CancellationToken, Task>? checkpoint)
    {
        var commands = resume?.Commands?.ToList() ?? [];
        var verdicts = resume?.Verdicts?.ToList() ?? [];
        var artifacts = resume?.Artifacts?.ToList() ?? [];
        DependencyCacheSession? candidateCache = null;
        string? reviewMaterial = null;
        try
        {
            candidateCache = await ExecutePreparationAsync(
                RepositoryPath,
                "candidate",
                baselineSha: null,
                ProcessEnvironment(),
                commands,
                artifacts,
                ct);

            foreach (var plannedCommand in _subject.Plan.Commands)
            {
                if (CanResumeCommand(plannedCommand, commands, verdicts))
                    continue;

                var headBefore = await GitValueAsync("rev-parse", "HEAD", ct);
                var treeBefore = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
                if (!string.Equals(headBefore, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
                    throw new ReviewInfrastructureException(
                        "CommandSubjectMismatch",
                        $"Step '{plannedCommand.StepId}' would run at '{headBefore}', not '{_subject.ExpectedResultSha}'.");

                // AGT-2820: the plan froze a budget for the prompt as authored.
                // The authoritative diff is appended here, at execution time, so
                // the budget is re-derived against what the model actually has to
                // read. The executed budget - never the frozen one - is what the
                // evidence and any violation report.
                var command = plannedCommand;
                if (ReviewCommandKinds.IsAgent(command.ExecutionKind))
                {
                    var prompt = AppendReviewMaterial(
                        command.Prompt!,
                        reviewMaterial ??= await BuildReviewMaterialAsync(
                            ReviewMaterialMaximumFiles,
                            ReviewMaterialMaximumLines,
                            ct));
                    var budget = ReviewAspectBudgetPolicy.Derive(
                        command.CliType,
                        command.Model,
                        command.ThinkingLevel,
                        ReviewAspectBudgetPolicy.MaterialCharacters(prompt));
                    command = command with
                    {
                        Prompt = prompt,
                        TimeoutSeconds = Math.Max(plannedCommand.TimeoutSeconds, budget.Seconds),
                    };
                    _log(
                        $"review-aspect-budget step={command.StepId} {budget.Describe()} " +
                        $"planned={plannedCommand.TimeoutSeconds}s effective={command.TimeoutSeconds}s");
                }

                var execution = ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    ? await _agentCommands.RunAsync(command, ct)
                    : await RunCommandAsync(command, RepositoryPath, ct);
                if (AspectCommandTimedOut(command, execution))
                {
                    // AGT-2749 addendum (2026-09-07 19:20): a review aspect call
                    // killed on its own timeout is a budget problem, not a
                    // broken toolchain. Before this check it fell into
                    // AgentCommandUnavailable() below and was classified
                    // ToolUnavailable - exit=124 on every card whose CLI budget
                    // was too small, most visibly Claude aspect calls under the
                    // old flat 60s budget.
                    commands.Add(await AddCommandEvidenceAsync(
                        command.StepId,
                        command.Aspect,
                        command.FileName,
                        command.Arguments,
                        headBefore,
                        treeBefore,
                        execution.Process,
                        execution.StartedAt,
                        execution.FinishedAt,
                        execution.Signal,
                        command.TimeoutSeconds,
                        "verification",
                        "candidate",
                        baselineSha: null,
                        comparison: null,
                        retryPerformed: false,
                        dependencyCacheHit: false,
                        dependencyCache: null,
                        artifacts,
                        ct,
                        command,
                        execution.AgentUsage));
                    SaveCaches(candidateCache);
                    throw await InfrastructureFailureAsync(
                        "AspectTimeout",
                        $"Review aspect '{command.StepId}' violated review-command budget on " +
                        $"model '{command.Model ?? "unspecified"}'; it was killed on its timeout, " +
                        $"not on its toolchain, and says nothing about the reviewed change: " +
                        $"{CommandLine(command)}; exit={execution.Process.ExitCode}; " +
                        $"budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                        commands,
                        artifacts,
                        ct);
                }

                if (execution.Signal == "stalled")
                {
                    commands.Add(await AddCommandEvidenceAsync(
                        command.StepId,
                        command.Aspect,
                        command.FileName,
                        command.Arguments,
                        headBefore,
                        treeBefore,
                        execution.Process,
                        execution.StartedAt,
                        execution.FinishedAt,
                        execution.Signal,
                        command.TimeoutSeconds,
                        "verification",
                        "candidate",
                        baselineSha: null,
                        comparison: null,
                        retryPerformed: false,
                        dependencyCacheHit: false,
                        dependencyCache: null,
                        artifacts,
                        ct,
                        command,
                        execution.AgentUsage));
                    SaveCaches(candidateCache);
                    throw await InfrastructureFailureAsync(
                        "CommandStalled",
                        $"Review command '{command.StepId}' produced no output for its whole silence " +
                        $"window and was killed: {CommandLine(command)}; " +
                        $"{FailureDetail(execution.Process)}; " +
                        $"budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                        commands,
                        artifacts,
                        ct);
                }

                if (StalledWithoutCpuProgress(execution))
                {
                    commands.Add(await AddCommandEvidenceAsync(
                        command.StepId,
                        command.Aspect,
                        command.FileName,
                        command.Arguments,
                        headBefore,
                        treeBefore,
                        execution.Process,
                        execution.StartedAt,
                        execution.FinishedAt,
                        execution.Signal,
                        command.TimeoutSeconds,
                        "verification",
                        "candidate",
                        baselineSha: null,
                        comparison: null,
                        retryPerformed: false,
                        dependencyCacheHit: false,
                        dependencyCache: null,
                        artifacts,
                        ct,
                        command,
                        execution.AgentUsage));
                    SaveCaches(candidateCache);
                    throw await InfrastructureFailureAsync(
                        NoCpuProgressClassification,
                        NoProgressSummary("Review command", command.StepId, CommandLine(command), execution),
                        commands,
                        artifacts,
                        ct);
                }

                if (MissingToolchain(execution.Process)
                    || AgentCommandUnavailable(command, execution.Process))
                {
                    commands.Add(await AddCommandEvidenceAsync(
                        command.StepId,
                        command.Aspect,
                        command.FileName,
                        command.Arguments,
                        headBefore,
                        treeBefore,
                        execution.Process,
                        execution.StartedAt,
                        execution.FinishedAt,
                        execution.Signal,
                        command.TimeoutSeconds,
                        "verification",
                        "candidate",
                        baselineSha: null,
                        comparison: null,
                        retryPerformed: false,
                        dependencyCacheHit: false,
                        dependencyCache: null,
                        artifacts,
                        ct,
                        command,
                        execution.AgentUsage));
                    SaveCaches(candidateCache);
                    throw await InfrastructureFailureAsync(
                        "ToolUnavailable",
                        $"Review command '{command.StepId}' could not use its declared toolchain: " +
                        $"{CommandLine(command)}; exit={execution.Process.ExitCode}; " +
                        $"budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                        commands,
                        artifacts,
                        ct);
                }

                if (TmpMountTornDownDuringBuild(execution.Process))
                {
                    commands.Add(await AddCommandEvidenceAsync(
                        command.StepId,
                        command.Aspect,
                        command.FileName,
                        command.Arguments,
                        headBefore,
                        treeBefore,
                        execution.Process,
                        execution.StartedAt,
                        execution.FinishedAt,
                        execution.Signal,
                        command.TimeoutSeconds,
                        "verification",
                        "candidate",
                        baselineSha: null,
                        comparison: null,
                        retryPerformed: false,
                        dependencyCacheHit: false,
                        dependencyCache: null,
                        artifacts,
                        ct,
                        command,
                        execution.AgentUsage));
                    SaveCaches(candidateCache);
                    throw await InfrastructureFailureAsync(
                        "TmpMountTornDown",
                        $"Review command '{command.StepId}' failed with a torn-down-/tmp signature " +
                        "(MSB1025, SocketException (99), or a NuGet mkdtemp ENOENT), not a product failure: " +
                        $"{CommandLine(command)}; exit={execution.Process.ExitCode}; " +
                        $"budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                        commands,
                        artifacts,
                        ct);
                }

                BaselineComparison? comparison = null;
                var retryPerformed = false;
                if (command.CompareToBaseline && !execution.Process.Success)
                {
                    comparison = await CompareToBaselineAsync(
                        command,
                        execution.Process,
                        candidateCache,
                        commands,
                        artifacts,
                        ct);
                    if (comparison.NewFailures.Count > 0)
                    {
                        var reviewFlakyTests = ReviewFlakyTestIndex.Discover(RepositoryPath, _log);
                        retryPerformed = true;
                        await AddArtifactsAsync(
                            $"candidate.{SafeSegment(command.StepId)}.initial",
                            execution.Process,
                            artifacts,
                            ct);
                        execution = await RunCommandAsync(command, RepositoryPath, ct);
                        if (MissingToolchain(execution.Process))
                        {
                            commands.Add(await AddCommandEvidenceAsync(
                                command.StepId,
                                command.Aspect,
                                command.FileName,
                                command.Arguments,
                                headBefore,
                                treeBefore,
                                execution.Process,
                                execution.StartedAt,
                                execution.FinishedAt,
                                execution.Signal,
                                command.TimeoutSeconds,
                                "verification",
                                "candidate",
                                comparison.BaselineSha,
                                comparison: null,
                                retryPerformed: true,
                                dependencyCacheHit: false,
                                dependencyCache: null,
                                artifacts,
                                ct));
                            SaveCaches(candidateCache);
                            throw await InfrastructureFailureAsync(
                                "ToolUnavailable",
                                $"Review retry '{command.StepId}' lost its declared toolchain; " +
                                $"exit={execution.Process.ExitCode}; budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                                commands,
                                artifacts,
                                ct);
                        }
                        if (StalledWithoutCpuProgress(execution))
                        {
                            commands.Add(await AddCommandEvidenceAsync(
                                command.StepId,
                                command.Aspect,
                                command.FileName,
                                command.Arguments,
                                headBefore,
                                treeBefore,
                                execution.Process,
                                execution.StartedAt,
                                execution.FinishedAt,
                                execution.Signal,
                                command.TimeoutSeconds,
                                "verification",
                                "candidate",
                                comparison.BaselineSha,
                                comparison: null,
                                retryPerformed: true,
                                dependencyCacheHit: false,
                                dependencyCache: null,
                                artifacts,
                                ct));
                            SaveCaches(candidateCache);
                            throw await InfrastructureFailureAsync(
                                NoCpuProgressClassification,
                                NoProgressSummary(
                                    "Review retry",
                                    command.StepId,
                                    CommandLine(command),
                                    execution),
                                commands,
                                artifacts,
                                ct);
                        }
                        if (TmpMountTornDownDuringBuild(execution.Process))
                        {
                            commands.Add(await AddCommandEvidenceAsync(
                                command.StepId,
                                command.Aspect,
                                command.FileName,
                                command.Arguments,
                                headBefore,
                                treeBefore,
                                execution.Process,
                                execution.StartedAt,
                                execution.FinishedAt,
                                execution.Signal,
                                command.TimeoutSeconds,
                                "verification",
                                "candidate",
                                comparison.BaselineSha,
                                comparison: null,
                                retryPerformed: true,
                                dependencyCacheHit: false,
                                dependencyCache: null,
                                artifacts,
                                ct));
                            SaveCaches(candidateCache);
                            throw await InfrastructureFailureAsync(
                                "TmpMountTornDown",
                                $"Review retry '{command.StepId}' failed with a torn-down-/tmp signature " +
                                "(MSB1025, SocketException (99), or a NuGet mkdtemp ENOENT), not a product failure; " +
                                $"exit={execution.Process.ExitCode}; budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                                commands,
                                artifacts,
                                ct);
                        }
                        comparison = comparison.Reclassify(
                            SubjectFailures(command, execution.Process),
                            reviewFlakyTests);
                    }
                }

                commands.Add(await AddCommandEvidenceAsync(
                    command.StepId,
                    command.Aspect,
                    command.FileName,
                    command.Arguments,
                    headBefore,
                    treeBefore,
                    execution.Process,
                    execution.StartedAt,
                    execution.FinishedAt,
                    execution.Signal,
                    command.TimeoutSeconds,
                    "verification",
                    "candidate",
                    comparison?.BaselineSha,
                    comparison,
                    retryPerformed,
                    dependencyCacheHit: false,
                    dependencyCache: null,
                    artifacts,
                    ct,
                    command,
                    execution.AgentUsage));
                var verdict = comparison is null
                    ? ParseVerdict(command, execution.Process)
                    : BaselineVerdict(command, comparison);
                verdicts.Add(ReviewVerdictCitationPolicy.Normalize(
                    verdict,
                    ReviewCommandKinds.IsAgent(command.ExecutionKind)));
                if (checkpoint is not null)
                {
                    await checkpoint(
                        new ReviewExecutionCheckpoint(
                            commands.Select(item => item.StepId).ToArray(),
                            commands.Sum(item => Math.Max(
                                0,
                                (item.FinishedAt - item.StartedAt).TotalSeconds)),
                            DateTime.UtcNow,
                            commands.ToArray(),
                            artifacts.ToArray(),
                            verdicts.ToArray()),
                        ct);
                }
                PurgeStepTemp(command.StepId);
            }
        }
        finally
        {
            if (candidateCache is not null)
                foreach (var message in candidateCache.Save()) _log(message);
        }

        var proof = await CurrentProofAsync(ct);
        // AGT-2749: a lone "concerns" verdict is a reservation, not a refusal
        // (AGT-2706 settled ProductFailure with every aspect pass and one
        // documentation-impact concern). ReviewGradingPolicy is the single
        // place that decides this; only a blocking token fails the review.
        var outcome = ReviewGradingPolicy.Grade(verdicts.Select(verdict => verdict.Status))
            == ReviewGrade.ProductFailure
            ? "ProductFailure"
            // AGT-2819: no verdict refuses the change, but at least one gate was
            // already red on the merge base. That is a defect of the integration
            // branch and reported as such, not a silent pass and not this
            // card's product failure.
            : IntegrationBranchDefectSteps(commands).Count > 0
                ? "IntegrationBranchDefect"
                : "Pass";
        return new ReviewExecutionEvidence(outcome, proof, commands, artifacts, verdicts);
    }

    /// <summary>
    /// Candidate verification steps whose failure the merge base already had
    /// (AGT-2819). Derived from the recorded evidence so a resumed attempt
    /// reaches the same terminal as an uninterrupted one.
    /// </summary>
    private IReadOnlyList<string> IntegrationBranchDefectSteps(
        IReadOnlyList<ReviewCommandEvidenceDto> commands)
        => commands
            .Where(command => string.Equals(command.Phase, "verification", StringComparison.Ordinal)
                              && string.Equals(command.WorkspaceRole, "candidate", StringComparison.Ordinal)
                              && ReviewFailureAttributionPolicy.Attribute(
                                     _subject.Plan.Commands.FirstOrDefault(planned => string.Equals(
                                         planned.StepId,
                                         command.StepId,
                                         StringComparison.Ordinal)),
                                     command)
                                 == ReviewFailureOwner.IntegrationBranch)
            .Select(command => command.StepId)
            .ToArray();

    private static bool CanResumeCommand(
        ReviewCommandDto command,
        IReadOnlyList<ReviewCommandEvidenceDto> commands,
        IReadOnlyList<ReviewVerdictDto> verdicts)
    {
        if (!commands.Any(item => string.Equals(item.StepId, command.StepId, StringComparison.Ordinal)))
            return false;

        // Deterministic commands carry their complete result in command
        // evidence. Semantic commands additionally require the persisted
        // verdict, otherwise skipping would manufacture a passing grade from
        // an incomplete checkpoint.
        return !ReviewCommandKinds.IsAgent(command.ExecutionKind)
               || verdicts.Any(item => string.Equals(item.Aspect, command.Aspect, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CommandExecution> RunCommandAsync(
        ReviewCommandDto command,
        string workingDirectory,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
        => await RunCommandAsync(
            command.StepId,
            command.FileName,
            command.Arguments,
            command.TimeoutSeconds,
            workingDirectory,
            ct,
            environment);

    private async Task<CommandExecution> RunCommandAsync(
        string stepId,
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        string workingDirectory,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var started = DateTime.UtcNow;
        ProcessResult process;
        string? signal = null;
        var noProgressAfter = NoCpuProgressWindow(timeoutSeconds);
        // Owned outside the try so the catch below can tell a wall-clock timeout
        // apart from a tree that stopped consuming CPU.
        CommandProgressWatchdog? watchdog = null;
        // The two hang detectors answer different questions and neither subsumes
        // the other: silence catches a command that keeps burning CPU without
        // ever producing a line, no-CPU-progress catches a tree blocked on a
        // host-shared handle. A blocked tree can trip both; StallDetection lets
        // whichever one actually observes its condition first claim the kill, so
        // the report always names the detector that fired rather than assuming
        // one window is always tighter than the other (AGT-2851: an operator
        // raised the silence window past the no-CPU-progress window and the old
        // "silence always wins" comment stopped matching reality).
        var silenceWindow = SilenceWindow(timeoutSeconds);
        var clock = new CommandOutputClock();
        var stall = new StallDetection();
        if (silenceWindow > TimeSpan.Zero || noProgressAfter > TimeSpan.Zero)
            _log(
                $"review-command-watchdogs step={stepId} silenceWindowSeconds={silenceWindow.TotalSeconds:0} " +
                $"noCpuProgressWindowSeconds={noProgressAfter.TotalSeconds:0} budgetSeconds={Math.Clamp(timeoutSeconds, 1, 7200)}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 7200)));
            using var silenceWatcher = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var completeStdout = new StringBuilder();
            var completeStderr = new StringBuilder();
            var watching = WatchForSilenceAsync(
                stepId, clock, stall, silenceWindow, timeout, silenceWatcher.Token);
            try
            {
                var boundedProcess = await ProcessRunner.RunAsync(
                    fileName,
                    arguments,
                    workingDirectory,
                    onStdOut: line =>
                    {
                        clock.Mark();
                        completeStdout.AppendLine(line);
                    },
                    onStdErr: line =>
                    {
                        clock.Mark();
                        completeStderr.AppendLine(line);
                    },
                    environment: environment ?? ProcessEnvironment(),
                    clearEnvironment: true,
                    onStarted: processId => watchdog = new CommandProgressWatchdog(
                        () => ProcessTreeCpu.Sample(processId),
                        noProgressAfter,
                        onStalled: () =>
                        {
                            var sampledCpu = ProcessTreeCpu.Sample(processId);
                            if (!stall.TryRecordNoCpuProgress(
                                    DateTime.UtcNow - started, noProgressAfter, sampledCpu))
                                return; // the silence watchdog already claimed this kill.
                            _log(
                                $"review command stalled step={stepId} pid={processId} " +
                                $"noCpuProgressSeconds={noProgressAfter.TotalSeconds:0} " +
                                $"measuredCpuSeconds={(sampledCpu?.TotalSeconds ?? -1):0.0} action=kill-tree");
                            // Cancelling the run token is what actually reaps the
                            // tree: ProcessRunner kills every descendant on cancel.
                            try { timeout.Cancel(); }
                            catch (ObjectDisposedException) { /* the command already finished */ }
                        }),
                    ct: timeout.Token);
                process = new ProcessResult(
                    boundedProcess.ExitCode,
                    completeStdout.ToString(),
                    completeStderr.ToString());
            }
            finally
            {
                silenceWatcher.Cancel();
                await watching;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                                  && stall.Detector == StallDetector.Silence)
        {
            var silent = stall.Silence!.Value;
            var window = stall.SilenceWindow!.Value;
            process = new ProcessResult(
                -1,
                string.Empty,
                $"Review command '{stepId}' produced no output for {silent.TotalSeconds:F0}s and was " +
                $"killed by the silence watchdog (window {window.TotalSeconds:F0}s) rather than " +
                "holding its review slot for the remaining command budget; detector=silence.");
            signal = "stalled";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                                  && stall.Detector == StallDetector.NoCpuProgress)
        {
            var window = stall.NoCpuProgressWindow!.Value;
            var cpuText = stall.SampledCpu is { } sampled
                ? $"{sampled.TotalSeconds:F1}s of CPU"
                : "no observable CPU";
            process = new ProcessResult(
                -1,
                string.Empty,
                $"Review command '{stepId}' consumed {cpuText} in " +
                $"{stall.CpuElapsed!.Value.TotalSeconds:F0}s against a {window.TotalSeconds:F0}s " +
                "no-CPU-progress window and was killed rather than holding its review slot for the " +
                "remaining command budget; detector=no-cpu-progress.");
            signal = StalledSignal;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process = new ProcessResult(-1, string.Empty, "Review command timed out.");
            signal = "timeout";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            process = new ProcessResult(
                127,
                string.Empty,
                $"Review step '{stepId}' could not start: {exception.Message}");
        }
        finally
        {
            if (watchdog is not null) await watchdog.DisposeAsync();
        }
        return new CommandExecution(process, started, DateTime.UtcNow, signal, Stall: stall.ToDiagnostics());
    }

    /// <summary>
    /// Empties the attempt temp directory once a plan step is finished
    /// (AGT-2858). A frozen plan runs preparation, build, several test commands
    /// and the semantic aspects one after another, and without this they all
    /// accumulated until the whole attempt workspace was deleted.
    ///
    /// The unit is the step, not the process: a flake retry is a second run of
    /// the same command and must still see whatever its own first run left in
    /// <c>TMPDIR</c>, exactly as a developer re-running it locally would.
    /// Bounded to paths below <see cref="AttemptRoot"/> by
    /// <see cref="ReviewTempResidue.IsInside"/>, so this executor can never
    /// empty a host-shared or foreign temp directory.
    /// </summary>
    private void PurgeStepTemp(string stepId)
    {
        var purge = ReviewTempResidue.Purge(TempPath, AttemptRoot);
        if (!purge.Observed) return;
        _log($"review-temp-purged step={stepId} removed={purge.Removed} retained={purge.Retained}");
    }

    /// <summary>
    /// Evidence signal for a command the hang watchdog reaped. Distinct from
    /// <c>timeout</c>: the command still had budget left, it had simply stopped
    /// doing work.
    /// </summary>
    internal const string StalledSignal = "no-progress";

    internal static bool StalledWithoutCpuProgress(CommandExecution execution)
        => execution.Signal == StalledSignal;

    /// <summary>Review report classification for a command the watchdog reaped.</summary>
    internal const string NoCpuProgressClassification = "NoCpuProgress";

    /// <summary>
    /// Names the detector, its effective window, and what was actually measured,
    /// so a card never has to guess which watchdog fired or what threshold it
    /// used (AGT-2851). Falls back to the command's own elapsed wall time only
    /// when no diagnostics were captured, which should not happen for a command
    /// classified <see cref="NoCpuProgressClassification"/>.
    /// </summary>
    private static string NoProgressSummary(
        string subject,
        string stepId,
        string? commandLine,
        CommandExecution execution)
    {
        var stall = execution.Stall;
        var window = stall?.NoCpuProgressWindow?.TotalSeconds ?? 0;
        var elapsed = stall?.CpuElapsed?.TotalSeconds
                      ?? Math.Max(0, (execution.FinishedAt - execution.StartedAt).TotalSeconds);
        var cpuText = stall?.SampledCpu is { } sampled
            ? $"{sampled.TotalSeconds:F1}s of CPU"
            : "no observable CPU";
        return $"{subject} '{stepId}' consumed {cpuText} in {elapsed:F0}s against a {window:F0}s " +
               "no-CPU-progress window and was killed as a hang, not a product failure: " +
               $"{commandLine}; detector=no-cpu-progress; signal={StalledSignal}; elapsed={elapsed:F0}s.";
    }

    private async Task<DependencyCacheSession?> ExecutePreparationAsync(
        string workspacePath,
        string workspaceRole,
        string? baselineSha,
        IReadOnlyDictionary<string, string?> environment,
        ICollection<ReviewCommandEvidenceDto> commands,
        ICollection<ReviewArtifactEvidenceDto> artifacts,
        CancellationToken ct)
    {
        var preparation = _subject.Plan.Preparation ?? [];
        if (preparation.Count == 0) return null;

        var scopes = preparation
            .SelectMany(command => command.DependencyScopes ?? [])
            .GroupBy(scope => scope.WorkingSubdir, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ReviewDependencyScopeDto(
                group.Key,
                group.SelectMany(scope => scope.Lockfiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
        var cacheRole = workspaceRole.StartsWith("baseline", StringComparison.Ordinal)
            ? "baseline"
            : "candidate";
        var cache = DependencyCacheSession.Create(
            DependencyCacheRoot,
            _subject.RepositoryId,
            workspacePath,
            scopes,
            _subject.Plan.PreserveGlobs,
            cacheRole,
            _log);
        var cacheMessages = cache.Restore();

        foreach (var command in preparation)
        {
            var headBefore = await GitValueAtAsync(
                workspacePath,
                ["rev-parse", "HEAD"],
                environment,
                ct);
            var treeBefore = await GitValueAtAsync(
                workspacePath,
                ["rev-parse", "HEAD^{tree}"],
                environment,
                ct);
            var dependencyDecisions = (command.DependencyScopes ?? [])
                .Select(scope =>
                {
                    var installRoot = ResolveWorkingDirectory(workspacePath, scope.WorkingSubdir);
                    return (InstallRoot: installRoot,
                        Decision: DependencyPreparationState.Evaluate(installRoot, scope));
                })
                .ToArray();
            var installNeeded = dependencyDecisions.Length == 0
                                || dependencyDecisions.Any(item => item.Decision.State != "hit");
            CommandExecution execution;
            if (installNeeded)
            {
                var workingDirectory = ResolveWorkingDirectory(
                    workspacePath,
                    command.WorkingSubdir);
                execution = Directory.Exists(workingDirectory)
                    ? await RunCommandAsync(
                        command.StepId,
                        command.FileName,
                        command.Arguments,
                        command.TimeoutSeconds,
                        workingDirectory,
                        ct,
                        environment)
                    : new CommandExecution(
                        new ProcessResult(
                            127,
                            string.Empty,
                            $"Dependency preparation directory is missing: {workingDirectory}"),
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        null);
            }
            else
            {
                var now = DateTime.UtcNow;
                var output = string.Join(Environment.NewLine,
                    cacheMessages.Concat(dependencyDecisions.Select(item =>
                        $"dependency-cache hit scope={item.Decision.Scope} " +
                        $"reason={item.Decision.Reason} lockHash={item.Decision.LockHash}")));
                execution = new CommandExecution(new ProcessResult(0, output, string.Empty), now, now, null);
            }

            var evidence = dependencyDecisions
                .Select(item => item.Decision with { InstallRan = installNeeded })
                .ToArray();
            if (installNeeded && execution.Process.Success)
            {
                foreach (var item in dependencyDecisions)
                {
                    if (!string.IsNullOrWhiteSpace(item.Decision.LockHash))
                        DependencyPreparationState.Stamp(item.InstallRoot, item.Decision.LockHash);
                }
            }

            commands.Add(await AddCommandEvidenceAsync(
                command.StepId,
                "preparation",
                command.FileName,
                command.Arguments,
                headBefore,
                treeBefore,
                execution.Process,
                execution.StartedAt,
                execution.FinishedAt,
                execution.Signal,
                command.TimeoutSeconds,
                "preparation",
                workspaceRole,
                baselineSha,
                comparison: null,
                retryPerformed: false,
                dependencyCacheHit: !installNeeded,
                dependencyCache: evidence,
                artifacts,
                ct));

            if (StalledWithoutCpuProgress(execution))
            {
                foreach (var message in cache.Save()) _log(message);
                throw await InfrastructureFailureAsync(
                    NoCpuProgressClassification,
                    NoProgressSummary(
                        "Dependency preparation",
                        command.StepId,
                        CommandLine(command),
                        execution),
                    commands,
                    artifacts,
                    ct);
            }

            if (!execution.Process.Success || execution.Signal is not null)
            {
                foreach (var message in cache.Save()) _log(message);
                var summary =
                    $"Dependency preparation '{command.StepId}' failed: " +
                    $"command={CommandLine(command)}; exit={execution.Process.ExitCode}; " +
                    $"detail={FailureDetail(execution.Process)}; " +
                    $"budget={BudgetSummary(command.TimeoutSeconds, execution)}; " +
                    $"stdout={ArtifactName(workspaceRole, command.StepId, "stdout")}; " +
                    $"stderr={ArtifactName(workspaceRole, command.StepId, "stderr")}.";
                throw await InfrastructureFailureAsync(
                    "PreparationFailed",
                    summary,
                    commands,
                    artifacts,
                    ct);
            }
        }

        return cache;
    }

    private async Task<ReviewCommandEvidenceDto> AddCommandEvidenceAsync(
        string stepId,
        string aspect,
        string fileName,
        IReadOnlyList<string> arguments,
        string headBefore,
        string treeBefore,
        ProcessResult process,
        DateTime startedAt,
        DateTime finishedAt,
        string? signal,
        int timeoutSeconds,
        string phase,
        string workspaceRole,
        string? baselineSha,
        BaselineComparison? comparison,
        bool retryPerformed,
        bool dependencyCacheHit,
        IReadOnlyList<ReviewDependencyCacheEvidenceDto>? dependencyCache,
        ICollection<ReviewArtifactEvidenceDto> artifacts,
        CancellationToken ct,
        ReviewCommandDto? plannedCommand = null,
        RemoteAgentUsage? agentUsage = null,
        string? reusedFromAttemptId = null,
        TimeSpan? reusedAge = null)
    {
        var stdoutName = ArtifactName(workspaceRole, stepId, "stdout");
        var stderrName = ArtifactName(workspaceRole, stepId, "stderr");
        var includeContent = !process.Success
                             || signal is not null
                             || ReviewCommandKinds.IsAgent(plannedCommand?.ExecutionKind);
        var stdout = await WriteArtifactAsync(stdoutName, process.StdOut, includeContent, ct);
        var stderr = await WriteArtifactAsync(stderrName, process.StdErr, includeContent, ct);
        artifacts.Add(stdout);
        artifacts.Add(stderr);
        var consumed = Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds);
        var limit = Math.Clamp(timeoutSeconds, 1, 7200) * 1000L;
        return new ReviewCommandEvidenceDto(
            stepId,
            aspect,
            fileName,
            arguments,
            _subject.ExpectedResultSha,
            headBefore,
            treeBefore,
            startedAt,
            finishedAt,
            process.ExitCode,
            signal,
            stdout.Sha256,
            stderr.Sha256,
            baselineSha,
            comparison?.NewFailures,
            comparison?.PreExistingFailures,
            comparison?.CacheHit ?? reusedFromAttemptId is not null,
            retryPerformed,
            comparison?.FlakyQuarantinedFailures,
            phase,
            workspaceRole,
            new ReviewCommandBudgetEvidenceDto(
                "review-command",
                limit,
                consumed,
                signal == "timeout" || consumed > limit),
            dependencyCacheHit,
            dependencyCache,
            plannedCommand?.ExecutionKind ?? ReviewCommandKinds.Tool,
            "remote",
            _lease.ExecutorId,
            _lease.HostId,
            _lease.AttemptId,
            plannedCommand?.Model,
            plannedCommand?.ThinkingLevel,
            agentUsage?.InputTokens ?? 0,
            agentUsage?.OutputTokens ?? 0,
            agentUsage?.CacheReadTokens ?? 0,
            agentUsage?.CacheCreationTokens ?? 0,
            reusedFromAttemptId ?? comparison?.ReusedFromAttemptId,
            (long)Math.Max(0, (reusedAge ?? comparison?.ReusedAge ?? TimeSpan.Zero).TotalSeconds),
            comparison?.BaselineExitCode);
    }

    private async Task<ReviewArtifactEvidenceDto> WriteArtifactAsync(
        string name,
        string content,
        bool includeContent,
        CancellationToken ct)
    {
        var path = Path.Combine(ArtifactPath, name);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ReviewArtifactEvidenceDto(
            name,
            "text/plain",
            HashText(content),
            bytes.LongLength,
            includeContent ? Convert.ToBase64String(bytes) : null);
    }

    private async Task<ReviewInfrastructureException> InfrastructureFailureAsync(
        string classification,
        string summary,
        ICollection<ReviewCommandEvidenceDto> commands,
        ICollection<ReviewArtifactEvidenceDto> artifacts,
        CancellationToken ct)
        => new(
            classification,
            summary,
            evidence: new ReviewExecutionEvidence(
                "ReviewInfra",
                await CurrentProofAsync(ct),
                commands.ToArray(),
                artifacts.ToArray(),
                []));

    private async Task<ReviewWorkspaceProofDto> CurrentProofAsync(CancellationToken ct)
    {
        var finalHead = await GitValueAsync("rev-parse", "HEAD", ct);
        var finalTree = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
        var status = await GitValueAsync("status", "--porcelain", "--untracked-files=all", ct);
        var productChanges = status.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Trim().EndsWith(
                DependencyPreparationState.MarkerFileName,
                StringComparison.Ordinal))
            .ToArray();
        var dirtyAfter = productChanges.Length > 0
                         || !string.Equals(finalHead, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(finalTree, _initialTree, StringComparison.OrdinalIgnoreCase);
        return Proof(_subject.RepositoryId, finalHead, finalTree, dirtyAfter);
    }

    private static bool MissingToolchain(ProcessResult process)
        => process.ExitCode == 127
           || (process.ExitCode != 0
               && (process.StdOut + "\n" + process.StdErr).Contains(
                   "node_modules/@angular/cli/bin/ng.js",
                   StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// AGT-2750: a <c>PrivateTmp=true</c> unit restart deletes the detached
    /// worker's <c>/tmp</c> mount while it keeps running under
    /// <c>KillMode=process</c>. MSBuild's node pipe and NuGet's global
    /// mutex mkdtemp both fail against the deleted mount, and the command
    /// produces no parseable test output. Without this check
    /// <see cref="SubjectFailures"/> falls back to a single
    /// <c>&lt;unparsed failure&gt;</c> entry that <see cref="BaselineVerdict"/>
    /// then reports as <c>NewTestFailures</c> - an infrastructure incident
    /// graded as a product regression. Checked before baseline comparison so
    /// it never reaches test-failure parsing.
    /// </summary>
    private static bool TmpMountTornDownDuringBuild(ProcessResult process)
    {
        if (process.Success) return false;
        var output = process.StdOut + "\n" + process.StdErr;
        // A torn-down /tmp leaves no parseable test output. When the test runner
        // did print its summary, the signature came from test content (a theory
        // case that quotes an mkdtemp ENOENT message, for example) and the red
        // result belongs to those tests, not to the mount.
        if (HasTestRunSummary(output)) return false;
        return output.Contains("MSB1025", StringComparison.Ordinal)
               || output.Contains("SocketException (99)", StringComparison.Ordinal)
               || (output.Contains("mkdtemp(\"/tmp/.dotnet.", StringComparison.Ordinal)
                   && output.Contains("ENOENT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The <c>dotnet test</c> console logger prints exactly one of these
    /// summary lines once the test host finished, whatever the outcome; vstest
    /// prints <c>Total tests:</c>. Their presence proves the runner ran to the
    /// end, so an infrastructure signature elsewhere in the output is test
    /// content, not a broken mount.
    /// </summary>
    internal static bool HasTestRunSummary(string output)
        => output.Contains("Passed!  - Failed:", StringComparison.Ordinal)
           || output.Contains("Failed!  - Failed:", StringComparison.Ordinal)
           || output.Contains("Total tests:", StringComparison.Ordinal);

    private static string FailureDetail(ProcessResult process)
    {
        var source = !string.IsNullOrWhiteSpace(process.StdErr)
            ? process.StdErr
            : process.StdOut;
        if (string.IsNullOrWhiteSpace(source)) return "no process detail";
        var detail = string.Join(' ', source
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (detail.StartsWith(
                "Dependency preparation directory is missing:",
                StringComparison.Ordinal))
            return detail;
        const int maximumLength = 1_000;
        return detail.Length <= maximumLength ? detail : detail[..maximumLength];
    }

    /// <summary>
    /// AGT-2820: a review command that produces nothing at all is not making
    /// progress, and the command budget is the wrong instrument to notice it -
    /// four stuck reviews would each have waited out two hours. The watchdog
    /// never shortens a command that is still talking; it only cuts one that has
    /// gone completely silent for a window far longer than any real gap in
    /// build or test output.
    /// </summary>
    private TimeSpan SilenceWindow(int timeoutSeconds)
    {
        var configured = _options.CommandSilenceWatchdogSeconds;
        if (configured <= 0) return TimeSpan.Zero;
        // A window that is not strictly tighter than the command budget would
        // only ever fire at, or after, the budget itself - it adds nothing and
        // would turn a short-budget command into a stall report.
        var budget = Math.Clamp(timeoutSeconds, 1, 7200);
        return configured >= budget ? TimeSpan.Zero : TimeSpan.FromSeconds(configured);
    }

    /// <summary>
    /// AGT-2851: a healthy integration suite with <c>ParallelizeTestCollections=
    /// false</c> and long real waits between test classes can sit near 0% CPU for
    /// stretches well past the configured floor without being stuck - the fixed
    /// 900s default killed reviews that would have finished in 20-25 minutes.
    /// The effective window is never smaller than the configured floor, but it
    /// also grows with the command's own budget, so a two-hour verify command
    /// gets a full hour of genuine quiet before its tree is judged blocked.
    /// </summary>
    private TimeSpan NoCpuProgressWindow(int timeoutSeconds)
    {
        var configured = Math.Max(0, _options.ReviewNoCpuProgressSeconds);
        if (configured <= 0) return TimeSpan.Zero;
        var budget = Math.Clamp(timeoutSeconds, 1, 7200);
        var derivedFromBudget = (int)(budget * NoCpuProgressBudgetFraction);
        return TimeSpan.FromSeconds(Math.Max(configured, derivedFromBudget));
    }

    /// <summary>Share of a verify command's own budget the no-CPU-progress floor may grow to.</summary>
    private const double NoCpuProgressBudgetFraction = 0.5;

    private async Task WatchForSilenceAsync(
        string stepId,
        CommandOutputClock clock,
        StallDetection stall,
        TimeSpan silenceWindow,
        CancellationTokenSource kill,
        CancellationToken ct)
    {
        if (silenceWindow <= TimeSpan.Zero) return;
        var poll = TimeSpan.FromSeconds(Math.Clamp(silenceWindow.TotalSeconds / 20, 1, 30));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(poll, ct).ConfigureAwait(false);
                var silent = clock.SilentFor(DateTime.UtcNow);
                if (silent < silenceWindow) continue;
                if (!stall.TryRecordSilence(silent, silenceWindow))
                    return; // the no-CPU-progress watchdog already claimed this kill.
                _log(
                    $"review-command-stalled step={stepId} silentSeconds={silent.TotalSeconds:F0} " +
                    $"window={silenceWindow.TotalSeconds:F0}s; killing the command instead of " +
                    "holding the review slot for the rest of its budget");
                await kill.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The command finished (or its own budget fired) first.
        }
    }

    /// <summary>
    /// AGT-2820: the budget line names the model as well as the limit. A review
    /// killed on its budget is an infrastructure fact about this model on this
    /// host, and an operator reading the card has to be able to tell that from a
    /// verdict about the change without opening the plan.
    /// </summary>
    private static string BudgetSummary(
        int timeoutSeconds,
        CommandExecution execution,
        string? model = null)
    {
        var limit = Math.Clamp(timeoutSeconds, 1, 7200) * 1000L;
        var consumed = Math.Max(0, (long)(execution.FinishedAt - execution.StartedAt).TotalMilliseconds);
        var violated = execution.Signal == "timeout" || consumed > limit;
        var summary = $"review-command limit={limit}ms consumed={consumed}ms " +
                      $"violated={violated.ToString().ToLowerInvariant()}";
        return string.IsNullOrWhiteSpace(model) ? summary : $"{summary} model={model}";
    }

    private static string ArtifactName(string workspaceRole, string stepId, string stream)
        => $"{SafeSegment(workspaceRole)}.{SafeSegment(stepId)}.{stream}.log";

    private static string CommandLine(ReviewPreparationCommandDto command)
        => string.Join(' ', new[] { command.FileName }.Concat(command.Arguments));

    private static string ResolveWorkingDirectory(string workspacePath, string? workingSubdir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var candidate = string.IsNullOrWhiteSpace(workingSubdir)
            ? root
            : Path.GetFullPath(Path.Combine(
                root,
                workingSubdir.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReviewInfrastructureException(
                "PreparationPathInvalid",
                $"Dependency preparation directory escaped the immutable workspace: {workingSubdir}");
        return candidate;
    }

    private async Task<BaselineComparison> CompareToBaselineAsync(
        ReviewCommandDto command,
        ProcessResult subjectResult,
        DependencyCacheSession? candidateCache,
        ICollection<ReviewCommandEvidenceDto> commands,
        ICollection<ReviewArtifactEvidenceDto> artifacts,
        CancellationToken ct)
    {
        var baselineSha = await ResolveBaselineShaAsync(ct);
        var commandHash = CommandHash(command);
        var key = new ReviewBaselineCacheKey(
            _subject.RepositoryId,
            baselineSha,
            commandHash,
            ToolchainFingerprint(command));
        await PruneBaselineCacheAsync(ct);
        var cachePath = ReviewBaselineResultCache.EntryPath(BaselineCacheRoot, key);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var cached = await ReviewBaselineResultCache.ReadAsync(
            cachePath, key, TestFailureParserVersion, DateTime.UtcNow, ct);
        if (cached is not null)
            return await ReuseBaselineResultAsync(command, subjectResult, cached, "hit", artifacts, commands, ct);

        var lockPath = cachePath + ".lock";
        await using var cacheLock = await AcquireCacheLockAsync(lockPath, ct);
        cached = await ReviewBaselineResultCache.ReadAsync(
            cachePath, key, TestFailureParserVersion, DateTime.UtcNow, ct);
        if (cached is not null)
            return await ReuseBaselineResultAsync(command, subjectResult, cached, "hit-after-wait", artifacts, commands, ct);

        var baselinePath = Path.Combine(AttemptRoot, $"baseline-{commandHash[..12]}");
        var worktree = await ProcessRunner.RunAsync(
            "git",
            ["worktree", "add", "--detach", baselinePath, baselineSha],
            RepositoryPath,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!worktree.Success)
            throw BaselineUnavailable(
                $"Baseline worktree at '{baselineSha}' could not be created: {worktree.StdErr.Trim()}",
                baselineSha,
                command);

        var environment = BaselineProcessEnvironment(commandHash);
        var workspaceRole = $"baseline-{commandHash[..12]}";
        DependencyCacheSession? baselineCache = null;
        CommandExecution execution;
        string baselineHead;
        string baselineTree;
        try
        {
            baselineCache = await ExecutePreparationAsync(
                baselinePath,
                workspaceRole,
                baselineSha,
                environment,
                commands,
                artifacts,
                ct);
            baselineHead = await GitValueAtAsync(
                baselinePath,
                ["rev-parse", "HEAD"],
                environment,
                ct);
            baselineTree = await GitValueAtAsync(
                baselinePath,
                ["rev-parse", "HEAD^{tree}"],
                environment,
                ct);
            execution = await RunCommandAsync(command, baselinePath, ct, environment);
            commands.Add(await AddCommandEvidenceAsync(
                command.StepId,
                command.Aspect,
                command.FileName,
                command.Arguments,
                baselineHead,
                baselineTree,
                execution.Process,
                execution.StartedAt,
                execution.FinishedAt,
                execution.Signal,
                command.TimeoutSeconds,
                "verification",
                workspaceRole,
                baselineSha,
                comparison: null,
                retryPerformed: false,
                dependencyCacheHit: false,
                dependencyCache: null,
                artifacts,
                ct));
            if (MissingToolchain(execution.Process))
            {
                SaveCaches(baselineCache, candidateCache);
                throw await InfrastructureFailureAsync(
                    "ToolUnavailable",
                    $"Baseline review command '{command.StepId}' could not use its declared toolchain: " +
                    $"{CommandLine(command)}; exit={execution.Process.ExitCode}; " +
                    $"budget={BudgetSummary(command.TimeoutSeconds, execution, command.Model)}.",
                    commands,
                    artifacts,
                    ct);
            }
            if (StalledWithoutCpuProgress(execution))
            {
                SaveCaches(baselineCache, candidateCache);
                throw await InfrastructureFailureAsync(
                    NoCpuProgressClassification,
                    NoProgressSummary(
                        "Baseline command",
                        command.StepId,
                        CommandLine(command),
                        execution),
                    commands,
                    artifacts,
                    ct);
            }
            if (execution.Signal is not null || execution.Process.ExitCode < 0)
            {
                var unavailable = BaselineUnavailable(
                    $"Baseline command '{command.StepId}' did not complete normally.",
                    baselineSha,
                    command);
                SaveCaches(baselineCache, candidateCache);
                throw await InfrastructureFailureAsync(
                    unavailable.Classification,
                    unavailable.Message,
                    commands,
                    artifacts,
                    ct);
            }
        }
        finally
        {
            if (baselineCache is not null)
                foreach (var message in baselineCache.Save()) _log(message);
        }
        var failures = BaselineFailures(command, execution.Process);
        var entry = new BaselineCacheEntry(
            TestFailureParserVersion,
            key.RepositoryId,
            key.BaselineSha,
            key.CommandHash,
            key.ToolchainFingerprint,
            _lease.AttemptId,
            baselineHead,
            baselineTree,
            execution.Process.ExitCode,
            failures,
            execution.Process.StdOut,
            execution.Process.StdErr,
            DateTime.UtcNow);
        await ReviewBaselineResultCache.WriteAsync(cachePath, entry, ct);
        _log(
            $"review baseline cache fill repository={_subject.RepositoryId} baseline={baselineSha} " +
            $"step={command.StepId} mode={command.BaselineMode} exit={execution.Process.ExitCode} " +
            $"failures={failures.Count}");
        return BaselineComparison.Create(
            baselineSha,
            failures,
            SubjectFailures(command, subjectResult),
            cacheHit: false,
            execution.Process.ExitCode);
    }

    /// <summary>
    /// Classifies the candidate failure against a baseline result an earlier
    /// attempt already produced. The cached process streams are re-attached as
    /// this attempt's baseline command evidence, so the grade cites the same
    /// artefacts a fresh baseline run would have produced. Only the baseline
    /// run is skipped; the candidate command has already executed in this
    /// attempt's own workspace.
    /// </summary>
    private async Task<BaselineComparison> ReuseBaselineResultAsync(
        ReviewCommandDto command,
        ProcessResult subjectResult,
        BaselineCacheEntry cached,
        string hitKind,
        ICollection<ReviewArtifactEvidenceDto> artifacts,
        ICollection<ReviewCommandEvidenceDto> commands,
        CancellationToken ct)
    {
        var age = ReviewBaselineResultCache.Age(cached, DateTime.UtcNow);
        var process = new ProcessResult(cached.ExitCode, cached.StdOut, cached.StdErr);
        commands.Add(await AddCommandEvidenceAsync(
            command.StepId,
            command.Aspect,
            command.FileName,
            command.Arguments,
            cached.HeadSha,
            cached.TreeSha,
            process,
            cached.CreatedAt,
            cached.CreatedAt,
            signal: null,
            command.TimeoutSeconds,
            "verification",
            $"baseline-{cached.CommandHash[..12]}",
            cached.BaselineSha,
            comparison: null,
            retryPerformed: false,
            dependencyCacheHit: false,
            dependencyCache: null,
            artifacts,
            ct,
            reusedFromAttemptId: cached.AttemptId,
            reusedAge: age));
        _log(
            $"review baseline cache {hitKind} repository={_subject.RepositoryId} " +
            $"baseline={cached.BaselineSha} step={command.StepId} " +
            $"reusedFromAttempt={cached.AttemptId} ageMinutes={age.TotalMinutes:F0}");
        return BaselineComparison.Create(
            cached.BaselineSha,
            cached.Failures,
            SubjectFailures(command, subjectResult),
            cacheHit: true,
            cached.ExitCode,
            cached.AttemptId,
            age);
    }

    private void SaveCaches(params DependencyCacheSession?[] sessions)
    {
        foreach (var session in sessions.Where(item => item is not null).Cast<DependencyCacheSession>())
            foreach (var message in session.Save()) _log(message);
    }

    /// <summary>
    /// Digest of the toolchain entries the review grade records for this
    /// command (<c>runtime</c>, <c>git</c>, and the resolved executable of the
    /// command itself and of every dependency-preparation step). A host that
    /// changed its SDK, its Git, or a planned executable therefore never
    /// reuses a baseline result produced by the previous toolchain.
    /// </summary>
    private string ToolchainFingerprint(ReviewCommandDto command)
    {
        var text = new StringBuilder("runtime=").Append(RuntimeInformation.FrameworkDescription);
        text.Append("\0git=").Append(ExecutableIdentity("git"));
        text.Append("\0command:").Append(command.StepId).Append('=')
            .Append(ExecutableIdentity(_agentCommands.PlannedExecutable(command)));
        foreach (var preparation in _subject.Plan.Preparation ?? [])
            text.Append("\0command:").Append(preparation.StepId).Append('=')
                .Append(ExecutableIdentity(preparation.FileName));
        return HashText(text.ToString());
    }

    /// <summary>
    /// Runs once per attempt, before the first cache lookup, so a result whose
    /// baseline SHA left the integration branch or whose bounded lifetime
    /// expired is gone before it can be read.
    /// </summary>
    private async Task PruneBaselineCacheAsync(CancellationToken ct)
    {
        if (_baselineCachePruned) return;
        _baselineCachePruned = true;
        var integrationHead = _integrationHeadSha;
        await ReviewBaselineResultCache.PruneAsync(
            BaselineCacheRoot,
            _subject.RepositoryId,
            async (sha, token) => integrationHead is null
                                  || await IsOnIntegrationBranchAsync(sha, integrationHead, token),
            DateTime.UtcNow,
            _log,
            ct);
    }

    private async Task<bool> IsOnIntegrationBranchAsync(
        string sha,
        string integrationHead,
        CancellationToken ct)
    {
        var ancestry = await ProcessRunner.RunAsync(
            "git",
            ["merge-base", "--is-ancestor", sha, integrationHead],
            RepositoryPath,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        // Exit 1 is the honest "not an ancestor" answer; anything else (an
        // unknown object, a broken repository) is not proof of removal, so the
        // entry is kept and the bounded age remains its only expiry.
        return ancestry.ExitCode != 1;
    }

    private IReadOnlyDictionary<string, string?> BaselineProcessEnvironment(string commandHash)
    {
        var root = Path.Combine(AttemptRoot, $"baseline-runtime-{commandHash[..12]}");
        var cache = Path.Combine(root, "cache");
        var temp = Path.Combine(root, "tmp");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(home);
        var environment = ProcessEnvironment()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        environment["HOME"] = home;
        environment["TMPDIR"] = temp;
        environment["TMP"] = temp;
        environment["TEMP"] = temp;
        environment["XDG_CACHE_HOME"] = cache;
        environment["NUGET_PACKAGES"] = Path.Combine(cache, "nuget");
        environment["npm_config_cache"] = Path.Combine(cache, "npm");
        environment["PIP_CACHE_DIR"] = Path.Combine(cache, "pip");
        environment["CARGO_HOME"] = Path.Combine(cache, "cargo");
        environment["GRADLE_USER_HOME"] = Path.Combine(cache, "gradle");
        environment["DOTNET_CLI_HOME"] = Path.Combine(cache, "dotnet");
        ReviewBuildServerIsolation.ApplyTo(environment, temp);
        return environment;
    }

    private async Task<string> ResolveBaselineShaAsync(CancellationToken ct)
    {
        if (_baselineSha is not null) return _baselineSha;
        if (string.IsNullOrWhiteSpace(_subject.Plan.IntegrationRef))
            throw BaselineUnavailable(
                "A baseline-compared review command requires the integration ref in the immutable review plan.",
                baselineSha: null,
                command: null);

        var fetch = await ProcessRunner.RunAsync(
            "git",
            ["-c", "credential.helper=", "fetch", "--no-tags", "origin", _subject.Plan.IntegrationRef!],
            RepositoryPath,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!fetch.Success)
            throw BaselineUnavailable(
                $"Integration ref '{_subject.Plan.IntegrationRef}' could not be fetched: {fetch.StdErr.Trim()}",
                baselineSha: null,
                command: null);
        _integrationHeadSha = await GitValueAsync(["rev-parse", "FETCH_HEAD"], ct);
        _baselineSha = await GitValueAsync(["merge-base", _subject.ExpectedResultSha, "FETCH_HEAD"], ct);
        if (_baselineSha.Length == 0)
            throw BaselineUnavailable(
                $"No merge-base exists between the subject and '{_subject.Plan.IntegrationRef}'.",
                baselineSha: null,
                command: null);
        _log(
            $"review baseline resolved repository={_subject.RepositoryId} " +
            $"ref={_subject.Plan.IntegrationRef} base={_baselineSha}");
        return _baselineSha;
    }

    /// <summary>
    /// Builds the immutable delivery material appended to every semantic aspect
    /// prompt. The comparison base is always the merge-base between the exact
    /// Result-SHA and a freshly fetched integration ref. Both Git streams are
    /// counted while only their bounded heads are retained, so exceeding either
    /// budget can never look like a complete diff.
    /// </summary>
    internal async Task<string> BuildReviewMaterialAsync(
        int maximumFiles,
        int maximumLines,
        CancellationToken ct)
    {
        if (maximumFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        if (maximumLines <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLines));

        var baselineSha = await ResolveBaselineShaAsync(ct);
        var changedFiles = new List<string>(Math.Min(maximumFiles, 256));
        var totalFiles = 0;
        var names = await ProcessRunner.RunAsync(
            "git",
            [
                "-c", "credential.helper=", "-c", "core.quotePath=false",
                "diff", "--name-only", "--find-renames", baselineSha,
                _subject.ExpectedResultSha,
            ],
            RepositoryPath,
            onStdOut: line =>
            {
                if (string.IsNullOrEmpty(line)) return;
                totalFiles++;
                if (changedFiles.Count < maximumFiles) changedFiles.Add(line);
            },
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!names.Success)
            throw BaselineUnavailable(
                $"Changed-file enumeration failed against merge-base '{baselineSha}': {names.StdErr.Trim()}",
                baselineSha,
                command: null);

        var diffLines = new List<string>(Math.Min(maximumLines, 16_384));
        var totalDiffLines = 0;
        var currentFile = 0;
        var diff = await ProcessRunner.RunAsync(
            "git",
            [
                "-c", "credential.helper=", "-c", "core.quotePath=false",
                "diff", "--no-ext-diff", "--find-renames", "--unified=3",
                baselineSha, _subject.ExpectedResultSha,
            ],
            RepositoryPath,
            onStdOut: line =>
            {
                totalDiffLines++;
                if (line.StartsWith("diff --git ", StringComparison.Ordinal)) currentFile++;
                if (currentFile <= maximumFiles && diffLines.Count < maximumLines)
                    diffLines.Add(line);
            },
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!diff.Success)
            throw BaselineUnavailable(
                $"Unified diff failed against merge-base '{baselineSha}': {diff.StdErr.Trim()}",
                baselineSha,
                command: null);

        var truncated = totalFiles > maximumFiles || totalDiffLines > maximumLines;
        var shownFiles = Math.Min(totalFiles, maximumFiles);
        var shownLines = diffLines.Count;
        _log(
            $"review material prepared ref={_subject.Plan.IntegrationRef} base={baselineSha} " +
            $"result={_subject.ExpectedResultSha} files={shownFiles}/{totalFiles} " +
            $"lines={shownLines}/{totalDiffLines} truncated={truncated.ToString().ToLowerInvariant()}");
        return RenderReviewMaterial(
            _subject.Plan.IntegrationRef!,
            baselineSha,
            _subject.ExpectedResultSha,
            changedFiles,
            diffLines,
            totalFiles,
            totalDiffLines,
            maximumFiles,
            maximumLines,
            truncated);
    }

    private static string AppendReviewMaterial(string prompt, string reviewMaterial)
        => prompt.TrimEnd() + Environment.NewLine + Environment.NewLine + reviewMaterial;

    private static string RenderReviewMaterial(
        string integrationRef,
        string baselineSha,
        string resultSha,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> diffLines,
        int totalFiles,
        int totalDiffLines,
        int maximumFiles,
        int maximumLines,
        bool truncated)
    {
        var text = new StringBuilder();
        text.AppendLine("## Authoritative delivery review material");
        text.AppendLine();
        text.AppendLine($"- Integration ref: `{integrationRef}` (freshly fetched)");
        text.AppendLine($"- Merge base: `{baselineSha}`");
        text.AppendLine($"- Delivery Result-SHA: `{resultSha}`");
        text.AppendLine($"- Changed files: {totalFiles}");
        text.AppendLine();
        text.AppendLine("### Changed-file list");
        text.AppendLine();
        if (changedFiles.Count == 0) text.AppendLine("_No changed files._");
        else foreach (var file in changedFiles) text.Append("- `").Append(file).AppendLine("`");
        text.AppendLine();
        text.AppendLine("### Unified diff");
        text.AppendLine();
        text.AppendLine("```diff");
        foreach (var line in diffLines) text.AppendLine(line);
        text.AppendLine("```");
        if (truncated)
        {
            text.AppendLine();
            text.Append("[[REVIEW_DIFF_TRUNCATED: diff truncated at ")
                .Append(Math.Min(totalFiles, maximumFiles)).Append(" files/")
                .Append(diffLines.Count).Append(" lines; delivery has ")
                .Append(totalFiles).Append(" changed files/")
                .Append(totalDiffLines).Append(" diff lines; budget ")
                .Append(maximumFiles).Append(" files/")
                .Append(maximumLines).AppendLine(" lines.]]");
        }
        return text.ToString();
    }

    /// <summary>
    /// Every <c>BaselineUnavailable</c> carries the base it used, the ref it
    /// resolved that base from, and the command that died. AGT-2220 repeated the
    /// bare classification four times, so nothing on the card ever showed that
    /// the base was an ancient merge-base against a stale integration ref.
    /// </summary>
    private ReviewInfrastructureException BaselineUnavailable(
        string message,
        string? baselineSha,
        ReviewCommandDto? command)
        => new(
            "BaselineUnavailable",
            ReviewInfrastructureDiagnosis.Append(
                message,
                [
                    new(ReviewInfrastructureDiagnosis.BaseKey,
                        baselineSha ?? ReviewInfrastructureDiagnosis.UnresolvedBase),
                    new(ReviewInfrastructureDiagnosis.RefKey, _subject.Plan.IntegrationRef),
                    new(ReviewInfrastructureDiagnosis.StepKey, command?.StepId),
                    new(ReviewInfrastructureDiagnosis.CommandKey, CommandLine(command)),
                ]));

    private static string? CommandLine(ReviewCommandDto? command)
        => command is null
            ? null
            : string.Join(' ', new[] { command.FileName }.Concat(command.Arguments));

    private static async Task<FileStream> AcquireCacheLockAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task AddArtifactsAsync(
        string name,
        ProcessResult process,
        ICollection<ReviewArtifactEvidenceDto> artifacts,
        CancellationToken ct)
    {
        foreach (var (suffix, content) in new[]
                 {
                     ("stdout.log", process.StdOut),
                     ("stderr.log", process.StdErr),
                 })
        {
            artifacts.Add(await WriteArtifactAsync(
                $"{name}.{suffix}",
                content,
                includeContent: true,
                ct));
        }
    }

    public ReviewEnvironmentDto EnvironmentEvidence(ReviewLeaseDto? authority = null)
    {
        // A re-claim changes who may submit the report without moving the
        // detached worker. Attribute the execution to the current authority,
        // while retaining the paths, ports, and namespace that were physically
        // materialized for the original lease.
        var attribution = authority ?? _lease;
        var toolchain = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["git"] = ExecutableIdentity("git"),
        };
        foreach (var command in _subject.Plan.Commands)
            toolchain[$"command:{command.StepId}"] = ExecutableIdentity(
                _agentCommands.PlannedExecutable(command));
        foreach (var command in _subject.Plan.Preparation ?? [])
            toolchain[$"command:{command.StepId}"] = ExecutableIdentity(command.FileName);
        return new ReviewEnvironmentDto(
            attribution.HostId,
            attribution.ExecutorId,
            attribution.InstanceId,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            toolchain,
            new Dictionary<string, string>
            {
                ["serviceRole"] = "remote-review-executor",
                ["workspace"] = AttemptRoot,
                ["cache"] = CachePath,
                ["baselineResultCache"] = BaselineCacheRoot,
                ["dependencyCache"] = DependencyCacheRoot,
                ["temp"] = TempPath,
                ["ports"] = $"{_lease.PortBase}-{_lease.PortBase + 7}",
                ["buildServers"] = ReviewBuildServerIsolation.IsIsolated(ProcessEnvironment())
                    ? "per-attempt"
                    : "host-shared",
                // The configured floor, not the effective per-command window: each
                // command's actual window also grows with its own budget (AGT-2851).
                ["hangWatchdogFloorSeconds"] = Math.Max(0, _options.ReviewNoCpuProgressSeconds)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["containers"] = _lease.ResourceNamespace,
                ["databases"] = _lease.ResourceNamespace,
                ["credentials"] = "review-read-only",
            });
    }

    public async Task<bool> CleanupAsync(string attemptId = "unknown")
    {
        if (!Directory.Exists(AttemptRoot)) return true;
        var expectedRoot = Path.GetFullPath(_options.ReviewWorkDir)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(AttemptRoot);
        if (!target.StartsWith(expectedRoot, StringComparison.Ordinal)
            || string.Equals(target.TrimEnd(Path.DirectorySeparatorChar),
                expectedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing review cleanup outside the configured attempt root.");
        // A worker that died without a live daemon watching it can leave its CLI
        // child running with this attempt root as its cwd. Deleting the tree out
        // from under a live process turns it into an untraceable "(deleted)" cwd
        // zombie (AGT-2759), so every process rooted here is reaped first.
        await CliProcessReaper.ReapWorkspaceAsync(target, attemptId, _log, CancellationToken.None);
        // The attempt root holds a clone, so it contains read-only git objects
        // and possibly reparse points - a plain recursive delete cannot remove it.
        ResilientDirectory.Delete(target);
        return !Directory.Exists(target);
    }

    private async Task MaterializeGitAsync(string repositoryUrl, CancellationToken ct)
    {
        var clone = await ProcessRunner.RunAsync(
            "git",
            ["-c", "credential.helper=", "clone", "--no-checkout", "--filter=blob:none", repositoryUrl, RepositoryPath],
            AttemptRoot,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!clone.Success)
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                $"Repository clone failed: {clone.StdErr.Trim()}");
        var resultRef = string.IsNullOrWhiteSpace(_subject.ResultRef)
            ? _subject.ExpectedResultSha
            : _subject.ResultRef!;
        var fetch = await ProcessRunner.RunAsync(
            "git",
            ["-c", "credential.helper=", "fetch", "--no-tags", "origin", resultRef],
            RepositoryPath,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!fetch.Success)
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                $"Immutable result ref '{resultRef}' could not be fetched: {fetch.StdErr.Trim()}");
        await GitRequiredAsync(["checkout", "--detach", "FETCH_HEAD"], ct);
    }

    private async Task MaterializeBundleAsync(TaskServerClient client, CancellationToken ct)
    {
        var artifact = await client.GetArtifactContentAsync(
            _subject.SourceRunId,
            _subject.SourceBundleArtifactId!,
            ct);
        if (artifact is null)
            throw new ReviewInfrastructureException("SnapshotUnavailable", "Immutable source bundle is unavailable.");
        var bytes = Convert.FromBase64String(artifact.ContentBase64);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, _subject.SourceBundleSha256, StringComparison.OrdinalIgnoreCase))
            throw new ReviewInfrastructureException(
                "SourceBundleDigestMismatch",
                $"Source bundle digest '{digest}' does not match '{_subject.SourceBundleSha256}'.");
        var bundlePath = Path.Combine(AttemptRoot, "source.bundle");
        await File.WriteAllBytesAsync(bundlePath, bytes, ct);
        var clone = await ProcessRunner.RunAsync(
            "git",
            ["clone", "--no-checkout", bundlePath, RepositoryPath],
            AttemptRoot,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!clone.Success)
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                $"Source bundle could not be materialized: {clone.StdErr.Trim()}");
        await GitRequiredAsync(["checkout", "--detach", _subject.ExpectedResultSha], ct);
    }

    private async Task<string> GitValueAsync(string first, string second, CancellationToken ct)
        => await GitValueAsync([first, second], ct);

    private async Task<string> GitValueAsync(string first, string second, string third, CancellationToken ct)
        => await GitValueAsync([first, second, third], ct);

    private async Task<string> GitValueAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        => await GitValueAtAsync(
            RepositoryPath,
            arguments,
            ProcessEnvironment(),
            ct);

    private static async Task<string> GitValueAtAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", arguments, repositoryPath,
            environment: environment, clearEnvironment: true, ct: ct);
        if (!result.Success)
            throw new ReviewInfrastructureException(
                "WorkspaceProofFailed",
                $"git {string.Join(' ', arguments)} failed: {result.StdErr.Trim()}");
        return result.StdOut.Trim();
    }

    private async Task GitRequiredAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        => _ = await GitValueAsync(arguments, ct);

    private ReviewWorkspaceProofDto Proof(
        string repositoryId,
        string head,
        string tree,
        bool dirtyAfter)
        => new(
            repositoryId,
            _subject.ExpectedResultSha,
            head,
            tree,
            _dirtyBefore,
            dirtyAfter,
            HashText(Path.GetFullPath(AttemptRoot)),
            _lease.ResourceNamespace);

    private static ReviewVerdictDto ParseVerdict(ReviewCommandDto command, ProcessResult result)
    {
        var marker = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.Contains("[[ASPECT_VERDICT:", StringComparison.Ordinal));
        if (marker is not null)
        {
            var status = Field(marker, "status") ?? (result.Success ? "pass" : "block");
            return new ReviewVerdictDto(
                command.Aspect,
                status,
                Field(marker, "classification") ?? "RemoteAspectVerdict",
                Field(marker, "summary") ?? $"Remote aspect '{command.Aspect}' returned {status}.",
                Field(marker, "evidence_checked"),
                Field(marker, "missing"));
        }
        return new ReviewVerdictDto(
            command.Aspect,
            result.Success ? "pass" : "block",
            result.Success ? "CommandPassed" : "CommandFailed",
            result.Success
                ? $"Review command '{command.StepId}' passed."
                : $"Review command '{command.StepId}' exited {result.ExitCode}.",
            $"command:{command.StepId}",
            result.Success ? "none" : $"successful execution of {command.StepId}");
    }

    internal static ReviewVerdictDto BaselineVerdict(
        ReviewCommandDto command,
        BaselineComparison comparison)
    {
        var newFailures = comparison.NewFailures.Count == 0
            ? "0 new failures"
            : $"{comparison.NewFailures.Count} new failures: {string.Join(", ", comparison.NewFailures)}";
        var preExisting = comparison.PreExistingFailures.Count == 0
            ? "0 pre-existing failures"
            : $"{comparison.PreExistingFailures.Count} pre-existing failures: {string.Join(", ", comparison.PreExistingFailures)}";
        var quarantined = comparison.FlakyQuarantinedFailures.Count == 0
            ? "0 flaky quarantined failures"
            : $"{comparison.FlakyQuarantinedFailures.Count} flaky quarantined failures: {string.Join(", ", comparison.FlakyQuarantinedFailures)}";
        var owner = ReviewFailureAttributionPolicy.Attribute(
            commandFailed: true,
            command.BaselineMode,
            comparison.BaselineSha,
            comparison.BaselineExitCode,
            comparison.NewFailures);
        var classification = owner switch
        {
            ReviewFailureOwner.IntegrationBranch => IntegrationBranchDefectClassification,
            _ when comparison.NewFailures.Count > 0 => "NewTestFailures",
            _ when comparison.FlakyQuarantinedFailures.Count > 0 => ReviewFlakyTestIndex.VerdictClassification,
            _ => "BaselineCompared",
        };
        var baselineState = comparison.BaselineExitCode == 0
            ? "green on the merge base"
            : $"already exiting {comparison.BaselineExitCode} on the merge base";
        return new ReviewVerdictDto(
            command.Aspect,
            owner == ReviewFailureOwner.Delivery ? "block" : "pass",
            classification,
            $"{newFailures}; {preExisting}; {quarantined}. Step {command.StepId} is {baselineState}. " +
            $"Baseline {comparison.BaselineSha} ({comparison.Provenance}).",
            $"command:{command.StepId}; baseline:{comparison.BaselineSha}",
            owner == ReviewFailureOwner.Delivery
                ? comparison.NewFailures.Count > 0
                    ? string.Join(", ", comparison.NewFailures)
                    : $"successful execution of {command.StepId}"
                : "none");
    }

    /// <summary>
    /// Verdict classification and review outcome token for a gate that was
    /// already red on the integration branch (AGT-2819).
    /// </summary>
    internal const string IntegrationBranchDefectClassification = "IntegrationBranchDefect";

    private static IReadOnlyList<string> SubjectFailures(
        ReviewCommandDto command,
        ProcessResult result)
    {
        // Exit-status comparison has no failure names to diff. Synthesising the
        // <unparsed failure> marker for a lint command made a gate that was
        // already red on the integration branch look like a brand-new product
        // failure on every card that passed through it (AGT-2819).
        if (ReviewBaselineModes.IsExitStatus(command.BaselineMode)) return [];
        var failures = ParsedTestFailures(result);
        if (!result.Success && failures.Count == 0)
            return [$"<unparsed failure in {command.StepId}>"];
        return failures;
    }

    /// <summary>
    /// Failure names recorded for the merge-base run. Empty under exit-status
    /// comparison, where the exit code alone carries the baseline's state.
    /// </summary>
    private static IReadOnlyList<string> BaselineFailures(
        ReviewCommandDto command,
        ProcessResult result)
        => ReviewBaselineModes.IsExitStatus(command.BaselineMode)
            ? []
            : ParsedTestFailures(result);

    internal static IReadOnlyList<string> ParsedTestFailures(ProcessResult result)
    {
        if (result.Success) return [];
        var failures = new HashSet<string>(StringComparer.Ordinal);
        var fileFailures = new HashSet<string>(StringComparer.Ordinal);
        var wrapperFailures = new HashSet<string>(StringComparer.Ordinal);
        string? jestFile = null;
        foreach (var line in $"{result.StdOut}\n{result.StdErr}"
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedLine = AnsiEscapeSequence.Replace(line, string.Empty);
            var match = DotNetFailedPrefix.Match(normalizedLine);
            if (!match.Success) match = DotNetFailSuffix.Match(normalizedLine);
            if (match.Success)
            {
                AddFailure(failures, match.Groups["name"].Value);
                continue;
            }

            match = KarmaFailure.Match(normalizedLine);
            if (match.Success)
            {
                AddFailure(failures, match.Groups["name"].Value);
                continue;
            }

            match = TestFileFailure.Match(normalizedLine);
            if (match.Success)
            {
                var name = NormalizeFailureName(match.Groups["name"].Value);
                if (name.Contains(" > ", StringComparison.Ordinal))
                    AddFailure(failures, name);
                else
                {
                    jestFile = name;
                    AddFailure(fileFailures, name);
                }
                continue;
            }

            match = JestFailure.Match(normalizedLine);
            if (match.Success)
            {
                var name = NormalizeFailureName(match.Groups["name"].Value);
                if (jestFile is not null)
                    fileFailures.Remove(jestFile);
                AddFailure(failures, jestFile is null ? name : $"{jestFile} > {name}");
                continue;
            }

            if (NodeTapNonFailure.IsMatch(normalizedLine))
                continue;

            match = NodeTapFailure.Match(normalizedLine);
            if (!match.Success) match = NodeSpecFailure.Match(normalizedLine);
            if (match.Success)
            {
                AddFailure(failures, match.Groups["name"].Value);
                continue;
            }

            match = NpmLifecycleFailure.Match(normalizedLine);
            if (!match.Success) match = NpmLegacyLifecycleFailure.Match(normalizedLine);
            if (match.Success)
            {
                AddFailure(wrapperFailures, $"npm script {match.Groups["script"].Value}");
                continue;
            }

            if (NpmTestFailure.IsMatch(normalizedLine))
                wrapperFailures.Add("npm test");
        }
        failures.UnionWith(fileFailures);
        var parsed = failures.Count > 0 ? failures : wrapperFailures;
        return parsed
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddFailure(ISet<string> failures, string value)
    {
        var name = NormalizeFailureName(value);
        if (name.Length > 0 && !name.StartsWith("!", StringComparison.Ordinal))
            failures.Add(name);
    }

    private static string NormalizeFailureName(string value)
        => FailureHierarchySeparator.Replace(value.Trim(), " > ");

    private static string CommandHash(ReviewCommandDto command)
    {
        var text = new StringBuilder(command.FileName);
        foreach (var argument in command.Arguments)
            text.Append('\0').Append(argument);
        // The comparison mode decides what the cached baseline entry means
        // (failure names versus exit status only), so it belongs in the key.
        text.Append('\0').Append(command.BaselineMode);
        return HashText(text.ToString());
    }

    private static readonly Regex DotNetFailedPrefix = new(
        @"^\s*Failed\s+(?<name>.+?)\s+\[[^\]]+\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DotNetFailSuffix = new(
        @"^\s*(?:\[[^\]]+\]\s+)?(?<name>.+?)\s+\[FAIL\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KarmaFailure = new(
        @"^\s*.+?\([^)]*\)\s+(?<name>.+?)\s+FAILED\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TestFileFailure = new(
        @"^\s*FAIL\s+(?<name>.+?)(?:\s+\(\d+(?:\.\d+)?\s*(?:ms|s)\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JestFailure = new(
        @"^\s*●\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NodeTapFailure = new(
        @"^\s*not ok\s+\d+\s+-\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NodeTapNonFailure = new(
        @"^\s*not ok\s+\d+\s+-\s+.+?\s+#\s+(?:SKIP|TODO)\b.*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NodeSpecFailure = new(
        @"^\s*✖\s+(?<name>.+?)(?:\s+\([\d.]+\s*m?s\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NpmLifecycleFailure = new(
        """^\s*npm\s+(?:ERR!|error)\s+Lifecycle script\s+[`'"](?<script>[^`'"]+)[`'"]\s+failed\b.*$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NpmLegacyLifecycleFailure = new(
        @"^\s*npm\s+ERR!\s+Failed at the\s+.+\s+(?<script>\S+)\s+script\.\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NpmTestFailure = new(
        @"^\s*npm\s+ERR!\s+Test failed\.\s+See above for more details\.?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex FailureHierarchySeparator = new(
        @"\s+(?:›|>)\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscapeSequence = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? Field(string marker, string key)
    {
        var content = marker[(marker.IndexOf(':') + 1)..].Replace("]]", string.Empty, StringComparison.Ordinal);
        foreach (var field in content.Split(';', StringSplitOptions.TrimEntries))
        {
            var split = field.IndexOf('=');
            if (split > 0 && string.Equals(field[..split].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return field[(split + 1)..].Trim();
        }
        return null;
    }

    private static bool AgentCommandUnavailable(ReviewCommandDto command, ProcessResult result)
        => ReviewCommandKinds.IsAgent(command.ExecutionKind)
           && !result.Success
           && !result.StdOut.Contains("[[ASPECT_VERDICT:", StringComparison.Ordinal);

    /// <summary>
    /// True when an agent-aspect call was killed on its own timeout budget
    /// (AGT-2749 addendum). <see cref="CarWorkerExecution"/> reports this as
    /// exit code 124 with a <c>"timeout"</c> signal; distinct from a genuinely
    /// missing or crashed toolchain, which <see cref="AgentCommandUnavailable"/>
    /// still classifies as <c>ToolUnavailable</c>.
    /// </summary>
    private static bool AspectCommandTimedOut(ReviewCommandDto command, CommandExecution execution)
        => ReviewCommandKinds.IsAgent(command.ExecutionKind)
           && (execution.Signal == "timeout" || execution.Process.ExitCode == 124);

    internal static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string ExecutableIdentity(string fileName)
    {
        var path = ResolveExecutable(fileName);
        if (path is null)
            return $"unresolved:{fileName}";
        using var stream = File.OpenRead(path);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return $"{Path.GetFullPath(path)};sha256={digest};size={stream.Length}";
    }

    private string? ResolveExecutable(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
            return File.Exists(fileName) ? fileName : null;
        if (fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            var repositoryRelative = Path.GetFullPath(Path.Combine(RepositoryPath, fileName));
            return File.Exists(repositoryRelative) ? repositoryRelative : null;
        }

        var pathValue = ProcessEnvironment().TryGetValue("PATH", out var configuredPath)
            ? configuredPath
            : null;
        foreach (var directory in (pathValue ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
            if (!OperatingSystem.IsWindows()) continue;
            foreach (var extension in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                         .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                candidate = Path.Combine(directory, fileName + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static bool SafeEnvironmentName(string name)
        => name.Length is > 0 and <= 128
           && (char.IsLetter(name[0]) || name[0] == '_')
           && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    internal static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
}

/// <summary>
/// Last time a running review command wrote anything on either stream. Shared
/// between the process reader callbacks and the silence watchdog, so the only
/// mutable state is one interlocked tick count.
/// </summary>
internal sealed class CommandOutputClock
{
    private long _ticks = DateTime.UtcNow.Ticks;

    public void Mark() => Interlocked.Exchange(ref _ticks, DateTime.UtcNow.Ticks);

    public TimeSpan SilentFor(DateTime nowUtc)
        => TimeSpan.FromTicks(Math.Max(0, nowUtc.Ticks - Interlocked.Read(ref _ticks)));
}

/// <summary>Which hang detector, if any, ended a review command.</summary>
internal enum StallDetector
{
    None,
    Silence,
    NoCpuProgress,
}

/// <summary>
/// Records which hang detector - not the command budget - ended a review
/// command, so the cancellation is reported under the detector that actually
/// fired instead of assuming one always wins. The silence watchdog and the
/// no-CPU-progress watchdog run concurrently and can both observe a blocked
/// tree; whichever calls <see cref="TryRecordSilence"/> or
/// <see cref="TryRecordNoCpuProgress"/> first claims the kill, and the other
/// call is a no-op (AGT-2851).
/// </summary>
internal sealed class StallDetection
{
    private readonly object _gate = new();
    private bool _claimed;

    public StallDetector Detector { get; private set; } = StallDetector.None;
    public TimeSpan? Silence { get; private set; }
    public TimeSpan? SilenceWindow { get; private set; }
    public TimeSpan? CpuElapsed { get; private set; }
    public TimeSpan? NoCpuProgressWindow { get; private set; }
    public TimeSpan? SampledCpu { get; private set; }

    public bool TryRecordSilence(TimeSpan silence, TimeSpan window)
    {
        lock (_gate)
        {
            if (_claimed) return false;
            _claimed = true;
            Detector = StallDetector.Silence;
            Silence = silence;
            SilenceWindow = window;
            return true;
        }
    }

    public bool TryRecordNoCpuProgress(TimeSpan elapsed, TimeSpan window, TimeSpan? sampledCpu)
    {
        lock (_gate)
        {
            if (_claimed) return false;
            _claimed = true;
            Detector = StallDetector.NoCpuProgress;
            CpuElapsed = elapsed;
            NoCpuProgressWindow = window;
            SampledCpu = sampledCpu;
            return true;
        }
    }

    public StallDiagnostics? ToDiagnostics()
        => Detector == StallDetector.None
            ? null
            : new StallDiagnostics(Detector, Silence, SilenceWindow, CpuElapsed, NoCpuProgressWindow, SampledCpu);
}

/// <summary>Effective thresholds and measured values behind a hang-watchdog kill, for reporting.</summary>
internal sealed record StallDiagnostics(
    StallDetector Detector,
    TimeSpan? Silence,
    TimeSpan? SilenceWindow,
    TimeSpan? CpuElapsed,
    TimeSpan? NoCpuProgressWindow,
    TimeSpan? SampledCpu);

public sealed record ReviewExecutionEvidence(
    string Outcome,
    ReviewWorkspaceProofDto Workspace,
    IReadOnlyList<ReviewCommandEvidenceDto> Commands,
    IReadOnlyList<ReviewArtifactEvidenceDto> Artifacts,
    IReadOnlyList<ReviewVerdictDto> Verdicts);

internal sealed record ReviewExecutionCheckpoint(
    IReadOnlyList<string> CompletedStepIds,
    double CompletedCommandSeconds,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ReviewCommandEvidenceDto>? Commands = null,
    IReadOnlyList<ReviewArtifactEvidenceDto>? Artifacts = null,
    IReadOnlyList<ReviewVerdictDto>? Verdicts = null);

internal sealed record CommandExecution(
    ProcessResult Process,
    DateTime StartedAt,
    DateTime FinishedAt,
    string? Signal,
    RemoteAgentUsage? AgentUsage = null,
    StallDiagnostics? Stall = null);

internal sealed record RemoteAgentUsage(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens);

internal sealed record BaselineComparison(
    string BaselineSha,
    IReadOnlyList<string> BaselineFailures,
    IReadOnlyList<string> NewFailures,
    IReadOnlyList<string> PreExistingFailures,
    IReadOnlyList<string> FlakyQuarantinedFailures,
    bool CacheHit,
    int BaselineExitCode,
    string? ReusedFromAttemptId = null,
    TimeSpan? ReusedAge = null)
{
    /// <summary>
    /// How the baseline side of this comparison was produced, worded once for
    /// the verdict summary, the Markdown grade, and the card projection.
    /// </summary>
    public string Provenance
        => CacheHit && !string.IsNullOrWhiteSpace(ReusedFromAttemptId)
            ? ReviewBaselineReuse.Citation(
                ReusedFromAttemptId,
                (long)Math.Max(0, (ReusedAge ?? TimeSpan.Zero).TotalSeconds))
            : ReviewBaselineReuse.ExecutedInThisAttempt;

    public static BaselineComparison Create(
        string baselineSha,
        IReadOnlyList<string> baselineFailures,
        IReadOnlyList<string> subjectFailures,
        bool cacheHit,
        int baselineExitCode,
        string? reusedFromAttemptId = null,
        TimeSpan? reusedAge = null)
    {
        var baseline = baselineFailures.ToHashSet(StringComparer.Ordinal);
        return new BaselineComparison(
            baselineSha,
            baselineFailures,
            subjectFailures.Where(failure => !baseline.Contains(failure))
                .Order(StringComparer.Ordinal)
                .ToArray(),
            subjectFailures.Where(baseline.Contains)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            [],
            cacheHit,
            baselineExitCode,
            reusedFromAttemptId,
            reusedAge);
    }

    public BaselineComparison Reclassify(
        IReadOnlyList<string> subjectFailures,
        ReviewFlakyTestIndex reviewFlakyTests)
    {
        var retried = Create(
            BaselineSha,
            BaselineFailures,
            subjectFailures,
            CacheHit,
            BaselineExitCode,
            ReusedFromAttemptId,
            ReusedAge);
        var retriedFailures = subjectFailures.ToHashSet(StringComparer.Ordinal);
        return retried with
        {
            FlakyQuarantinedFailures = NewFailures
                .Where(failure => !retriedFailures.Contains(failure) && reviewFlakyTests.Contains(failure))
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };
    }
}

public sealed class ReviewInfrastructureException : Exception
{
    public ReviewInfrastructureException(
        string classification,
        string message,
        Exception? inner = null,
        ReviewExecutionEvidence? evidence = null)
        : base(message, inner)
    {
        Classification = classification;
        Evidence = evidence;
    }

    public string Classification { get; }
    public ReviewExecutionEvidence? Evidence { get; }
}
