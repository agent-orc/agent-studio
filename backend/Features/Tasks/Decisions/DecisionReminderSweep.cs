namespace AgentStudio.Tasks;

/// <summary>One reminder the sweep posted.</summary>
public sealed record DecisionReminder(string JobId, string Key, DateTime DueAt, IReadOnlyList<string> BlockedCards);

/// <summary>
/// Reminds the decider once a pending decision card passes its due date
/// (Dossier decision-cards D5=A, default three days). Each reminder posts an
/// inbox entry (the decision's wiki record, listed as "wants review" in the
/// workbench inbox) and an activity feed line naming the blocked cards, and
/// stamps <see cref="DecisionContent.RemindedAt"/> so the pending cycle is
/// reminded only once. The sweep never moves a card.
/// </summary>
public sealed class DecisionReminderSweep
{
    private const string Actor = "orchestrator";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly DecisionRecordService _records;
    private readonly OrchestratorLog _activityFeed;
    private readonly TimelineLog _timeline;
    private readonly ILogger<DecisionReminderSweep> _logger;
    private readonly TimeProvider _clock;

    public DecisionReminderSweep(TaskScannerService scanner, TaskMutationService mutations,
        DecisionRecordService records, OrchestratorLog activityFeed, TimelineLog timeline,
        ILogger<DecisionReminderSweep> logger, TimeProvider? clock = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _records = records;
        _activityFeed = activityFeed;
        _timeline = timeline;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<DecisionReminder> Sweep(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var cards = _scanner.ScanAllAutomationJobs();
        var reminders = new List<DecisionReminder>();
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            if (!TaskKinds.IsDecision(card.Kind) || card.Decision is not { } decision) continue;
            if (!DecisionReminderPolicy.IsDue(decision, card.CreatedAt, now)) continue;

            var key = card.Key ?? card.Id;
            var waiting = cards
                .Where(other => !ReferenceEquals(other, card) && !string.IsNullOrWhiteSpace(other.Key ?? other.Id))
                .Select(other => new DecisionWaitingCard(other.Key ?? other.Id, other.State,
                    other.References?.DependsOn.Any(edge =>
                        string.Equals(edge.Key, key, StringComparison.OrdinalIgnoreCase)) == true))
                .ToList();
            var blocked = DecisionReminderPolicy.BlockedCards(decision, waiting);
            var dueAt = DecisionReminderPolicy.DueAt(decision, card.CreatedAt);
            if (Remind(card, decision, key, dueAt, blocked, now))
                reminders.Add(new(card.Id, key, dueAt, blocked));
        }
        return reminders;
    }

    private bool Remind(TaskInfo card, DecisionContent decision, string key, DateTime dueAt,
        IReadOnlyList<string> blocked, DateTime now)
    {
        var reminded = decision with { RemindedAt = now };
        if (!_mutations.SetDecisionContent(card.Id, reminded, card.WatchPath))
        {
            _logger.LogWarning("decision-reminder-stamp-failed job={JobId}", card.Id);
            return false;
        }

        var path = $"{WikiProducerTargets.DecisionsFolder}/{key}.md";
        var inbox = _records.Write(card.ProjectName, path, new DecisionRecord(key, card.Title, reminded)
        {
            Reminder = new DecisionReminderNotice(dueAt, now, blocked, Actor),
        });
        if (!inbox.Success)
            _logger.LogWarning("decision-reminder-inbox-failed job={JobId} error={Error}", card.Id, inbox.Error);

        var names = blocked.Count == 0 ? "no recorded cards" : string.Join(", ", blocked);
        var summary = $"Decision overdue: {key} waits on {DeciderName(decision)} since {dueAt:yyyy-MM-dd}; blocks {names}";
        _activityFeed.Append(card.WatchPath, new OrchestratorLogEntry
        {
            Ts = now,
            Kind = OrchestratorLogKinds.Alert,
            Topic = OrchestratorLogTopics.DecisionCard,
            Summary = summary,
            Reasoning = decision.Question,
            JobId = card.Id,
        });
        _timeline.Append(card.FolderPath, TimelineEventKinds.DecisionReminded, TimelineActors.Orchestrator,
            summary: summary, payloadRef: inbox.Success ? path : null,
            details: new()
            {
                ["dueAt"] = dueAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["blockedCards"] = string.Join(",", blocked),
                ["decider"] = DeciderName(decision),
            });
        _logger.LogInformation("decision-reminded job={JobId} blocked={Blocked}", card.Id, names);
        return true;
    }

    private static string DeciderName(DecisionContent decision) =>
        string.IsNullOrWhiteSpace(decision.Decider) ? DecisionDeciders.Operator : decision.Decider.Trim();
}

/// <summary>Runs <see cref="DecisionReminderSweep"/> on a slow timer; a reminder is a day-scale signal.</summary>
public sealed class DecisionReminderSweepHostedService : BackgroundService
{
    public const int DefaultIntervalMinutes = 30;

    private readonly DecisionReminderSweep _sweep;
    private readonly IConfiguration _config;
    private readonly ILogger<DecisionReminderSweepHostedService> _logger;

    public DecisionReminderSweepHostedService(DecisionReminderSweep sweep, IConfiguration config,
        ILogger<DecisionReminderSweepHostedService> logger)
    {
        _sweep = sweep;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _config.GetValue<int?>("Supervisor:DecisionReminderSweepIntervalMinutes") ?? DefaultIntervalMinutes;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                _sweep.Sweep(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Decision reminder sweep failed");
            }
        }
    }
}
