using System.Text.Json;
using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DispatchRecutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-recut-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private DispatchStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Dispatch:StoragePath"] = _root }).Build());
    private DispatchRecutService Service(DispatchStore store, PersonaItemStore? items = null)
        => new(store, null!, null!, null!, items ?? new PersonaItemStore((string?)null), null!, _clock);

    private DispatchRecutReceipt Receipt(bool live = true)
    {
        var originals = Enumerable.Range(0, 4).Select(i => new DispatchRecutOriginal(new TaskInfo
        {
            Id = "slug-" + i, Key = "P-" + i, Title = "Finding " + i, Agent = "codex", CliType = "codex",
            ProjectName = "p", State = TaskStates.Ready, CreationSource = "playthrough",
            References = new() { DependsOn = [new("EXTERNAL", releaseGate: true, origin: "owner")], RelatedTo = ["SOURCE"] },
        }, "Implement finding " + i + " with its original acceptance criteria.", ["finding:" + i])).ToArray();
        var facts = new DispatchRecutFacts("p", "w", originals.Select((o, i) => new DispatchRecutCard(o.Info.Key!, i,
            "playthrough", ["a.cs", "b.cs"], ["m"], 1, 8, null, true)).ToArray(), [], [], live);
        var r = new DispatchRecutReceipt("receipt", "p", "workspace", "w", _clock.GetUtcNow(), "applying", facts,
            DispatchRecutPolicy.Plan(facts), originals, new Dictionary<string, DispatchRecutText>(),
            new Dictionary<string, string>(), [], _clock.GetUtcNow().AddHours(24));
        return r with { Texts = DispatchRecutDrafting.Stubs(r) };
    }

    [Fact]
    public async Task Apply_and_restart_undo_preserve_original_prompts_links_lanes_and_owner_edges()
    {
        var r = Receipt();
        var api = new MemoryApi(r.Originals);
        var store = Store();
        store.WriteRecut(r);
        var applied = await Service(store).ApplyAsync(r, api, default);
        Assert.Equal("applied", applied.State);
        var replacement = Assert.Single(applied.Replacements).Value;
        Assert.Equal(1, api.Creates);
        Assert.Equal(TaskStates.Backlog, api.Cards[replacement].Info.State);
        Assert.StartsWith("playthrough:dispatch:packer:", api.Cards[replacement].Info.CreationSource);
        Assert.All(r.Originals.Take(3), o =>
        {
            var c = api.Cards[o.Info.Key!];
            Assert.Equal(TaskStates.Archive, c.Info.State);
            Assert.Contains(replacement, c.Info.References.RelatedTo);
            Assert.Equal(o.Prompt, c.PromptMarkdown);
            Assert.Contains(o.Info.Key!, api.Cards[replacement].Info.References.Supersedes);
            Assert.Contains(o.Prompt, api.Cards[replacement].PromptMarkdown);
            Assert.Contains(o.FindingRefs[0], api.Cards[replacement].PromptMarkdown);
        });
        var remaining = api.Cards["P-3"].Info;
        Assert.Equal(TaskStates.Ready, remaining.State);
        Assert.Contains(remaining.References.DependsOn, e => e.Key == replacement && e.Origin == "dispatch:packer");
        Assert.Contains(remaining.References.DependsOn, e => e.Key == "EXTERNAL" && e.ReleaseGate && e.Origin == "owner");
        var undone = await Service(Store()).UndoAsync("p", r.Id, api, default);
        Assert.Equal("undone", undone.State);
        foreach (var o in r.Originals)
        {
            var c = api.Cards[o.Info.Key!];
            Assert.Equal(o.Info.State, c.Info.State);
            Assert.Equal(JsonSerializer.Serialize(o.Info.References), JsonSerializer.Serialize(c.Info.References));
            Assert.Equal(o.Prompt, c.PromptMarkdown);
        }
        Assert.Equal(TaskStates.Archive, api.Cards[replacement].Info.State);
        Assert.True(api.Cards[replacement].Info.References.IsEmpty);
        Assert.Equal("undone", (await Service(Store()).UndoAsync("p", r.Id, api, default)).State);
    }

    [Fact]
    public async Task Interrupted_application_resumes_without_duplicate_cards_and_can_be_undone()
    {
        var r = Receipt();
        var api = new MemoryApi(r.Originals) { FailReferencesOnce = true };
        Store().WriteRecut(r);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(Store()).ApplyAsync(r, api, default));
        var interrupted = Store().ReadRecut("p", r.Id)!;
        Assert.Single(interrupted.Replacements);
        var recovered = await Service(Store()).ApplyAsync(interrupted, api, default);
        Assert.Equal(1, api.Creates);
        Assert.Equal("applied", recovered.State);
        Assert.Equal("undone", (await Service(Store()).UndoAsync("p", r.Id, api, default)).State);
    }

    [Fact]
    public async Task Changed_prompt_and_started_cards_refuse_application_before_archiving()
    {
        foreach (var started in new[] { false, true })
        {
            var r = Receipt();
            var api = new MemoryApi(r.Originals);
            api.Cards["P-0"] = started ? api.Cards["P-0"] with { Info = api.Cards["P-0"].Info with { SessionName = "started" } }
                : api.Cards["P-0"] with { PromptMarkdown = "Owner changed the acceptance criteria" };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(Store()).ApplyAsync(r, api, default));
            Assert.Equal(0, api.Creates);
            Assert.DoesNotContain(api.Cards.Values, c => c.Info.State == TaskStates.Archive);
        }
    }

    [Fact]
    public async Task Undo_refuses_later_owner_edge_edits_and_expires_after_twenty_four_hours()
    {
        var r = Receipt();
        var api = new MemoryApi(r.Originals);
        Store().WriteRecut(r);
        await Service(Store()).ApplyAsync(r, api, default);
        var before = api.Cards["P-3"];
        api.Cards["P-3"] = before with { Info = before.Info with { References = before.Info.References with { RelatedTo = ["OWNER-EDIT"] } } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(Store()).UndoAsync("p", r.Id, api, default));
        api.Cards["P-3"] = before;
        _clock.Advance(TimeSpan.FromHours(24));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(Store()).UndoAsync("p", r.Id, api, default));
    }

    [Fact]
    public async Task Concurrent_owner_reference_edit_is_preserved_by_the_API_snapshot_fence()
    {
        var r = Receipt();
        var api = new MemoryApi(r.Originals);
        api.BeforeReferences = key =>
        {
            api.BeforeReferences = null;
            var card = api.Cards[key];
            api.Cards[key] = card with { Info = card.Info with { References = card.Info.References with { RelatedTo = ["OWNER-EDIT"] } } };
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(Store()).ApplyAsync(r, api, default));
        Assert.Equal(new[] { "OWNER-EDIT" }, api.Cards["P-0"].Info.References.RelatedTo);
        Assert.DoesNotContain(api.Cards.Values, c => c.Info.State == TaskStates.Archive);
    }

    [Fact]
    public async Task Shadow_cut_publishes_owner_proposals_once_without_task_mutations()
    {
        var r = Receipt(false) with { Id = DispatchRecutService.ReceiptId("p", "w", false), State = "drafted" };
        Store().WriteRecut(r);
        var items = new PersonaItemStore((string?)null);
        var api = new MemoryApi(r.Originals);
        var request = new DispatchRecutRequest(r.Originals.Select(o => new DispatchWaveCard(o.Info.Key!, o.FindingRefs)).ToArray());
        var service = Service(Store(), items);
        var proposed = await service.PlanAsync("p", "workspace", "w", request, api, default);
        Assert.Equal("proposed", proposed.State);
        Assert.Equal(2, items.List("workspace").Count);
        Assert.All(items.List("workspace"), i => Assert.Contains("dispatch.recut.propose", i.Body));
        Assert.Contains(items.List("workspace"), i => i.Body.Contains("P-3 depends on package (P-0, P-1, P-2)", StringComparison.Ordinal));
        Assert.Equal(proposed.ProposalIds, (await service.PlanAsync("p", "workspace", "w", request, api, default)).ProposalIds);
        Assert.Equal(0, api.Creates);
        Assert.Equal(0, api.Writes);
        var proposal = items.List("workspace")[0];
        var lifecycle = new PersonaProposalLifecycle(items, null!, null!, null!, new ConfigurationBuilder().Build());
        Assert.Equal("accepted", lifecycle.Decide("workspace", proposal.Id, new("accepted"), _clock.GetUtcNow().UtcDateTime).State);
    }

    [Fact]
    public async Task Live_application_rechecks_the_same_lease_before_each_API_mutation()
    {
        var store = Store();
        var lease = store.AcquireLease("p", "packer", 0, _clock.GetUtcNow(), TimeSpan.FromHours(1), true, true, true)!;
        var r = Receipt() with { Id = DispatchRecutService.ReceiptId("p", "w", true), LeaseFence = lease.Fence };
        store.WriteRecut(r);
        var d = new PersonaCatalogue().Find("packer")!.Descriptor;
        var at = _clock.GetUtcNow().UtcDateTime;
        var activation = new PersonaActivationV1("packer", "catalogue", PersonaStates.Active, 1, d.Revision,
            PersonaDescriptorDigest.Compute(d), d, new(null, null, null), d.Attributes, d.Budget, d.Scope,
            ["observe", "dispatch.shadow.record"], at, "owner", at, null, null, []);
        var activationRoot = Path.Combine(_root, "personas");
        Directory.CreateDirectory(activationRoot);
        File.WriteAllText(Path.Combine(activationRoot, "workspace.json"), JsonSerializer.Serialize(
            new PersonaWorkspaceStateV1(1, "workspace", [activation], []), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var activations = new PersonaActivationStore(activationRoot, new(), null!, _ => "workspace", _clock);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetDispatchMode("p", "live");
        var service = new DispatchRecutService(store, null!, settings, activations, new PersonaItemStore((string?)null), null!, _clock);
        var api = new MemoryApi(r.Originals)
        { AfterMove = () => store.ReleaseLease("p", "packer", lease.Fence, _clock.GetUtcNow()) };
        var request = new DispatchRecutRequest(r.Originals.Select(o => new DispatchWaveCard(o.Info.Key!, o.FindingRefs)).ToArray(), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanAsync("p", "workspace", "w", request, api, default));
        Assert.Equal(1, api.Writes);
        Assert.Equal(0, api.Creates);
        Assert.NotNull(store.ReadRecut("p", r.Id)!.Error);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("urgent")]
    public async Task Undo_preserves_a_later_owner_pin_or_urgent_mark_before_moving_any_card(string edit)
    {
        var r = Receipt();
        var api = new MemoryApi(r.Originals);
        Store().WriteRecut(r);
        var applied = await Service(Store()).ApplyAsync(r, api, default);
        var key = edit == "pin" ? "P-3" : applied.Replacements.Values.Single();
        var card = api.Cards[key];
        api.Cards[key] = card with { Info = edit == "pin" ? card.Info with { Order = 0 } : card.Info with { Tags = ["urgent"] } };
        var writes = api.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(Store()).UndoAsync("p", r.Id, api, default));
        Assert.Equal(writes, api.Writes);
    }

    internal sealed class MemoryApi(IEnumerable<DispatchRecutOriginal> originals) : IDispatchRecutTaskApi
    {
        public Dictionary<string, TaskDetail> Cards { get; } = originals.ToDictionary(o => o.Info.Key!,
            o => new TaskDetail { Info = o.Info, PromptMarkdown = o.Prompt });
        public int Creates { get; private set; }
        public int Writes { get; private set; }
        public bool FailReferencesOnce { get; set; }
        public Action? AfterMove { get; init; }
        public Action<string>? BeforeReferences { get; set; }
        public Task<TaskDetail?> ReadAsync(string key, CancellationToken ct)
            => Task.FromResult(Cards.GetValueOrDefault(key) ?? Cards.Values.FirstOrDefault(c => c.Info.Id == key));
        public async Task<TaskInfo> CreateAsync(CreateTaskRequest request, CancellationToken ct)
        {
            if (await ReadAsync(request.Id, ct) is { } exists) return exists.Info;
            Creates++;
            var card = new TaskInfo { Id = request.Id, Key = "MERGED-" + Creates, Title = request.Title,
                ProjectName = "p", State = request.TargetState!, Agent = request.Agent, CreationSource = request.CreationSource! };
            Cards[card.Key] = new() { Info = card, PromptMarkdown = request.PromptMarkdown };
            return card;
        }
        public Task ReferencesAsync(string key, TaskReferences references, CancellationToken ct, TaskReferences? expected = null)
        {
            if (FailReferencesOnce) { FailReferencesOnce = false; throw new HttpRequestException("injected failure"); }
            BeforeReferences?.Invoke(key);
            if (expected is not null && JsonSerializer.Serialize(Cards[key].Info.References) != JsonSerializer.Serialize(expected))
                throw new HttpRequestException("references-changed");
            Writes++;
            Cards[key] = Cards[key] with { Info = Cards[key].Info with { References = references } };
            return Task.CompletedTask;
        }
        public Task MoveAsync(string key, string from, string to, CancellationToken ct)
        {
            Assert.Equal(from, Cards[key].Info.State);
            Writes++;
            Cards[key] = Cards[key] with { Info = Cards[key].Info with { State = to } };
            AfterMove?.Invoke();
            return Task.CompletedTask;
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
