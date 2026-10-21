namespace AgentStudio.Runner;

/// <summary>
/// AGT-3060 (I18): readiness of the Orchestrator Engine as the authority sees
/// it. The per-transition switch (<see cref="MonolithTransitionSwitch"/>) is
/// the only selector of who decides; this class never holds a decision. When a
/// transition profile or the AI Operator profile selects the engine, its
/// occurrences wait for an engine receipt, so <c>GET
/// /healthz/orchestration-engine</c> reports whether an engine instance
/// contacted the orchestration endpoints within the contact window, and the
/// readiness service alerts the operator when it did not. With every profile
/// on Monolith (the default) readiness is not required.
/// </summary>
public sealed class OrchestrationEngineReadiness
{
    public const string ContactWindowKey = "Orchestration:EngineReadiness:ContactWindowSeconds";
    public const int DefaultContactWindowSeconds = 30;
    public const string Ready = "ready";
    public const string NotLive = "engine-not-live";
    public const string NotRequired = "not-required";

    /// <summary>Every switch profile whose Engine or Shadow mode waits for an engine receipt.</summary>
    public static readonly IReadOnlyList<string> EngineProfiles =
        [.. OrchestrationRolloutStore.Profiles, "OperatorSweep", AiOperatorDecisionGateway.Transition];

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<OrchestrationEngineReadiness> _logger;
    private long _lastContactTicks;
    private string? _lastEngineId;

    public OrchestrationEngineReadiness(IConfiguration configuration, TimeProvider time,
        ILogger<OrchestrationEngineReadiness> logger)
    {
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// True when a switch profile selects the engine and the orchestration
    /// endpoints are served locally. Behind the Task Server plane proxy the
    /// engine talks to the Task Server, so this host cannot observe it (I11
    /// owns that topology); a public-demo host runs no automation.
    /// </summary>
    public bool Required => EngineProfiles.Any(profile =>
            MonolithTransitionSwitch.For(_configuration, profile) != OrchestrationExecutionMode.Monolith)
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

    private int _overdueDecisions;
    private long _oldestOverdueTicks;

    /// <summary>AGT-3281: cards that have waited past the bound for an engine decision while the engine is live.</summary>
    public int OverdueDecisions => Volatile.Read(ref _overdueDecisions);

    /// <summary>When the oldest overdue decision was created; null when none is overdue.</summary>
    public DateTimeOffset? OldestOverdueSince => Interlocked.Read(ref _oldestOverdueTicks) is var ticks and > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>Called by the orchestration recovery pass with its current count.</summary>
    public void ReportOverdueDecisions(int count, DateTimeOffset? oldestSince)
    {
        Volatile.Write(ref _overdueDecisions, Math.Max(0, count));
        Interlocked.Exchange(ref _oldestOverdueTicks, oldestSince?.UtcTicks ?? 0);
    }

    /// <summary>Called by the orchestration endpoints for every authenticated engine request.</summary>
    public void Observe(string engineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        if (LastContact is not { } last || _time.GetUtcNow() - last > ContactWindow)
            _logger.LogInformation("orchestration-engine-live engine={EngineId}", engineId);
        Volatile.Write(ref _lastEngineId, engineId);
        Interlocked.Exchange(ref _lastContactTicks, _time.GetUtcNow().UtcTicks);
    }
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
        logger.LogError("orchestration-engine-not-live lastContact={LastContact}; engine-switched transitions wait",
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
                Summary = "The Orchestrator Engine is not running; transitions switched to Engine wait.",
                Reasoning = HasEnrolledEngine()
                    ? "No engine instance contacted the orchestration endpoints. Pending occurrences stay durable and "
                        + "resume when an engine runs; start or restart the Orchestrator Engine, or switch the "
                        + "transitions back to Monolith."
                    : "No engine identity is enrolled. Enroll an Orchestrator Engine and start it, or switch the "
                        + "transitions back to Monolith; pending occurrences stay durable until then.",
            });
        }
    }
}
