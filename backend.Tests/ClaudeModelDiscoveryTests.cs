

using Xunit;

namespace AgentStudio.Tests;

public class ClaudeModelDiscoveryTests
{
    private const string ClaudeCode2126xPicker = """
    Select modelSwitch betwen Claude models.Your pickbecomesthedefaultfornewsessions.Forother/previousmodelnames,specifywith--model.
    1.Default(recommended)Opus5with1Mcontext·Bestforeveryday,complextasks2.Opus(1Mcontext)Opus5with1Mcontext·Bestforeveryday,complextasks❯3.Fable✔Fable5.1·Mostcapableforyourhardestandlongest-runningtasks4.SonnetSonnet5·Efficientforroutinetasks5.HaikuHaiku4.5·Fastestforquickanswers●Higheffort(default)←/→toadjustEntertosetasdefault·stousethissessiononly·Esctocancel
    """;

    [Fact]
    public void ParsePickerSnapshot_ParsesCollapsedClaudeCode2126xNumberedEntries()
    {
        var models = ClaudeModelDiscovery.ParsePickerSnapshot(ClaudeCode2126xPicker);

        Assert.Equal(
            [ModelIds.ClaudeOpus5, ModelIds.ClaudeFable51, ModelIds.ClaudeSonnet5, ModelIds.ClaudeHaiku45],
            models.Select(model => model.Id));
        Assert.Single(models, model => model.IsDefault && model.Id == ModelIds.ClaudeFable51);
        Assert.DoesNotContain(models, model => model.Label.Contains("effort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reconcile_PreservesPickerCurrentModelAsDefault()
    {
        var reconciled = ClaudeModelDiscovery.Reconcile(
            ClaudeModelDiscovery.ParsePickerSnapshot(ClaudeCode2126xPicker));

        Assert.Single(reconciled, model => model.IsDefault && model.Id == ModelIds.ClaudeFable51);
    }

    [Fact]
    public void ParsePickerSnapshot_MapsKnownClaudeLabelsThroughRegistry()
    {
        var models = ClaudeModelDiscovery.ParsePickerSnapshot("""
        Select Model
        > Claude Opus 4.8
          Sonnet 4.6
          Claude Haiku 4.5
        """);

        Assert.Contains(models, m => m.Id == ModelIds.ClaudeOpus48 && m.Label == "Claude Opus 4.8");
        Assert.Contains(models, m => m.Id == ModelIds.ClaudeSonnet46 && m.Vendor == "anthropic");
        Assert.Contains(models, m => m.Id == ModelIds.ClaudeHaiku45);
    }

    [Fact]
    public void ParsePickerSnapshot_MapsSonnet5WithFullThinkingLadder()
    {
        var models = ClaudeModelDiscovery.ParsePickerSnapshot("""
        Select Model
        > Claude Opus 4.8
          Sonnet 5
        """);

        var sonnet5 = Assert.Single(models, m => m.Id == ModelIds.ClaudeSonnet5);
        Assert.Equal("Claude Sonnet 5", sonnet5.Label);
        Assert.True(sonnet5.Available);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, sonnet5.ThinkingLevels);
    }

    [Fact]
    public void Reconcile_MarksRegistryModelsMissingFromCliUnavailable()
    {
        var discovered = new[]
        {
            ModelMetadataRegistry.ToCliModelInfo(
                ModelMetadataRegistry.Find(ModelIds.ClaudeOpus48)!,
                CliTypes.Claude)
        };

        var reconciled = ClaudeModelDiscovery.Reconcile(discovered);

        var missing = Assert.Single(reconciled, m => m.Id == ModelIds.ClaudeOpus47);
        Assert.False(missing.Available);
        Assert.False(missing.Deprecated);
        Assert.NotNull(missing.AvailabilityNote);
        Assert.DoesNotContain(reconciled, m => m.Id == ModelIds.ClaudeOpus47 && m.IsDefault);
    }

    [Fact]
    public void FallbackCatalog_OffersAvailableRegistryModels()
    {
        var catalog = ClaudeModelDiscovery.FallbackCatalog();

        Assert.NotEmpty(catalog.Models);
        Assert.All(catalog.Models, m => Assert.True(m.Available));
        Assert.Single(catalog.Models, m => m.IsDefault);
        Assert.Contains(catalog.Models, m => m.Id == ModelIds.ClaudeOpus5 && m.IsDefault);
    }

    [Fact]
    public void FallbackCatalog_SelectableModelsHaveContextMetadata()
    {
        var catalog = ClaudeModelDiscovery.FallbackCatalog();

        foreach (var model in catalog.Models.Where(m => m.Available))
        {
            Assert.NotNull(ModelMetadataRegistry.ContextWindowFor(model.Id));
        }
    }

    [Fact]
    public void Registry_ContainsClaude5MetadataAndKeepsClaude4ModelsNonDeprecated()
    {
        var opus = ModelMetadataRegistry.Find(ModelIds.ClaudeOpus5);
        var fable = ModelMetadataRegistry.Find(ModelIds.ClaudeFable51);

        Assert.NotNull(opus);
        Assert.Equal("Claude Opus 5", opus.Label);
        Assert.Equal(1_000_000, opus.ContextWindow);
        Assert.NotNull(fable);
        Assert.Equal("Claude Fable 5.1", fable.Label);
        Assert.Equal(200_000, fable.ContextWindow);
        Assert.Same(
            ModelMetadataRegistry.Find(ModelIds.ClaudeHaiku45),
            ModelMetadataRegistry.Find("claude-haiku-4-5-20251001"));
        Assert.All(
            ModelMetadataRegistry.ForVendor("anthropic").Where(model => model.Id.Contains("-4-")),
            model => Assert.False(model.Deprecated));
    }

    [Fact]
    public void Registry_ProvidesStudioCompatibilityLadderForFable51()
    {
        var model = ModelMetadataRegistry.ToCliModelInfo(
            ModelMetadataRegistry.Find(ModelIds.ClaudeFable51)!,
            CliTypes.Claude);

        Assert.Equal(["low", "medium", "high", "xhigh", "max"], model.ThinkingLevels);
        Assert.Equal("high", model.DefaultThinkingLevel);
        Assert.Equal("max", ModelMetadataRegistry.ResolveThinkingLevel(
            CliTypes.Claude, ModelIds.ClaudeFable51, "MAX"));
        Assert.Equal("high", ModelMetadataRegistry.ResolveThinkingLevel(
            CliTypes.Claude, ModelIds.ClaudeFable51, "unsupported"));
    }
}
