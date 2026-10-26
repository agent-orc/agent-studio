using System.Text.Json;

namespace AgentStudio.Supervisor;

/// <summary>
/// Backs the four pre-emptive control primitives in <see cref="ISupervisor"/>.
/// Every invocation:
/// <list type="bullet">
/// <item>Records a typed <see cref="SupervisorIntervention"/> to
/// <c>interventions.jsonl</c> with a mandatory reason.</item>
/// <item>Routes the actual side effect through the orchestrator's existing
/// authority paths (<see cref="TaskRunnerService.StopJob"/>,
/// <see cref="TaskRunnerService.SetMode"/>) so the runner's state machine
/// remains the single source of truth.</item>
/// <item>Writes a meta-message to the orchestrator chat log when the
/// intervention is user-visible at the task level.</item>
/// </list>
/// </summary>
/// <remarks>
/// Out of scope in this first cut: pause-TTL auto-resume background service
/// (the TTL is recorded but enforcement is a follow-up task). The proper
/// [supervisor] activity-log participant is Task 08; until then the chat log
/// uses the existing <see cref="OrchestratorMessageKind.Decision"/> kind with
/// a "supervisor:" prefix.
/// </remarks>
public sealed class SupervisorInterventionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly TaskRunnerService _taskRunner;
    private readonly TaskScannerService _jobScanner;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupervisorInterventionService> _logger;
    private readonly TimeProvider _time;
    private readonly AgentMessageBusBridge? _bus;

    public SupervisorInterventionService(
        TaskRunnerService taskRunner,
        TaskScannerService jobScanner,
        OrchestratorChatLog chatLog,
        IConfiguration configuration,
        ILogger<SupervisorInterventionService> logger,
        TimeProvider? time = null,
        AgentMessageBusBridge? bus = null)
    {
        _taskRunner = taskRunner;
        _jobScanner = jobScanner;
        _chatLog = chatLog;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _bus = bus;
    }

    public Task CancelRunAsync(string project, string jobId, string reason, SupervisorSource source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var intervention = new SupervisorIntervention(
            CreatedAt: _time.GetUtcNow().UtcDateTime,
            Project: project,
            Kind: SupervisorInterventionKind.CancelRun,
            Source: source,
            Reason: reason,
            JobId: jobId);

        WriteIntervention(intervention);

        var info = SafeFindJob(jobId);
        if (info != null)
        {
            try
            {
                _taskRunner.StopJob(jobId, info.WatchPath, RunStopReason.UserStop);
                _chatLog.AppendSupervisor(info, "cancel-run", $"reason={reason} source={source}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SupervisorInterventionService.CancelRun stop failed for {JobId}", jobId);
            }
        }
        else
        {
            _logger.LogWarning("SupervisorInterventionService.CancelRun: job {JobId} not found in project {Project}", jobId, project);
        }

        return Task.CompletedTask;
    }

    public Task PausePickupAsync(string project, string reason, TimeSpan? ttl, SupervisorSource source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var intervention = new SupervisorIntervention(
            CreatedAt: _time.GetUtcNow().UtcDateTime,
            Project: project,
            Kind: SupervisorInterventionKind.PausePickup,
            Source: source,
            Reason: reason,
            PauseTtl: ttl);

        WriteIntervention(intervention);

        try
        {
            _taskRunner.SetMode(project, "paused", $"supervisor pause: {reason} (source={source})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SupervisorInterventionService.PausePickup SetMode failed for {Project}", project);
        }
        return Task.CompletedTask;
    }

    public Task ForceFailAsync(string project, string jobId, string reason, SupervisorSource source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var intervention = new SupervisorIntervention(
            CreatedAt: _time.GetUtcNow().UtcDateTime,
            Project: project,
            Kind: SupervisorInterventionKind.ForceFail,
            Source: source,
            Reason: reason,
            JobId: jobId);

        WriteIntervention(intervention);

        var info = SafeFindJob(jobId);
        if (info != null)
        {
            try
            {
                _taskRunner.StopJob(jobId, info.WatchPath, RunStopReason.UserStop);
                _chatLog.AppendSupervisor(info, "force-fail", $"reason={reason} source={source}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SupervisorInterventionService.ForceFail stop failed for {JobId}", jobId);
            }
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(string project, string reason, SupervisorSource source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var intervention = new SupervisorIntervention(
            CreatedAt: _time.GetUtcNow().UtcDateTime,
            Project: project,
            Kind: SupervisorInterventionKind.Resume,
            Source: source,
            Reason: reason);

        WriteIntervention(intervention);

        try
        {
            _taskRunner.SetMode(project, "auto-continuous", $"supervisor resume: {reason} (source={source})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SupervisorInterventionService.Resume SetMode failed for {Project}", project);
        }
        return Task.CompletedTask;
    }

    private void WriteIntervention(SupervisorIntervention intervention)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; supervisor intervention not persisted");
            return;
        }
        try
        {
            AppendInterventionRecord(workspace, intervention);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SupervisorInterventionService failed to persist intervention {Kind} for {Project}", intervention.Kind, intervention.Project);
        }

        // Bridge to the Agent Message Bus. The legacy interventions.jsonl above
        // remains canonical (Auto-intervention reader, Layer 3 review tooling
        // still parse it directly); the bus is a derived projection so the
        // project screen can render typed events without re-parsing.
        try { _ = _bus?.EmitInterventionAsync(intervention); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of intervention {Kind} for {Project} failed", intervention.Kind, intervention.Project); }
    }

    /// <summary>
    /// Pure file-write helper. Public + static so tests can exercise the
    /// persistence path without standing up a runner / config / DI container.
    /// </summary>
    public static void AppendInterventionRecord(string workspaceRoot, SupervisorIntervention intervention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var dir = SupervisorLogPaths.ProjectLogDir(workspaceRoot, intervention.Project);
        Directory.CreateDirectory(dir);
        var path = SupervisorLogPaths.InterventionsFile(workspaceRoot, intervention.Project);
        var line = JsonSerializer.Serialize(intervention, Json);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private TaskInfo? SafeFindJob(string jobId)
    {
        try { return _jobScanner.FindPublishedJob(jobId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SupervisorInterventionService FindJob failed for {JobId}", jobId);
            return null;
        }
    }
}
