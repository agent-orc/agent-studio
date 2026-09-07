using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The family contract, checked against the catalogs the installed CLIs really
/// produced on 06.09.2026 (Claude Code 2.1.263 `/model`, codex-cli
/// `debug models`). These are parsed by the production parsers rather than
/// hand-built model lists, so a parser change cannot quietly invalidate the
/// ranking these tests assert.
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public class ModelFamilyResolverTests : IDisposable
{
    // The published catalog snapshot is process-global, like the Codex
    // detected default it sits beside. Share that collection so no sibling
    // test observes a half-published catalog.
    public ModelFamilyResolverTests() => ModelMetadataRegistry.ClearDiscoveredCatalogs();

    public void Dispose() => ModelMetadataRegistry.ClearDiscoveredCatalogs();

    // Claude Code 2.1.263 picker, captured 06.09.2026. There is no Haiku 5:
    // Haiku 4.5 is the current member of that family.
    private const string ClaudeCliPicker = """
    Select modelSwitch betwen Claude models.Your pickbecomesthedefaultfornewsessions.Forother/previousmodelnames,specifywith--model.
    1.Default(recommended)Opus5with1Mcontext·Bestforeveryday,complextasks2.Opus(1Mcontext)Opus5with1Mcontext·Bestforeveryday,complextasks❯3.FableFable5.1·Mostcapableforyourhardestandlongest-runningtasks4.SonnetSonnet5·Efficientforroutinetasks5.Haiku✔Haiku4.5·Fastestforquickanswers●Higheffort(default)←/→toadjustEntertosetasdefault·stousethissessiononly·Esctocancel
    """;

    // codex-cli `debug models`, captured 06.09.2026. gpt-5.6 ships as three
    // reasoning sizes at the same generation; the CLI's own priority decides
    // which one leads.
    private const string CodexCliModels = """
    {"models":[
      {"slug":"gpt-5.6-sol","display_name":"GPT-5.6-Sol","visibility":"list","priority":1},
      {"slug":"gpt-5.6-terra","display_name":"GPT-5.6-Terra","visibility":"list","priority":2},
      {"slug":"gpt-5.6-luna","display_name":"GPT-5.6-Luna","visibility":"list","priority":3},
      {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":7},
      {"slug":"gpt-5.4-mini","display_name":"GPT-5.4-Mini","visibility":"list","priority":9},
      {"slug":"gpt-5-codex","display_name":"GPT-5 Codex","visibility":"list","priority":12}
    ]}
    """;

    private static List<CliModelInfo> ClaudeCatalog()
        => ClaudeModelDiscovery.Reconcile(ClaudeModelDiscovery.ParsePickerSnapshot(ClaudeCliPicker));

    private static List<CliModelInfo> CodexCatalog()
        => CodexModelDiscovery.ParseDebugModelsJson(CodexCliModels, activeModel: "gpt-5.6-sol");

    [Theory]
    [InlineData(ModelFamilies.ClaudeOpus, ModelIds.ClaudeOpus5)]
    [InlineData(ModelFamilies.ClaudeSonnet, ModelIds.ClaudeSonnet5)]
    // "Latest in family" keeps Haiku 4.5: the picker offers no Haiku 5.
    [InlineData(ModelFamilies.ClaudeHaiku, ModelIds.ClaudeHaiku45)]
    [InlineData(ModelFamilies.ClaudeFable, ModelIds.ClaudeFable51)]
    public void Newest_RanksTheCapturedClaudeCatalogByFamily(string family, string expected)
        => Assert.Equal(expected, ModelFamilyResolver.Newest(family, ClaudeCatalog()));

    [Theory]
    // Same generation across sol/terra/luna: the CLI's own default wins.
    [InlineData(ModelFamilies.GptFlagship, "gpt-5.6-sol")]
    [InlineData(ModelFamilies.GptMini, "gpt-5.4-mini")]
    public void Newest_RanksTheCapturedCodexCatalogByFamily(string family, string expected)
        => Assert.Equal(expected, ModelFamilyResolver.Newest(family, CodexCatalog()));

    [Fact]
    public void Newest_PrefersTheNewerGenerationOverTheCliDefault()
    {
        // The CLI still points at gpt-5.5 while advertising gpt-5.6: generation
        // outranks the CLI's active model, and IsDefault only breaks ties.
        var catalog = CodexModelDiscovery.ParseDebugModelsJson(CodexCliModels, activeModel: "gpt-5.5");

        Assert.Equal("gpt-5.6-sol", ModelFamilyResolver.Newest(ModelFamilies.GptFlagship, catalog));
    }

    [Fact]
    public void Newest_IgnoresUnavailableAndDeprecatedMembers()
    {
        var catalog = new List<CliModelInfo>
        {
            new() { Id = ModelIds.ClaudeOpus5, Label = "Claude Opus 5", Available = false },
            new() { Id = ModelIds.ClaudeOpus48, Label = "Claude Opus 4.8", Available = true, Deprecated = true },
            new() { Id = ModelIds.ClaudeOpus47, Label = "Claude Opus 4.7", Available = true },
        };

        Assert.Equal(ModelIds.ClaudeOpus47, ModelFamilyResolver.Newest(ModelFamilies.ClaudeOpus, catalog));
    }

    [Fact]
    public void Newest_ReturnsNullWhenTheCatalogHasNoMemberOfTheFamily()
        => Assert.Null(ModelFamilyResolver.Newest(ModelFamilies.GptMini, ClaudeCatalog()));

    [Theory]
    [InlineData(ModelIds.ClaudeHaiku45, ModelFamilies.ClaudeHaiku)]
    [InlineData("claude-haiku-4.5", ModelFamilies.ClaudeHaiku)]
    [InlineData("claude-haiku-4-5-20251001", ModelFamilies.ClaudeHaiku)]
    [InlineData(ModelIds.ClaudeOpus48, ModelFamilies.ClaudeOpus)]
    [InlineData("gpt-5.6-sol", ModelFamilies.GptFlagship)]
    [InlineData(ModelIds.Gpt5Codex, ModelFamilies.GptFlagship)]
    [InlineData(ModelIds.Gpt54Mini, ModelFamilies.GptMini)]
    [InlineData(ModelIds.Gemini25Pro, null)]
    [InlineData("", null)]
    public void Of_DerivesTheFamilyFromTheIdIncludingAliases(string modelId, string? expected)
        => Assert.Equal(expected, ModelFamilies.Of(modelId));

    [Theory]
    [InlineData(ModelIds.ClaudeOpus5, new[] { 5 })]
    [InlineData(ModelIds.ClaudeOpus48, new[] { 4, 8 })]
    [InlineData("gpt-5.6-sol", new[] { 5, 6 })]
    [InlineData(ModelIds.Gpt54Mini, new[] { 5, 4 })]
    [InlineData(ModelIds.Gpt5Codex, new[] { 5 })]
    [InlineData(ModelIds.Gpt4o, new[] { 4 })]
    public void GenerationOf_StopsAtTheFirstNonNumericSegment(string modelId, int[] expected)
        => Assert.Equal(expected, ModelFamilies.GenerationOf(modelId));

    [Fact]
    public void CompareGeneration_TreatsAMissingSegmentAsZero()
    {
        Assert.True(ModelFamilies.CompareGeneration([5], [4, 8]) > 0);
        Assert.Equal(0, ModelFamilies.CompareGeneration([5], [5, 0]));
        Assert.True(ModelFamilies.CompareGeneration([5], [5, 1]) < 0);
    }

    [Fact]
    public void Resolve_FallsBackToTheRegistryForEveryDeclaredFamily()
    {
        // The registry is the floor: with no discovery published at all, every
        // family a call site can reference still resolves to a real id, so a
        // stale or unprobed CLI can never leave a default unresolved.
        ModelMetadataRegistry.ClearDiscoveredCatalogs();

        foreach (var family in ModelFamilies.All)
        {
            var resolution = ModelFamilyResolver.ResolveDetailed(family);
            Assert.Equal("registry", resolution.Source);
            Assert.Equal(family, ModelFamilies.Of(resolution.ModelId));
        }
    }

    [Fact]
    public void ResolveDetailed_PrefersFreshDiscoveryAndFallsBackWhenItIsStale()
    {
        ModelMetadataRegistry.ClearDiscoveredCatalogs();
        try
        {
            var fetchedAt = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
            ModelMetadataRegistry.PublishDiscoveredCatalog(CliTypes.Codex, new CliModelCatalog
            {
                Models = CodexCatalog(),
                Source = "cli-debug-models",
                FetchedAt = fetchedAt,
            });

            var fresh = ModelFamilyResolver.ResolveDetailed(
                ModelFamilies.GptFlagship, now: fetchedAt.AddHours(1));
            Assert.Equal("gpt-5.6-sol", fresh.ModelId);
            // The snapshot's own label, not a flat "discovery": the audit has to
            // show whether the CLI answered or its cache did.
            Assert.Equal("cli-debug-models", fresh.Source);

            // Past DiscoveryMaxAge the snapshot is no longer evidence about the
            // installed CLI, so the repository's own knowledge takes over.
            var stale = ModelFamilyResolver.ResolveDetailed(
                ModelFamilies.GptFlagship, now: fetchedAt + ModelFamilyResolver.DiscoveryMaxAge.Add(TimeSpan.FromMinutes(1)));
            Assert.Equal("registry", stale.Source);
            Assert.Equal(ModelIds.Gpt55, stale.ModelId);
        }
        finally
        {
            ModelMetadataRegistry.ClearDiscoveredCatalogs();
        }
    }

    [Fact]
    public void Resolve_ThrowsOnAnUnknownFamilyRatherThanSubstitutingAModel()
        => Assert.Throws<InvalidOperationException>(() => ModelFamilyResolver.Resolve("gemini-flash"));
}
