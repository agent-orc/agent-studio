using AgentRunner;
using System.Runtime.InteropServices;

// Standalone agent host. With a task key it performs the RM-5 one-shot run;
// without one (or with --poll) it continuously fills bounded host slots. See
// docs/operations/setup/linux-runner-host.md.

if (args is ["--version"])
{
    Console.WriteLine($"agent-host {RunnerReleaseIdentity.Current}");
    return 0;
}

if (args is ["--detached-worker", var detachedSpec])
    return await DurableAgentProcess.RunWorkerAsync(detachedSpec);

if (args is ["--detached-review-worker", var detachedReviewSpec])
    return await DurableReviewProcess.RunWorkerAsync(detachedReviewSpec);

var (options, taskKey, once, help) = RunnerOptions.Parse(args);

void Log(string message) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [agent-host] {message}");

// Capability advertisements distinguish binary presence from provider login.
// Keep the process boundary in the composition root and let the cached probe
// decide when each bounded, low-contention status command needs to run.
ProviderAuthProbe.Shared.UseLauncher(
    (fileName, arguments, ct) =>
    {
        var invocation = ProviderAuthProbe.LowPriorityInvocation(fileName, arguments);
        return ProcessRunner.RunAsync(invocation.FileName, invocation.Arguments, ct: ct);
    },
    Log);

if (help)
{
    PrintUsage();
    return 0;
}

var daemonMode = taskKey is null || !once;
Log($"agent-host starting: server={options.ServerUrl} role={options.Role} engine={options.ExecEngine} mode={(daemonMode ? "daemon" : "one-shot")} task={taskKey ?? "(assigned projects)"}");

// Migration note (AGT-2370): under the car engine the CAR descriptor owns the
// argv, so a configured RUNNER_CLI_ARGS is loudly ignored instead of silently
// half-applied. RUNNER_CLI_BIN stays meaningful on both engines (binary path).
if (options.ExecEngine == RunnerOptions.ExecEngineCar
    && !string.IsNullOrWhiteSpace(RunnerOptions.Env("RUNNER_CLI_ARGS")))
    Log("RUNNER_CLI_ARGS is set but RUNNER_EXEC_ENGINE=car builds the CLI arguments itself; "
        + "the configured args apply to the legacy engine only and are ignored. "
        + "Set RUNNER_EXEC_ENGINE=legacy to fall back to the raw spawn (removed in AGT-2373).");
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

// Restart guard and drain are pure host-local operator commands: they inspect
// (and for a drain, request) review slot state without touching the Task Server.
// agent-runner-deploy calls the guard before it restarts the review unit.
if (options.RestartGuardOnly || options.DrainOnly)
{
    if (options.Role != "review")
    {
        Log("--drain and --restart-guard apply to the Remote Review Executor only (--role review).");
        return 2;
    }
    return options.RestartGuardOnly
        ? ReviewDrainCommand.RunRestartGuard(options, Log)
        : await ReviewDrainCommand.RunDrainAsync(options, Log, shutdown.Token);
}

using var client = new TaskServerClient(options);

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
    await client.EnsureCompatibleAsync(shutdown.Token);
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

static void PrintUsage()
{
    Console.Error.WriteLine("""
        agent-host - standalone remote task host (RM-5)

        Usage:
          agent-host <TASK-KEY> [options]
          agent-host --task <TASK-KEY> [options]
          agent-host --poll [options]
          agent-host --health-check [--server <url>]
          agent-host --drain --role review [--drain-timeout-seconds <n>]
          agent-host --restart-guard --role review [--force]

        Most configuration comes from environment variables (see the runbook,
        docs/operations/setup/linux-runner-host.md). Command-line flags override:

          --health-check          Probe the Task Server and exit (0 reachable,
                                  4 not). Readiness check for the tunnel service.
          --drain                 Stop claiming reviews, wait for the running
                                  ones, then let the daemon exit (0 drained,
                                  3 timed out). The sanctioned way to restart a
                                  review host without losing gate work.
          --restart-guard         Report whether stopping now would discard
                                  review gate work (0 safe, 3 refused).
          --force                 Let --restart-guard pass on busy slots.
          --drain-timeout-seconds <n>
                                  Drain wait bound     (RUNNER_DRAIN_TIMEOUT_SECONDS, default 3600)
          --version               Print release version and Git SHA, then exit.
          --server <url>          Task Server base URL       (RUNNER_SERVER_URL)
          --runner-id <id>        Stable runner identity     (RUNNER_ID)
          --runner-name <name>    Board-facing runner name   (RUNNER_NAME)
          --role <coding|review>  Separate service role      (RUNNER_ROLE)
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
          --cli <bin>             Agent CLI binary            (RUNNER_CLI_BIN)
          --claude-cli <bin>      Claude card binary           (RUNNER_CLAUDE_CLI_BIN)
          --codex-cli <bin>       Codex card/chat binary       (RUNNER_CODEX_CLI_BIN)
          --cli-args "<args>"     Headless CLI args, legacy engine only
                                                            (RUNNER_CLI_ARGS)
          --exec-engine <car|legacy>
                                  CLI execution engine        (RUNNER_EXEC_ENGINE, default car).
                                  car drives the CLI through CodingAgentRunner
                                  (descriptor argv, stream-json, permission
                                  injection, clean config home); legacy is the
                                  pre-AGT-2370 raw spawn and disappears with
                                  AGT-2373.
          --auth-token-file <p>   Protected credential file  (RUNNER_AUTH_TOKEN_FILE)
          --tls-certificate-sha256 <hex>
                                  Private-CA/rehearsal certificate pin
                                                            (RUNNER_TLS_CERTIFICATE_SHA256)
          --ttl <seconds>         Requested lease TTL         (RUNNER_TTL_SECONDS)
          --max-parallelism <n>   Bootstrap/fallback host slots (RUNNER_MAX_PARALLELISM, default 2)
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
