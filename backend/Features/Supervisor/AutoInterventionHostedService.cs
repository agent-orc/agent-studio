
namespace AgentStudio.Supervisor;

/// <summary>
/// Optional supervisor extension that promotes selected advisories to
/// automatic invocations of the emergency primitives.
/// </summary>
/// <remarks>
/// <para><b>OFF by default.</b> Enabling auto-intervention is a per-instance
/// decision via <c>Supervisor:AutoInterventionEnabled = true</c>. Per-project
/// enable + severity overrides are a follow-up; the first cut is global.</para>
/// <para>Bounded rate: at most <c>Supervisor:AutoInterventionRateLimit</c>
/// invocations per project per hour (default 3). When the limit triggers,
/// the policy stops calling primitives until the window slides.</para>
/// <para>Feedback-loop guard: advisories with source <c>Supervisor</c> or
/// <c>User</c> never count.</para>
/// </remarks>
public sealed class AutoInterventionHostedService : BackgroundService
{
    private readonly TaskRunnerService _taskRunner;
    private readonly SupervisorInterventionService _interventions;
    private readonly ProjectObservationService _observe;
    private readonly SupervisorAdvisoryStore _advisoryStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoInterventionHostedService> _logger;

    private readonly Dictionary<string, Queue<DateTime>> _ratePerProject = new();
    private readonly Dictionary<string, long> _projectCursor = new(StringComparer.OrdinalIgnoreCase);

    public AutoInterventionHostedService(
        TaskRunnerService taskRunner,
        SupervisorInterventionService interventions,
        ProjectObservationService observe,
        SupervisorAdvisoryStore advisoryStore,
        IConfiguration configuration,
        ILogger<AutoInterventionHostedService> logger)
    {
        _taskRunner = taskRunner;
        _interventions = interventions;
        _observe = observe;
        _advisoryStore = advisoryStore;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; AutoInterventionHostedService idle.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue("Supervisor:AutoInterventionEnabled", false))
                    await TickOnceAsync(workspace!, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "AutoIntervention tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var status = _taskRunner.GetStatus();
        if (status?.Projects == null) return;

        var thresholdName = _configuration.GetValue("Supervisor:AutoInterventionSeverityThreshold", "High");
        var threshold = Enum.TryParse<SupervisorSeverity>(thresholdName, ignoreCase: true, out var t) ? t : SupervisorSeverity.High;
        var rateLimit = _configuration.GetValue("Supervisor:AutoInterventionRateLimit", 3);

        foreach (var (project, _) in status.Projects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var advisories = ReadNewAdvisories(workspace, project);
                foreach (var a in advisories)
                {
                    var action = AutoInterventionMapping.MapAdvisory(a, threshold);
                    if (action == null) continue;
                    if (!RateLimitOk(project, rateLimit))
                    {
                        _logger.LogInformation("AutoIntervention rate-limited for {Project}", project);
                        break;
                    }
                    await DispatchAsync(project, a.JobId, action, ct);
                    _ratePerProject.GetValueOrDefault(project)?.Enqueue(DateTime.UtcNow);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AutoIntervention failed for project {Project}", project);
            }
        }
    }

    private async Task DispatchAsync(string project, string? jobId, AutoInterventionMapping.Action action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case SupervisorInterventionKind.CancelRun when jobId != null:
                await _interventions.CancelRunAsync(project, jobId, action.Reason, SupervisorSource.AutoIntervention, ct);
                break;
            case SupervisorInterventionKind.PausePickup:
                await _interventions.PausePickupAsync(project, action.Reason, TimeSpan.FromMinutes(30), SupervisorSource.AutoIntervention, ct);
                break;
            case SupervisorInterventionKind.ForceFail when jobId != null:
                await _interventions.ForceFailAsync(project, jobId, action.Reason, SupervisorSource.AutoIntervention, ct);
                break;
            case SupervisorInterventionKind.Resume:
                await _interventions.ResumeAsync(project, action.Reason, SupervisorSource.AutoIntervention, ct);
                break;
        }
    }

    private bool RateLimitOk(string project, int rateLimit)
    {
        if (!_ratePerProject.TryGetValue(project, out var q)) { q = new Queue<DateTime>(); _ratePerProject[project] = q; }
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
        return q.Count < rateLimit;
    }

    private List<SupervisorAdvisory> ReadNewAdvisories(string workspace, string project)
    {
        // External writer (HardHealthCheckHostedService.AppendObservationRecord)
        // bypasses the store; invalidate the projection so the next read picks
        // up any lines appended out-of-band since the last tick. Once every
        // writer routes through the store this invalidation can go away.
        _advisoryStore.InvalidateProjection(workspace, project);

        var cursor = _projectCursor.GetValueOrDefault(project, 0L);
        var (records, newCursor) = _advisoryStore.ReadSince(workspace, project, cursor);
        _projectCursor[project] = newCursor;
        return records.ToList();
    }
}
