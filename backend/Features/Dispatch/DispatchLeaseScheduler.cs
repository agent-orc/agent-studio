using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Bus;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Tasks;

namespace AgentStudio.Dispatch;

/// <summary>Collects competition evidence, applies fenced policy receipts and recovers guard feed publication.</summary>
public sealed class DispatchLeaseScheduler(DispatchStore store, DispatchReviewService review,
    TaskScannerService scanner, ProjectRegistry projects, ProjectSettingsService settings,
    PersonaActivationStore activations, TimelineLog timeline, PersonaItemStore items,
    AgentMessageBusBridge bus, TimeProvider time, ILogger<DispatchLeaseScheduler> logger,
    AiOperatorDecisionGateway decisions) : BackgroundService, IAiOperatorEffect
{
    public string Decider => AiOperatorDeciders.DispatchLease;
    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Window, long Fence, HashSet<string> Scoped)> _refreshed = new(StringComparer.OrdinalIgnoreCase);

    public DispatchLeaseV1? Refresh(string project, string? wave = null, string? action = null,
        string? persona = null, long? expectedFence = null, string? expectedGuard = null, string actor = "platform")
    {
        var registered = projects.FindByIdOrDisplayName(project) ?? throw new ArgumentException("Project was not found.");
        project = registered.DisplayName;
        lock (_gates.GetOrAdd(project, _ => new object()))
        {
            var now = time.GetUtcNow();
            var old = store.ReadLease(project);
            if (action is not null && wave is null && old?.Window?.StartsWith("wave:", StringComparison.Ordinal) == true)
                wave = old.Window[5..];
            if (action is not null && old?.Fence != expectedFence) throw new InvalidOperationException("The lease changed. Reload before applying the control.");
            if (settings.Get(project).DispatchMode != "live")
            {
                if (action is not null) throw new InvalidOperationException("Live dispatch must be enabled for owner controls.");
                return old;
            }
            var window = DispatchLeaseControls.Window(wave, now);
            var watchPath = scanner.GetWatchPaths().First(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase)).Path;
            var scoped = ScopedPlanners(registered.WorkspaceId, registered.Id, project, old);
            if (action is null && _refreshed.TryGetValue(project, out var cached)
                && cached.Window == window && cached.Scoped.SetEquals(scoped) && now - cached.At < TimeSpan.FromMinutes(1)
                && old?.Rollout is not null && old.Fence == cached.Fence && old.ExpiresAt > now)
                return old;
            var state = old?.Rollout ?? new DispatchLeaseState(Champion: old?.Persona ?? "queue-keeper");
            var projection = review.Review(project, registered.WorkspaceId, watchPath, null, null);
            var candidates = projection.Competition.Select(c => new DispatchChallenger(c.Challenger,
                projection.Personas.First(r => r.Raw.Persona == c.Challenger).Raw.Strategy, c.Facts)).ToArray();
            var available = state.AvailablePlanners(candidates, scoped);
            var cards = scanner.ScanAllJobsWithArchive().Where(c => !c.Fixture
                && c.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)).ToArray();
            var integrated = cards.Select(c => (Card: c, At: DispatchReviewService.IntegratedAt(c, timeline.ReadAll(c.FolderPath))))
                .Where(c => c.At is not null && c.At <= now).ToDictionary(c => c.Card.Key ?? c.Card.TaskKey, c => c.At!.Value);
            var plans = store.ReadShadow(project).ToDictionary(r => r.DecisionId);
            var admissions = store.ReadRunDecisions(project).Where(r => r.Admitted && r.Claimed)
                .GroupBy(r => r.Card).Select(g => g.OrderByDescending(r => r.At).First()).ToArray();
            var guard = Guard(project, registered.WorkspaceId, watchPath, old?.Persona, integrated, plans, admissions, now);
            var results = WaveResults(project, registered.WorkspaceId, watchPath, state, cards,
                integrated, plans, admissions, now);
            store.WriteCompetitionEvidence(project, projection.From, projection.To, projection.Competition.Select(c =>
                new DispatchCompetitionEvidence(c.Challenger, c.Facts.ShadowIntegratedCards, c.Facts.ShadowWaves,
                    results.Count(r => r.Complete && r.Persona == c.Challenger), false,
                    guard.Cards, guard.LiveConflictsPer100, guard.BaselineShadowConflictsPer100, guard.SameCards)).ToArray());
            var facts = new DispatchLeaseFacts(now, wave, old?.Persona, candidates, available.ToArray(), results, guard,
                action, persona, expectedGuard);
            DispatchLeaseDecision decision;
            if (action is not null) decision = DispatchLeaseControls.Decide(state, facts);
            else
            {
                var occurrence = new DispatchLeaseOccurrence(state, facts, old?.Fence ?? 0, old?.ExpiresAt);
                var payload = new AiOperatorDecisionPayload(Decider,
                    AiOperatorDecisionGateway.FactsSubjectKey(Decider, project, occurrence), DispatchLease: occurrence);
                var preview = decisions.Preview(payload);
                if (preview.Disposition == AiOperatorDisposition.Hold)
                {
                    _refreshed[project] = (now, window, old!.Fence, scoped);
                    PublishGuard(registered.WorkspaceId, project, old!);
                    return old;
                }
                var submission = decisions.Submit(new(project, "dispatch:" + registered.Id, "dispatch-lease", payload, null,
                    AuthorityFence: old?.Fence ?? 0));
                if (submission.WaitsForEngine) return null;
                decision = submission.MonolithDecision!.DispatchLease!;
            }
            if (!decision.Allowed) throw new InvalidOperationException(decision.Reason);
            var lease = store.ApplyLeaseDecision(project, old?.Fence ?? 0, decision, now, actor);
            if (lease is null) throw new InvalidOperationException("The lease changed while collecting evidence. Retry the operation.");
            _refreshed[project] = (now, window, lease.Fence, scoped);
            var evidence = projection.Competition.Select(c => new DispatchCompetitionEvidence(c.Challenger,
                c.Facts.ShadowIntegratedCards, c.Facts.ShadowWaves,
                results.Count(r => r.Complete && r.Persona == c.Challenger),
                c.Challenger == state.Challenger && decision.State.Champion == c.Challenger,
                guard.Cards, guard.LiveConflictsPer100, guard.BaselineShadowConflictsPer100, guard.SameCards)).ToArray();
            store.WriteCompetitionEvidence(project, projection.From, projection.To, evidence);
            PublishGuard(registered.WorkspaceId, project, lease);
            return lease;
        }
    }

    private DispatchGuardSample Guard(string project, string workspace, string path, string? persona,
        IReadOnlyDictionary<string, DateTimeOffset> integrated, IReadOnlyDictionary<string, DispatchShadowRecordV1> plans,
        IReadOnlyList<DispatchRunDecisionV1> admissions, DateTimeOffset now)
    {
        var last = admissions.Where(a => integrated.TryGetValue(a.Card, out var at) && at >= a.At
                && plans.TryGetValue(a.DecisionId, out var p) && p.Plan.Persona == persona)
            .OrderByDescending(a => integrated[a.Card]).ThenBy(a => a.Card, StringComparer.Ordinal).Take(20).ToArray();
        if (last.Length != 20 || persona is null or "queue-keeper") return new("", last.Length, null, null, false);
        var keys = last.Select(a => a.Card).ToHashSet(StringComparer.Ordinal);
        var from = last.Min(a => a.At).AddTicks(-1);
        var baseline = review.Cohort(project, workspace, path, from, now.AddTicks(1), keys)
            .Personas.First(r => r.Raw.Persona == "queue-keeper").Raw;
        var same = baseline.Cards.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal))
            && review.ShadowEvidence(project, "queue-keeper", baseline.Cards, DateTimeOffset.MinValue, now.AddTicks(1)).Cards == 20;
        var conflicts = store.ReadConflicts(project, to: now.AddTicks(1)).Select(c => store.ConflictAdmission(project, c))
            .OfType<DispatchRunDecisionV1>().Where(a => last.Any(l => l.Card == a.Card && l.DecisionId == a.DecisionId))
            .Select(a => a.Card).Distinct().Order(StringComparer.Ordinal).ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', last.Select(a => a.DecisionId + ":" + a.Card).Order(StringComparer.Ordinal)) + "\n" + string.Join('\n', conflicts))));
        return new(digest, 20, 100.0 * conflicts.Length / 20, baseline.Criteria.GetValueOrDefault("conflicts"), same);
    }

    private IReadOnlyList<DispatchLiveWaveResult> WaveResults(string project, string workspace, string path,
        DispatchLeaseState state, IReadOnlyList<TaskInfo> cards, IReadOnlyDictionary<string, DateTimeOffset> integrated,
        IReadOnlyDictionary<string, DispatchShadowRecordV1> plans, IReadOnlyList<DispatchRunDecisionV1> admissions, DateTimeOffset now)
    {
        var results = new List<DispatchLiveWaveResult>();
        foreach (var window in state.Windows.Where(w => w.Epoch == state.Epoch))
        {
            var own = admissions.Where(a => plans.TryGetValue(a.DecisionId, out var p)
                && p.Window == window.Key && p.Plan.Persona == window.Persona).ToArray();
            var members = window.Key.StartsWith("wave:", StringComparison.Ordinal)
                ? cards.Where(c => c.WaveId == window.Key[5..]).Select(c => c.Key ?? c.TaskKey).ToArray()
                : own.Select(a => a.Card).ToArray();
            var closed = window.Key.StartsWith("wave:", StringComparison.Ordinal)
                || window.Key != DispatchLeaseControls.Window(null, now);
            var complete = closed && members.Length > 0 && members.All(integrated.ContainsKey)
                && members.All(key => own.Any(a => a.Card == key && integrated[key] >= a.At));
            double? value = null;
            if (complete)
            {
                var scored = review.Cohort(project, workspace, path, window.OpenedAt.AddTicks(-1), now.AddTicks(1),
                    members.ToHashSet(StringComparer.Ordinal), $"{window.Epoch}:{window.Key}");
                value = scored.Personas.FirstOrDefault(r => r.Raw.Persona == window.Persona)?.Value;
            }
            results.Add(new(window.Key, window.Epoch, window.Persona, value, complete));
        }
        return results;
    }

    private HashSet<string> ScopedPlanners(string workspace, string projectId, string project, DispatchLeaseV1? lease)
    {
        // Generic persona grants contain only shadow operations. A certified lease keeps
        // its project-specific plan grant across a temporary pause of the assigned holder.
        var assigned = new[] { lease?.Persona, lease?.Rollout?.Champion, lease?.Rollout?.Challenger, lease?.Rollout?.Pinned };
        return activations.List(workspace).Personas.Select(p => p.Activation).OfType<PersonaActivationV1>()
            .Where(a => a.Descriptor.Kind == PersonaKinds.Dispatcher && a.State == PersonaStates.Active
                && (a.Scope.Projects.Contains("*") || a.Scope.Projects.Contains(projectId) || a.Scope.Projects.Contains(project))
                && (a.Operations.Contains("dispatch.plan.write") || assigned.Contains(a.PersonaId)
                    && a.Operations.Contains("dispatch.shadow.record") && a.Descriptor.Operations.Contains("dispatch.plan.write")))
            .Select(a => a.PersonaId).ToHashSet(StringComparer.Ordinal);
    }

    public Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        var occurrence = application.Payload.DispatchLease;
        var decision = application.Decision.DispatchLease;
        if (occurrence is null || decision is null) throw new InvalidDataException("Dispatch lease receipt is missing.");
        var registered = projects.FindByIdOrDisplayName(application.ProjectId);
        if (registered is null || settings.Get(registered.DisplayName).DispatchMode != "live") return Task.CompletedTask;
        lock (_gates.GetOrAdd(registered.DisplayName, _ => new object()))
        {
            var scoped = ScopedPlanners(registered.WorkspaceId, registered.Id,
                registered.DisplayName, store.ReadLease(registered.DisplayName));
            if (!decision.CanApplyWith(occurrence, scoped))
                return Task.CompletedTask;
            var lease = store.ApplyLeaseDecision(registered.DisplayName, occurrence.ExpectedFence, decision, time.GetUtcNow(), "platform");
            if (lease is not null) PublishGuard(registered.WorkspaceId, registered.DisplayName, lease);
        }
        return Task.CompletedTask;
    }

    private void PublishGuard(string workspace, string project, DispatchLeaseV1 lease)
    {
        if (lease.Rollout?.Guard is not { } guard) return;
        if (items.List(workspace).Any(i => i.Evidence.Any(e => e.Ref == guard.Id))) return;
        var summary = $"Dispatch guard returned {project} to Queue keeper.";
        items.Upsert(workspace, "queue-keeper", "dispatcher", guard.Id,
            new(PersonaItemKinds.Finding, summary,
                $"The live conflict rate exceeded the same-card baseline by at least 10 per 100 over 20 integrated cards. T1 platform action. Owner can undo guard {guard.Id} on Dispatch review.",
                project, [new(guard.Id, true)], 4, 4, 1, guard.Id), 70, guard.At.UtcDateTime);
        _ = bus.EmitWatcherEventAsync("action", "dispatch-guard-fallback", project, null, summary, "Warn",
            new { guard.Id, guard.Persona, importance = 70, authorityTier = "T1", ownerUndo = true }, guard.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            foreach (var project in scanner.GetWatchPaths())
            {
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    // A sweep observes safety and settled waves without opening a synthetic day window for wave-only projects.
                    var wave = store.ReadLease(project.Name)?.Window;
                    Refresh(project.Name, wave?.StartsWith("wave:", StringComparison.Ordinal) == true ? wave[5..] : null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
                { logger.LogWarning(ex, "dispatch-lease-refresh-failed project={Project}", project.Name); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
