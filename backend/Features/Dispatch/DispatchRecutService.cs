using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Dispatch;

/// <summary>Collects facts, journals the pure cut, and applies it exclusively through the task API.</summary>
public sealed class DispatchRecutService(DispatchStore store, TaskScannerService scanner,
    ProjectSettingsService settings, PersonaActivationStore activations, PersonaItemStore items,
    IDispatchRecutDrafting drafting, TimeProvider time, ProjectRegistry? projects = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private static string Key(TaskInfo c) => c.Key ?? c.TaskKey;
    private static bool HasStarted(TaskInfo c) => c.ExecutionLocation?.StartedAt is not null
        || c.SessionName is not null || c.SessionChain.Count > 0 || c.Commits.Count > 0 || c.LastUsage is not null;
    private static bool Unstarted(TaskInfo c) => c.State is TaskStates.Backlog or TaskStates.Ready
        && !HasStarted(c);
    private static bool SameReferences(TaskReferences a, TaskReferences b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    public static string ReceiptId(string project, string wave, bool apply)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project + "\n" + wave + "\n" + apply)))[..24];

    public async Task<DispatchRecutReceipt> PlanAsync(string project, string workspace, string wave,
        DispatchRecutRequest request, IDispatchRecutTaskApi api, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(wave) || wave.Length > 200 || request.Cards is not { Count: > 0 and <= 200 }
            || request.Cards.Any(c => c is null || string.IsNullOrWhiteSpace(c.Key) || c.FindingRefs is null
                || c.FindingRefs.Count > 100 || c.FindingRefs.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 2000))
            || request.Cards.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Cards.Count)
            throw new ArgumentException("Provide one source wave with 1 to 200 distinct cards and their finding references.");
        var gate = _gates.GetOrAdd(project, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var id = ReceiptId(project, wave, request.Apply);
            var receipt = store.ReadRecut(project, id);
            if (receipt is not null)
            {
                if (!receipt.Originals.Select(o => Key(o.Info)).SequenceEqual(request.Cards.Select(c => c.Key), StringComparer.OrdinalIgnoreCase)
                    || !receipt.Originals.SelectMany(o => o.FindingRefs).SequenceEqual(request.Cards.SelectMany(c => c.FindingRefs)))
                    throw new InvalidOperationException("The source wave is already recorded with a different membership.");
                if (receipt.State is "applied" or "proposed" or "undone") return receipt;
                if (receipt.State == "undoing") throw new InvalidOperationException("Finish the recut undo before planning again.");
            }
            else
            {
                var originals = new List<DispatchRecutOriginal>();
                foreach (var member in request.Cards)
                {
                    var detail = scanner.GetJobDetail(member.Key) ?? throw new ArgumentException("Wave card was not found: " + member.Key);
                    if (!detail.Info.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase) || Key(detail.Info) != member.Key)
                        throw new ArgumentException("Wave cards must use canonical keys from the same project.");
                    originals.Add(new(detail.Info, detail.PromptMarkdown ?? "", member.FindingRefs));
                }
                var now = time.GetUtcNow();
                var all = scanner.ScanAllJobsWithArchive().Where(c => c.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)).ToArray();
                var config = settings.Get(project);
                var hubs = config.DispatchHubFiles ?? TouchForecast.DefaultHubFiles;
                var history = all.Where(c => c.State is TaskStates.Completed or TaskStates.Archive)
                    .Select(c => (Kind: MonolithTransitionAdapter.DispatchSourceKind(c.CreationSource),
                        Delivery: store.Read(project, c.TaskKey)?.Deliveries.Where(d => d.RecordedAt < now)
                            .OrderByDescending(d => d.RecordedAt).FirstOrDefault()))
                    .Where(d => d.Delivery is not null).ToArray();
                var hot = store.ReadConflicts(project, now.AddDays(-14), now).SelectMany(c => c.ConflictedFiles)
                    .Except(hubs, StringComparer.Ordinal).Select(TouchForecast.Module).Distinct().ToArray();
                var cards = originals.Select((o, i) =>
                {
                    var c = o.Info;
                    var f = store.Read(project, c.TaskKey);
                    var kind = MonolithTransitionAdapter.DispatchSourceKind(c.CreationSource);
                    var prior = MonolithTransitionAdapter.DispatchSizePrior(history.Where(d => d.Kind == kind)
                        .Select(d => d.Delivery!.RealFiles.Distinct().Count(TouchForecast.IsCode)).ToArray());
                    // Archiving a referenced original would prematurely satisfy someone else's edge.
                    // Such a rewrite needs an owner proposal rather than silently changing its meaning.
                    var referenced = all.Any(other => other.References.DependsOn.Any(d => d.Key.Equals(Key(c), StringComparison.OrdinalIgnoreCase)));
                    var compatible = originals.All(other => Compatible(c, other.Info));
                    return new DispatchRecutCard(Key(c), i, kind, f?.ForecastFiles ?? [], f?.Modules ?? [],
                        Math.Max(1, o.FindingRefs.Count), prior, c.Dossier?.Path, Unstarted(c),
                        c.Fixture || c.Order != 999 || c.Tags.Contains("urgent", StringComparer.OrdinalIgnoreCase)
                            || referenced || !compatible || c.Kind != TaskKinds.Task);
                }).ToArray();
                var facts = new DispatchRecutFacts(project, wave, cards, hubs, hot, request.Apply && Authority(project, workspace));
                var plan = MonolithTransitionAdapter.DispatchRecut(facts);
                receipt = new(id, project, workspace, wave, now, "drafting", facts, plan, originals,
                    new Dictionary<string, DispatchRecutText>(), new Dictionary<string, string>(), [],
                    LeaseFence: facts.LiveAuthority ? store.ReadLease(project)?.Fence : null);
                store.WriteRecut(receipt);
            }
            store.LogRecut(receipt with { State = "planned" });
            if (receipt.State == "drafting")
            {
                // Persist before any model work. A restart of an uncertain call uses the deterministic stub.
                var stubbed = receipt with { State = "drafted", Texts = DispatchRecutDrafting.Stubs(receipt) };
                store.WriteRecut(stubbed);
                receipt = stubbed with { Texts = await drafting.DraftAsync(stubbed, ct) };
                store.WriteRecut(receipt);
            }
            receipt = PublishProposals(receipt);
            if (!receipt.Plan.Actions.Any(a => a.Apply))
            {
                receipt = receipt with { State = "proposed" };
                store.WriteRecut(receipt);
                store.LogRecut(receipt);
                return receipt;
            }
            void Fence()
            {
                if (!Authority(project, workspace) || store.ReadLease(project)?.Fence != receipt.LeaseFence
                    || receipt.UndoUntil <= time.GetUtcNow())
                    throw new InvalidOperationException("Packer's live lease, grants or reversible application window are no longer current.");
            }
            Fence();
            receipt = receipt with { State = "applying", UndoUntil = receipt.UndoUntil ?? time.GetUtcNow().AddHours(24), Error = null };
            store.WriteRecut(receipt);
            try { return await ApplyAsync(receipt, new FencedApi(api, Fence), ct); }
            catch (Exception ex)
            {
                var current = store.ReadRecut(project, id)!;
                store.WriteRecut(current with { Error = ex.GetType().Name + ": recut application interrupted; retry or undo this receipt." });
                store.LogRecut(current with { State = "interrupted" });
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private static bool Compatible(TaskInfo a, TaskInfo b) => a.Agent == b.Agent && a.CliType == b.CliType
        && a.Model == b.Model && a.ThinkingLevel == b.ThinkingLevel && a.Mode == b.Mode
        && a.ModelExplicit == b.ModelExplicit && a.ThinkingLevelExplicit == b.ThinkingLevelExplicit
        && a.NoBranchExpected == b.NoBranchExpected && a.RequiresIntegration == b.RequiresIntegration
        && a.AllowWebAccess == b.AllowWebAccess
        && a.OwnerClientId == b.OwnerClientId && JsonSerializer.Serialize(a.Dossier) == JsonSerializer.Serialize(b.Dossier);

    public DispatchRecutArchivePermit? ArchivePermit(TaskInfo card, string id)
    {
        bool Allows(TaskInfo current)
        {
            if (current.ProjectName != card.ProjectName || Key(current) != Key(card)) return false;
            var r = store.ReadRecut(current.ProjectName, id);
            if (r is null) return false;
            var original = r.Originals.FirstOrDefault(o => Key(o.Info) == Key(current));
            var package = r.Plan.Actions.FirstOrDefault(a => a.Apply && a.Kind == "package" && a.Cards.Contains(Key(current)));
            var replacement = r.Replacements.FirstOrDefault(p => p.Value == Key(current));
            var expectedText = original?.Prompt ?? (replacement.Key is not null ? r.Texts[replacement.Key].Prompt : null);
            var expectedTitle = original?.Info.Title ?? (replacement.Key is not null ? r.Texts[replacement.Key].Title : null);
            var detail = scanner.GetJobDetailFromInfo(current);
            var sameText = expectedText == (detail.PromptMarkdown ?? "") && expectedTitle == current.Title;
            var foreignIncoming = scanner.ScanAllJobsWithArchive().Any(c => c.References.DependsOn.Any(e =>
                e.Key.Equals(Key(current), StringComparison.OrdinalIgnoreCase)
                && !(r.State == "undoing" && e.Origin == "dispatch:packer" && Affected(r).Any(o => Key(o.Info) == Key(c)))));
            return MonolithTransitionAdapter.DispatchRecutArchive(new(current.State == TaskStates.Backlog && Unstarted(current)
                && sameText, r.UndoUntil > time.GetUtcNow(), r.State == "applying", r.State == "undoing",
                original is not null && package is not null, replacement.Key is not null,
                package is not null && r.Replacements.TryGetValue(package.Id, out var target) && current.References.RelatedTo.Contains(target),
                r.State == "applying" && r.LeaseFence == store.ReadLease(r.Project)?.Fence && Authority(r.Project, r.Workspace),
                foreignIncoming, current.Order != 999 || current.Tags.Contains("urgent", StringComparer.OrdinalIgnoreCase)));
        }
        return Allows(card) ? new DispatchRecutArchivePermit(Allows) : null;
    }

    private bool Authority(string project, string workspace)
    {
        var lease = store.ReadLease(project);
        var registered = projects?.FindByIdOrDisplayName(project);
        var a = activations.List(workspace).Personas.Select(p => p.Activation).OfType<PersonaActivationV1>()
            .FirstOrDefault(a => a.PersonaId == "packer" && a.State == PersonaStates.Active
                && (a.Scope.Projects.Contains("*") || a.Scope.Projects.Contains(project)
                    || registered is not null && a.Scope.Projects.Contains(registered.Id)));
        // Like the DS-6 pickup host, the fenced live lease certifies the standing
        // live grants. Activation alone still carries only shadow grants.
        return settings.Get(project).DispatchMode == "live" && a is not null && lease?.Persona == "packer"
            && lease.ExpiresAt > time.GetUtcNow() && DispatcherCapabilities.Admits(new("dispatch.recut.apply", "packer",
                false, true, a.Descriptor.Operations.Contains("dispatch.recut.apply"), Packaging: true, Now: time.GetUtcNow()))
            && a.Descriptor.Operations.Contains("dispatch.dependency.add");
    }

    private DispatchRecutReceipt PublishProposals(DispatchRecutReceipt receipt)
    {
        if (receipt.ProposalIds.Count > 0) return receipt;
        var ids = new List<string>();
        foreach (var action in receipt.Plan.Actions.Where(a => !a.Apply))
        {
            var text = receipt.Texts[action.Id];
            var evidence = action.Cards.Select(c => new PersonaEvidenceV1("card:" + c))
                .Concat(receipt.Originals.Where(o => action.Cards.Contains(Key(o.Info)))
                    .SelectMany(o => o.FindingRefs).Distinct().Select(r => new PersonaEvidenceV1(r))).ToArray();
            var draft = new PersonaItemDraftV1(PersonaItemKinds.Proposal, text.Title,
                "dispatch.recut.propose\n" + text.Prompt, receipt.Project, evidence, 3, 2, 0.7,
                "dispatch.recut:" + receipt.Id + ":" + action.Id, DraftTitle: text.Title, DraftPrompt: text.Prompt);
            ids.Add(items.Upsert(receipt.Workspace, "packer", "dispatcher", receipt.Id, draft, 0,
                time.GetUtcNow().UtcDateTime).Id);
        }
        receipt = receipt with { ProposalIds = ids };
        store.WriteRecut(receipt);
        return receipt;
    }

    internal async Task<DispatchRecutReceipt> ApplyAsync(DispatchRecutReceipt receipt, IDispatchRecutTaskApi api, CancellationToken ct)
    {
        var affected = Affected(receipt);
        // Move Ready members to Backlog through the claim-gated CAS route before creating or archiving.
        // The original lane is in the journal and is restored for unmerged chain members and undo.
        foreach (var original in affected)
        {
            var detail = await api.ReadAsync(Key(original.Info), ct) ?? throw new InvalidOperationException("Recut member disappeared.");
            var current = detail.Info;
            if (current.Title != original.Info.Title || (detail.PromptMarkdown ?? "") != original.Prompt
                || current.CreationSource != original.Info.CreationSource || current.Order != original.Info.Order
                || JsonSerializer.Serialize(current.AcceptanceScope) != JsonSerializer.Serialize(original.Info.AcceptanceScope)
                || !current.Tags.SequenceEqual(original.Info.Tags)
                || !Compatible(current, original.Info) || HasStarted(current))
                throw new InvalidOperationException("Recut member changed after the plan was frozen.");
            if (current.State == TaskStates.Ready)
                await api.MoveAsync(Key(current), TaskStates.Ready, TaskStates.Backlog, ct);
            else if (current.State is not (TaskStates.Backlog or TaskStates.Archive))
                throw new InvalidOperationException("A recut member has started or left its staged lane.");
            if (current.State == TaskStates.Archive && !receipt.Plan.Actions.Any(p => p.Apply && p.Kind == "package"
                && p.Cards.Contains(Key(current)) && receipt.Replacements.TryGetValue(p.Id, out var replacement)
                && current.References.RelatedTo.Contains(replacement)))
                throw new InvalidOperationException("The original was archived outside this recut.");
        }
        var replacements = receipt.Replacements.ToDictionary();
        foreach (var package in receipt.Plan.Actions.Where(a => a.Apply && a.Kind == "package"))
        {
            if (replacements.ContainsKey(package.Id)) continue;
            var original = receipt.Originals.Single(o => Key(o.Info) == package.Cards[0]);
            var members = receipt.Originals.Where(o => package.Cards.Contains(Key(o.Info))).ToArray();
            var text = receipt.Texts[package.Id];
            var created = await api.CreateAsync(new()
            {
                Id = "packer-" + receipt.Id + "-" + replacements.Count, Title = text.Title,
                PromptMarkdown = text.Prompt, Project = receipt.Project, TargetState = TaskStates.Backlog,
                Agent = original.Info.Agent, CliType = original.Info.CliType, Mode = original.Info.Mode,
                NoBranchExpected = original.Info.NoBranchExpected, RequiresIntegration = original.Info.RequiresIntegration,
                AllowWebAccess = original.Info.AllowWebAccess, TaskType = original.Info.TaskType,
                Tags = package.Cards.SelectMany(k => receipt.Originals.Single(o => Key(o.Info) == k).Info.Tags).Distinct().ToList(),
                Model = original.Info.Model, ThinkingLevel = original.Info.ThinkingLevel,
                ModelExplicit = original.Info.ModelExplicit, ThinkingLevelExplicit = original.Info.ThinkingLevelExplicit,
                AcceptanceScope = members.Any(o => o.Info.AcceptanceScope is not null)
                    ? TaskAcceptanceScopes.BoundedSlice("Packer package " + string.Join(", ", package.Cards),
                        members.SelectMany(o => o.Info.AcceptanceScope is { DeliveryMode: TaskAcceptanceDeliveryModes.BoundedSlice } scope
                            ? scope.Criteria.Select(c => Key(o.Info) + ": " + c)
                            : [Key(o.Info) + ": Complete the original work reproduced in this prompt."]).ToArray()) : null,
                OwnerClientId = original.Info.OwnerClientId, Dossier = original.Info.Dossier,
                CreationSource = original.Info.CreationSource + ":dispatch:packer:" + receipt.Id, CreatedBy = "persona:packer",
            }, ct);
            replacements[package.Id] = Key(created);
            receipt = receipt with { Replacements = new Dictionary<string, string>(replacements) };
            store.WriteRecut(receipt);
        }
        foreach (var (key, references) in ExpectedReferences(receipt))
        {
            var card = await RequireAsync(api, key, ct);
            if (SameReferences(card.References, references)) continue;
            var before = receipt.Originals.FirstOrDefault(o => Key(o.Info) == key)?.Info.References ?? new();
            if (!SameReferences(card.References, before)) throw new InvalidOperationException("Recut references changed; preserve the owner's edit.");
            await api.ReferencesAsync(key, references, ct, before);
        }
        foreach (var original in affected)
        {
            var key = Key(original.Info);
            var current = await RequireAsync(api, key, ct);
            var merged = receipt.Plan.Actions.Any(a => a.Apply && a.Kind == "package" && a.Cards.Contains(key));
            var target = merged ? TaskStates.Archive : original.Info.State;
            if (current.State != target) await api.MoveAsync(key, current.State, target, ct);
        }
        receipt = receipt with { State = "applied", Error = null };
        store.WriteRecut(receipt);
        store.LogRecut(receipt);
        return receipt;
    }

    private static DispatchRecutOriginal[] Affected(DispatchRecutReceipt receipt)
        => receipt.Originals.Where(o => receipt.Plan.Actions.Any(a => a.Apply && a.Cards.Contains(Key(o.Info)))).ToArray();

    private static Dictionary<string, TaskReferences> ExpectedReferences(DispatchRecutReceipt r)
    {
        var refs = Affected(r).ToDictionary(o => Key(o.Info), o => o.Info.References);
        foreach (var p in r.Plan.Actions.Where(a => a.Apply && a.Kind == "package"))
        {
            var key = r.Replacements[p.Id];
            refs[key] = new TaskReferences
            {
                DependsOn = TaskReferenceValidator.NormalizeDependencies(p.Cards.SelectMany(k => refs[k].DependsOn)),
                RelatedTo = p.Cards.Concat(p.Cards.SelectMany(k => refs[k].RelatedTo)).Distinct().ToList(),
                Supersedes = p.Cards.ToList(), Workbenches = p.Cards.SelectMany(k => refs[k].Workbenches).Distinct().ToList(),
                BlockedBy = p.Cards.SelectMany(k => refs[k].BlockedBy).Distinct().ToList(),
                FollowUpOf = p.Cards.SelectMany(k => refs[k].FollowUpOf).Distinct().ToList(),
                RaisedFollowUps = p.Cards.SelectMany(k => refs[k].RaisedFollowUps).Distinct().ToList(),
            };
            foreach (var original in p.Cards)
                refs[original] = refs[original] with { RelatedTo = refs[original].RelatedTo.Append(key).Distinct().ToList() };
        }
        var chains = r.Plan.Actions.Where(a => a.Apply && a.Kind == "chain").ToArray();
        foreach (var edge in r.Plan.Edges.Where(e => chains.Any(a => a.Cards.Contains(e.Card)
            || r.Plan.Actions.Any(p => p.Id == e.Card && p.Cards.Any(a.Cards.Contains)))))
        {
            var key = r.Replacements.GetValueOrDefault(edge.Card, edge.Card);
            var target = r.Replacements.GetValueOrDefault(edge.DependsOn, edge.DependsOn);
            refs[key] = refs[key] with { DependsOn = TaskReferenceValidator.NormalizeDependencies(
                refs[key].DependsOn.Append(new TaskDependencyReference(target, origin: edge.Origin))) };
        }
        return refs;
    }

    private static async Task<TaskInfo> RequireAsync(IDispatchRecutTaskApi api, string key, CancellationToken ct)
        => (await api.ReadAsync(key, ct))?.Info ?? throw new InvalidOperationException("Recut card is unavailable: " + key);

    public async Task<DispatchRecutReceipt> UndoAsync(string project, string id, IDispatchRecutTaskApi api, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(project, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var r = store.ReadRecut(project, id) ?? throw new KeyNotFoundException("Recut receipt was not found.");
            if (r.State == "undone") return r;
            if (!MonolithTransitionAdapter.DispatchRecutUndo(r.State, r.UndoUntil, time.GetUtcNow()))
                throw new InvalidOperationException("The 24-hour recut undo is unavailable.");
            // Resolve a create that completed immediately before a host crash.
            var replacements = r.Replacements.ToDictionary();
            foreach (var p in r.Plan.Actions.Where(a => a.Apply && a.Kind == "package"))
                if (!replacements.ContainsKey(p.Id)
                    && await api.ReadAsync("packer-" + r.Id + "-" + replacements.Count, ct) is { } found)
                    replacements[p.Id] = Key(found.Info);
            r = r with { Replacements = replacements };
            var expected = r.Plan.Actions.Where(a => a.Apply && a.Kind == "package").All(a => replacements.ContainsKey(a.Id))
                ? ExpectedReferences(r) : Affected(r).ToDictionary(o => Key(o.Info), o => o.Info.References);
            // Partial application can leave replacements outside the expected-reference map.
            // Fence writes with the actual snapshot validated for every original and replacement.
            var validatedReferences = new Dictionary<string, TaskReferences>();
            foreach (var key in expected.Keys.Concat(replacements.Values).Distinct())
            {
                var detail = await api.ReadAsync(key, ct) ?? throw new InvalidOperationException("Undo card disappeared.");
                var card = detail.Info;
                var original = r.Originals.FirstOrDefault(o => Key(o.Info) == key);
                var package = r.Plan.Actions.FirstOrDefault(a => a.Kind == "package" && replacements.GetValueOrDefault(a.Id) == key);
                if (original is not null && (card.Order != original.Info.Order || !card.Tags.SequenceEqual(original.Info.Tags))
                    || package is not null && (card.Order != 999 || card.Tags.Contains("urgent", StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Undo would move a later owner pin or priority edit.");
                if (original is not null && ((detail.PromptMarkdown ?? "") != original.Prompt || card.Title != original.Info.Title)
                    || package is not null && (detail.PromptMarkdown != r.Texts[package.Id].Prompt || card.Title != r.Texts[package.Id].Title))
                    throw new InvalidOperationException("Undo would discard a later title or prompt edit.");
                if (HasStarted(card)
                    || card.State is not (TaskStates.Backlog or TaskStates.Ready or TaskStates.Archive)
                    || !SameReferences(card.References, expected.GetValueOrDefault(key) ?? new())
                        && !SameReferences(card.References, original?.Info.References ?? new()))
                    throw new InvalidOperationException("Undo would overwrite a started card or a later reference edit.");
                validatedReferences[key] = card.References;
            }
            r = r with { State = "undoing" };
            store.WriteRecut(r);
            foreach (var o in Affected(r))
            {
                var c = await RequireAsync(api, Key(o.Info), ct);
                if (c.State == TaskStates.Ready) await api.MoveAsync(Key(c), TaskStates.Ready, TaskStates.Backlog, ct);
            }
            foreach (var key in replacements.Values)
            {
                var c = await RequireAsync(api, key, ct);
                if (c.State == TaskStates.Ready)
                {
                    await api.MoveAsync(key, TaskStates.Ready, TaskStates.Backlog, ct);
                    c = await RequireAsync(api, key, ct);
                }
                if (c.State != TaskStates.Archive) await api.MoveAsync(key, c.State, TaskStates.Archive, ct);
                c = await RequireAsync(api, key, ct);
                if (!c.References.IsEmpty) await api.ReferencesAsync(key, new(), ct, validatedReferences[key]);
            }
            foreach (var o in Affected(r))
            {
                var key = Key(o.Info);
                var before = await RequireAsync(api, key, ct);
                if (!SameReferences(before.References, o.Info.References))
                    await api.ReferencesAsync(key, o.Info.References, ct, validatedReferences[key]);
                var c = await RequireAsync(api, key, ct);
                if (c.State != o.Info.State) await api.MoveAsync(key, c.State, o.Info.State, ct);
            }
            r = r with { State = "undone", Error = null };
            store.WriteRecut(r);
            store.LogRecut(r);
            return r;
        }
        finally { gate.Release(); }
    }

    private sealed class FencedApi(IDispatchRecutTaskApi inner, Action fence) : IDispatchRecutTaskApi
    {
        public Task<TaskDetail?> ReadAsync(string key, CancellationToken ct) => inner.ReadAsync(key, ct);
        public Task<TaskInfo> CreateAsync(CreateTaskRequest request, CancellationToken ct)
        { fence(); return inner.CreateAsync(request, ct); }
        public Task ReferencesAsync(string key, TaskReferences references, CancellationToken ct, TaskReferences? expected = null)
        { fence(); return inner.ReferencesAsync(key, references, ct, expected); }
        public Task MoveAsync(string key, string from, string to, CancellationToken ct)
        { fence(); return inner.MoveAsync(key, from, to, ct); }
    }
}
