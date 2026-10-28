using AgentRunner;
using AgentStudio.CliHosting;
using System.Runtime.InteropServices;

if (WindowsProviderBridge.IsBridgeProcess(Environment.ProcessPath, out var bridgeProvider))
    return await WindowsProviderBridge.RunAsync(bridgeProvider, args);

// Standalone agent host. With a task key it performs the RM-5 one-shot run;
// without one (or with --poll) it continuously fills bounded host slots. See
// docs/operations/setup/linux-runner-host.md.

if (args is ["--version"])
{
    Console.WriteLine($"agent-host {RunnerReleaseIdentity.Current}");
    return 0;
}

if (args is ["host-agent", ..])
{
    using var hostStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; hostStop.Cancel(); };
    using var hostSigterm = OperatingSystem.IsWindows() ? null
        : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            hostStop.Cancel();
        });
    return await HostAgentSupervisor.ExecuteAsync(args[1..],
        message => Console.Error.WriteLine($"[host-agent] {message}"), hostStop.Token);
}

if (args is ["--enroll", var installationUrl, var enrollmentCode])
    return await RunnerEnrollmentCommand.RunAsync(
        installationUrl, enrollmentCode, Console.Out, Console.Error, CancellationToken.None);

if (args is ["deploy-poll", ..])
    return await DeployHostCommand.RunAsync(args[1..], CancellationToken.None);

if (args is ["deploy-loop", ..])
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
    while (!stop.IsCancellationRequested)
    {
        try { await DeployHostCommand.RunAsync(args[1..], stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception ex) { Console.Error.WriteLine($"Deploy poll failed: {ex.GetType().Name}"); }
        try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    }
    return 0;
}

// AGT-3005: every process this host starts, daemon or detached worker, inherits
// the build-server fence, so no dotnet invocation it spawns can leave a reusable
// MSBuild node or MSBuild server behind after its run.
foreach (var (key, value) in WorkerBuildServerHygiene.Variables)
    Environment.SetEnvironmentVariable(key, value);

// AGT-2868 belt and braces: the worker environment already disables MSBuild node
// reuse and the MSBuild server, so there should be nothing left to shut down.
// Asking anyway costs one bounded command per attempt. Only the real detached
// entry point asks for it, so an in-process test that drives the same worker
// loop never shuts down the build servers of the machine running it.
if (args is ["--detached-worker", var detachedSpec])
    return await DurableAgentProcess.RunWorkerAsync(detachedSpec, shutdownBuildServers: true);

if (args is ["--detached-review-worker", var detachedReviewSpec])
    return await DurableReviewProcess.RunWorkerAsync(detachedReviewSpec, shutdownBuildServers: true);

// AGT-W63 I03: offline tooling for the owned runner-host record.
if (args is ["host-record", ..])
    return RunnerHostRecordCommand.Run(args[1..], Console.Out, Console.Error);

// DP-S3: Linux work container executor operations for the host agent.
if (args is ["work-container", ..])
{
    using var workContainerStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; workContainerStop.Cancel(); };
    using var workContainerSigterm = OperatingSystem.IsWindows() ? null
        : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; workContainerStop.Cancel(); });
    try { return await AgentRunner.WorkContainers.WorkContainerCommand.RunAsync(args[1..], Console.Out, Console.Error, workContainerStop.Token); }
    catch (OperationCanceledException) when (workContainerStop.IsCancellationRequested) { return 130; }
}

// AGT-3231: render, apply and verify the zone network policy on this host.
if (args is ["zone-policy", ..])
    return await ZonePolicyCommand.RunAsync(args[1..], Console.Out, Console.Error, CancellationToken.None);

// AGT-3074: scheduled release-category coverage run, started by agent-coverage-run.timer.
if (args is ["coverage-run", ..])
{
    using var coverageStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; coverageStop.Cancel(); };
    using var coverageSigterm = OperatingSystem.IsWindows()
        ? null
        : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; coverageStop.Cancel(); });
    try
    {
        return await CoverageRunCommand.RunAsync(args[1..], Console.Out, Console.Error, coverageStop.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("coverage-run cancelled");
        return 130;
    }
}

if (args is ["post-merge-run", ..])
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var postMergeSigterm = OperatingSystem.IsWindows() ? null
        : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
    try { return await PostMergeRunCommand.RunAsync(args[1..], Console.Out, Console.Error, stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
}

if (args.Length == 3 && args[0] == "--rebind-provider-auth"
    && args[1] is ("codex" or "claude") && args[2] == "--drained")
{
    try
    {
        var provider = args[1];
        if (System.Diagnostics.Process.GetProcessesByName(provider).Any(process =>
        {
            using (process) return !process.HasExited;
        }))
        {
            Console.Error.WriteLine("Provider workers are still active; complete the drain before rebinding.");
            return 2;
        }
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = TaskCleanContextStore.ResolveRoot(userHome);
        var count = TaskCleanContextStore.RebindProviderCredential(provider, userHome, root,
            () => !System.Diagnostics.Process.GetProcessesByName(provider).Any(process =>
            {
                using (process) return !process.HasExited;
            }));
        Console.WriteLine($"provider-auth-rebound={provider}:{count}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Provider credential rebinding failed: {ex.GetType().Name}");
        return 2;
    }
}

if (args.Length is 3 or 4 && args[0] == "--renew-repository-access"
    && (args.Length == 3 || args[3] == "--drained"))
{
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        return await RepositoryRenewalCommand.RunAsync(args[1], args[2],
            args.Length == 4, Console.In, Console.Out, deadline.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"repository renewal failed: {ex.GetType().Name}");
        return 2;
    }
}

if (args is ["--repository-admin"])
{
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return await RepositoryRenewalCommand.RunAdminAsync(Console.In, Console.Out, deadline.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"repository administration failed: {ex.GetType().Name}");
        return 2;
    }
}

var (options, taskKey, once, help) = RunnerOptions.Parse(args);

void Log(string message) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [agent-host] {message}");

// Capability advertisements distinguish binary presence from provider login.
// Keep the process boundary in the composition root and let the cached probe
// decide when each bounded, low-contention status command needs to run.
//
// The probe must never share config or session context with an active coding
// run: same CLAUDE_CONFIG_DIR/CODEX_HOME plus the same working directory means
// the same `<home>/projects/<cwd>` session store, and a status command that
// lands there can read back a live run's transcript instead of an auth answer
// (AGT-2823). Each provider gets its own stable, linked-credential clean-context
// home - identical isolation to a task's clean home, just keyed by a fixed
// identity instead of a task id - so the OAuth refresh still writes through
// but no project transcript or history ever lands in it. A credential
// generation change re-acquires the home so a later login is linked (AGT-3216).
var authProbeContexts = new ProviderAuthProbeContexts(
    provider => TaskCleanContextStore.Acquire(provider, ProviderAuthProbe.CleanContextIdentity),
    provider => ProviderCredentialMonitor.Inspect(provider).CredentialGeneration,
    Log);
using var client = new TaskServerClient(options);

TaskCleanContextLease? AuthProbeContext(string provider) => authProbeContexts.Get(provider);

ProviderAuthProbe.Shared.UseLauncher(
    (fileName, arguments, ct) =>
    {
        var invocation = ProviderAuthProbe.LowPriorityInvocation(fileName, arguments);
        var provider = RunnerCapabilityProbe.Provider(fileName);
        var context = AuthProbeContext(provider);
        var environment = context?.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
            ?? new Dictionary<string, string?>();
        // A device sign-in probes its unverified Claude token in this child only (AGT-3225).
        if (ProviderAuthEnvironment.TryGetForCli(provider, out var authName, out var authValue))
            environment[authName] = authValue;
        return ProcessRunner.RunAsync(
            invocation.FileName,
            invocation.Arguments,
            workingDirectory: context?.HomePath,
            environment: environment,
            ct: ct);
    },
    Log);

// A real request uses the same configured CLI selection and CAR execution
// route as coding work, read-only and in the probe's protected clean context
// with no repository prompt.
using var providerStatusHttp = new HttpClient();
ProviderAuthProbe.Shared.UseRealRequest(
    async (fileName, statusArguments, ct) =>
    {
        var provider = RunnerCapabilityProbe.Provider(fileName);
        var context = AuthProbeContext(provider);
        if (context is null)
            return new ProcessResult(1, "", "probe isolation unavailable");
        var selection = CliSelection.Resolve(options, new RunSpecDto(CliType: provider));
        if (!string.Equals(selection.FileName, fileName, StringComparison.Ordinal))
            return new ProcessResult(1, "", "configured CLI mismatch");
        var root = Path.Combine(context.HomePath, ".provider-real-probe");
        return await ProviderRealRequestHostFlight.RunAsync(
            Path.Combine(context.HomePath, ".provider-real-probe.lock"),
            async deadline =>
            {
                // An empty scratch repository: codex exec refuses a working
                // directory outside Git, and no application checkout is exposed.
                var workspace = Path.Combine(root, "workspace");
                var worker = Path.Combine(root, "worker");
                Directory.CreateDirectory(workspace);
                Directory.CreateDirectory(worker);
                if (!Directory.Exists(Path.Combine(workspace, ".git")))
                {
                    var init = await ProcessRunner.RunAsync("git", ["init", "-q"],
                        workingDirectory: workspace, ct: deadline);
                    if (init.ExitCode != 0)
                        return (new ProcessResult(1, "", "probe workspace unavailable"), false, false);
                }
                // The probe deadline stops the CLI, so the host-local lock is
                // released when it exits instead of when CAR's own timeout fires.
                return await CarWorkerExecution.RunAsync(
                    new DetachedJobSpec(
                        fileName, [], workspace,
                        "Reply with OK. Do not use tools or access files.",
                        worker,
                        (int)ProviderAuthProbe.DefaultTimeout.TotalSeconds - 5,
                        selection.CliType, selection.Model, selection.ThinkingLevel,
                        CodingAgentRunner.Model.CliPermissionModes.ReadOnly,
                        CodingAgentRunner.Model.CliContextModes.Clean,
                        RunId: ProviderAuthProbe.CleanContextIdentity,
                        CleanContextKey: ProviderAuthProbe.CleanContextIdentity),
                    worker,
                    (_, _) => { },
                    stopToken: deadline);
            },
            ct);
    },
    ProviderStatusIncidentAdapter.Official(providerStatusHttp),
    client.ReadProviderComparisonAsync,
    options.Hostname);
ProviderAuthProbe.Shared.UseCanaryPermit(client.ReserveProviderCanaryAsync);

// An operator-supplied generation labels an environment login. Otherwise the
// daemon session is the fallback identity; completed device logins invalidate
// its cached model verdicts, and session-bound ready verdicts are rechecked.
var environmentCredentialGeneration = $"daemon-session:{Guid.NewGuid():N}";
string? ModelProbeCredentialGeneration(string provider)
{
    var credential = ProviderCredentialMonitor.Inspect(provider);
    return credential.CredentialGeneration
           // A CLI can authenticate through a store the metadata reader does
           // not recognize. Keep probing that login instead of advertising
           // every named model as unknown forever. Session evidence is
           // periodically rechecked in RunnerModelAcceptanceProbe.
           ?? environmentCredentialGeneration;
}
RunnerModelAcceptanceProbe.Shared.Configure(
    async (provider, model, ct) =>
    {
        var context = AuthProbeContext(provider);
        if (context is null) return new ProcessResult(1, "", "probe isolation unavailable");
        var selection = CliSelection.Resolve(options, new RunSpecDto(CliType: provider, Model: model));
        var root = Path.Combine(context.HomePath, ".model-acceptance-probe");
        return await ProviderRealRequestHostFlight.RunAsync(
            Path.Combine(context.HomePath, ".provider-real-probe.lock"),
            async deadline =>
            {
                var workspace = Path.Combine(root, "workspace");
                var worker = Path.Combine(root, model, "worker");
                Directory.CreateDirectory(workspace);
                Directory.CreateDirectory(worker);
                if (!Directory.Exists(Path.Combine(workspace, ".git")))
                {
                    var init = await ProcessRunner.RunAsync("git", ["init", "-q"],
                        workingDirectory: workspace, ct: deadline);
                    if (init.ExitCode != 0)
                        return (new ProcessResult(1, "", "probe workspace unavailable"), false, false);
                }
                return await CarWorkerExecution.RunAsync(
                    new DetachedJobSpec(
                        selection.FileName, [], workspace, "Reply OK.", worker, 35,
                        selection.CliType, model, selection.ThinkingLevel,
                        CodingAgentRunner.Model.CliPermissionModes.ReadOnly,
                        CodingAgentRunner.Model.CliContextModes.Clean,
                        RunId: ProviderAuthProbe.CleanContextIdentity,
                        CleanContextKey: ProviderAuthProbe.CleanContextIdentity),
                    worker, (_, _) => { }, stopToken: deadline);
            }, ct);
    },
    ModelProbeCredentialGeneration);

if (help)
{
    PrintUsage();
    return 0;
}

var daemonMode = taskKey is null || !once;
Log($"agent-host starting: server={options.ServerUrl} role={options.Role} engine=car mode={(daemonMode ? "daemon" : "one-shot")} task={taskKey ?? "(assigned projects)"}");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log(daemonMode
        ? "shutdown requested (Ctrl+C); draining daemon without stopping detached jobs..."
        : "shutdown requested (Ctrl+C); cancelling one-shot run...");
    shutdown.Cancel();
};
using var sigterm = !OperatingSystem.IsWindows()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        Log(daemonMode
            ? "planned shutdown requested (SIGTERM); stopping claims and flushing durable slot state..."
            : "shutdown requested (SIGTERM); cancelling one-shot run...");
        if (daemonMode && options.Role == "review")
        {
            var busy = ReviewDrainGuard.Inspect(options.StateDir);
            if (busy.Busy > 0)
                Log($"{busy.Busy} review slot(s) are busy at shutdown "
                    + $"({string.Join(", ", busy.BusyAttemptIds)}). {ReviewDrainGuard.DrainHint}");
        }
        shutdown.Cancel();
    })
    : null;

// Restart guard and drain are host-local operator commands: each closes claim
// admission through a request-id marker without touching the Task Server.
// agent-runner-deploy holds the guard marker through old-MainPID exit.
if (options.RestartGuardOnly || options.DrainOnly)
{
    if (options.Role != "review")
    {
        Log("--drain and --restart-guard apply to the Remote Review Executor only (--role review).");
        return 2;
    }
    return options.RestartGuardOnly
        ? await ReviewDrainCommand.RunRestartGuardAsync(options, Log, shutdown.Token)
        : await ReviewDrainCommand.RunDrainAsync(options, Log, shutdown.Token);
}


// Readiness probe (--health-check): confirm the Task Server is reachable over the
// tunnel and exit, without touching a task. This is the check the reverse-tunnel
// service runs so a down connection is reported once and cleanly (exit 4) instead
// of cascading through a launch attempt. No task key is required.
if (options.HealthCheckOnly)
{
    var health = await client.ProbeHealthAsync(shutdown.Token);
    if (health is null)
    {
        Log($"health-check ok: task server reachable at {options.ServerUrl}");
        return 0;
    }
    Log($"health-check failed: cannot reach the task server at {options.ServerUrl} ({health}). " +
        "The reverse tunnel / autossh service is likely down.");
    return 4;
}


try
{
    // A host supervisor can restart after a crash while its prior child is
    // still alive. The role-local lock keeps the replacement from registering
    // or reattaching until that exact executor process has exited.
    using var hostAdapterLock = options.HostAdapter && daemonMode && options.Role is "coding" or "review" or "post"
        ? AcquireHostAdapterLock(options)
        : null;
    // IP-1: a run target talks to Studio's deploy routes only, never to Task
    // Server work queues, and opens no listener.
    if (options.Role == "run-target")
    {
        if (!daemonMode)
            throw new ArgumentException("Run target runs as a polling service and does not accept coding task keys.");
        var runTarget = AgentRunner.RunTargets.RunTargetOptions.From(options, Environment.GetEnvironmentVariable);
        using var studio = new HttpClient(TaskServerClient.CreateServiceHandler(options))
        {
            BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.ServerRequestTimeoutSeconds),
        };
        studio.DefaultRequestHeaders.TryAddWithoutValidation("X-Principal-Consumer-Id", options.RunnerId);
        if (!string.IsNullOrWhiteSpace(options.AuthToken) && string.IsNullOrWhiteSpace(options.AuthTokenFile))
            studio.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.AuthToken);
        await AgentRunner.RunTargets.RunTargetDaemon.Create(runTarget, studio, Log).RunAsync(shutdown.Token);
        Log("run-target daemon stopped");
        return 0;
    }
    await client.EnsureCompatibleAsync(shutdown.Token);
    // The host polls for device-login commands independently of task slots.
    // Only coding and review service identities own provider CLI homes.
    var deviceLoginPoll = daemonMode && options.Role is ("coding" or "review")
        ? new DeviceLoginPoller(client, Log, options.StateDir, options.CodexCliBin, options.ClaudeCliBin)
            .RunAsync(shutdown.Token)
        : Task.CompletedTask;
    _ = deviceLoginPoll.ContinueWith(task => Log($"device-login poll stopped: {task.Exception?.GetBaseException().GetType().Name}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    if (options.Role == "gate")
    {
        if (!daemonMode)
            throw new ArgumentException("Gate Executor runs as a polling service and does not accept coding task keys.");
        await new RemoteGateDaemon(options, client, Log).RunAsync(shutdown.Token);
        Log("gate daemon stopped");
        return 0;
    }
    if (options.Role == "post")
    {
        if (!daemonMode)
            throw new ArgumentException("Post-work Executor runs as a polling service and does not accept coding task keys.");
        var postOptions = PostWorkHostOptions.From(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());
        await client.RegisterAsync(options.RunnerName, "post-work-executor", shutdown.Token);
        IPostWorkStepExecutor postExecutor = postOptions.Executor == AgentStudio.TaskServer.Contracts.PostWorkExecutorKinds.Container
            ? new ContainerPostWorkStepExecutor(postOptions, Log)
            : new ProcessPostWorkStepExecutor(Log);
        await new PostWorkDaemon(new TaskServerPostWorkAuthority(client, options.Hostname), postExecutor,
                options.RunnerId, () => client.RunnerInstanceId, options.StateDir, postOptions.LeaseSeconds,
                TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), Log)
            .RunAsync(shutdown.Token);
        Log("post-work daemon stopped");
        return 0;
    }
    if (options.Role == "integration")
    {
        if (!daemonMode)
            throw new ArgumentException("Integration Executor runs as a polling service and does not accept coding task keys.");
        await new RemoteIntegrationDaemon(options, client, Log).RunAsync(shutdown.Token);
        Log("integration daemon stopped");
        return 0;
    }
    if (options.Role == "review")
    {
        if (!daemonMode)
            throw new ArgumentException("Remote Review Executor runs as a polling service and does not accept coding task keys.");
        await new RemoteReviewDaemon(options, client, Log).RunAsync(shutdown.Token);
        Log("review daemon stopped");
        return 0;
    }
    if (daemonMode)
    {
        await new RemoteRunnerDaemon(options, client, Log).RunAsync(shutdown.Token);
        Log("daemon stopped");
        return 0;
    }

    var exitCode = await new RemoteTaskRunner(options, client, Log).RunAsync(taskKey!, shutdown.Token);
    Log($"done, exit code {exitCode}");
    return exitCode;
}

catch (OperationCanceledException)
{
    Log("cancelled");
    return 130; // 128 + SIGINT
}
catch (TaskServerException ex)
{
    Log($"task server error: {ex.Message}");
    return 4;
}
catch (HttpRequestException ex)
{
    Log($"could not reach the task server at {options.ServerUrl}: {ex.Message}");
    return 4;
}
catch (Exception ex)
{
    Log($"unhandled error: {ex}");
    return 1;
}

static FileStream AcquireHostAdapterLock(RunnerOptions options)
{
    Directory.CreateDirectory(options.StateDir);
    return new FileStream(Path.Combine(options.StateDir, $"host-{options.Role}-adapter.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        agent-host - standalone remote task host (RM-5)

        Usage:
          agent-host <TASK-KEY> [options]
          agent-host --task <TASK-KEY> [options]
          agent-host --poll [options]
          agent-host --enroll <installation-url> <one-time-code>
          agent-host --health-check [--server <url>]
          agent-host --drain --role review [--drain-timeout-seconds <n>]
          agent-host --restart-guard --role review [--hold-admission] [--force]

        Most configuration comes from environment variables (see the runbook,
        docs/operations/setup/linux-runner-host.md). Command-line flags override:

          --health-check          Probe the Task Server and exit (0 reachable,
                                  4 not). Readiness check for the tunnel service.
          --drain                 Stop claiming reviews, wait for the running
                                  ones, then stop the daemon (0 drained,
                                  3 refused/timed out). Start it later through
                                  the guarded deploy helper.
          --restart-guard         Report whether stopping now would discard
                                  review gate work (0 safe, 3 refused).
          --hold-admission        Keep an accepted restart guard closed until
                                  the replacement helper stops the old daemon.
          --force                 Let --restart-guard pass on busy slots.
          --drain-timeout-seconds <n>
                                  Drain wait bound     (RUNNER_DRAIN_TIMEOUT_SECONDS, default 3600)
          --review-release-drain 1
                                  Finish adopted reviews that run a superseded
                                  agent-host release before claiming again; the
                                  adopted workers are never killed
                                             (RUNNER_REVIEW_RELEASE_DRAIN)
          --version               Print release version and Git SHA, then exit.
          --server <url>          Task Server base URL       (RUNNER_SERVER_URL)
          --runner-id <id>        Stable runner identity     (RUNNER_ID)
          --runner-name <name>    Board-facing runner name   (RUNNER_NAME)
          --role <coding|review|gate|integration|run-target>
                                  Separate service role (RUNNER_ROLE)
          --client-id <id>        Attribution label only     (RUNNER_CLIENT_ID)
          --git-remote <url>      Startup probe/one-shot URL  (RUNNER_GIT_REMOTE)
          --git-push-remote <url> Probe/one-shot push URL     (RUNNER_GIT_PUSH_REMOTE)
          --branch <name>         Branch to check out         (RUNNER_BRANCH)
          --base-branch <name>    Fallback branch             (RUNNER_BASE_BRANCH)
          --workdir <path>        Checkout + results dir      (RUNNER_WORKDIR)
          --review-workdir <path> Disposable review root      (RUNNER_REVIEW_WORKDIR)
          --state-dir <path>      Durable slot/process state  (RUNNER_STATE_DIR)
          RUNNER_CLAIM_MAX_LOAD_PER_CORE                      Load/core threshold (default 1.5)
          RUNNER_LOAD_GATE_SUSTAINED_SECONDS                  High-load window (default 120)
          --cli-type <type>       Default provider            (RUNNER_CLI_TYPE, claude|codex)
          --claude-cli <bin>      Claude card binary           (RUNNER_CLAUDE_CLI_BIN)
          --codex-cli <bin>       Codex card/chat binary       (RUNNER_CODEX_CLI_BIN)
          --auth-token-file <p>   Protected credential file  (RUNNER_AUTH_TOKEN_FILE)
          --tls-certificate-sha256 <hex>
                                  Private-CA/rehearsal certificate pin
                                                            (RUNNER_TLS_CERTIFICATE_SHA256)
          --ttl <seconds>         Requested lease TTL         (RUNNER_TTL_SECONDS)
          --max-parallelism <n>   Bootstrap/fallback host slots (RUNNER_MAX_PARALLELISM, default 2)
          RUNNER_HOST_CODING_SLOTS / RUNNER_HOST_REVIEW_SLOTS
                                  Host slot split both roles read for the
                                  per-worker resource envelope (default: this
                                  service's own RUNNER_MAX_PARALLELISM each).
                                  12 cores across 2+2 slots is 3 cores per slot.
          RUNNER_WORKER_CPU_BURST How far above its fair share one worker may
                                  burst on an otherwise idle host (default 2.0)
          --worker-envelope 0     Start detached workers without a per-worker
                                  cgroup                  (RUNNER_WORKER_ENVELOPE)
          --poll-seconds <n>      Empty-queue poll delay       (RUNNER_POLL_SECONDS, default 5)
          --server-request-timeout-seconds <n>
                                  Per-request Task Server cap  (RUNNER_SERVER_REQUEST_TIMEOUT_SECONDS, default 60)
          --idle-watchdog-minutes <n>
                                  Exit after a slot-free poll stall
                                                               (RUNNER_IDLE_WATCHDOG_MINUTES, default 5)
          --poll                  Run continuously (also the default without a task key)
          -h, --help              Show this help

        RUNNER_* remains the bootstrap-compatible environment prefix and takes
        precedence. Every setting also accepts its matching AGENT_HOST_* alias.
        """);
}
