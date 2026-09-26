namespace AgentRunner;

using System.Globalization;

/// <summary>
/// Resolved runner configuration for a single remote task run. Every value comes
/// from an environment variable (systemd-friendly) with a small set of required
/// command-line overrides for the per-task identifiers. See
/// docs/operations/setup/linux-runner-host.md for the operator-facing table.
/// </summary>
public sealed class RunnerOptions
{
    public const int ProtocolVersion = AgentStudio.TaskServer.Contracts.TaskServerProtocol.Current;

    /// <summary>Central Task Server base URL, e.g. http://127.0.0.1:5030 or the reverse-proxied central URL.</summary>
    public required string ServerUrl { get; init; }

    /// <summary>Stable per-runner identity used as the lease owner (fencing is per task, not per pid).</summary>
    public required string RunnerId { get; init; }

    /// <summary>Human-facing runner name shown on the board (the project the board assigns, e.g. agent-runner-01).</summary>
    public required string RunnerName { get; init; }

    /// <summary>
    /// Optional pre-registered Task Server client identity. When set, startup
    /// verifies this exact identity instead of silently registering another id
    /// from <see cref="RunnerName"/>. This is the systemd-friendly onboarding
    /// path for hosts whose X-Client-Id was created before the runner install.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>Reported hostname; defaults to the machine name.</summary>
    public required string Hostname { get; init; }

    /// <summary>Free-form label describing which backend/topology this runner serves.</summary>
    public required string BackendName { get; init; }

    /// <summary>
    /// Service role. Coding, review, and gate use different registered identities,
    /// daemon loops, workspace roots, credentials, and server-side claims.
    /// </summary>
    public string Role { get; init; } = "coding";

    /// <summary>Runner service credential sent as Authorization on every networked-profile request.</summary>
    public string? AuthToken { get; init; }

    /// <summary>
    /// Explicit opt-in (<c>RUNNER_ALLOW_INSECURE_HTTP=1|true</c>) that allows a
    /// plain <c>http://</c> Task Server URL outside loopback. It exists for
    /// container networks where the Task Server is reachable only under its
    /// service name and never leaves the network (e.g.
    /// <c>http://orchestrator-api:5031</c> in docker compose). Off by default:
    /// without it every non-loopback URL must be HTTPS.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    /// Optional SHA-256 pin for a private or rehearsal Task Server certificate.
    /// Public deployments should normally rely on the operating-system trust
    /// store; the pin keeps CI and private-CA topologies explicit and fail closed.
    /// </summary>
    public string? TlsServerCertificateSha256 { get; init; }

    /// <summary>Fallback git remote for one-shot runs and the daemon startup capability probe.</summary>
    public string? GitRemote { get; init; }

    /// <summary>
    /// Optional write-only URL for the daemon startup capability probe and legacy
    /// one-shot runs. Project-scoped clones always use their registry URL for both
    /// fetch and push and never inherit this fallback.
    /// </summary>
    public string? GitPushRemote { get; init; }

    /// <summary>Directory the runner checks the repo out into on the runner host.</summary>
    public required string WorkDir { get; init; }

    /// <summary>Disposable workspace root used only by the Remote Review Executor.</summary>
    public string ReviewWorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "agent-review-work");

    /// <summary>
    /// Comma-separated environment variable names admitted into review child
    /// processes. The review service account must provision them as read-only.
    /// </summary>
    public IReadOnlyList<string> ReviewCredentialEnvironment { get; init; } = [];

    /// <summary>
    /// Floor for how long a review verify command's process tree may run without
    /// clearing its CPU floor before the hang watchdog kills it and the attempt
    /// is classified <c>ReviewInfra/NoCpuProgress</c>
    /// (<c>RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS</c>). A deadlocked build blocks
    /// on a socket instead of exiting, so it would otherwise hold its review slot
    /// for the whole command budget. <c>0</c> disables the watchdog. Linux only:
    /// the tree's CPU time is read from <c>/proc</c>.
    /// <para>
    /// AGT-2851: this is a floor, not the effective window. A verify command's
    /// actual no-CPU-progress window is the larger of this value and 50% of that
    /// command's own budget (<c>RemoteReviewWorkspace.NoCpuProgressWindow</c>), so
    /// a legitimately quiet integration suite with a large budget is not killed
    /// mid-run for sitting near 0% CPU during a real test wait.
    /// </para>
    /// </summary>
    public int ReviewNoCpuProgressSeconds { get; init; } = 900;

    /// <summary>Durable daemon slot records and detached-worker logs.</summary>
    public string StateDir { get; init; } = Path.Combine(Path.GetTempPath(), "agent-runner-state");

    /// <summary>
    /// Additional host capabilities required by every claim for this service
    /// identity, for example toolchain:dotnet, toolchain:node, or
    /// toolchain:playwright. The Task Server combines these with the role,
    /// provider, source, disk, and connectivity requirements.
    /// </summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>Branch to check out for the run. When empty, the runner stays on <see cref="BaseBranch"/>.</summary>
    public string? Branch { get; init; }

    /// <summary>Fallback branch when the task branch is absent or unspecified.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>
    /// Which execution engine drives the coding CLI inside the detached worker
    /// (<c>RUNNER_EXEC_ENGINE</c>). <c>car</c> (default) drives it through the
    /// CodingAgentRunner library: descriptor-built argv, structured events,
    /// permission-mode injection, clean config home. <c>legacy</c> keeps the
    /// pre-AGT-2370 raw spawn. The switch is a rollout instrument for the T1
    /// canary cohorts and is deleted in AGT-2373 together with the legacy path.
    /// </summary>
    public string ExecEngine { get; init; } = ExecEngineCar;

    public const string ExecEngineCar = "car";
    public const string ExecEngineLegacy = "legacy";

    /// <summary>Agent CLI binary to spawn (claude, codex, ...).</summary>
    public required string CliBin { get; init; }

    /// <summary>Codex binary used by the GPT-only project chat work path.</summary>
    public string CodexCliBin { get; init; } = "codex";

    /// <summary>Claude binary used when a Claude-pinned card runs on a host whose primary CLI is Codex.</summary>
    public string ClaudeCliBin { get; init; } = "claude";

    /// <summary>Extra CLI arguments inserted before the prompt is streamed on stdin (space-split, shell-unaware).</summary>
    public required string CliArgs { get; init; }

    /// <summary>
    /// Optional provider-specific arguments for resuming a captured session.
    /// The value must contain <c>{sessionId}</c>. When absent, the provider is
    /// treated as not supporting same-session recovery on this host.
    /// </summary>
    public string? CliResumeArgs { get; init; }

    /// <summary>
    /// Lease TTL requested on acquire/renew; the server clamps to its own
    /// bounds. The 15-minute default leaves ten complete wall-clock minutes
    /// after the normal heartbeat safety margin without granting unbounded
    /// offline authority.
    /// </summary>
    public int TtlSeconds { get; init; }

    /// <summary>Heartbeat cadence; kept well under the TTL so a slow network still renews in time.</summary>
    public int HeartbeatSeconds { get; init; }

    /// <summary>
    /// TTL requested by the final renewal a review daemon sends before handing a
    /// detached worker to its replacement. It only has to outlive the restart
    /// window (systemd <c>TimeoutStopSec</c> plus start), and the Task Server
    /// clamps it to its own ceiling, so five minutes is deliberately generous.
    /// </summary>
    public int HandoffLeaseTtlSeconds { get; init; } = 300;

    /// <summary>
    /// How long <c>--drain</c> waits for busy review slots to finish before it
    /// gives up and reports the remaining attempts to the operator.
    /// </summary>
    public int DrainTimeoutSeconds { get; init; } = 3600;

    /// <summary>
    /// AGT-2863: <c>RUNNER_REVIEW_RELEASE_DRAIN</c> / <c>--review-release-drain</c>.
    /// A replacement review daemon adopts the detached workers of the previous
    /// release and, by default, claims new work beside them. With this opted in
    /// the daemon still finishes every adopted attempt but takes no new claim
    /// until the ones running a superseded build are done, so a worker-side
    /// hotfix reaches every attempt this daemon grades. Adopted workers are
    /// never killed for a release mismatch.
    /// </summary>
    public bool ReviewReleaseDrain { get; init; }

    /// <summary>Hard cap on a single CLI run before the runner gives up and reports a blocked completion.</summary>
    public int RunTimeoutSeconds { get; init; }

    /// <summary>
    /// Bootstrap/fallback host slot ceiling. A versioned Task Server persists
    /// the first reported value and returns the centrally managed live ceiling.
    /// </summary>
    public int HostMaxParallelism { get; init; }

    /// <summary>
    /// Coding worker slots this host offers, across roles
    /// (<c>RUNNER_HOST_CODING_SLOTS</c>). Together with
    /// <see cref="HostReviewSlots"/> it is the denominator of the per-worker
    /// resource envelope (AGT-2866): a 12-core host with 2 coding and 2 review
    /// slots offers 3 cores per slot. Both roles read both numbers, so a coding
    /// daemon and a review daemon on the same machine agree on what one slot is
    /// worth. Defaults to this service's own <see cref="HostMaxParallelism"/>,
    /// which matches the symmetric two-plus-two install. Onboarding therefore
    /// declares only the <em>other</em> role's count in each role environment
    /// file: this role's own count is <c>RUNNER_MAX_PARALLELISM</c>, so a
    /// sanctioned parallelism change moves the envelope with it instead of
    /// leaving a stale number behind. A single-role host sets the other role
    /// to 0.
    /// </summary>
    public int HostCodingSlots { get; init; }

    /// <summary>Review worker slots this host offers (<c>RUNNER_HOST_REVIEW_SLOTS</c>).</summary>
    public int HostReviewSlots { get; init; }

    /// <summary>
    /// How far above its fair share one worker may burst while the host is
    /// otherwise idle (<c>RUNNER_WORKER_CPU_BURST</c>, default 2.0). The envelope
    /// is meant to cap peaks, not to slow down a normal build on an empty host.
    /// </summary>
    public double WorkerCpuBurst { get; init; } = WorkerResourceEnvelope.DefaultCpuBurst;

    /// <summary>
    /// Escape hatch for a host whose cgroup delegation is broken
    /// (<c>RUNNER_WORKER_ENVELOPE=0</c>). Off means workers launch exactly as
    /// they did before AGT-2866, uncapped.
    /// </summary>
    public bool WorkerEnvelopeEnabled { get; init; } = true;

    /// <summary>Delay between empty daemon pickup polls.</summary>
    public int PollSeconds { get; init; }

    /// <summary>Hard deadline for one Task Server HTTP exchange.</summary>
    public int ServerRequestTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum time a slot-free daemon may go without starting a claim poll.
    /// The daemon exits after this deadline so the service manager can replace it.
    /// </summary>
    public int IdleWatchdogMinutes { get; init; } = 5;

    /// <summary>New claims stop when the one-minute load average divided by CPU cores exceeds this value.</summary>
    public double ClaimMaxLoadPerCore { get; init; } = 1.5;

    /// <summary>
    /// How long a review command may produce no output at all before the silence
    /// watchdog kills it (AGT-2820). Four concurrent reviews once sat on the same
    /// <c>dotnet test</c> at 0.0% CPU with nothing written for ten minutes and a
    /// host load average of 0.47; each would have held its slot for the full
    /// two-hour command budget, so one stuck batch cost eight review-hours. Set
    /// to 0 to disable the watchdog and fall back to the command budget alone.
    /// <para>
    /// AGT-2851: <c>ReviewPlanResourcePolicy</c> adds <c>--logger
    /// "console;verbosity=normal"</c> to every review <c>dotnet test</c>
    /// invocation, so a healthy run keeps printing per-test lines through a long
    /// <c>ParallelizeTestCollections=false</c> suite and this watchdog only fires
    /// on genuine silence, not on quiet default-logger output.
    /// </para>
    /// </summary>
    public int CommandSilenceWatchdogSeconds { get; init; } = 600;

    /// <summary>Continuous high-load duration required before claim admission closes.</summary>
    public int LoadGateSustainedSeconds { get; init; } = 120;

    /// <summary>
    /// When set (<c>--health-check</c>), the runner only probes the Task Server's
    /// liveness and exits: 0 when the server is reachable, 4 when it is not. No task
    /// key is required. This is the readiness probe the reverse-tunnel service uses
    /// to confirm the connection before assigning work.
    /// </summary>
    public bool HealthCheckOnly { get; init; }

    /// <summary>
    /// When set (<c>--drain</c>), the process asks the running review daemon to
    /// stop claiming and waits for its slots to finish instead of starting a
    /// daemon of its own. A successful drain leaves the review role stopped;
    /// guarded replacement is a separate explicit action.
    /// </summary>
    public bool DrainOnly { get; init; }

    /// <summary>
    /// When set (<c>--restart-guard</c>), the process reports whether a plain
    /// restart would discard review gate work and exits. <c>agent-runner-deploy</c>
    /// calls it before it restarts the review unit.
    /// </summary>
    public bool RestartGuardOnly { get; init; }

    /// <summary>Overrides a refusal from <see cref="RestartGuardOnly"/> (<c>--force</c>).</summary>
    public bool Force { get; init; }

    /// <summary>
    /// Keeps the restart guard's acknowledged admission barrier in place until
    /// the replacement helper has stopped the old daemon. Operator diagnostics
    /// omit this flag and withdraw their temporary request before returning.
    /// </summary>
    public bool HoldAdmission { get; init; }

    public static string Env(string name, string fallback = "")
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            return value;

        const string bootstrapPrefix = "RUNNER_";
        if (name.StartsWith(bootstrapPrefix, StringComparison.Ordinal)
            && Environment.GetEnvironmentVariable($"AGENT_HOST_{name[bootstrapPrefix.Length..]}") is { Length: > 0 } alias)
            return alias;

        return fallback;
    }

    public static int EnvInt(string name, int fallback)
        => int.TryParse(Env(name), out var v) && v > 0 ? v : fallback;

    /// <summary>
    /// Like <see cref="EnvInt"/> but admits <c>0</c>, so an operator can switch a
    /// guard off explicitly instead of silently getting the default back.
    /// </summary>
    public static int EnvIntAllowingZero(string name, int fallback)
        => int.TryParse(Env(name), out var v) && v >= 0 ? v : fallback;

    /// <summary>Boolean opt-in flag as operators write it in a unit file or compose env.</summary>
    private static bool OptIn(string value)
        => value.Trim() is { Length: > 0 } flag
           && (flag == "1" || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mirror of <see cref="OptIn"/> for a guard that is on by default: only an
    /// explicit <c>0</c> / <c>false</c> switches it off, so a typo leaves the
    /// protection in place.
    /// </summary>
    private static bool OptOut(string value)
        => value.Trim() is not { Length: > 0 } flag
           || !(flag == "0" || string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase));

    public static double EnvDouble(string name, double fallback)
        => double.TryParse(
               Environment.GetEnvironmentVariable(name),
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out var value)
           && value > 0
            ? value
            : fallback;

    /// <summary>
    /// Build options from environment defaults, then apply <c>--key value</c> and
    /// <c>--flag</c> command-line overrides. The single positional argument (if any)
    /// is the task key. Returns the parsed task key alongside the options.
    /// </summary>
    public static (RunnerOptions Options, string? TaskKey, bool Once, bool Help) Parse(string[] args)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? positional = null;
        var once = true;
        var help = false;
        var healthCheck = false;
        var drain = false;
        var restartGuard = false;
        var force = false;
        var holdAdmission = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") { help = true; continue; }
            if (a == "--once") { once = true; continue; }
            if (a == "--poll") { once = false; continue; }
            if (a == "--health-check") { healthCheck = true; continue; }
            // Boolean modes must be listed here: the generic branch below treats
            // an unknown --flag as "--key value" and would swallow the next
            // argument.
            if (a == "--drain") { drain = true; continue; }
            if (a == "--restart-guard") { restartGuard = true; continue; }
            if (a == "--force") { force = true; continue; }
            if (a == "--hold-admission") { holdAdmission = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                var value = i + 1 < args.Length ? args[++i] : "";
                overrides[key] = value;
                continue;
            }
            positional ??= a;
        }

        string Val(string cliKey, string envName, string fallback = "")
            => overrides.TryGetValue(cliKey, out var v) && v.Length > 0 ? v : Env(envName, fallback);

        if (overrides.ContainsKey("auth-token"))
            throw new ArgumentException("Runner credentials are not accepted on the command line. Use RUNNER_AUTH_TOKEN_FILE or RUNNER_AUTH_TOKEN.");
        var authTokenFile = Val("auth-token-file", "RUNNER_AUTH_TOKEN_FILE").Trim();
        var directAuthToken = Env("RUNNER_AUTH_TOKEN").Trim();
        if (authTokenFile.Length > 0 && directAuthToken.Length > 0)
            throw new ArgumentException("Configure only one of RUNNER_AUTH_TOKEN_FILE or RUNNER_AUTH_TOKEN.");
        var authToken = authTokenFile.Length > 0 ? ReadAuthTokenFile(authTokenFile) : directAuthToken;

        // The slot budget is derived before the object initializer because both
        // role slot counts default to this service's own ceiling.
        var hostMaxParallelism =
            overrides.TryGetValue("max-parallelism", out var maxParallelism)
            && int.TryParse(maxParallelism, out var maxParallelismValue)
            && maxParallelismValue > 0
                ? maxParallelismValue
                : EnvInt("RUNNER_MAX_PARALLELISM", 2);

        var options = new RunnerOptions
        {
            ServerUrl = Val("server", "RUNNER_SERVER_URL", "http://127.0.0.1:5030").TrimEnd('/'),
            RunnerId = Val("runner-id", "RUNNER_ID", $"agent-runner-{Environment.MachineName}".ToLowerInvariant()),
            RunnerName = Val("runner-name", "RUNNER_NAME", "agent-runner-01"),
            ClientId = Val("client-id", "RUNNER_CLIENT_ID").Trim() is { Length: > 0 } clientId ? clientId : null,
            Hostname = Val("hostname", "RUNNER_HOSTNAME", Environment.MachineName),
            BackendName = Val("backend-name", "RUNNER_BACKEND_NAME", "remote-runner"),
            Role = Val("role", "RUNNER_ROLE", "coding").Trim().ToLowerInvariant(),
            AuthToken = authToken.Length > 0 ? authToken : null,
            TlsServerCertificateSha256 = Val(
                "tls-certificate-sha256",
                "RUNNER_TLS_CERTIFICATE_SHA256").Trim() is { Length: > 0 } certificateSha
                    ? certificateSha
                    : null,
            GitRemote = Val("git-remote", "RUNNER_GIT_REMOTE").Trim() is { Length: > 0 } gitRemote ? gitRemote : null,
            GitPushRemote = Val("git-push-remote", "RUNNER_GIT_PUSH_REMOTE").Trim() is { Length: > 0 } gitPushRemote ? gitPushRemote : null,
            WorkDir = Val("workdir", "RUNNER_WORKDIR", Path.Combine(Path.GetTempPath(), "agent-runner-work")),
            ReviewWorkDir = Val(
                "review-workdir",
                "RUNNER_REVIEW_WORKDIR",
                Path.Combine(Path.GetTempPath(), "agent-review-work")),
            ReviewCredentialEnvironment = Val(
                    "review-credential-env",
                "RUNNER_REVIEW_CREDENTIAL_ENV")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ReviewNoCpuProgressSeconds = overrides.TryGetValue("review-no-cpu-progress-seconds", out var noProgress)
                                         && int.TryParse(noProgress, out var noProgressValue)
                                         && noProgressValue >= 0
                ? noProgressValue
                : EnvIntAllowingZero("RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS", 900),
            StateDir = Val("state-dir", "RUNNER_STATE_DIR",
                Path.Combine(Val("workdir", "RUNNER_WORKDIR", Path.Combine(Path.GetTempPath(), "agent-runner-work")), ".runner-state")),
            RequiredCapabilities = Val(
                    "required-capabilities",
                    "RUNNER_REQUIRED_CAPABILITIES")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Branch = Val("branch", "RUNNER_BRANCH") is { Length: > 0 } b ? b : null,
            BaseBranch = Val("base-branch", "RUNNER_BASE_BRANCH", "main"),
            ExecEngine = Val("exec-engine", "RUNNER_EXEC_ENGINE", ExecEngineCar).Trim().ToLowerInvariant(),
            CliBin = Val("cli", "RUNNER_CLI_BIN", "claude"),
            CodexCliBin = Val("codex-cli", "RUNNER_CODEX_CLI_BIN", "codex"),
            ClaudeCliBin = Val("claude-cli", "RUNNER_CLAUDE_CLI_BIN", "claude"),
            CliArgs = Val("cli-args", "RUNNER_CLI_ARGS", "-p"),
            CliResumeArgs = Val("cli-resume-args", "RUNNER_CLI_RESUME_ARGS").Trim() is { Length: > 0 } resumeArgs
                ? resumeArgs
                : null,
            TtlSeconds = overrides.TryGetValue("ttl", out var ttl) && int.TryParse(ttl, out var ttlV) ? ttlV : EnvInt("RUNNER_TTL_SECONDS", 900),
            HeartbeatSeconds = EnvInt("RUNNER_HEARTBEAT_SECONDS", 30),
            HandoffLeaseTtlSeconds = EnvInt("RUNNER_HANDOFF_LEASE_TTL_SECONDS", 300),
            DrainTimeoutSeconds = overrides.TryGetValue("drain-timeout-seconds", out var drainTimeout)
                                          && int.TryParse(drainTimeout, out var drainTimeoutValue)
                                          && drainTimeoutValue > 0
                ? drainTimeoutValue
                : EnvInt("RUNNER_DRAIN_TIMEOUT_SECONDS", 3600),
            RunTimeoutSeconds = EnvInt("RUNNER_RUN_TIMEOUT_SECONDS", 3600),
            HostMaxParallelism = hostMaxParallelism,
            HostCodingSlots = EnvIntAllowingZero("RUNNER_HOST_CODING_SLOTS", hostMaxParallelism),
            HostReviewSlots = EnvIntAllowingZero("RUNNER_HOST_REVIEW_SLOTS", hostMaxParallelism),
            WorkerCpuBurst = EnvDouble(
                "RUNNER_WORKER_CPU_BURST",
                WorkerResourceEnvelope.DefaultCpuBurst),
            WorkerEnvelopeEnabled = OptOut(Val("worker-envelope", "RUNNER_WORKER_ENVELOPE")),
            PollSeconds = overrides.TryGetValue("poll-seconds", out var poll) && int.TryParse(poll, out var pollV) && pollV > 0
                ? pollV : EnvInt("RUNNER_POLL_SECONDS", 5),
            ServerRequestTimeoutSeconds = overrides.TryGetValue("server-request-timeout-seconds", out var requestTimeout)
                                                  && int.TryParse(requestTimeout, out var requestTimeoutValue)
                                                  && requestTimeoutValue > 0
                ? requestTimeoutValue
                : EnvInt("RUNNER_SERVER_REQUEST_TIMEOUT_SECONDS", 60),
            IdleWatchdogMinutes = overrides.TryGetValue("idle-watchdog-minutes", out var idleWatchdog)
                                      && int.TryParse(idleWatchdog, out var idleWatchdogValue)
                                      && idleWatchdogValue > 0
                ? idleWatchdogValue
                : EnvInt("RUNNER_IDLE_WATCHDOG_MINUTES", 5),
            ClaimMaxLoadPerCore = overrides.TryGetValue("claim-max-load-per-core", out var maxLoad)
                                      && double.TryParse(
                                          maxLoad,
                                          NumberStyles.Float,
                                          CultureInfo.InvariantCulture,
                                          out var maxLoadValue)
                                      && maxLoadValue > 0
                ? maxLoadValue
                : EnvDouble("RUNNER_CLAIM_MAX_LOAD_PER_CORE", 1.5),
            CommandSilenceWatchdogSeconds = EnvInt("RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS", 600),
            LoadGateSustainedSeconds = EnvInt("RUNNER_LOAD_GATE_SUSTAINED_SECONDS", 120),
            AllowInsecureHttp = OptIn(Val("allow-insecure-http", "RUNNER_ALLOW_INSECURE_HTTP")),
            ReviewReleaseDrain = OptIn(
                Val("review-release-drain", "RUNNER_REVIEW_RELEASE_DRAIN")),
            HealthCheckOnly = healthCheck,
            DrainOnly = drain,
            RestartGuardOnly = restartGuard,
            Force = force,
            HoldAdmission = holdAdmission,
        };

        var serverUri = new Uri(options.ServerUrl, UriKind.Absolute);
        // Plain HTTP outside loopback stays refused unless the operator opted in
        // explicitly. The opt-in covers exactly one legitimate topology: a private
        // container network where the Task Server is addressed by service name and
        // never published outside it.
        var insecureHttpPermitted = options.AllowInsecureHttp
                                    && serverUri.Scheme == Uri.UriSchemeHttp;
        if (serverUri.Scheme != Uri.UriSchemeHttps && !serverUri.IsLoopback && !insecureHttpPermitted)
            throw new ArgumentException(
                "RUNNER_SERVER_URL must use HTTPS unless it is a loopback address. "
                + "Set RUNNER_ALLOW_INSECURE_HTTP=1 to opt in to plain HTTP on a trusted private "
                + "network (for example a container network such as http://orchestrator-api:5031).");
        if (!serverUri.IsLoopback && string.IsNullOrWhiteSpace(options.AuthToken))
            throw new ArgumentException("RUNNER_AUTH_TOKEN is required for a non-loopback Task Server.");
        if (options.Role is not ("coding" or "review" or "gate"))
            throw new ArgumentException("RUNNER_ROLE must be 'coding', 'review', or 'gate'.");
        if (options.Role is "review" or "gate"
            && string.Equals(
                Path.GetFullPath(options.WorkDir),
                Path.GetFullPath(options.ReviewWorkDir),
                StringComparison.Ordinal))
            throw new ArgumentException("Review and coding workspace roots must be different.");
        if (options.CliResumeArgs is not null
            && !options.CliResumeArgs.Contains("{sessionId}", StringComparison.Ordinal))
            throw new ArgumentException("RUNNER_CLI_RESUME_ARGS must contain the {sessionId} placeholder.");
        if (options.ExecEngine is not (ExecEngineCar or ExecEngineLegacy))
            throw new ArgumentException("RUNNER_EXEC_ENGINE must be 'car' or 'legacy'.");

        var taskKey = positional ?? (overrides.TryGetValue("task", out var tk) ? tk : null);
        return (options, string.IsNullOrWhiteSpace(taskKey) ? null : taskKey.Trim(), once, help);
    }

    private static string ReadAuthTokenFile(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"RUNNER_AUTH_TOKEN_FILE does not exist: {path}");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ArgumentException("RUNNER_AUTH_TOKEN_FILE must not be accessible to other users.");
        }
        var token = File.ReadAllText(path).Trim();
        if (token.Length == 0) throw new ArgumentException("RUNNER_AUTH_TOKEN_FILE is empty.");
        return token;
    }
}
