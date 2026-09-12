

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Parser and merged-catalog coverage for Codex model discovery.
///
/// Fixture provenance: <c>Fixtures/cli/codex/debug-models-v0.153.4.json</c> is a
/// real field-trimmed <c>codex debug models</c> capture from codex-cli 0.153.4,
/// installed under <c>/tmp/codex-0153</c> with npm on 2026-09-11 for AGT-2772.
/// It preserves the model order plus every field consumed by this parser. The
/// <c>v0.151.0</c> fixture is the same catalog before astra was published.
/// <c>debug-models-v0.144.1.json</c> is a real, unedited (field-trimmed)
/// <c>codex debug models</c> capture from codex-cli 0.144.1 on a ChatGPT
/// account (agent-runner-01, 2026-09-11, AGT-2707 round 2): it lists
/// <c>gpt-5.6-sol</c>, <c>gpt-5.6-terra</c>, and <c>gpt-5.6-luna</c>, and omits
/// <c>gpt-6-astra</c>, <c>gpt-5.4-mini</c>, and <c>gpt-5-codex</c> entirely
/// (the live account rejects those three with HTTP 400).
///
/// Shares <see cref="CodexDetectedDefaultCollection"/> because the parser reads
/// the process-global detected ladder/default when a model reports neither.
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public class CodexModelDiscoveryTests : IDisposable
{
    public CodexModelDiscoveryTests() => ResetDetection();

    public void Dispose() => ResetDetection();

    private static void ResetDetection()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        ModelMetadataRegistry.SetDetectedCodexLadders(null);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli", "codex", name));

    [Fact]
    public void ParseDebugModelsJson_TakesTheReasoningLadderAndDefaultFromTheCli_OnlyForOnboardedModels()
    {
        // codex-cli 0.153.4 lists gpt-6-astra with a ladder the static
        // CliThinkingLevels table does not know: it has no `minimal`, and it
        // carries both `max` and `ultra` (AGT-2707).
        var models = CodexModelDiscovery.ParseDebugModelsJson(
            Fixture("debug-models-v0.153.4.json"), activeModel: ModelIds.Gpt6Astra);

        var astra = Assert.Single(models, m => m.Id == ModelIds.Gpt6Astra);
        Assert.Equal("GPT-6-Astra", astra.Label);
        Assert.True(astra.IsDefault);
        Assert.True(astra.Available);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], astra.ThinkingLevels);
        Assert.Equal("medium", astra.DefaultThinkingLevel);
        // gpt-6-astra is the only model onboarded for a live-discovered
        // ladder; every other model - gpt-5.6-sol included - ignores the same
        // fixture's supported_reasoning_levels/default_reasoning_level and
        // keeps the static top-of-ladder rule byte-for-byte (2026-09-07
        // review: a CLI-stated default must not silently override an
        // already-shipped model's product default; see
        // CodexDetectedDefaultTests.Gpt56Ladder_And_Default_StayByteForByte_WhenCliReportsADifferentOne).
        Assert.Equal(
            ModelMetadataRegistry.StaticThinkingLevelsFor(CliTypes.Codex, ModelIds.Gpt56Sol).Last(),
            Assert.Single(models, m => m.Id == ModelIds.Gpt56Sol).DefaultThinkingLevel);
        Assert.Equal(
            ModelMetadataRegistry.StaticThinkingLevelsFor(CliTypes.Codex, ModelIds.Gpt55).Last(),
            Assert.Single(models, m => m.Id == ModelIds.Gpt55).DefaultThinkingLevel);
        // Registry-known models carry no "missing metadata" note; the rest do.
        Assert.Null(Assert.Single(models, m => m.Id == ModelIds.Gpt55).AvailabilityNote);
        Assert.Equal("Discovered from CLI; missing registry metadata.",
            Assert.Single(models, m => m.Id == ModelIds.Gpt56Sol).AvailabilityNote);
    }

    [Fact]
    public void ParseDebugModelsJson_UsesTheTopRung_WhenTheCliStatesNoDefault()
    {
        const string output = """
        {"models":[
          {"slug":"gpt-6-astra","display_name":"GPT-6-Astra","visibility":"list","priority":1,
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"max"}]}
        ]}
        """;

        var astra = Assert.Single(CodexModelDiscovery.ParseDebugModelsJson(output));

        Assert.Equal(["low", "medium", "max"], astra.ThinkingLevels);
        Assert.Equal("max", astra.DefaultThinkingLevel);
    }

    [Fact]
    public void WithKnownButUnavailableModels_ShowsAstraDisabled_WhenTheCliDoesNotListIt()
    {
        // Acceptance: on a codex-cli without astra the model stays visible and
        // is disabled with an attributable note, never silently hidden.
        var catalog = new CliModelCatalog
        {
            Models = CodexModelDiscovery.ParseDebugModelsJson(Fixture("debug-models-v0.151.0.json")),
            Source = "cli-pty",
            FetchedAt = DateTime.UtcNow
        };

        var merged = CodexModelDiscovery.WithKnownButUnavailableModels(catalog, "0.151.0");

        var astra = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt6Astra);
        Assert.False(astra.Available);
        Assert.False(astra.Deprecated);
        Assert.False(astra.IsDefault);
        Assert.Equal("Needs codex-cli ≥ 0.153 (host has 0.151.0).", astra.AvailabilityNote);
        // Live entries are untouched and still lead the list.
        Assert.Equal(ModelIds.Gpt56Sol, merged.Models[0].Id);
        Assert.All(merged.Models.Where(m => !m.Available), m => Assert.NotNull(m.AvailabilityNote));
        // A merged-in unavailable entry can never become the detected default.
        Assert.Equal(ModelIds.Gpt56Sol, CodexModelDiscovery.PickDetectedDefault(merged));
    }

    [Fact]
    public void WithKnownButUnavailableModels_OnRealCodex01534Catalog_AstraUsesCliLadder_SolStaysStatic_MiniDisabled()
    {
        var catalog = new CliModelCatalog
        {
            Models = CodexModelDiscovery.ParseDebugModelsJson(Fixture("debug-models-v0.153.4.json")),
            Source = "cli-pty",
            FetchedAt = DateTime.UtcNow
        };

        var merged = CodexModelDiscovery.WithKnownButUnavailableModels(catalog, "0.153.4");

        var astra = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt6Astra);
        Assert.True(astra.Available);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], astra.ThinkingLevels);
        Assert.Equal("medium", astra.DefaultThinkingLevel);

        // Although this CLI reports low as Sol's default and omits minimal from
        // its ladder, already-shipped models retain the product's static order
        // and top-rung default.
        var sol = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt56Sol);
        Assert.True(sol.Available);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh", "ultra"], sol.ThinkingLevels);
        Assert.Equal("ultra", sol.DefaultThinkingLevel);

        // The real 0.153.4 catalog does not list Mini, so registry merging keeps
        // it visible but disabled with the installed-version explanation.
        var mini = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt54Mini);
        Assert.False(mini.Available);
        Assert.Equal("Not offered by the installed codex-cli 0.153.4.", mini.AvailabilityNote);

        // gpt-5-codex is a registry model the 0.153.4 catalog no longer lists.
        var codex = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt5Codex);
        Assert.False(codex.Available);
        Assert.Equal("Not offered by the installed codex-cli 0.153.4.", codex.AvailabilityNote);
    }

    [Fact]
    public void WithKnownButUnavailableModels_OnRealCodex0144Catalog_TerraAndLunaSelectable_MiniCodexAstraDisabled()
    {
        // Real codex-cli 0.144.1 evidence (AGT-2707 round 2, 2026-09-11): the
        // installed CLI lists sol/terra/luna but not astra, mini, or gpt-5-codex.
        // Terra and luna are now registry entries (round 2), so they carry no
        // "missing registry metadata" note even though sol - which still has no
        // registry entry (AGT-2025) - does.
        var catalog = new CliModelCatalog
        {
            Models = CodexModelDiscovery.ParseDebugModelsJson(
                Fixture("debug-models-v0.144.1.json"), activeModel: ModelIds.Gpt56Sol),
            Source = "cli-pty",
            FetchedAt = DateTime.UtcNow
        };

        var merged = CodexModelDiscovery.WithKnownButUnavailableModels(catalog, "0.144.1");

        var sol = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt56Sol);
        Assert.True(sol.Available);
        Assert.True(sol.IsDefault);
        Assert.Equal("Discovered from CLI; missing registry metadata.", sol.AvailabilityNote);

        var terra = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt56Terra);
        Assert.True(terra.Available);
        Assert.Null(terra.AvailabilityNote);

        var luna = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt56Luna);
        Assert.True(luna.Available);
        Assert.Null(luna.AvailabilityNote);

        var mini = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt54Mini);
        Assert.False(mini.Available);
        Assert.Equal("Not offered by the installed codex-cli 0.144.1.", mini.AvailabilityNote);

        var codex = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt5Codex);
        Assert.False(codex.Available);
        Assert.Equal("Not offered by the installed codex-cli 0.144.1.", codex.AvailabilityNote);

        var astra = Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt6Astra);
        Assert.False(astra.Available);
        Assert.Equal("Needs codex-cli ≥ 0.153 (host has 0.144.1).", astra.AvailabilityNote);
    }

    [Fact]
    public void WithKnownButUnavailableModels_OmitsTheVersion_WhenNoProbeHasSeenOne()
    {
        var merged = CodexModelDiscovery.WithKnownButUnavailableModels(
            new CliModelCatalog { Models = [], Source = "test", FetchedAt = DateTime.UtcNow },
            cliVersion: null);

        Assert.Equal(
            "Not offered by the installed codex-cli.",
            Assert.Single(merged.Models, m => m.Id == ModelIds.Gpt6Astra).AvailabilityNote);
    }

    [Fact]
    public void Registry_OnboardsAstraWithoutMakingItTheDefault()
    {
        var astra = ModelMetadataRegistry.Find(ModelIds.Gpt6Astra);

        Assert.NotNull(astra);
        Assert.Equal("GPT-6 Astra", astra.Label);
        Assert.Equal("openai", astra.Vendor);
        Assert.Equal(272_000, astra.ContextWindow);
        Assert.False(astra.IsDefault);
        Assert.False(astra.Deprecated);
        // Pricing stays a live TokenEconomy catalog pass-through.
        Assert.Equal(10.00m, astra.InputPricePerMillion);
        Assert.Equal(50.00m, astra.OutputPricePerMillion);
        // The product default is unchanged by onboarding astra.
        Assert.Equal(ModelIds.Gpt55, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    [Fact]
    public void Registry_OnboardsTerraLunaAndMini_WithoutMakingAnyOfThemTheDefault()
    {
        foreach (var (id, label) in new[]
                 {
                     (ModelIds.Gpt56Terra, "GPT-5.6 Terra"),
                     (ModelIds.Gpt56Luna, "GPT-5.6 Luna"),
                     (ModelIds.Gpt54Mini, "GPT-5.4 Mini")
                 })
        {
            var metadata = ModelMetadataRegistry.Find(id);
            Assert.NotNull(metadata);
            Assert.Equal(label, metadata.Label);
            Assert.Equal("openai", metadata.Vendor);
            Assert.Equal(272_000, metadata.ContextWindow);
            Assert.False(metadata.IsDefault);
            Assert.False(metadata.Deprecated);
        }

        // gpt-5.6-sol still deliberately has no registry entry (AGT-2025): the
        // flagship's availability stays purely detection-driven.
        Assert.Null(ModelMetadataRegistry.Find(ModelIds.Gpt56Sol));

        // The whole gpt-5.6 family stays detection-only: terra/luna's registry
        // baseline is Available:false so a total CLI-probe failure never assumes
        // one is offered. Mini has no such restriction.
        Assert.False(ModelMetadataRegistry.Find(ModelIds.Gpt56Terra)!.Available);
        Assert.False(ModelMetadataRegistry.Find(ModelIds.Gpt56Luna)!.Available);
        Assert.True(ModelMetadataRegistry.Find(ModelIds.Gpt54Mini)!.Available);

        // The product default and ladder/default resolution are unchanged by
        // onboarding any of these three (regression coverage for the ladder side
        // lives in CodexDetectedDefaultTests.Gpt56Ladder_And_Default_StayByteForByte_WhenCliReportsADifferentOne).
        Assert.Equal(ModelIds.Gpt55, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }


    [Fact]
    public void ParseDebugModelsJson_ReturnsVisibleModelsInPriorityOrder()
    {
        const string output = """
        leading terminal noise
        {"models":[
          {"slug":"gpt-5.4-mini","display_name":"GPT-5.4-Mini","visibility":"list","priority":4},
          {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":29},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":0},
          {"slug":"gpt-5.4","display_name":"gpt-5.4","visibility":"list","priority":2}
        ]}
        trailing terminal noise
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output, activeModel: "gpt-5.4");

        Assert.Equal(["gpt-5.5", "gpt-5.4", "gpt-5.4-mini"], models.Select(m => m.Id));
        Assert.DoesNotContain(models, m => m.Id == "codex-auto-review");
        Assert.Equal("gpt-5.4", Assert.Single(models, m => m.IsDefault).Id);
        Assert.All(models, m => Assert.Equal("openai", m.Vendor));
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh"],
            Assert.Single(models, m => m.Id == "gpt-5.5").ThinkingLevels);
        Assert.Equal(["minimal", "low", "medium", "high"],
            Assert.Single(models, m => m.Id == "gpt-5.4").ThinkingLevels);
        Assert.Equal(["minimal", "low", "medium", "high"],
            Assert.Single(models, m => m.Id == "gpt-5.4-mini").ThinkingLevels);
        // Per-model default reasoning is the top of each model's CLI ladder
        // (AGT-2025): gpt-5.5 exposes xhigh; the gpt-5.4 family tops at high.
        Assert.Equal("xhigh", Assert.Single(models, m => m.Id == "gpt-5.5").DefaultThinkingLevel);
        Assert.Equal("high", Assert.Single(models, m => m.Id == "gpt-5.4").DefaultThinkingLevel);
        Assert.Equal("high", Assert.Single(models, m => m.Id == "gpt-5.4-mini").DefaultThinkingLevel);
    }

    [Fact]
    public void ParseDebugModelsJson_SurfacesGpt56_FollowingTheLiveCli()
    {
        // Mirrors the live `codex debug models` shape (codex-cli 0.144.0): the
        // gpt-5.6 family is list-visible and ranks first. The catalog must
        // surface it with the extended reasoning ladder (xhigh + ultra) and a
        // top-of-ladder default, so nothing about gpt-5.6 is hardwired here.
        const string output = """
        {"models":[
          {"slug":"gpt-5.6-sol","display_name":"GPT-5.6-Sol","visibility":"list","priority":1},
          {"slug":"gpt-5.6-terra","display_name":"GPT-5.6-Terra","visibility":"list","priority":2},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":7},
          {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":43}
        ]}
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output, activeModel: "gpt-5.6-sol");

        Assert.Equal(["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.5"], models.Select(m => m.Id));
        Assert.DoesNotContain(models, m => m.Id == "codex-auto-review");
        var sol = Assert.Single(models, m => m.Id == "gpt-5.6-sol");
        Assert.True(sol.IsDefault);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh", "ultra"], sol.ThinkingLevels);
        Assert.Equal("ultra", sol.DefaultThinkingLevel);
    }

    [Fact]
    public void PickDetectedDefault_ReturnsHighestPriorityGpt56_WhenNoneFlagged()
    {
        var cat = Catalog(
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-5.6-terra", isDefault: false),
            Model("gpt-5.5", isDefault: false));

        Assert.Equal("gpt-5.6-sol", CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_FollowsTheCliFlaggedDefault_WhenItIsGpt56()
    {
        // config.toml pins gpt-5.6-terra even though sol ranks first: respect it.
        var cat = Catalog(
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-5.6-terra", isDefault: true),
            Model("gpt-5.5", isDefault: false));

        Assert.Equal("gpt-5.6-terra", CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_IgnoresNonGpt56FlaggedDefault()
    {
        // The CLI's active model is an older gpt-5.5, but a gpt-5.6 is list-
        // visible, so "as soon as 5.6 is detected" it becomes the default.
        var cat = Catalog(
            Model("gpt-5.5", isDefault: true),
            Model("gpt-5.6-sol", isDefault: false));

        Assert.Equal("gpt-5.6-sol", CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_ReturnsNull_WhenNoGpt56Present()
    {
        var cat = Catalog(
            Model("gpt-5.5", isDefault: true),
            Model("gpt-5.4", isDefault: false));

        Assert.Null(CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void FallbackCatalog_OffersRegistryOpenAiModels_WithGpt55Default_AndNoGpt56()
    {
        // Task item 1: with no CLI and no cache, the model surface falls back to
        // today's static registry list. gpt-5.6 is detection-only (AGT-2025), so
        // it must NOT appear here even now that gpt-5.6-terra/luna are registry
        // entries (AGT-2707 round 2: their Available baseline is false for
        // exactly this reason), and gpt-5.5 stays the default.
        var catalog = CodexModelDiscovery.FallbackCatalog();

        Assert.NotEmpty(catalog.Models);
        Assert.All(catalog.Models, m => Assert.Equal("openai", m.Vendor));
        Assert.Contains(catalog.Models, m => m.Id == ModelIds.Gpt55);
        Assert.DoesNotContain(catalog.Models, m => m.Id.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ModelIds.Gpt55, Assert.Single(catalog.Models, m => m.IsDefault).Id);
        // And it advertises no gpt-5.6 default, so the published default stays gpt-5.5.
        Assert.Null(CodexModelDiscovery.PickDetectedDefault(catalog));
    }

    private static CliModelCatalog Catalog(params CliModelInfo[] models)
        => new() { Models = models.ToList(), Source = "test", FetchedAt = DateTime.UtcNow };

    private static CliModelInfo Model(string id, bool isDefault)
        => new() { Id = id, Label = id, Vendor = "openai", IsDefault = isDefault };

    [Fact]
    public void ParseDebugModelsJson_FallsBackToFirstVisibleModelAsDefault()
    {
        const string output = """
        {"models":[
          {"slug":"gpt-5.4","display_name":"gpt-5.4","visibility":"list","priority":2},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":0}
        ]}
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output);

        Assert.Equal("gpt-5.5", Assert.Single(models, m => m.IsDefault).Id);
    }

    [Fact]
    public void WithCurrentCodexCapabilities_KeepsCliLadders_AndFillsOnlyMissingOnes()
    {
        // A cached ladder came from the CLI itself, so recomputing it from the
        // static table would reintroduce exactly the drift AGT-2707 removed.
        // Only an entry with no ladder at all gets the static answer.
        var cached = new CliModelCatalog
        {
            Source = "disk-cache",
            FetchedAt = DateTime.UtcNow,
            Models =
            [
                new CliModelInfo
                {
                    Id = ModelIds.Gpt6Astra,
                    Label = "GPT-6-Astra",
                    Vendor = "openai",
                    ThinkingLevels = ["low", "medium", "high", "xhigh", "max", "ultra"],
                    DefaultThinkingLevel = "medium"
                },
                new CliModelInfo
                {
                    Id = ModelIds.Gpt55,
                    Label = "GPT-5.5",
                    Vendor = "openai",
                    ThinkingLevels = []
                }
            ]
        };

        var updated = CodexModelDiscovery.WithCurrentCodexCapabilities(cached);

        var astra = Assert.Single(updated.Models, m => m.Id == ModelIds.Gpt6Astra);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], astra.ThinkingLevels);
        Assert.Equal("medium", astra.DefaultThinkingLevel);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh"],
            Assert.Single(updated.Models, m => m.Id == ModelIds.Gpt55).ThinkingLevels);
    }
}
