using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly RunnerOptions _options;
    private readonly ReviewSubjectDto _subject;
    private readonly ReviewLeaseDto _lease;
    private readonly Action<string> _log;
    private readonly RemoteReviewAgentCommandRunner _agentCommands;
    private string? _initialTree;
    private string? _baselineSha;
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
        => ExecutePlanAsync(ct, checkpoint: null);

    internal async Task<ReviewExecutionEvidence> ExecutePlanAsync(
        CancellationToken ct,
        Func<ReviewExecutionCheckpoint, CancellationToken, Task>? checkpoint)
    {
        var commands = new List<ReviewCommandEvidenceDto>();
        var verdicts = new List<ReviewVerdictDto>();
        var artifacts = new List<ReviewArtifactEvidenceDto>();
        DependencyCacheSession? candidateCache = null;
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

            foreach (var command in _subject.Plan.Commands)
            {
                var headBefore = await GitValueAsync("rev-parse", "HEAD", ct);
                var treeBefore = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
                if (!string.Equals(headBefore, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
                    throw new ReviewInfrastructureException(
                        "CommandSubjectMismatch",
                        $"Step '{command.StepId}' would run at '{headBefore}', not '{_subject.ExpectedResultSha}'.");
                var execution = ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    ? await _agentCommands.RunAsync(command, ct)
                    : await RunCommandAsync(command, RepositoryPath, ct);
                var infrastructureClassification = ReviewCommandInfrastructureClassification(
                    command,
                    execution.Process);
                if (infrastructureClassification is not null)
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
                    var providerAuthenticationUnavailable =
                        infrastructureClassification == "ProviderAuthenticationUnavailable";
                    throw await InfrastructureFailureAsync(
                        infrastructureClassification,
                        providerAuthenticationUnavailable
                            ? $"Review command '{command.StepId}' was not run because its declared " +
                              $"{command.CliType} toolchain is logged out; re-authenticate the host and requeue the review."
                            : $"Review command '{command.StepId}' could not use its declared toolchain: " +
                        $"{CommandLine(command)}; exit={execution.Process.ExitCode}; " +
                        $"budget={BudgetSummary(command.TimeoutSeconds, execution)}.",
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
                                $"exit={execution.Process.ExitCode}; budget={BudgetSummary(command.TimeoutSeconds, execution)}.",
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
                verdicts.Add(comparison is null
                    ? ParseVerdict(command, execution.Process)
                    : BaselineVerdict(command, comparison));
                if (checkpoint is not null)
                {
                    await checkpoint(
                        new ReviewExecutionCheckpoint(
                            commands.Select(item => item.StepId).ToArray(),
                            commands.Sum(item => Math.Max(
                                0,
                                (item.FinishedAt - item.StartedAt).TotalSeconds)),
                            DateTime.UtcNow),
                        ct);
                }
            }
        }
        finally
        {
            if (candidateCache is not null)
                foreach (var message in candidateCache.Save()) _log(message);
        }

        var proof = await CurrentProofAsync(ct);
        var outcome = verdicts.Any(verdict =>
            verdict.Status is "block" or "concerns" or "fail")
            ? "ProductFailure"
            : "Pass";
        return new ReviewExecutionEvidence(outcome, proof, commands, artifacts, verdicts);
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
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 7200)));
            var completeStdout = new StringBuilder();
            var completeStderr = new StringBuilder();
            var boundedProcess = await ProcessRunner.RunAsync(
                fileName,
                arguments,
                workingDirectory,
                onStdOut: line => completeStdout.AppendLine(line),
                onStdErr: line => completeStderr.AppendLine(line),
                environment: environment ?? ProcessEnvironment(),
                clearEnvironment: true,
                ct: timeout.Token);
            process = new ProcessResult(
                boundedProcess.ExitCode,
                completeStdout.ToString(),
                completeStderr.ToString());
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
        return new CommandExecution(process, started, DateTime.UtcNow, signal);
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
        RemoteAgentUsage? agentUsage = null)
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
            comparison?.CacheHit ?? false,
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
            agentUsage?.CacheCreationTokens ?? 0);
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

    private static string BudgetSummary(int timeoutSeconds, CommandExecution execution)
    {
        var limit = Math.Clamp(timeoutSeconds, 1, 7200) * 1000L;
        var consumed = Math.Max(0, (long)(execution.FinishedAt - execution.StartedAt).TotalMilliseconds);
        return $"review-command limit={limit}ms consumed={consumed}ms " +
               $"violated={(execution.Signal == "timeout" || consumed > limit).ToString().ToLowerInvariant()}";
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
        var cacheDirectory = Path.Combine(
            BaselineCacheRoot,
            HashText(_subject.RepositoryId),
            baselineSha);
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory, $"{commandHash}.json");
        var cached = await ReadBaselineCacheAsync(cachePath, baselineSha, commandHash, ct);
        if (cached is not null)
        {
            _log($"review baseline cache hit repository={_subject.RepositoryId} baseline={baselineSha} step={command.StepId}");
            return BaselineComparison.Create(
                baselineSha,
                cached.Failures,
                SubjectFailures(command, subjectResult),
                cacheHit: true);
        }

        var lockPath = cachePath + ".lock";
        await using var cacheLock = await AcquireCacheLockAsync(lockPath, ct);
        cached = await ReadBaselineCacheAsync(cachePath, baselineSha, commandHash, ct);
        if (cached is not null)
        {
            _log($"review baseline cache hit after wait repository={_subject.RepositoryId} baseline={baselineSha} step={command.StepId}");
            return BaselineComparison.Create(
                baselineSha,
                cached.Failures,
                SubjectFailures(command, subjectResult),
                cacheHit: true);
        }

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
            var headBefore = await GitValueAtAsync(
                baselinePath,
                ["rev-parse", "HEAD"],
                environment,
                ct);
            var treeBefore = await GitValueAtAsync(
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
                headBefore,
                treeBefore,
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
                    $"budget={BudgetSummary(command.TimeoutSeconds, execution)}.",
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
        var failures = ParsedTestFailures(execution.Process);
        var entry = new BaselineCacheEntry(
            TestFailureParserVersion,
            baselineSha,
            commandHash,
            execution.Process.ExitCode,
            failures,
            DateTime.UtcNow);
        await WriteBaselineCacheAsync(cachePath, entry, ct);
        _log($"review baseline cache fill repository={_subject.RepositoryId} baseline={baselineSha} step={command.StepId} failures={failures.Count}");
        return BaselineComparison.Create(
            baselineSha,
            failures,
            SubjectFailures(command, subjectResult),
            cacheHit: false);
    }

    private void SaveCaches(params DependencyCacheSession?[] sessions)
    {
        foreach (var session in sessions.Where(item => item is not null).Cast<DependencyCacheSession>())
            foreach (var message in session.Save()) _log(message);
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

    private static async Task<BaselineCacheEntry?> ReadBaselineCacheAsync(
        string path,
        string baselineSha,
        string commandHash,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var entry = await JsonSerializer.DeserializeAsync<BaselineCacheEntry>(stream, cancellationToken: ct);
            return entry is not null
                   && entry.ParserVersion == TestFailureParserVersion
                   && string.Equals(entry.BaselineSha, baselineSha, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(entry.CommandHash, commandHash, StringComparison.Ordinal)
                ? entry
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteBaselineCacheAsync(
        string path,
        BaselineCacheEntry entry,
        CancellationToken ct)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: ct);
            await stream.FlushAsync(ct);
        }
        File.Move(temporary, path, overwrite: true);
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

    public ReviewEnvironmentDto EnvironmentEvidence()
    {
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
            _lease.HostId,
            _lease.ExecutorId,
            _lease.InstanceId,
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
                ["containers"] = _lease.ResourceNamespace,
                ["databases"] = _lease.ResourceNamespace,
                ["credentials"] = "review-read-only",
            });
    }

    public Task<bool> CleanupAsync()
    {
        if (!Directory.Exists(AttemptRoot)) return Task.FromResult(true);
        var expectedRoot = Path.GetFullPath(_options.ReviewWorkDir)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(AttemptRoot);
        if (!target.StartsWith(expectedRoot, StringComparison.Ordinal)
            || string.Equals(target.TrimEnd(Path.DirectorySeparatorChar),
                expectedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing review cleanup outside the configured attempt root.");
        // The attempt root holds a clone, so it contains read-only git objects
        // and possibly reparse points - a plain recursive delete cannot remove it.
        ResilientDirectory.Delete(target);
        return Task.FromResult(!Directory.Exists(target));
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
                Field(marker, "summary") ?? $"Remote aspect '{command.Aspect}' returned {status}.");
        }
        return new ReviewVerdictDto(
            command.Aspect,
            result.Success ? "pass" : "block",
            result.Success ? "CommandPassed" : "CommandFailed",
            result.Success
                ? $"Review command '{command.StepId}' passed."
                : $"Review command '{command.StepId}' exited {result.ExitCode}.");
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
        var classification = comparison.NewFailures.Count > 0
            ? "NewTestFailures"
            : comparison.FlakyQuarantinedFailures.Count > 0
                ? ReviewFlakyTestIndex.VerdictClassification
                : "BaselineCompared";
        return new ReviewVerdictDto(
            command.Aspect,
            comparison.NewFailures.Count == 0 ? "pass" : "block",
            classification,
            $"{newFailures}; {preExisting}; {quarantined}. Baseline {comparison.BaselineSha} ({(comparison.CacheHit ? "cache hit" : "cache fill")}).");
    }

    private static IReadOnlyList<string> SubjectFailures(
        ReviewCommandDto command,
        ProcessResult result)
    {
        var failures = ParsedTestFailures(result);
        if (!result.Success && failures.Count == 0)
            return [$"<unparsed failure in {command.StepId}>"];
        return failures;
    }

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

    private static bool ProviderAuthenticationUnavailable(
        ReviewCommandDto command,
        ProcessResult result)
        => ReviewCommandKinds.IsAgent(command.ExecutionKind)
           && ProviderAccessClassifier.Classify(
                   result.ExitCode,
                   result.StdOut,
                   result.StdErr)
               .Kind == ProviderAccessEvidenceKind.AuthenticationFailure;

    internal static string? ReviewCommandInfrastructureClassification(
        ReviewCommandDto command,
        ProcessResult result)
    {
        if (ProviderAuthenticationUnavailable(command, result))
            return "ProviderAuthenticationUnavailable";
        if (MissingToolchain(result) || AgentCommandUnavailable(command, result))
            return "ToolUnavailable";
        return null;
    }

    private static string HashText(string value)
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

public sealed record ReviewExecutionEvidence(
    string Outcome,
    ReviewWorkspaceProofDto Workspace,
    IReadOnlyList<ReviewCommandEvidenceDto> Commands,
    IReadOnlyList<ReviewArtifactEvidenceDto> Artifacts,
    IReadOnlyList<ReviewVerdictDto> Verdicts);

internal sealed record ReviewExecutionCheckpoint(
    IReadOnlyList<string> CompletedStepIds,
    double CompletedCommandSeconds,
    DateTime UpdatedAtUtc);

internal sealed record CommandExecution(
    ProcessResult Process,
    DateTime StartedAt,
    DateTime FinishedAt,
    string? Signal,
    RemoteAgentUsage? AgentUsage = null);

internal sealed record RemoteAgentUsage(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens);

internal sealed record BaselineCacheEntry(
    int ParserVersion,
    string BaselineSha,
    string CommandHash,
    int ExitCode,
    IReadOnlyList<string> Failures,
    DateTime CreatedAt);

internal sealed record BaselineComparison(
    string BaselineSha,
    IReadOnlyList<string> BaselineFailures,
    IReadOnlyList<string> NewFailures,
    IReadOnlyList<string> PreExistingFailures,
    IReadOnlyList<string> FlakyQuarantinedFailures,
    bool CacheHit)
{
    public static BaselineComparison Create(
        string baselineSha,
        IReadOnlyList<string> baselineFailures,
        IReadOnlyList<string> subjectFailures,
        bool cacheHit)
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
            cacheHit);
    }

    public BaselineComparison Reclassify(
        IReadOnlyList<string> subjectFailures,
        ReviewFlakyTestIndex reviewFlakyTests)
    {
        var retried = Create(BaselineSha, BaselineFailures, subjectFailures, CacheHit);
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
