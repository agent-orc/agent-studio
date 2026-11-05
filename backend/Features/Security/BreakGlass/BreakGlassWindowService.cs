using System.Text;
using System.Text.Json;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Registry;

namespace AgentStudio.Security;

/// <summary>Closes elapsed windows and creates one review card per closed window through the task mutation service.</summary>
public interface IBreakGlassReviewCardWriter
{
    bool ValidProject(string project);
    string? Find(string id);
    string? Create(BreakGlassWindow window, string id, string prompt);
}

public sealed class TaskBreakGlassReviewCardWriter(TaskScannerService scanner, TaskMutationService mutations,
    ProjectRegistry projects) : IBreakGlassReviewCardWriter
{
    public bool ValidProject(string project) => projects.FindById(project) is not null || projects.FindByShortCode(project) is not null;
    public string? Find(string id) => scanner.FindJob(id)?.Id;
    public string? Create(BreakGlassWindow window, string id, string prompt) => mutations.CreateJob(new CreateTaskRequest
    {
        Id = id, Project = window.Project, Title = $"Review break-glass window {window.Id}",
        PromptMarkdown = prompt, TargetState = TaskStates.HumanReview,
        CreationSource = "break-glass", CreatedBy = TimelineActors.System,
    });
}

public sealed class BreakGlassWindowService(
    AccessSecurityStore store, IBreakGlassReviewCardWriter cards,
    IControlPlaneAuditAlertChannel alerts, ControlPlaneAuditSinkOptions sinkOptions,
    TimeProvider clock, ILogger<BreakGlassWindowService> logger) : BackgroundService
{
    private readonly object _reviewGate = new();
    public async Task<BreakGlassWindow> OpenAsync(HumanPrincipal owner, OpenBreakGlassRequest request, CancellationToken ct)
    {
        if (!sinkOptions.Configured)
            throw new SecurityOperationException(503, "break-glass-sink-required", "Configure the off-host audit sink before opening a window.");
        if (request.Project is null || !cards.ValidProject(request.Project))
            throw new SecurityOperationException(400, "break-glass-project-invalid", "Select a current project ID or short code for the review card.");
        var window = store.OpenBreakGlassWindow(owner, request.Credential, request.SessionIds, request.LifetimeHours, request.Project);
        await AlertAsync(window, ct);
        return window;
    }

    public BreakGlassWindow Close(string id, string actor)
    {
        var window = store.CloseBreakGlassWindow(id, actor);
        Reconcile(window);
        return store.ListBreakGlassWindows().First(item => item.Id == id);
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var window in store.ListBreakGlassWindows())
        {
            try
            {
                if (window.AlertedAt is null) await AlertAsync(window, ct);
                var closed = window.ClosedAt is null && window.ExpiresAt <= now
                    ? store.CloseBreakGlassWindow(window.Id, "system:break-glass-expiry") : window;
                if (closed.ClosedAt is not null && closed.ReviewCardId is null) Reconcile(closed);
            }
            catch (Exception exception) { logger.LogError(exception, "Break-glass reconciliation failed for {WindowId}", window.Id); }
        }
    }

    private async Task AlertAsync(BreakGlassWindow window, CancellationToken ct)
    {
        try
        {
            await alerts.RaiseAsync(new ControlPlaneAuditAlert("break-glass-open",
                $"Break-glass window {window.Id} opened for {window.SessionIds.Count} sessions until {window.ExpiresAt:O}.",
                clock.GetUtcNow().UtcDateTime), ct);
            store.MarkBreakGlassAlerted(window.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Break-glass opening alert is pending for {WindowId}", window.Id);
        }
    }

    private void Reconcile(BreakGlassWindow window)
    {
        lock (_reviewGate) ReconcileLocked(window);
    }

    private void ReconcileLocked(BreakGlassWindow window)
    {
        window = store.ListBreakGlassWindows().First(item => item.Id == window.Id);
        if (window.ClosedAt is null || window.ReviewCardId is not null) return;
        var id = "break-glass-review-" + window.Id[4..];
        // If the host stopped after creating the card but before saving its id, find the deterministic card on recovery.
        var existing = cards.Find(id);
        if (existing is null)
        {
            var entries = store.BreakGlassEntries(window.Id);
            var journalPath = Path.Combine(store.SecurityFolder, AccessSecurityStore.BreakGlassJournalFileName);
            var journalLines = File.Exists(journalPath)
                ? BoundedFileRead.ReadTailLines(journalPath, BoundedFileRead.LedgerBytes, out var truncated).ToList()
                : [];
            var journalEntries = journalLines.Select(ControlPlaneAuditLog.Parse)
                .Where(entry => entry is not null && entry.Stream == ControlPlaneAuditStreams.BreakGlass
                    && entry.Details is { } details && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("windowId", out var id)
                    && id.GetString() == window.Id).ToList();
            var journalMatches = journalEntries.Count == entries.Count
                && journalEntries.Select(entry => entry!.Raw).SequenceEqual(entries.Select(entry => entry.Raw));
            var forwarded = store.ControlPlaneAudit.ReadForwardPosition().Sequence;
            var sinkCaughtUp = entries.Count == 0 || forwarded >= entries[^1].Sequence;
            var prompt = new StringBuilder()
                .AppendLine("# Break-glass review")
                .AppendLine()
                .AppendLine($"Window: {window.Id}")
                .AppendLine($"Opened: {window.OpenedAt:O}")
                .AppendLine($"Closed: {window.ClosedAt:O}")
                .AppendLine($"Sessions: {string.Join(", ", window.SessionIds)}")
                .AppendLine($"Host journal: {(journalMatches ? "matches the control plane entries" : "MISMATCH, investigate")}")
                .AppendLine($"Off-host sink: {(sinkCaughtUp ? "forwarded through this window" : "PENDING, verify after recovery")}, acknowledged through #{forwarded}")
                .AppendLine()
                .AppendLine("Reconcile these entries with the host journal and the off-host audit sink:")
                .AppendLine();
            foreach (var entry in entries)
                prompt.AppendLine($"- #{entry.Sequence} {entry.At:O} {entry.Actor} {entry.Operation} "
                    + $"target={entry.Target ?? "none"} digest={entry.ParameterDigest ?? "none"} outcome={entry.Outcome} hash={entry.Hash}");
            prompt.AppendLine().AppendLine("Review the emergency actions, the chain, host journal and sink positions. Record any gap as an incident.");
            var created = cards.Create(window, id, prompt.ToString());
            if (created is null) existing = cards.Find(id);
            else store.SetBreakGlassReviewCard(window.Id, created);
        }
        if (existing is not null) store.SetBreakGlassReviewCard(window.Id, existing);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}

public static class BreakGlassWindowServices
{
    public static IServiceCollection AddBreakGlassWindows(this IServiceCollection services)
    {
        services.AddSingleton<IBreakGlassReviewCardWriter, TaskBreakGlassReviewCardWriter>();
        services.AddSingleton<BreakGlassWindowService>();
        services.AddHostedService(sp => sp.GetRequiredService<BreakGlassWindowService>());
        return services;
    }
}
