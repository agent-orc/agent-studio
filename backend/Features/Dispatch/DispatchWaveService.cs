using System.Collections.Concurrent;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Dispatch;

public sealed record DispatchWaveResult(string WaveId, string State, string? DecisionId, IReadOnlyList<string> Released);

/// <summary>Collects a completed source wave, records its plan, and applies lane moves through the task API.</summary>
public sealed class DispatchWaveService(TaskScannerService scanner, ProjectSettingsService settings,
    DispatchStore store, DispatcherShadowService dispatcher, DispatchRecutService recuts,
    ProjectRegistry projects, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DispatchWaveResult> PlanAsync(string project, string wave, IDispatchRecutTaskApi api, CancellationToken ct)
    {
        var registered = projects.FindByIdOrDisplayName(project) ?? throw new ArgumentException("Project was not found.");
        var gate = _gates.GetOrAdd(registered.Id + ":" + wave, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { return await PlanCoreAsync(registered.Id, wave, api, ct); }
        finally { gate.Release(); }
    }

    private async Task<DispatchWaveResult> PlanCoreAsync(string project, string wave, IDispatchRecutTaskApi api, CancellationToken ct)
    {
        var registered = projects.FindByIdOrDisplayName(project) ?? throw new ArgumentException("Project was not found.");
        project = registered.DisplayName;
        var mode = settings.Get(project).DispatchMode;
        var membersOfWave = scanner.ScanAllJobsWithArchive().Where(c => c.ProjectName == project && c.WaveId == wave).ToArray();
        var cards = membersOfWave.Where(c => c.State is TaskStates.Backlog or TaskStates.Ready)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
        if (cards.Length == 0) throw new ArgumentException("No unstarted source-wave cards were found.");
        if (membersOfWave.Any(c => c.ExecutionLocation?.StartedAt is not null || c.SessionChain.Count > 0 || c.Commits.Count > 0))
            throw new InvalidOperationException("A source-wave card has already started.");
        if (mode == "off") return new(wave, "off", null, []);
        // Do not plan with empty stand-ins while the shared forecast worker catches up.
        if (cards.Any(c => store.Read(project, c.TaskKey) is null))
            return new(wave, "awaiting-forecasts", null, []);
        dispatcher.RefreshLease(project, wave);
        var lease = store.ReadLease(project);
        if (mode == "live" && (lease is null || lease.ExpiresAt <= time.GetUtcNow()))
            return new(wave, "awaiting-live-dispatcher", null, []);
        if (lease?.Persona == "packer")
        {
            var prior = store.ReadRecut(project, DispatchRecutService.ReceiptId(project, wave, mode == "live"));
            var members = prior?.Originals.Select(o => new DispatchWaveCard(o.Info.Key ?? o.Info.TaskKey, o.FindingRefs)).ToArray()
                ?? cards.Select(c => new DispatchWaveCard(c.Key ?? c.TaskKey, [c.Key ?? c.TaskKey])).ToArray();
            var receipt = await recuts.PlanAsync(project, registered.WorkspaceId, wave, new(members, mode == "live"), api, ct);
            if (receipt.State is not ("applied" or "proposed")) return new(wave, "awaiting-recut", null, []);
            cards = scanner.ScanAllJobsWithArchive().Where(c => c.ProjectName == project && c.WaveId == wave
                && c.State is TaskStates.Backlog or TaskStates.Ready).ToArray();
        }
        if (cards.Length == 0 || cards.Any(c => store.Read(project, c.TaskKey) is null))
            return new(wave, "awaiting-forecasts", null, []);
        var record = dispatcher.RecordPick(cards[0], [], [], "source-wave", planningWave: wave);
        if (mode == "shadow") return new(wave, "shadow", null, []);
        var live = store.ReadLive(project);
        var current = store.ReadLease(project);
        var planned = record is not null && cards.All(c => record.Plan.Order.Any(e => e.CardKey == (c.Key ?? c.TaskKey)));
        if (!MonolithTransitionAdapter.DispatchWaveRelease(mode, record is not null && current?.Fence == live?.Fence
                && current?.Fence == lease?.Fence && current?.ExpiresAt > time.GetUtcNow(), planned, true))
            return new(wave, "awaiting-live-dispatcher", record?.DecisionId, []);
        var release = new DispatchWaveReleaseV1(record!.DecisionId, record.Plan.Persona, lease!.Fence,
            cards.Select(c => c.Key ?? c.TaskKey).ToArray(), false);
        if (!store.WriteWaveRelease(project, wave, release, time.GetUtcNow()))
            return new(wave, "interrupted", record.DecisionId, []);
        var released = new List<string>();
        // Newest lane entry sorts first. Release in reverse so the stored order survives the next pickup plan.
        foreach (var key in record.Plan.Order.OrderByDescending(e => e.Position).Select(e => e.CardKey).Where(key => release.Cards.Contains(key)))
        {
            // A lease or mode change during HTTP work stops the remainder in Backlog.
            current = store.ReadLease(project);
            if (!MonolithTransitionAdapter.DispatchWaveRelease(settings.Get(project).DispatchMode,
                    current?.Fence == lease.Fence && current?.ExpiresAt > time.GetUtcNow(), true, true))
                return new(wave, "interrupted", record.DecisionId, released);
            var card = await api.ReadAsync(key, ct) ?? throw new InvalidOperationException("A wave card disappeared.");
            if (card.Info.State == TaskStates.Ready) continue;
            if (card.Info.State != TaskStates.Backlog || card.Info.WaveId != wave)
                throw new InvalidOperationException("A wave card changed after planning.");
            await api.MoveAsync(key, TaskStates.Backlog, TaskStates.Ready, ct);
            released.Add(key);
        }
        var complete = store.WriteWaveRelease(project, wave, release with { Complete = true }, time.GetUtcNow());
        return new(wave, complete ? "planned" : "interrupted", record.DecisionId, released);
    }
}
