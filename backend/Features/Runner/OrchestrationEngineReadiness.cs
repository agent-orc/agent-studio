namespace AgentStudio.Runner;

/// <summary>
/// AGT-3060 (I18, AGT-W65 D14 A): readiness of the Orchestrator Engine as the
/// authority sees it. The engine is the only decider, so a production host has
/// no monolith fallback. Instead every automatic decision waits for engine
/// readiness: the pinned transition profiles and AI Operator deciders leave
/// pending occurrences, and each residual monolith decider holds its pass while
/// no engine instance has contacted the orchestration endpoints within the
/// contact window. <c>GET /healthz/orchestration-engine</c> reports the state
/// so an upgrade can wait for it.
/// </summary>
public sealed class OrchestrationEngineReadiness
{
    public const string ContactWindowKey = "Orchestration:EngineReadiness:ContactWindowSeconds";
    public const int DefaultContactWindowSeconds = 30;
    public const string Ready = "ready";
    public const string NotLive = "engine-not-live";
    public const string NotRequired = "not-required";

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<OrchestrationEngineReadiness> _logger;
    private long _lastContactTicks;
    private string? _lastEngineId;
    private int _holding;

    public OrchestrationEngineReadiness(IConfiguration configuration, TimeProvider time,
        ILogger<OrchestrationEngineReadiness> logger)
    {
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// True on an engine-only host whose orchestration endpoints are served
    /// locally. Behind the Task Server plane proxy the engine talks to the Task
    /// Server, so this host cannot observe it (I11 owns that topology); a
    /// public-demo host runs no automation.
    /// </summary>
    public bool Required => OrchestrationDecisionAuthority.IsEngineOnly(_configuration)
        && !TaskServerPlaneProxy.IsConfigured(_configuration)
        && !SecurityProfiles.IsPublicDemo(_configuration);

    public DateTimeOffset? LastContact => Interlocked.Read(ref _lastContactTicks) is var ticks and > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public string? LastEngineId => Volatile.Read(ref _lastEngineId);

    public TimeSpan ContactWindow => TimeSpan.FromSeconds(Math.Clamp(
        _configuration.GetValue<int?>(ContactWindowKey) ?? DefaultContactWindowSeconds, 5, 600));

    /// <summary>True when no engine is required or an engine made contact within the window.</summary>
    public bool IsLive => !Required
        || LastContact is { } contact && _time.GetUtcNow() - contact <= ContactWindow;

    public string State => !Required ? NotRequired : IsLive ? Ready : NotLive;

    /// <summary>Called by the orchestration endpoints for every authenticated engine request.</summary>
    public void Observe(string engineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        Volatile.Write(ref _lastEngineId, engineId);
        Interlocked.Exchange(ref _lastContactTicks, _time.GetUtcNow().UtcTicks);
        if (Interlocked.Exchange(ref _holding, 0) == 1)
            _logger.LogInformation("orchestration-engine-live engine={EngineId}; residual deciders resume", engineId);
    }

    /// <summary>
    /// The guard of every residual monolith decider (see
    /// docs/system/architecture/monolith-hosted-services.md): false while the
    /// engine is not live, so the decider skips its pass and moves no card.
    /// </summary>
    public bool AllowsResidualDecision(string decider)
    {
        if (IsLive) return true;
        if (Interlocked.Exchange(ref _holding, 1) == 0)
            _logger.LogWarning(
                "orchestration-engine-not-live decider={Decider} lastContact={LastContact}; residual deciders hold",
                decider, LastContact);
        return false;
    }

    /// <summary>Static form for deciders that tests construct without the readiness service.</summary>
    public static bool Allows(OrchestrationEngineReadiness? readiness, string decider)
        => readiness?.AllowsResidualDecision(decider) ?? true;
}

/// <summary>
/// Reports engine readiness after startup: it logs whether an active engine
/// identity is enrolled, and when no engine makes contact within the startup
/// grace or the engine goes silent, it raises one operator feed alert per watch
/// path and outage.
/// </summary>
public sealed class OrchestrationEngineReadinessService(
    OrchestrationEngineReadiness readiness,
    AccessSecurityStore security,
    TaskScannerService scanner,
    OrchestratorLog operatorLog,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<OrchestrationEngineReadinessService> logger) : BackgroundService
{
    public const string StartupGraceKey = "Orchestration:EngineReadiness:StartupGraceSeconds";
    public const int DefaultStartupGraceSeconds = 60;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (readiness.Required)
        {
            if (HasEnrolledEngine())
                logger.LogInformation("orchestration-engine-readiness-required; waiting for engine contact");
            else
                logger.LogError("orchestration-engine-not-provisioned: no active engine_ identity is enrolled. "
                    + "Every automatic decision waits until an Orchestrator Engine is enrolled and running "
                    + "(docs/operations/setup/linux-runner-host.md).");
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!readiness.Required) return;
        var startedAt = time.GetUtcNow();
        var grace = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>(StartupGraceKey) ?? DefaultStartupGraceSeconds, 0, 3600));
        DateTimeOffset? alertedOutage = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, time, stoppingToken);
                if (readiness.IsLive) { alertedOutage = null; continue; }
                var outage = readiness.LastContact ?? startedAt;
                if (time.GetUtcNow() - startedAt < grace || alertedOutage == outage) continue;
                try
                {
                    Alert(outage);
                    alertedOutage = outage;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "orchestration-engine-readiness-alert-failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
    }

    internal bool HasEnrolledEngine()
        => security.ListRunners().Any(runner => runner.Id.StartsWith("engine_", StringComparison.Ordinal)
            && runner.RevokedAt is null && runner.RetiredAt is null);

    internal void Alert(DateTimeOffset outage)
    {
        logger.LogError("orchestration-engine-not-live lastContact={LastContact}; no automatic decision runs",
            readiness.LastContact);
        var topic = "engine-not-live:" + outage.UtcTicks;
        foreach (var entry in scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            if (operatorLog.Read(entry.Path).Any(item => item.Topic == topic)) continue;
            operatorLog.Append(entry.Path, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Alert,
                Topic = topic,
                Summary = "The Orchestrator Engine is not running; no automatic decision runs.",
                Reasoning = HasEnrolledEngine()
                    ? "No engine instance contacted the orchestration endpoints. Pending occurrences stay durable and "
                        + "resume when an engine runs; start or restart the Orchestrator Engine."
                    : "No engine identity is enrolled. Enroll an Orchestrator Engine and start it; pending "
                        + "occurrences stay durable until then.",
            });
        }
    }
}
