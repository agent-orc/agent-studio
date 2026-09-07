using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Captured-catalog coverage for latest-in-family resolution. The snapshots
/// mirror the Claude Code 2.1.26x picker and codex-cli debug-models fixtures
/// captured on 2026-09-06/07.
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class ModelFamilyResolverTests : IDisposable
{
    public ModelFamilyResolverTests() => ModelFamilyResolver.ClearPublishedCatalogs();

    public void Dispose() => ModelFamilyResolver.ClearPublishedCatalogs();

    [Fact]
    public void ClaudeCapturedCatalog_ResolvesNewestGenerationWithinEachFamily()
    {
        const string picker = """
        Select modelSwitch betwen Claude models.Your pickbecomesthedefaultfornewsessions.Forother/previousmodelnames,specifywith--model.
        1.Default(recommended)Opus5with1Mcontext·Bestforeveryday,complextasks2.Opus(1Mcontext)Opus5with1Mcontext·Bestforeveryday,complextasks❯3.Fable✔Fable5.1·Mostcapableforyourhardestandlongest-runningtasks4.SonnetSonnet5·Efficientforroutinetasks5.HaikuHaiku4.5·Fastestforquickanswers●Higheffort(default)←/→toadjustEntertosetasdefault·stousethissessiononly·Esctocancel
        """;
        var catalog = Catalog(ClaudeModelDiscovery.Reconcile(
            ClaudeModelDiscovery.ParsePickerSnapshot(picker)));

        Assert.Equal(ModelIds.ClaudeOpus5,
            ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus, catalog));
        Assert.Equal(ModelIds.ClaudeSonnet5,
            ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet, catalog));
        Assert.Equal(ModelIds.ClaudeHaiku45,
            ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku, catalog));
    }

    [Fact]
    public void CodexCapturedCatalog_ResolvesMiniAndFlagshipIndependently()
    {
        var catalog = Catalog(CodexModelDiscovery.ParseDebugModelsJson(
            Fixture("debug-models-v0.153.4.json")));

        Assert.Equal(ModelIds.Gpt54Mini,
            ModelFamilyResolver.Resolve(ModelFamilies.GptMini, catalog));
        Assert.Equal(ModelIds.Gpt6Astra,
            ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship, catalog));
    }

    [Fact]
    public void OlderCodexCatalog_SkipsUnavailableRegisteredAstra()
    {
        var parsed = Catalog(CodexModelDiscovery.ParseDebugModelsJson(
            Fixture("debug-models-v0.151.0.json")));
        var catalog = CodexModelDiscovery.WithKnownButUnavailableModels(parsed, "0.151.0");

        Assert.False(Assert.Single(catalog.Models, model => model.Id == ModelIds.Gpt6Astra).Available);
        Assert.Equal(ModelIds.Gpt56Sol,
            ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship, catalog));
    }

    [Fact]
    public void NewlyDiscoveredGeneration_WinsWithoutARegistryEntry()
    {
        var catalog = Catalog(
        [
            Model("claude-sonnet-5"),
            Model("claude-sonnet-6"),
            Model("claude-sonnet-4-6"),
        ]);

        Assert.Equal("claude-sonnet-6",
            ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet, catalog));
    }

    [Fact]
    public void StaleDiscovery_FallsBackToRegistryGenerationOrder()
    {
        var now = DateTime.UtcNow;
        var stale = new CliModelCatalog
        {
            Models = [Model("claude-haiku-6")],
            Source = "stale-test",
            FetchedAt = now - TimeSpan.FromHours(3),
        };

        Assert.Equal(ModelIds.ClaudeHaiku45, ModelFamilyResolver.Resolve(
            ModelFamilies.ClaudeHaiku,
            stale,
            now,
            TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Availability_UsesFreshCatalogBeforeRegistryFallback()
    {
        var now = DateTime.UtcNow;
        var fresh = Catalog(
        [
            Model(ModelIds.ClaudeOpus5) with { Available = false },
            Model(ModelIds.ClaudeOpus48),
        ], now);

        Assert.False(ModelFamilyResolver.IsAvailable(ModelIds.ClaudeOpus5, fresh, now));
        Assert.True(ModelFamilyResolver.IsAvailable(ModelIds.ClaudeOpus48, fresh, now));

        var stale = fresh with { FetchedAt = now - TimeSpan.FromHours(3) };
        Assert.True(ModelFamilyResolver.IsAvailable(ModelIds.ClaudeOpus5, stale, now));
    }

    [Fact]
    public void AutomaticMigrationAvailability_RequiresAFreshInstalledCatalog()
    {
        var now = DateTime.UtcNow;
        var fresh = Catalog([Model(ModelIds.ClaudeOpus5)], now);
        var stale = fresh with { FetchedAt = now - TimeSpan.FromHours(3) };

        Assert.True(ModelFamilyResolver.IsAvailableInFreshCatalog(
            ModelIds.ClaudeOpus5, fresh, now));
        Assert.False(ModelFamilyResolver.IsAvailableInFreshCatalog(
            ModelIds.ClaudeOpus5, stale, now));
        Assert.False(ModelFamilyResolver.IsAvailableInFreshCatalog(
            ModelIds.ClaudeOpus5, catalog: null, now));
    }

    [Fact]
    public void ResolveConfigured_FirstExplicitPinWinsBeforeFamilyDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Primary:Model"] = "claude-haiku-4-5-20251001",
                ["Inherited:Model"] = ModelIds.ClaudeOpus5,
            })
            .Build();

        Assert.Equal("claude-haiku-4-5-20251001", ModelFamilyResolver.ResolveConfigured(
            configuration,
            ModelFamilies.ClaudeHaiku,
            "Primary:Model",
            "Inherited:Model"));
    }

    [Fact]
    public async Task SharedCatalogAccess_PublishesForRuntimeResolutionWithoutAnotherProbe()
    {
        var catalog = Catalog(
        [
            Model("gpt-5.5-mini"),
            Model(ModelIds.Gpt54Mini),
        ]);
        var behavior = new CliBehavior
        {
            CliType = CliTypes.Codex,
            GetCliPath = _ => "codex",
            BuildStartInfo = (_, _, _, _, _, _, _, _) => new System.Diagnostics.ProcessStartInfo(),
            GetModelCatalog = (_, _, _) => Task.FromResult(catalog),
        };
        var service = new GenericCliExecutionService(
            behavior,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new ConfigurationBuilder().Build());

        var returned = await service.GetModelCatalogAsync();

        Assert.Same(catalog, returned);
        Assert.Equal("gpt-5.5-mini", ModelFamilyResolver.Resolve(ModelFamilies.GptMini));
        Assert.True(ModelFamilyResolver.IsAvailable("gpt-5.5-mini"));
    }

    [Fact]
    public void RuntimeDefaults_ResolvePublishedFamilyGenerationsAtReadTime()
    {
        ModelFamilyResolver.PublishCatalog(CliTypes.Claude, Catalog(
        [
            Model("claude-haiku-5"),
            Model("claude-sonnet-6"),
            Model("claude-opus-6"),
        ]));
        ModelFamilyResolver.PublishCatalog(CliTypes.Codex, Catalog(
        [
            Model("gpt-5.5-mini"),
            Model(ModelIds.Gpt54Mini),
        ]));

        Assert.Equal("claude-haiku-5", OrchestratorRunner.DefaultModel);
        Assert.Equal("claude-sonnet-6", WikiMaintenanceModelService.DefaultModel);
        Assert.Equal("claude-opus-6", GenericCliExecutionService.DefaultOpusModel);
        Assert.Equal("gpt-5.5-mini", PipelineStepModelDefaults.SupportModel);
        Assert.Equal("gpt-5.5-mini", DriftPostStepRunner.DefaultModel);
        Assert.Equal("gpt-5.5-mini", OrchestratorPrepHostedService.PrepFallbackModel);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "cli",
            "codex",
            name));

    private static CliModelCatalog Catalog(IEnumerable<CliModelInfo> models, DateTime? fetchedAt = null)
        => new()
        {
            Models = models.ToList(),
            Source = "captured-test",
            FetchedAt = fetchedAt ?? DateTime.UtcNow,
        };

    private static CliModelInfo Model(string id)
        => new()
        {
            Id = id,
            Label = id,
            Vendor = id.StartsWith("claude-", StringComparison.Ordinal) ? "anthropic" : "openai",
            Available = true,
            Deprecated = false,
        };
}
