using System.Text.Json;
using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DispatcherPersonaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatcher-personas-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private DispatchStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Dispatch:StoragePath"] = _root }).Build());

    [Fact]
    public void Starter_descriptors_name_strategies_and_keep_shadow_as_the_default()
    {
        var descriptors = new PersonaCatalogue().Entries.Where(e => e.Descriptor.Kind == "dispatcher").ToArray();
        Assert.Equal(5, descriptors.Length);
        foreach (var entry in descriptors)
        {
            var d = entry.Descriptor;
            Assert.Empty(PersonaDescriptorValidator.Validate(d));
            Assert.Equal("delivery", d.Aspect);
            Assert.Equal("shadow", d.Dispatch!.Mode);
            Assert.Equal("model-free", d.Dispatch.Forecast);
            Assert.Equal(d.Id, DispatchStrategies.Find(d.Dispatch.Strategy.Id)!.Persona);
            Assert.Equal(d.Id == "packer" ? 60_000 : 20_000, d.Budget.TokensPerWeek);
            Assert.Equal(60, PersonaImportanceService.DescriptorFloor(d, "dispatch.conflict.attributed"));
            Assert.Equal(new[] { "observe", "dispatch.shadow.record" }, PersonaAutonomy.Grantable(d, "A1"));
            Assert.Equal(new[] { "observe", "dispatch.shadow.record" },
                PersonaAutonomy.Grantable(d with { Dispatch = d.Dispatch with { Mode = "live" } }, "A1"));
            Assert.DoesNotContain("dispatch.plan.write", PersonaAutonomy.Grantable(d, "A1"));
            Assert.NotNull(PersonaReportTemplates.Find(d.Report.Template));
            var invalid = d with { Dispatch = d.Dispatch with { Mode = "autonomous" } };
            Assert.NotEmpty(PersonaDescriptorValidator.Validate(invalid));
        }
        var defaultBlock = JsonSerializer.Deserialize<PersonaDispatchV1>(
            "{\"strategy\":{\"id\":\"lane-entry-v1\",\"revision\":\"2026.11.1\"}}", PersonaDescriptorJson.Options)!;
        Assert.Equal("shadow", defaultBlock.Mode);
    }

    [Fact]
    public void Narrow_catalogue_refuses_shadow_writes_foreign_edges_expired_T2_and_unbounded_actions()
    {
        Assert.Equal(9, DispatcherCapabilities.Operations.Count);
        Assert.Equal("dispatcher", DispatcherCapabilities.Role.Kind);
        Assert.Equal("dispatcher-v1", DispatcherCapabilities.Role.ReportTemplate);
        Assert.Contains("dispatch.conflict.attributed:60", DispatcherCapabilities.Role.Floors);
        Assert.All(DispatcherCapabilities.Catalogue.Grants, g => Assert.Equal("T1", g.Tier));
        var f = new DispatcherOperationFacts("dispatch.dependency.remove", "surveyor", false, true, true,
            EdgeOrigin: "dispatch:surveyor", Now: Now);
        Assert.True(DispatcherCapabilities.Admits(f));
        Assert.False(DispatcherCapabilities.Admits(f with { EdgeOrigin = "owner" }));
        Assert.False(DispatcherCapabilities.Admits(f with { EdgeOrigin = null }));
        Assert.False(DispatcherCapabilities.Admits(f with { Shadow = true }));
        Assert.False(DispatcherCapabilities.Admits(f with { CurrentLease = false }));
        Assert.False(DispatcherCapabilities.Admits(f with { StandingGrant = false }));
        var t2 = f with { EdgeOrigin = "dossier", ExplicitT2 = true, T2Expires = Now.AddMinutes(1), T2UsesRemaining = 1 };
        Assert.True(DispatcherCapabilities.Admits(t2));
        Assert.False(DispatcherCapabilities.Admits(t2 with { T2Expires = Now }));
        Assert.False(DispatcherCapabilities.Admits(t2 with { T2UsesRemaining = 0 }));
        Assert.False(DispatcherCapabilities.Admits(t2 with { Operation = "dispatch.plan.write", WithinBounds = false }));
        Assert.False(DispatcherCapabilities.Admits(t2 with { Operation = "dispatch.recut.apply", Packaging = true, SameProject = false }));
        Assert.False(DispatcherCapabilities.Admits(f with { Operation = "dispatch.plan.write", WithinBounds = false }));
        Assert.False(DispatcherCapabilities.Admits(f with { Operation = "slots.change" }));
        Assert.True(DispatcherCapabilities.Admits(f with { Operation = "dispatch.shadow.record", Shadow = true, CurrentLease = false }));
        Assert.False(DispatcherCapabilities.Admits(f with { Operation = "dispatch.recut.apply" }));
        Assert.False(DispatcherCapabilities.Admits(f with { Operation = "dispatch.recut.apply", Packaging = true, AutomatedSource = false }));
        Assert.False(DispatcherCapabilities.Admits(f with { Operation = "dispatch.dependency.add", Acyclic = false }));
    }

    [Fact]
    public void Lease_is_project_scoped_fenced_and_survives_restart_without_descriptor_authority()
    {
        var store = Store();
        Assert.Null(store.AcquireLease("p", "area-warden", 0, Now, TimeSpan.FromHours(1), true, true, false));
        Assert.Null(store.AcquireLease("p", "surveyor", 0, Now, TimeSpan.FromHours(1), true, false, true));
        var first = store.AcquireLease("p", "queue-keeper", 0, Now, TimeSpan.FromHours(1), true, true, false)!;
        Assert.Equal(1, first.Fence);
        Assert.Null(Store().AcquireLease("p", "area-warden", 1, Now, TimeSpan.FromHours(1), true, true, true));
        Assert.NotNull(store.AcquireLease("other", "area-warden", 0, Now, TimeSpan.FromHours(1), true, true, true));
        Assert.False(store.ReleaseLease("p", "surveyor", first.Fence, Now));
        Assert.True(Store().ReleaseLease("p", first.Persona, first.Fence, Now));
        var next = store.AcquireLease("p", "area-warden", 1, Now, TimeSpan.FromHours(1), true, true, true)!;
        Assert.Equal(2, next.Fence);
        Assert.False(store.ReleaseLease("p", first.Persona, first.Fence, Now));
        Assert.Equal("area-warden", Store().ReadLease("p")!.Persona);
    }

    [Fact]
    public void Shadow_plans_share_frozen_facts_have_unique_decisions_and_leave_lane_and_lease_untouched()
    {
        var store = Store();
        var service = new DispatcherShadowService(store, null!, null!, null!, null!, new FakeTimeProvider(Now),
            NullLogger<DispatcherShadowService>.Instance);
        var forecast = new DispatchForecast(["backend/Features/X/a.cs"], ["backend/Features/X"], DispatchForecastConfidence.High);
        var facts = new DispatchFacts("p", Now,
            [new("A", 999, Now, [], true, forecast, []), new("B", 999, Now.AddMinutes(-1), [], true, forecast, [])],
            [], 2, DispatchLedgerSummary.Empty, []);
        var descriptors = new PersonaCatalogue().Entries.Select(e => e.Descriptor).Where(d => d.Kind == "dispatcher").ToArray();
        var records = service.Record(facts, descriptors, "local");
        Assert.Equal(5, records.Count);
        Assert.Single(records.Select(r => r.FactsDigest).Distinct());
        Assert.Single(records.Select(r => r.EvaluationId).Distinct());
        Assert.Equal(5, records.Select(r => r.DecisionId).Distinct().Count());
        Assert.All(records, r => Assert.True(r.Shadow));
        Assert.Equal(2, records.Single(r => r.Plan.Persona == "queue-keeper").Plan.Admitted.Count);
        Assert.Single(records.Single(r => r.Plan.Persona == "surveyor").Plan.Admitted);
        Assert.Null(Store().ReadLease("p"));
        Assert.All(facts.Ready, c => Assert.Equal(999, c.Order));
        Assert.Equal(5, Store().ReadShadow("p").Count);
        Assert.True(Store().VerifyDispatcherLog("p"));
        Assert.All(store.ReadDispatcherLog("p"), row => { Assert.True(row.Shadow); Assert.True(row.Reason.Length <= 280); });
        service.Record(facts, descriptors, "remote-claim");
        Assert.Equal(10, Store().ReadDispatcherLog("p").Count);
        Assert.True(Store().VerifyDispatcherLog("p"));
        var file = Directory.GetFiles(_root, "dispatcher.jsonl", SearchOption.AllDirectories).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("admitted", "tampered", StringComparison.Ordinal));
        Assert.False(Store().VerifyDispatcherLog("p"));
    }

    [Fact]
    public void Dependency_origins_round_trip_without_changing_legacy_strings_and_normalization_never_grants_ownership()
    {
        foreach (var origin in new[] { "owner", "dossier", "dispatch:surveyor" })
        {
            var edge = new TaskDependencyReference("A-1", origin: origin);
            var roundtrip = JsonSerializer.Deserialize<TaskDependencyReference>(JsonSerializer.Serialize(edge))!;
            Assert.Equal(edge, roundtrip);
            Assert.Equal(origin, TaskReferenceValidator.NormalizeDependencies([edge]).Single().Origin);
        }
        Assert.Equal("\"A-1\"", JsonSerializer.Serialize(new TaskDependencyReference("A-1")));
        Assert.Null(JsonSerializer.Deserialize<TaskDependencyReference>("\"A-1\"")!.Origin);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TaskDependencyReference>("{\"key\":\"A-1\",\"origin\":\"robot\"}"));
        var mixed = TaskReferenceValidator.NormalizeDependencies([
            new("A-1", origin: "dispatch:surveyor"), new("a-1", origin: "owner")]);
        Assert.Equal("owner", Assert.Single(mixed).Origin);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
