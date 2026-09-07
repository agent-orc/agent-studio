using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModelFamilyResolverCollection
{
    public const string Name = "ModelFamilyResolver";
}

/// <summary>
/// Model-family policy coverage using catalogs captured from the installed
/// Claude Code and Codex CLIs on 2026-09-06.
/// </summary>
[Collection(ModelFamilyResolverCollection.Name)]
public sealed class ModelFamilyResolverTests : IDisposable
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);

    public ModelFamilyResolverTests()
        => ModelFamilyResolver.ClearPublishedCatalogs();

    public void Dispose()
        => ModelFamilyResolver.ClearPublishedCatalogs();

    [Fact]
    public void CapturedClaudeCatalog_ResolvesNewestAvailableNamedFamilies()
    {
        const string picker = """
        Select model
        1.Default(recommended)Opus5with1Mcontext·Bestforeveryday,complextasks
        2.Opus(1Mcontext)Opus5with1Mcontext·Bestforeveryday,complextasks
        3.Fable✔Fable5.1·Mostcapableforyourhardestandlongest-runningtasks
        4.SonnetSonnet5·Efficientforroutinetasks
        5.HaikuHaiku4.5·Fastestforquickanswers
        ● Higheffort(default)
        """;
        var catalog = Catalog(ClaudeModelDiscovery.Reconcile(
            ClaudeModelDiscovery.ParsePickerSnapshot(picker)));

        Assert.Equal(ModelIds.ClaudeHaiku45,
            Resolve(ModelFamilies.ClaudeHaiku, catalog));
        Assert.Equal(ModelIds.ClaudeSonnet5,
            Resolve(ModelFamilies.ClaudeSonnet, catalog));
        Assert.Equal(ModelIds.ClaudeOpus5,
            Resolve(ModelFamilies.ClaudeOpus, catalog));
    }

    [Fact]
    public void CapturedCodexCatalog_ResolvesMiniAndFlagshipFamilies()
    {
        const string output = """
        {"models":[
          {"slug":"gpt-5.4-mini","display_name":"GPT-5.4-Mini","visibility":"list","priority":6},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":4},
          {"slug":"gpt-5.6-terra","display_name":"GPT-5.6-Terra","visibility":"list","priority":2},
          {"slug":"gpt-5.6-sol","display_name":"GPT-5.6-Sol","visibility":"list","priority":1}
        ]}
        """;
        var catalog = Catalog(CodexModelDiscovery.ParseDebugModelsJson(
            output,
            activeModel: ModelIds.Gpt56Sol));

        Assert.Equal(ModelIds.Gpt54Mini, Resolve(ModelFamilies.GptMini, catalog));
        Assert.Equal(ModelIds.Gpt56Sol, Resolve(ModelFamilies.GptFlagship, catalog));
    }

    [Fact]
    public void LiveAvailability_WinsWithinRegistryGenerationOrder()
    {
        var catalog = Catalog(
        [
            Model(ModelIds.ClaudeOpus47),
            Model(ModelIds.ClaudeOpus5, available: false),
            Model(ModelIds.ClaudeOpus48),
        ]);

        Assert.Equal(ModelIds.ClaudeOpus48, Resolve(ModelFamilies.ClaudeOpus, catalog));
    }

    [Fact]
    public void NewlyDiscoveredNumericGeneration_CanLeadBeforeExactRegistryMetadataShips()
    {
        var catalog = Catalog(
        [
            Model(ModelIds.Gpt56Sol),
            Model("gpt-6-astra"),
            Model(ModelIds.Gpt55),
        ]);

        Assert.Equal("gpt-6-astra", Resolve(ModelFamilies.GptFlagship, catalog));
    }

    [Fact]
    public void StaleCatalog_FallsBackToNewestAvailableRegistryGeneration()
    {
        var stale = Catalog(
            [Model("claude-opus-6")],
            fetchedAt: CapturedAt.Subtract(TimeSpan.FromHours(2)));

        Assert.Equal(
            ModelFamilyResolver.ResolveFromRegistry(ModelFamilies.ClaudeOpus),
            Resolve(ModelFamilies.ClaudeOpus, stale));
    }

    [Fact]
    public async Task ResolveAsync_RequestsOwningCliAndPublishesForSynchronousCallers()
    {
        string? requestedCli = null;
        var catalog = Catalog([Model(ModelIds.ClaudeSonnet5)]);
        var resolver = Resolver((cliType, _) =>
        {
            requestedCli = cliType;
            return Task.FromResult(catalog);
        });

        var resolved = await resolver.ResolveAsync(ModelFamilies.ClaudeSonnet);

        Assert.Equal(CliTypes.Claude, requestedCli);
        Assert.Equal(ModelIds.ClaudeSonnet5, resolved);
        Assert.Equal(ModelIds.ClaudeSonnet5,
            ModelFamilyResolver.ResolveCurrent(
                ModelFamilies.ClaudeSonnet,
                CapturedAt,
                Freshness));
    }

    [Fact]
    public async Task ResolveAsync_DiscoveryFailureUsesRegistryFallback()
    {
        var resolver = Resolver((_, _) =>
            Task.FromException<CliModelCatalog>(new InvalidOperationException("discovery failed")));

        var resolved = await resolver.ResolveAsync(ModelFamilies.GptMini);

        Assert.Equal(ModelIds.Gpt54Mini, resolved);
    }

    [Fact]
    public async Task ResolveAsync_FreshCatalogWithoutAvailableFamily_DoesNotUseRegistryFallback()
    {
        var catalog = Catalog(
        [
            Model(ModelIds.ClaudeOpus5, available: false),
            Model(ModelIds.ClaudeSonnet5),
        ]);
        var resolver = Resolver((_, _) => Task.FromResult(catalog));

        var error = await Assert.ThrowsAsync<ModelFamilyUnavailableException>(() =>
            resolver.ResolveAsync(ModelFamilies.ClaudeOpus));

        Assert.Equal(ModelFamilies.ClaudeOpus, error.FamilyId);
        Assert.Equal(CliTypes.Claude, error.CliType);
    }

    [Fact]
    public void ResolveCurrent_PartialPublishedCatalogFallsBackWhilePureResolutionRemainsAuthoritative()
    {
        var partial = Catalog([Model(ModelIds.ClaudeSonnet5)]);
        ModelFamilyResolver.PublishCatalog(CliTypes.Claude, partial);

        var supportingDefault = ModelFamilyResolver.ResolveCurrent(
            ModelFamilies.ClaudeOpus,
            CapturedAt,
            Freshness);

        Assert.Equal(
            ModelFamilyResolver.ResolveFromRegistry(ModelFamilies.ClaudeOpus),
            supportingDefault);
        Assert.Throws<ModelFamilyUnavailableException>(() =>
            Resolve(ModelFamilies.ClaudeOpus, partial));
    }

    [Fact]
    public void RegistryExposesFamilyAndGenerationForMigrationComparison()
    {
        Assert.Equal(ModelFamilies.ClaudeHaiku,
            ModelMetadataRegistry.FamilyFor("claude-haiku-4-5-20251001"));
        Assert.True(
            ModelMetadataRegistry.GenerationOrderFor(ModelIds.ClaudeSonnet5)
            > ModelMetadataRegistry.GenerationOrderFor(ModelIds.ClaudeSonnet46));
        Assert.Contains(
            ModelMetadataRegistry.ForFamily(ModelFamilies.GptMini),
            model => model.Id == ModelIds.Gpt54Mini);
    }

    [Fact]
    public void UnknownFamily_IsRejectedAtThePolicyBoundary()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ModelFamilyResolver.ResolveFromRegistry("claude-fast"));

        Assert.Equal("value", error.ParamName);
    }

    private static string Resolve(string family, CliModelCatalog catalog)
        => ModelFamilyResolver.Resolve(family, catalog, CapturedAt, Freshness);

    private static CliModelCatalog Catalog(
        IReadOnlyList<CliModelInfo> models,
        DateTimeOffset? fetchedAt = null)
        => new()
        {
            Models = models.ToList(),
            Source = "captured-2026-09-06",
            FetchedAt = (fetchedAt ?? CapturedAt).UtcDateTime,
        };

    private static CliModelInfo Model(string id, bool available = true)
        => new()
        {
            Id = id,
            Label = id,
            Available = available,
            Deprecated = false,
        };

    private static ModelFamilyResolver Resolver(
        Func<string, CancellationToken, Task<CliModelCatalog>> provider)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ClaudeModelsCacheMinutes"] = "60",
                ["CodexModelsCacheMinutes"] = "60",
            }).Build();
        return new ModelFamilyResolver(
            provider,
            configuration,
            NullLogger<ModelFamilyResolver>.Instance,
            new FixedTimeProvider(CapturedAt));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
