using System.Diagnostics;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Delegation;
using CodingAgentRunner.Execution;
using CarRunInfo = CodingAgentRunner.Model.CliRunInfo;
using LibOutcome = CodingAgentRunner.Model.RunOutcome;

namespace AgentStudio.Cli;

/// <summary>
/// CAR-backed half of <see cref="GenericCliExecutionService"/>. CAR owns CLI
/// descriptors, argv, hardening, process lifecycle and typed events. This host
/// bridge keeps Studio's durable output mirror, UI rendering, usage/session
/// capture, active-job reaper and terminal sentinel classification.
/// </summary>
public partial class GenericCliExecutionService
{
    /// <summary>
    /// Test seam used by parity fixtures to replace only the final process
    /// launch. Production leaves it null.
    /// </summary>
    internal Func<CliOptions, CliOptions>? CarOptionsCustomizer { get; set; }

    /// <summary>
    /// Test seam for a failure after the process exists but before CAR can
    /// finish registering the run. Production leaves it null.
    /// </summary>
    internal Action<Process>? CarAfterSpawnForTest { get; set; }

    private bool SupportsCarExecution
        => string.Equals(CliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
           || string.Equals(CliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase);

    private async Task<(CliExecution? Execution, string? Error)> StartCarAsync(
        string jobId,
        string jobKey,
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model,
        string? thinkingLevel,
        string? jobFolderPath,
        string? permissionMode,
        string? contextMode,
        CancellationToken ct)
    {
        ProcInfo? previousExited = null;
        if (_processes.TryGetValue(jobKey, out var existing))
        {
            if (!existing.Process.HasExited)
                return (null, $"{CliType} CLI process already running for job '{jobId}'");
            // Keep the retained attempt in place until the replacement has
            // been adopted. If startup fails, its identity-guarded retention
            // timer still owns the task-stable clean-context home.
            previousExited = existing;
        }

        // Studio still owns model qualification against its live catalog. CAR
        // then performs the common trim/thinking normalization when it builds
        // the descriptor launch.
        var invocationModel = NormalizeModelForInvocation(model);
        var invocationThinkingLevel = ModelMetadataRegistry.ResolveThinkingLevel(
            CliType, invocationModel, thinkingLevel);
        var usesStudioThinkingCompatibility = RequiresStudioThinkingCompatibility(
            CliType, invocationModel, invocationThinkingLevel);

        // Studio's shared host library owns the longer task boundary so Codex
        // rollouts remain resumable across attempts and backend restarts. Hand
        // that home to CAR as shared + explicit env so CAR does not create a
        // second process-scoped home.
        CleanContextPreparation? cleanContext = null;
        var cleanContextReused = false;
        if (CliContextModes.Normalize(contextMode) == CliContextModes.Clean && SupportsCleanContext)
        {
            (cleanContext, cleanContextReused) = AcquireCleanContext(jobKey, workingDirectory);
            if (cleanContext != null)
            {
                _logger.LogInformation(
                    "{Cli} CAR run for job {JobId}: {Mode} task-scoped clean home at {Home}",
                    CliType,
                    jobId,
                    cleanContextReused ? "reusing" : "seeded",
                    cleanContext.TempHome);
            }
        }

        var extraEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(jobFolderPath))
            extraEnvironment["JOB_RESULTS_DIR"] = Path.Combine(jobFolderPath, "results");
        if (cleanContext != null)
            foreach (var pair in cleanContext.EnvOverrides)
                extraEnvironment[pair.Key] = pair.Value;

        // Codex has no system-prompt-file flag. Preserve the Studio-owned
        // completion/sandbox instructions in the stdin prompt while CAR owns
        // the descriptor argv and prompt transport.
        var effectivePrompt = string.Equals(CliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrEmpty(prompt)
            ? BuiltInCliBehaviors.BuildSystemPromptPrefix(OperatingSystem.IsWindows()) + prompt
            : prompt;

        var carLogPaths = new StudioCarLogPathProvider(this);
        var rulesPath = string.Equals(CliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
            ? BuiltInCliBehaviors.ResolveAgentRulesPath(this)
            : null;

        var baseOptions = new CliOptions
        {
            ClaudePath = string.Equals(CliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
                ? GetCliPath()
                : null,
            CodexPath = string.Equals(CliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
                ? GetCliPath()
                : null,
            // CAR-A: keep large prompts out of argv and process listings. CAR
            // writes the complete one-shot prompt, flushes it, and closes stdin
            // before the model turn starts.
            ClaudePromptTransport = ClaudePromptTransport.Stdin,
            AllowAgentGitMutation = false,
            Delegation = new DelegationOptions { Enabled = false },
        };
        if (CarOptionsCustomizer != null) baseOptions = CarOptionsCustomizer(baseOptions);

        // CAR 0.7.0 has no descriptor/extra-argv overlay. Its public spawner
        // seam is therefore the narrow interim integration point for Studio's
        // Claude system-prompt file. The wrapper also exposes the exact Process
        // instance for host active-job tracking. PROJ-011/public-cli-launch-overlay
        // and public-hardened-spawner-composition record the missing library APIs.
        TaskProcessReaper? processReaper = null;
        var processSpawner = new DecoratingCliProcessSpawner(
            startInfo =>
            {
                AddClaudeRulesArgument(startInfo, rulesPath);
                if (usesStudioThinkingCompatibility)
                    AddClaudeThinkingArgument(startInfo, invocationThinkingLevel);
            },
            baseOptions.Spawner,
            process =>
            {
                if (OperatingSystem.IsWindows())
                {
                    try { processReaper = TaskProcessReaper.CreateForProcess(process, _logger); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not attach the Windows task process reaper to CAR PID {Pid}", process.Id);
                    }
                }
                CarAfterSpawnForTest?.Invoke(process);
            });
        var options = baseOptions with { Spawner = processSpawner };

        var runner = new CliRunner(options, _logger, carLogPaths);
        var driver = runner.Get(CliType);
        var bridge = new CarCallbackBridge(this, driver, jobKey);
        bridge.Subscribe();

        var request = new CliRunRequest
        {
            RunId = jobKey,
            Prompt = effectivePrompt,
            WorkingDirectory = workingDirectory,
            Model = invocationModel,
            ThinkingLevel = invocationThinkingLevel,
            ResumeSessionId = resumeSession && IsCompatibleSessionName(sessionName)
                ? sessionName
                : null,
            PermissionMode = permissionMode,
            // The task-scoped clean home is already injected above. Asking CAR
            // for another clean home would make resume point at the wrong state.
            ContextMode = CliContextModes.Shared,
            ExtraEnvironment = extraEnvironment,
        };

        CarRunInfo? carRun;
        string? startError;
        try
        {
            (carRun, startError) = await driver.StartAsync(request, ct);
        }
        catch (Exception ex)
        {
            CleanupFailedCarStart(
                jobKey,
                driver,
                bridge,
                processSpawner.SpawnedProcess,
                processReaper,
                cleanContext,
                cleanContextReused,
                carLogPaths);
            _logger.LogError(ex, "CAR failed to start {Cli} for job {JobId}", CliType, jobId);
            return (null, $"Failed to start {CliType} CLI through CAR: {ex.Message}");
        }

        if (carRun == null)
        {
            CleanupFailedCarStart(
                jobKey,
                driver,
                bridge,
                processSpawner.SpawnedProcess,
                processReaper,
                cleanContext,
                cleanContextReused,
                carLogPaths);
            return (null, startError ?? $"CAR failed to start {CliType} CLI");
        }

        Process? process = null;
        ProcInfo? adoptedInfo = null;
        try
        {
            process = processSpawner.SpawnedProcess;
            if (process == null)
            {
                process = Process.GetProcessById(carRun.ProcessId);
            }

            var execution = new CliExecution
            {
                JobId = jobId,
                TaskKey = jobKey,
                ProcessId = carRun.ProcessId,
                StartedAt = carRun.StartedAt,
                Status = "running",
                Model = carRun.Model,
                ThinkingLevel = carRun.ThinkingLevel
                                ?? (usesStudioThinkingCompatibility ? invocationThinkingLevel : null),
            };

            var logDir = GetOutputLogDir(jobKey);
            adoptedInfo = new ProcInfo(process, execution, workingDirectory)
            {
                OutputLogPath = logDir,
                OutputLog = new RunLogStore(logDir),
                SessionName = sessionName,
                LastStreamedAt = execution.StartedAt,
                PermissionMode = permissionMode,
                ContextMode = CliContextModes.Normalize(contextMode),
                CleanContext = cleanContext,
                ProcessReaper = processReaper,
                CarDriver = driver,
            };
            var info = adoptedInfo;
            try { info.OutputLog.Reset(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to reset CLI output log dir {Path}", logDir); }
            _processes[jobKey] = info;

            try
            {
                UpsertActiveJob(new ActiveJob
                {
                    TaskKey = jobKey,
                    JobId = jobId,
                    ProcessId = process.Id,
                    ProcessName = SafeProcessName(process),
                    ProcessStartTimeUtc = SafeProcessStartTime(process),
                    StartedAt = execution.StartedAt,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record active-job entry for {JobId} ({Cli})", jobId, CliType);
            }

            try { OnStarted?.Invoke(jobKey, execution); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnStarted subscriber threw for {JobId}", jobId); }

            bridge.PublishRunStarted(info);

            try { StartSessionLiveness(jobKey, info, resumeSession, sessionName); }
            catch (Exception ex) { _logger.LogDebug(ex, "StartSessionLiveness hook threw for {JobId}", jobId); }

            var startedLine = new CliOutputLine
            {
                Timestamp = DateTime.UtcNow,
                Stream = "system",
                Text = BuildStartedLineText(
                    CliType,
                    process.Id,
                    invocationModel,
                    invocationThinkingLevel,
                    sessionName,
                    resumeSession),
            };
            info.OutputBuffer.Add(startedLine);
            if (!info.OutputLog.Append(startedLine)) NotePersistFailure(jobKey, info);
            try { OnOutput?.Invoke(jobKey, startedLine); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnOutput subscriber threw for {JobId}", jobId); }

            _logger.LogInformation(
                "Started {Cli} CLI through CAR for job {JobId} (PID {Pid}) in {Cwd}",
                CliType,
                jobId,
                process.Id,
                workingDirectory);

            bridge.Attach(info, carLogPaths);
            return (info.Execution, null);
        }
        catch (Exception ex)
        {
            if (adoptedInfo != null)
                _processes.TryRemove(new KeyValuePair<string, ProcInfo>(jobKey, adoptedInfo));
            if (previousExited != null)
                _processes.TryAdd(jobKey, previousExited);
            try { RemoveActiveJob(jobKey); }
            catch (Exception removeEx) { _logger.LogDebug(removeEx, "CAR cleanup active-job removal failed for {JobId}", jobId); }
            try { adoptedInfo?.OutputLog.Dispose(); }
            catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "CAR cleanup output-log dispose failed for {JobId}", jobId); }
            CleanupFailedCarStart(
                jobKey,
                driver,
                bridge,
                process,
                processReaper,
                cleanContext,
                cleanContextReused,
                carLogPaths);
            _logger.LogError(ex, "CAR host failed to adopt {Cli} process for job {JobId}", CliType, jobId);
            return (null, $"CAR started {CliType} but Studio could not adopt the process: {ex.Message}");
        }
    }

    private void CleanupFailedCarStart(
        string jobKey,
        ICliDriver driver,
        CarCallbackBridge bridge,
        Process? spawnedProcess,
        TaskProcessReaper? processReaper,
        CleanContextPreparation? cleanContext,
        bool cleanContextReused,
        StudioCarLogPathProvider carLogPaths)
    {
        bridge.Detach();

        // StartAsync can throw after the spawner has returned but before CAR
        // records the run in its driver table. Stop is still attempted first,
        // but the captured process is the authority on this partial-start path.
        try { driver.Stop(jobKey, RunStopReason.Cancelled); }
        catch (Exception ex) { _logger.LogDebug(ex, "CAR cleanup stop failed for {JobId}", jobKey); }

        if (OperatingSystem.IsWindows())
        {
            try { processReaper?.Terminate(); }
            catch (Exception ex) { _logger.LogDebug(ex, "CAR cleanup process reaper failed for {JobId}", jobKey); }
            try { processReaper?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "CAR cleanup process-reaper dispose failed for {JobId}", jobKey); }
        }

        if (spawnedProcess != null)
        {
            try
            {
                if (!spawnedProcess.HasExited)
                    spawnedProcess.Kill(entireProcessTree: true);
                spawnedProcess.WaitForExit(10_000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Direct CAR process cleanup failed for {JobId}", jobKey);
            }
        }

        try { driver.Forget(jobKey); }
        catch (Exception ex) { _logger.LogDebug(ex, "CAR cleanup forget failed for {JobId}", jobKey); }

        CleanupFreshCleanContext(jobKey, cleanContext, cleanContextReused);
        carLogPaths.DeleteRun(jobKey);
    }

    private void CleanupFreshCleanContext(
        string jobKey,
        CleanContextPreparation? cleanContext,
        bool cleanContextReused)
    {
        if (cleanContext == null || cleanContextReused) return;
        _cleanContextsByJob.TryRemove(
            new KeyValuePair<string, CleanContextPreparation>(jobKey, cleanContext));
        try { cleanContext.Delete(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed-start clean-context cleanup failed for {JobId}", jobKey);
        }
    }

    private static void AddClaudeRulesArgument(
        ProcessStartInfo startInfo,
        string? rulesPath)
    {
        if (string.IsNullOrWhiteSpace(rulesPath)) return;
        if (startInfo.ArgumentList.Contains("--append-system-prompt-file")) return;
        startInfo.ArgumentList.Add("--append-system-prompt-file");
        startInfo.ArgumentList.Add(rulesPath);
    }

    private static bool RequiresStudioThinkingCompatibility(
        string cliType,
        string? model,
        string? thinkingLevel)
        => string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(thinkingLevel)
           && ModelMetadataRegistry.Find(model)?.ThinkingLevels is { Length: > 0 }
           && CliThinkingLevels.For(cliType, model).Count == 0;

    private static void AddClaudeThinkingArgument(
        ProcessStartInfo startInfo,
        string? thinkingLevel)
    {
        if (string.IsNullOrWhiteSpace(thinkingLevel)) return;
        if (startInfo.ArgumentList.Contains("--effort")) return;
        startInfo.ArgumentList.Add("--effort");
        startInfo.ArgumentList.Add(thinkingLevel);
    }

    private void HandleCarOutput(string jobKey, ProcInfo info, CliOutputLine rawLine)
    {
        if (!info.OutputLog.Append(rawLine)) NotePersistFailure(jobKey, info);
        else if (info.PersistFailureCount > 0)
        {
            _logger.LogInformation(
                "CLI output persistence recovered for {JobId} after {Count} dropped line(s)",
                jobKey,
                info.PersistFailureCount);
            info.PersistFailureCount = 0;
        }

        info.LastStreamedAt = DateTime.UtcNow;
        CheckEnvironmentBlocker(jobKey, info, rawLine);
        try { CaptureRawLine(jobKey, rawLine); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CaptureRawLine threw for CAR job {JobId}", jobKey);
        }

        IEnumerable<CliOutputLine> transformed;
        try { transformed = TransformReadLine(rawLine); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TransformReadLine threw for CAR job {JobId}; falling back to raw", jobKey);
            transformed = [rawLine];
        }

        foreach (var outputLine in transformed)
        {
            info.OutputBuffer.Add(outputLine);
            while (info.OutputBuffer.Count > 5000) info.OutputBuffer.RemoveAt(0);
            try { OnOutputLine(info, outputLine); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnOutputLine hook threw for CAR job {JobId}", jobKey); }
            try { OnOutput?.Invoke(jobKey, outputLine); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnOutput subscriber threw for CAR job {JobId}", jobKey); }
        }
    }

    private CliRunEvent NormalizeCarEvent(CliRunEvent evt, CliOutputLine? sourceLine = null)
    {
        if (!string.Equals(CliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            || evt is not CliRunEvent.ToolCompleted completed
            || BuiltInCliBehaviors.TryExtractCommandExecution(sourceLine?.Text)?.ExitCode is not int exitCode
            || exitCode == 0
            || completed.IsError)
        {
            return evt;
        }

        return new CliRunEvent.ToolCompleted(
            completed.ToolName,
            IsError: true,
            completed.FirstLine)
        {
            RunId = evt.RunId,
        };
    }

    private void FinishCarRun(
        string jobKey,
        ProcInfo info,
        CarRunInfo carRun,
        ICliDriver driver,
        CarCallbackBridge bridge,
        StudioCarLogPathProvider carLogPaths)
    {
        try
        {
            try { info.SessionLiveness?.Dispose(); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "BackendCarExecution: liveness dispose"); }
            info.SessionLiveness = null;

            try { RemoveActiveJob(jobKey); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear active-job entry for {TaskKey}", jobKey); }

            var duration = carRun.DurationSeconds
                           ?? (DateTime.UtcNow - info.Execution.StartedAt).TotalSeconds;
            var exitCode = carRun.ExitCode;
            if (info.StopReason == RunStopReason.None
                && string.Equals(carRun.Status, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                // CAR can observe cancellation directly through the request
                // token. Mirror that intent into the host classifier so it
                // cannot turn the resulting kill exit code into a failure.
                info.StopReason = RunStopReason.Cancelled;
            }
            var status = RunStatusClassifier.Classify(exitCode, info.StopReason);
            var terminalOutcome = TerminalRunOutcomeClassifier.Classify(
                status,
                info.OutputBuffer.ToList(),
                duration,
                exitCode: exitCode);
            status = TerminalRunOutcomeClassifier.ExecutionStatusFor(terminalOutcome, status);

            var finalExecution = info.Execution with
            {
                Status = status,
                ExitCode = exitCode,
                DurationSeconds = duration,
                RunOutcome = terminalOutcome.Kind,
            };
            info.Execution = finalExecution;

            var exitLine = new CliOutputLine
            {
                Timestamp = DateTime.UtcNow,
                Stream = "system",
                Text = $"[taskboard] {CliType} CLI exited: status={status}, exitCode={exitCode?.ToString() ?? "?"}, duration={duration:F1}s",
            };
            info.OutputBuffer.Add(exitLine);
            info.OutputLog.Append(exitLine);
            try { OnOutput?.Invoke(jobKey, exitLine); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnOutput subscriber threw for CAR job {JobId}", jobKey); }

            var endOutcome = string.Equals(status, RunStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                ? LibOutcome.Completed
                : string.Equals(status, RunStatuses.Stopped, StringComparison.OrdinalIgnoreCase)
                    ? LibOutcome.Stopped
                    : LibOutcome.Failed;
            var endReason = endOutcome switch
            {
                LibOutcome.Stopped => info.StopReason.ToString(),
                LibOutcome.Failed => info.LastTurnFailureReason ?? terminalOutcome.Reason,
                _ => null,
            };
            RaiseRunEvent(jobKey, new CliRunEvent.RunEnded(endOutcome, endReason, exitCode, duration)
            {
                RunId = jobKey,
            });

            if (OperatingSystem.IsWindows())
            {
                try { info.ProcessReaper?.Terminate(); }
                catch (Exception __ex) { SilentCatch.Note(__ex, "BackendCarExecution: process-reaper terminate"); }
            }

            try { OnFinished?.Invoke(jobKey, finalExecution); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnFinished subscriber threw for CAR job {JobId}", jobKey); }

            ReleaseOutputResources(jobKey);
            _logger.LogInformation(
                "{Cli} CAR run finished for job {JobId}: exit={ExitCode}, duration={Duration:F1}s",
                CliType,
                jobKey,
                exitCode,
                duration);
            ScheduleEviction(jobKey, info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CAR host finalization crashed for {Cli} job {JobId}", CliType, jobKey);
        }
        finally
        {
            bridge.Detach();
            try { driver.Forget(jobKey); }
            catch (Exception ex) { _logger.LogDebug(ex, "CAR Forget failed for {JobId}", jobKey); }
            carLogPaths.DeleteRun(jobKey);
        }
    }

    /// <summary>
    /// CAR emits typed protocol events before the matching raw stdout callback,
    /// while stderr diagnostics can follow their raw callback. Correlate both
    /// orders while keeping Studio's synchronous usage-ledger contract intact.
    /// </summary>
    private sealed class CarCallbackBridge(
        GenericCliExecutionService owner,
        ICliDriver driver,
        string jobKey)
    {
        private readonly object _gate = new();
        private readonly Queue<CallbackBatch> _ready = new();
        private readonly List<CliRunEvent> _pendingEvents = [];
        private readonly Queue<string> _recentUnmatchedStdErr = new();
        private ProcInfo? _info;
        private StudioCarLogPathProvider? _logPaths;
        private CliRunEvent.RunStarted? _runStarted;
        private bool _runStartedPublishRequested;
        private bool _runStartedPublished;
        private bool _draining;
        private bool _detached;

        public void Subscribe()
        {
            driver.OnOutput += OnCarOutput;
            driver.OnRunEvent += OnCarRunEvent;
            driver.OnFinished += OnCarFinished;
        }

        public void Detach()
        {
            lock (_gate)
            {
                if (_detached) return;
                _detached = true;
            }
            driver.OnOutput -= OnCarOutput;
            driver.OnRunEvent -= OnCarRunEvent;
            driver.OnFinished -= OnCarFinished;
        }

        public void Attach(ProcInfo info, StudioCarLogPathProvider logPaths)
        {
            lock (_gate)
            {
                _info = info;
                _logPaths = logPaths;
            }
            Drain();
        }

        public void PublishRunStarted(ProcInfo info)
        {
            CliRunEvent.RunStarted? started;
            lock (_gate)
            {
                _runStartedPublishRequested = true;
                started = _runStartedPublished ? null : _runStarted;
                if (started != null)
                {
                    _runStarted = null;
                    _runStartedPublished = true;
                }
            }
            if (started != null)
                owner.RaiseRunEvent(jobKey, owner.NormalizeCarEvent(started));
        }

        private void OnCarRunEvent(string id, CliRunEvent evt)
        {
            if (!string.Equals(id, jobKey, StringComparison.Ordinal)) return;
            var shouldDrain = false;
            CliRunEvent.RunStarted? startedToPublish = null;
            lock (_gate)
            {
                if (_detached) return;
                if (evt is CliRunEvent.RunStarted started)
                {
                    if (_runStartedPublishRequested && !_runStartedPublished)
                    {
                        _runStartedPublished = true;
                        startedToPublish = started;
                    }
                    else if (!_runStartedPublished)
                    {
                        _runStarted = started;
                    }
                }
                // Studio owns the terminal classifier. Forwarding CAR's
                // exit-code-only RunEnded here can disagree with the sentinel,
                // EmptyFastExit, or deliberate-stop outcome applied later.
                else if (evt is not CliRunEvent.RunEnded)
                {
                    var rawDetail = RawDetailOf(evt);
                    if (rawDetail != null && RemoveRecentStdErr(rawDetail))
                    {
                        // CAR 0.7.0 reports stderr's raw callback before the
                        // adapter callback. Emit the correlated typed event as
                        // the immediately following batch.
                        _ready.Enqueue(new CallbackBatch(null, [evt], Finished: null));
                        shouldDrain = true;
                    }
                    else
                    {
                        _pendingEvents.Add(evt);
                    }
                }
            }
            if (startedToPublish != null)
                owner.RaiseRunEvent(jobKey, owner.NormalizeCarEvent(startedToPublish));
            if (shouldDrain) Drain();
        }

        private void OnCarOutput(string id, CliOutputLine line)
        {
            if (!string.Equals(id, jobKey, StringComparison.Ordinal)) return;

            var isCarStartedMarker = string.Equals(line.Stream, "system", StringComparison.OrdinalIgnoreCase)
                                     && line.Text.StartsWith("[runner] Started ", StringComparison.Ordinal);
            lock (_gate)
            {
                if (_detached) return;
                // Diagnostics and Unknown events carry the source line. Use
                // that correlation for stderr in either callback order instead
                // of deferring them until unrelated stdout or process exit.
                var events = TakePendingEventsForLine(line);
                if (string.Equals(line.Stream, "stderr", StringComparison.OrdinalIgnoreCase)
                    && events.Count == 0)
                {
                    _recentUnmatchedStdErr.Enqueue(line.Text);
                    while (_recentUnmatchedStdErr.Count > 64)
                        _recentUnmatchedStdErr.Dequeue();
                }
                _ready.Enqueue(new CallbackBatch(
                    isCarStartedMarker ? null : line,
                    events,
                    Finished: null));
            }
            Drain();
        }

        private List<CliRunEvent> TakePendingEventsForLine(CliOutputLine line)
        {
            var isStdErr = string.Equals(
                line.Stream,
                "stderr",
                StringComparison.OrdinalIgnoreCase);
            var result = new List<CliRunEvent>();
            for (var i = 0; i < _pendingEvents.Count;)
            {
                var evt = _pendingEvents[i];
                var carriesRawDetail = evt is CliRunEvent.Diagnostic
                                       or CliRunEvent.Unknown;
                var rawDetailMatches = evt switch
                {
                    CliRunEvent.Diagnostic diagnostic => string.Equals(
                        diagnostic.RawDetail,
                        line.Text,
                        StringComparison.Ordinal),
                    CliRunEvent.Unknown unknown => string.Equals(
                        unknown.RawDetail,
                        line.Text,
                        StringComparison.Ordinal),
                    _ => false,
                };
                var belongsToLine = isStdErr
                    ? carriesRawDetail && rawDetailMatches
                    : !carriesRawDetail || rawDetailMatches;
                if (!belongsToLine)
                {
                    i++;
                    continue;
                }

                result.Add(evt);
                _pendingEvents.RemoveAt(i);
            }
            return result;
        }

        private bool RemoveRecentStdErr(string rawDetail)
        {
            var found = false;
            var retained = new Queue<string>();
            while (_recentUnmatchedStdErr.Count > 0)
            {
                var candidate = _recentUnmatchedStdErr.Dequeue();
                if (!found && string.Equals(candidate, rawDetail, StringComparison.Ordinal))
                {
                    found = true;
                    continue;
                }
                retained.Enqueue(candidate);
            }
            while (retained.Count > 0)
                _recentUnmatchedStdErr.Enqueue(retained.Dequeue());
            return found;
        }

        private static string? RawDetailOf(CliRunEvent evt)
            => evt switch
            {
                CliRunEvent.Diagnostic diagnostic => diagnostic.RawDetail,
                CliRunEvent.Unknown unknown => unknown.RawDetail,
                _ => null,
            };

        private void OnCarFinished(string id, CarRunInfo run)
        {
            if (!string.Equals(id, jobKey, StringComparison.Ordinal)) return;
            lock (_gate)
            {
                if (_detached) return;
                _ready.Enqueue(new CallbackBatch(null, TakePendingEvents(), run));
            }
            Drain();
        }

        private List<CliRunEvent> TakePendingEvents()
        {
            var result = _pendingEvents.ToList();
            _pendingEvents.Clear();
            return result;
        }

        private void Drain()
        {
            lock (_gate)
            {
                if (_draining || _info == null || _logPaths == null) return;
                _draining = true;
            }

            try
            {
                while (true)
                {
                    CallbackBatch batch;
                    ProcInfo info;
                    StudioCarLogPathProvider logPaths;
                    lock (_gate)
                    {
                        if (_ready.Count == 0) return;
                        batch = _ready.Dequeue();
                        info = _info!;
                        logPaths = _logPaths!;
                    }

                    if (batch.RawLine != null)
                        owner.HandleCarOutput(jobKey, info, batch.RawLine);
                    foreach (var evt in batch.Events)
                        owner.RaiseRunEvent(jobKey, owner.NormalizeCarEvent(evt, batch.RawLine));
                    if (batch.Finished != null)
                    {
                        owner.FinishCarRun(jobKey, info, batch.Finished, driver, this, logPaths);
                        return;
                    }
                }
            }
            finally
            {
                var restart = false;
                lock (_gate)
                {
                    _draining = false;
                    // Close the enqueue-vs-empty lost-wakeup window: a callback
                    // can append after the loop observes an empty queue but
                    // before this finally block clears _draining.
                    restart = !_detached
                              && _info != null
                              && _logPaths != null
                              && _ready.Count > 0;
                }
                if (restart) Drain();
            }
        }

        private sealed record CallbackBatch(
            CliOutputLine? RawLine,
            IReadOnlyList<CliRunEvent> Events,
            CarRunInfo? Finished);
    }

    private sealed class StudioCarLogPathProvider(GenericCliExecutionService owner) : IRunLogPathProvider
    {
        private string BaseDirectory
        {
            get
            {
                var taskRepo = owner._configuration["TaskRepository"];
                var root = !string.IsNullOrWhiteSpace(taskRepo)
                    ? Path.Combine(taskRepo, ".runtime", "car-cli-output")
                    : Path.Combine(AppContext.BaseDirectory, "runtime", "car-cli-output");
                Directory.CreateDirectory(root);
                return root;
            }
        }

        public string GetRunLogDirectory(string runId)
            => Path.Combine(BaseDirectory, SanitizeForFile($"{owner.CliType}-{runId}"));

        public string GetActiveJobsFile()
            => Path.Combine(BaseDirectory, $"active-runs-{SanitizeForFile(owner.CliType)}.json");

        public void DeleteRun(string runId)
        {
            try
            {
                var path = GetRunLogDirectory(runId);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                owner._logger.LogDebug(ex, "Could not delete CAR mirror log for {RunId}", runId);
            }
        }
    }
}
