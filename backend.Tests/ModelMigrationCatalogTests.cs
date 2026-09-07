using System.Text.Json;

using AgentStudio.ModelMigrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelMigrationCatalogTests : IDisposable
{
    private readonly string _repository = Path.Combine(
        Path.GetTempPath(), "model-migration-catalog-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetSnapshot_CachesBySourceAndLastWriteTime()
    {
        var source = WriteCatalog(CatalogJson("2026-09-06"));
        var initialWriteTime = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, initialWriteTime);
        var service = CreateService();

        var first = service.GetSnapshot();
        Assert.True(first.IsAvailable, first.Error);
        Assert.Equal("2026-09-06", first.Catalog?.CatalogVersion);

        File.WriteAllText(source, CatalogJson("2026-09-07"));
        File.SetLastWriteTimeUtc(source, initialWriteTime);
        var cached = service.GetSnapshot();
        Assert.Same(first, cached);
        Assert.Equal("2026-09-06", service.CurrentCatalogVersion);

        File.SetLastWriteTimeUtc(source, initialWriteTime.AddSeconds(1));
        var refreshed = service.GetSnapshot();
        Assert.Equal("2026-09-07", refreshed.Catalog?.CatalogVersion);
        Assert.NotSame(first.Catalog, refreshed.Catalog);
    }

    [Fact]
    public void GetSnapshot_InvalidRefreshRetainsLastKnownGood()
    {
        var source = WriteCatalog(CatalogJson("2026-09-06"));
        var service = CreateService();
        var valid = service.GetSnapshot();
        Assert.True(valid.IsAvailable, valid.Error);
        var nextWriteTime = File.GetLastWriteTimeUtc(source).AddSeconds(2);

        File.WriteAllText(source, "{ invalid JSON");
        File.SetLastWriteTimeUtc(source, nextWriteTime);
        var stale = service.GetSnapshot();

        Assert.True(stale.IsAvailable);
        Assert.True(stale.IsStale);
        Assert.Same(valid.Catalog, stale.Catalog);
        Assert.Equal("2026-09-06", stale.Catalog?.CatalogVersion);
        Assert.NotNull(stale.Error);
        Assert.Equal("2026-09-06", service.GetStatus().CatalogVersion);
    }

    [Fact]
    public void Validate_RejectsEveryUnsafeSafeAutoGate()
    {
        var evidence = JsonSerializer.SerializeToElement(new
        {
            kind = "controlledBenchmark",
            reference = "benchmarks/result.json",
            conclusion = "noRegression",
            summary = "Both models passed the same cases."
        });
        var validRule = new ModelMigrationRule
        {
            From = "claude-opus-4-8",
            To = "claude-opus-5",
            Family = "claude-opus",
            Vendor = "anthropic",
            GenerationOrder = new ModelMigrationGenerationOrder { From = 408, To = 500 },
            CostClassFrom = "premium",
            CostClassTo = "premium",
            LadderCompatible = true,
            ContextChange = "increase",
            Evidence = evidence,
            SafeAuto = true,
            Since = "2026-09-06",
            Note = "A validated same-family successor."
        };

        Assert.Empty(ModelMigrationCatalogPolicy.Validate(Catalog(validRule)));

        var cases = new[]
        {
            Catalog(validRule with { To = "claude-sonnet-5" }),
            Catalog(validRule with { GenerationOrder = new ModelMigrationGenerationOrder { From = 500, To = 500 } }),
            Catalog(validRule with { CostClassFrom = "economy", CostClassTo = "standard" }),
            Catalog(validRule with { LadderCompatible = false }),
            Catalog(validRule with { Evidence = JsonSerializer.SerializeToElement("none") })
        };

        foreach (var catalog in cases)
            Assert.NotEmpty(ModelMigrationCatalogPolicy.Validate(catalog));
    }

    [Fact]
    public void GetProposal_ReturnsPinnedCrossFamilyUpdateWithoutAuthorizingAutoApply()
    {
        WriteCatalog(CatalogJson(
            "2026-09-06",
            from: "claude-haiku-4-5",
            to: "claude-sonnet-5",
            family: "claude-haiku",
            costClassFrom: "economy",
            costClassTo: "standard",
            ladderCompatible: true,
            safeAuto: false));
        var service = CreateService();

        var displayProposal = service.GetProposal("claude-haiku-4.5");
        Assert.NotNull(displayProposal);
        Assert.Equal("claude-haiku-4-5", displayProposal.From);
        Assert.Equal("claude-sonnet-5", displayProposal.To);
        Assert.Equal("2026-09-06", displayProposal.CatalogVersion);
        Assert.Equal("economy", displayProposal.CostClassFrom);
        Assert.Equal("standard", displayProposal.CostClassTo);
        Assert.True(displayProposal.LadderCompatible);
        Assert.Null(displayProposal.TargetAvailable);
        Assert.False(displayProposal.SafeAutoCandidate);

        var checkedProposal = service.FindProposal(
            "claude-haiku-4-5",
            target => target == "claude-sonnet-5");
        Assert.NotNull(checkedProposal);
        Assert.True(checkedProposal.TargetAvailable);
        Assert.False(checkedProposal.SafeAuto);
        Assert.False(checkedProposal.SafeAutoCandidate);
    }

    [Fact]
    public void FindProposal_RequiresCurrentTargetAvailabilityForSafeAutoCandidate()
    {
        WriteCatalog(CatalogJson("2026-09-06"));
        var service = CreateService();

        var unavailable = service.FindProposal("claude-opus-4-8", _ => false);
        var available = service.FindProposal("claude-opus-4-8", _ => true);

        Assert.NotNull(unavailable);
        Assert.True(unavailable.SafeAuto);
        Assert.False(unavailable.TargetAvailable);
        Assert.False(unavailable.SafeAutoCandidate);
        Assert.NotNull(available);
        Assert.True(available.TargetAvailable);
        Assert.True(available.SafeAutoCandidate);
        Assert.NotEmpty(available.ToReasoningLevels);
    }

    [Fact]
    public void GetProposal_DoesNotTreatRetentionAsAnUpdate()
    {
        WriteCatalog(CatalogJson(
            "2026-09-06",
            from: "claude-haiku-4-5",
            to: "claude-haiku-4-5",
            family: "claude-haiku",
            costClassFrom: "economy",
            costClassTo: "economy",
            ladderCompatible: true,
            safeAuto: false));

        Assert.Null(CreateService().GetProposal("claude-haiku-4-5"));
    }

    [Fact]
    public async Task Admission_applies_safe_rule_to_non_explicit_model_and_writes_timeline_event()
    {
        WriteCatalog(CatalogJson("2026-09-06"));
        var harness = BuildAdmissionHarness();
        harness.StateMachine.EnsureStateFoldersAndMigrate();
        harness.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "safe-auto",
            Title = "Safe auto",
            WatchPath = harness.WatchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            ModelExplicit = false,
            TargetState = TaskStates.Ready,
        });
        var task = harness.Scanner.FindJob("safe-auto", harness.WatchPath)!;

        var migrated = await harness.Coordinator.ApplyAtAdmissionAsync(task);

        Assert.Equal("claude-opus-5", migrated.Model);
        Assert.False(migrated.ModelExplicit);
        var evt = Assert.Single(
            harness.Timeline.ReadAll(migrated.FolderPath),
            item => item.Kind == TimelineEventKinds.ModelMigrated);
        Assert.Equal("claude-opus-4-8", evt.Details?["from"]);
        Assert.Equal("claude-opus-5", evt.Details?["to"]);
        Assert.Equal("2026-09-06", evt.Details?["catalogVersion"]);
        Assert.Contains("latestInFamily", evt.Details?["rule"]);
    }

    [Fact]
    public async Task Admission_never_changes_an_explicit_pin()
    {
        WriteCatalog(CatalogJson("2026-09-06"));
        var harness = BuildAdmissionHarness();
        harness.StateMachine.EnsureStateFoldersAndMigrate();
        harness.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "explicit",
            Title = "Explicit",
            WatchPath = harness.WatchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            ModelExplicit = true,
            TargetState = TaskStates.Ready,
        });
        var task = harness.Scanner.FindJob("explicit", harness.WatchPath)!;

        var unchanged = await harness.Coordinator.ApplyAtAdmissionAsync(task);

        Assert.Equal("claude-opus-4-8", unchanged.Model);
        Assert.True(unchanged.ModelExplicit);
        Assert.DoesNotContain(
            harness.Timeline.ReadAll(unchanged.FolderPath),
            item => item.Kind == TimelineEventKinds.ModelMigrated);
    }

    [Fact]
    public void Explicit_task_projection_offers_the_catalog_update_for_the_card_badge()
    {
        WriteCatalog(CatalogJson(
            "2026-09-06",
            from: "claude-haiku-4-5",
            to: "claude-sonnet-5",
            family: "claude-haiku",
            costClassFrom: "economy",
            costClassTo: "standard",
            ladderCompatible: true,
            safeAuto: false));
        var harness = BuildAdmissionHarness();
        harness.StateMachine.EnsureStateFoldersAndMigrate();
        harness.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "pinned-proposal",
            Title = "Pinned proposal",
            WatchPath = harness.WatchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-haiku-4-5",
            ModelExplicit = true,
            TargetState = TaskStates.Ready,
        });

        var projected = harness.Coordinator.WithProposal(
            harness.Scanner.FindJob("pinned-proposal", harness.WatchPath)!);

        Assert.NotNull(projected.ModelMigration);
        Assert.Equal("claude-haiku-4-5", projected.ModelMigration.From);
        Assert.Equal("claude-sonnet-5", projected.ModelMigration.To);
        Assert.True(projected.ModelMigration.TargetAvailable);
        Assert.False(projected.ModelMigration.SafeAutoCandidate);
    }

    [Fact]
    public async Task Admission_respects_the_workspace_auto_application_switch()
    {
        WriteCatalog(CatalogJson("2026-09-06"));
        var harness = BuildAdmissionHarness();
        harness.StateMachine.EnsureStateFoldersAndMigrate();
        harness.CoordinatorState.SetAutoModelMigrations(false);
        harness.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "auto-disabled",
            Title = "Auto disabled",
            WatchPath = harness.WatchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            ModelExplicit = false,
            TargetState = TaskStates.Ready,
        });
        var task = harness.Scanner.FindJob("auto-disabled", harness.WatchPath)!;

        var unchanged = await harness.Coordinator.ApplyAtAdmissionAsync(task);

        Assert.Equal("claude-opus-4-8", unchanged.Model);
        Assert.DoesNotContain(
            harness.Timeline.ReadAll(unchanged.FolderPath),
            item => item.Kind == TimelineEventKinds.ModelMigrated);
    }

    [Fact]
    public void Configuration_pin_apply_returns_stale_when_the_pin_was_removed()
    {
        WriteCatalog(CatalogJson("2026-09-06"));
        var harness = BuildAdmissionHarness();

        var result = harness.Coordinator.ApplyConfigurationPin(
            "summary",
            new ApplyConfigurationPinRequest
            {
                ExpectedFrom = "claude-opus-4-8",
                ToModel = "claude-opus-5",
                CatalogVersion = "2026-09-06",
                Rule = "latestInFamily:claude-opus:claude-opus-4-8->claude-opus-5",
            });

        Assert.Equal("stale", result.Status);
        Assert.False(result.Applied);
        Assert.Contains("removed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private ModelMigrationCatalogService CreateService()
        => new(() => _repository, NullLogger<ModelMigrationCatalogService>.Instance);

    private AdmissionHarness BuildAdmissionHarness()
    {
        var watchPath = Path.Combine(_repository, "projects", "demo");
        Directory.CreateDirectory(watchPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repository,
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            summary);
        var stateMachine = new TaskStateMachine(
            scanner,
            NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var state = new ModelRoutingPolicyStateStore(
            configuration,
            NullLogger<ModelRoutingPolicyStateStore>.Instance);
        var coordinator = new ModelMigrationCoordinator(
            CreateService(),
            state,
            mutations,
            timeline,
            bus: null,
            configuration: configuration,
            configurationWriter: null!,
            logger: NullLogger<ModelMigrationCoordinator>.Instance,
            isModelAvailable: _ => true,
            hasDatedPrice: _ => true);
        return new AdmissionHarness(
            watchPath,
            scanner,
            stateMachine,
            mutations,
            timeline,
            state,
            coordinator);
    }

    private sealed record AdmissionHarness(
        string WatchPath,
        TaskScannerService Scanner,
        TaskStateMachine StateMachine,
        TaskMutationService Mutations,
        TimelineLog Timeline,
        ModelRoutingPolicyStateStore CoordinatorState,
        ModelMigrationCoordinator Coordinator);

    private string WriteCatalog(string json)
    {
        var source = Path.Combine(_repository, ModelMigrationCatalogService.CatalogRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, json);
        return source;
    }

    private static string CatalogJson(
        string version,
        string from = "claude-opus-4-8",
        string to = "claude-opus-5",
        string family = "claude-opus",
        string costClassFrom = "premium",
        string costClassTo = "premium",
        bool ladderCompatible = true,
        bool safeAuto = true)
    {
        object evidence = safeAuto
            ? new Dictionary<string, object?>
            {
                ["kind"] = "controlledBenchmark",
                ["reference"] = "benchmarks/result.json",
                ["conclusion"] = "noRegression",
                ["summary"] = "Both models passed the same cases."
            }
            : "none";
        var catalog = new Dictionary<string, object?>
        {
            ["$schema"] = "model-migrations.v1.schema.json",
            ["schemaVersion"] = 1,
            ["catalogVersion"] = version,
            ["evidenceAsOfDate"] = "2026-09-06",
            ["defaultStrategy"] = "latestInFamily",
            ["authority"] = new
            {
                rules = "docs/model-migrations.md",
                routingPolicy = "docs/system/domains/model-routing-policy.md",
                priceCatalog = "src/TokenEconomy/catalog/model-prices.json"
            },
            ["costClassOrder"] = new[] { "economy", "standard", "premium" },
            ["migrations"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["family"] = family,
                    ["vendor"] = "anthropic",
                    ["generationOrder"] = new { from = 405, to = from == to ? 405 : 500 },
                    ["costClassFrom"] = costClassFrom,
                    ["costClassTo"] = costClassTo,
                    ["ladderCompatible"] = ladderCompatible,
                    ["contextChange"] = from == to ? "same" : "unknown",
                    ["evidence"] = evidence,
                    ["safeAuto"] = safeAuto,
                    ["since"] = "2026-09-06",
                    ["note"] = "Fixture migration rule."
                }
            },
            ["taskClassRecommendations"] = new object[] { new(), new(), new(), new(), new() }
        };
        return JsonSerializer.Serialize(catalog);
    }

    private static ModelMigrationCatalog Catalog(ModelMigrationRule rule)
        => new()
        {
            Schema = "model-migrations.v1.schema.json",
            SchemaVersion = 1,
            CatalogVersion = "2026-09-06",
            EvidenceAsOfDate = "2026-09-06",
            DefaultStrategy = "latestInFamily",
            Authority = JsonSerializer.SerializeToElement(new
            {
                rules = "docs/model-migrations.md",
                routingPolicy = "docs/system/domains/model-routing-policy.md",
                priceCatalog = "src/TokenEconomy/catalog/model-prices.json"
            }),
            CostClassOrder = ["economy", "standard", "premium"],
            Migrations = [rule],
            TaskClassRecommendations = JsonSerializer.SerializeToElement(
                new object[] { new(), new(), new(), new(), new() })
        };

    public void Dispose()
    {
        if (Directory.Exists(_repository)) Directory.Delete(_repository, recursive: true);
    }
}
