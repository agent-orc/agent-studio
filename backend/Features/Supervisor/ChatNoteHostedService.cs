using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Supervisor;

/// <summary>
/// Periodic per-project ticker that pushes a single, low-frequency summary
/// line into the project chat via the existing
/// <see cref="OrchestratorChatLog.AppendSupervisor"/> path. Companion to the
/// hard-check / soft-reasoning / meta-cycle services: those write structured
/// evidence to disk; this surfaces a one-liner so the user notices the
/// supervisor is alive without scrolling logs.
/// </summary>
/// <remarks>
/// <para><b>Quiet beats spam.</b> When the window had no advisories, no
/// meta-cycle activity, and no jobs reaching <c>4-review</c>, no message is
/// written. The window cursor still advances so a single quiet window does
/// not let a later note retroactively cover hours of evidence.</para>
/// <para>Persistence reuses the supervisor stream tag, so the activity-log
/// parser already renders these notes alongside other
/// <c>[supervisor]</c> messages. The <c>chat-note</c> tag distinguishes
/// them from intervention messages.</para>
/// <para>Best-effort by contract: any failure is logged at warning level
/// and never raised. The runner must not be blocked by chat-note write
/// errors.</para>
/// </remarks>
public sealed class ChatNoteHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int MinPeriodMinutes = 5;
    private const int MaxPeriodMinutes = 240;
    private const int DefaultPeriodMinutes = 30;

    private readonly TaskRunnerService _taskRunner;
    private readonly TaskScannerService _scanner;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatNoteHostedService> _logger;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, ProjectChatNoteState> _state =
        new(StringComparer.OrdinalIgnoreCase);

    public ChatNoteHostedService(
        TaskRunnerService taskRunner,
        TaskScannerService scanner,
        OrchestratorChatLog chatLog,
        IConfiguration configuration,
        ILogger<ChatNoteHostedService> logger,
        TimeProvider? time = null)
    {
        _taskRunner = taskRunner;
        _scanner = scanner;
        _chatLog = chatLog;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; ChatNoteHostedService idle.");
            return;
        }

        // Tick at one minute so cadence aligns with the 5-240 min period
        // bounds. The window evaluation itself decides whether a tick emits.
        var tickSeconds = _configuration.GetValue("Supervisor:ChatNoteTickSeconds", 60);

        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }
        await _scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue("Supervisor:ChatNoteEnabled", true))
                    await TickOnceAsync(workspace!, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "ChatNote tick failed"); }

            tickSeconds = _configuration.GetValue("Supervisor:ChatNoteTickSeconds", 60);
            try { await Task.Delay(TimeSpan.FromSeconds(tickSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One pass over every known project. Public so tests can drive the
    /// cadence deterministically without spinning up a hosted service.
    /// </summary>
    public async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var period = ResolvePeriod();

        var status = _taskRunner.GetStatus();
        if (status?.Projects == null) return;

        var allJobs = _scanner.ScanAllAutomationJobs();
        var byProject = allJobs
            .GroupBy(j => j.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var project in status.Projects.Keys)
        {
            ct.ThrowIfCancellationRequested();
            byProject.TryGetValue(project, out var projectJobs);
            projectJobs ??= new List<TaskInfo>();

            try
            {
                EvaluateProject(workspace, project, projectJobs, period);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ChatNote EvaluateProject failed for {Project}", project);
            }
        }

        // Tests use the sync return path; this await keeps the method async
        // for symmetry with the other hosted services.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Decision pass for one project. Returns the message that was written,
    /// or <c>null</c> if either the period had not elapsed yet or the
    /// window was empty.
    /// </summary>
    public string? EvaluateProject(
        string workspace,
        string project,
        IReadOnlyList<TaskInfo> projectJobs,
        TimeSpan period)
    {
        var state = _state.GetOrAdd(project, _ => new ProjectChatNoteState());
        var now = _time.GetUtcNow().UtcDateTime;

        // First-tick bootstrap: lock the cursor at "now" so we do not
        // retroactively summarise everything written before the service
        // started.
        if (state.LastNoteAt == null)
        {
            state.LastNoteAt = now;
            return null;
        }

        var elapsed = now - state.LastNoteAt.Value;
        if (elapsed < period) return null;

        var from = state.LastNoteAt.Value;
        var to = now;

        var advisories = ReadAdvisoriesSince(workspace, project, from, to);
        var cycles = ReadCyclesSince(workspace, project, from, to);
        var reviewCount = CountJobsReachedReview(projectJobs, from, to);

        var window = new ChatNoteWindow(from, to, advisories, cycles, reviewCount);
        var message = ChatNoteSummary.Build(window);

        // Always advance the cursor so the next window is bounded; quiet
        // windows simply produce no message.
        state.LastNoteAt = now;

        if (message == null) return null;

        var target = SelectChatTarget(projectJobs);
        if (target == null)
        {
            _logger.LogDebug("ChatNote skipped for {Project}: no chat target job found.", project);
            return null;
        }

        try
        {
            _chatLog.AppendSupervisor(target, "chat-note", message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatNote failed to append for {Project} job {JobId}", project, target.Id);
            return null;
        }

        return message;
    }

    private TimeSpan ResolvePeriod()
    {
        var configured = _configuration.GetValue("Supervisor:ChatNotePeriodMinutes", DefaultPeriodMinutes);
        var clamped = Math.Clamp(configured, MinPeriodMinutes, MaxPeriodMinutes);
        return TimeSpan.FromMinutes(clamped);
    }

    private static IReadOnlyList<SupervisorAdvisory> ReadAdvisoriesSince(
        string workspace,
        string project,
        DateTime from,
        DateTime to)
    {
        var path = SupervisorLogPaths.ObservationsFile(workspace, project);
        if (!File.Exists(path)) return Array.Empty<SupervisorAdvisory>();

        var advisories = new List<SupervisorAdvisory>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                SupervisorAdvisory? adv;
                try { adv = JsonSerializer.Deserialize<SupervisorAdvisory>(line, Json); }
                catch { continue; }
                if (adv == null) continue;
                if (adv.CreatedAt <= from || adv.CreatedAt > to) continue;
                advisories.Add(adv);
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ChatNoteHostedService: best-effort read"); /* best-effort read */ }
        return advisories;
    }

    private static IReadOnlyList<ChatNoteCycleEntry> ReadCyclesSince(
        string workspace,
        string project,
        DateTime from,
        DateTime to)
    {
        var path = SupervisorLogPaths.MetaCycleTailLog(workspace, project);
        if (!File.Exists(path)) return Array.Empty<ChatNoteCycleEntry>();

        var entries = new List<ChatNoteCycleEntry>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;
                if (!DateTime.TryParse(
                        parts[0],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var ts)) continue;
                if (ts <= from || ts > to) continue;
                entries.Add(new ChatNoteCycleEntry(ts, parts[1], parts[2], parts[3], parts[4]));
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ChatNoteHostedService: best-effort read"); /* best-effort read */ }
        return entries;
    }

    private static int CountJobsReachedReview(
        IReadOnlyList<TaskInfo> projectJobs,
        DateTime from,
        DateTime to)
    {
        var count = 0;
        foreach (var job in projectJobs)
        {
            // ADR-0025: a job that has reached either review lane (auto or
            // human) counts as "reached review" for the cycle counter.
            if (job.State != TaskStates.AutoReview && job.State != TaskStates.HumanReview) continue;
            if (job.LastActivity <= from || job.LastActivity > to) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Pick the job whose <c>cli-output.log</c> the chat-note attaches to.
    /// Active job wins; otherwise the most recently active job in any of
    /// the project's user-visible lanes.
    /// </summary>
    internal static TaskInfo? SelectChatTarget(IReadOnlyList<TaskInfo> projectJobs)
    {
        if (projectJobs.Count == 0) return null;

        var inProgress = projectJobs
            .Where(j => j.State == TaskStates.Progress)
            .OrderByDescending(j => j.LastActivity)
            .FirstOrDefault();
        if (inProgress != null) return inProgress;

        return projectJobs
            .Where(j => j.State == TaskStates.Progress
                     || j.State == TaskStates.Ready
                     || j.State == TaskStates.AutoReview
                     || j.State == TaskStates.HumanReview
                     || j.State == TaskStates.Completed)
            .OrderByDescending(j => j.LastActivity)
            .FirstOrDefault()
            ?? projectJobs.OrderByDescending(j => j.LastActivity).FirstOrDefault();
    }

    /// <summary>
    /// Test seam: pre-seed the cursor for a project so a unit test can
    /// place itself inside an existing cadence without first calling
    /// <see cref="EvaluateProject"/> to bootstrap.
    /// </summary>
    internal void SeedLastNoteAt(string project, DateTime atUtc)
    {
        var state = _state.GetOrAdd(project, _ => new ProjectChatNoteState());
        state.LastNoteAt = atUtc;
    }

    private sealed class ProjectChatNoteState
    {
        public DateTime? LastNoteAt;
    }
}
