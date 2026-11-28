

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
        // The gpt-5.6 family remains outside the live-discovered ladder
        // allowlist, so gpt-5.6-sol ignores the same
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
        Assert.Null(CodexModelDiscovery.PickDetectedDefault(merged));
    }

    [Fact]
    public void NewGpt6Models_require_codex_0155_and_live_discovery()
    {
        const string output = """
        {"models":[
          {"slug":"gpt-6-sol","display_name":"GPT-6 Sol","visibility":"list","priority":1,
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"}],
           "default_reasoning_level":"medium"}
        ]}
        """;
        var parsed = CodexModelDiscovery.ParseDebugModelsJson(output);
        var old = CodexModelDiscovery.WithKnownButUnavailableModels(
            new CliModelCatalog { Models = parsed }, "codex-cli 0.154.0");
        var oldSol = Assert.Single(old.Models, m => m.Id == ModelIds.Gpt6Sol);
        Assert.False(oldSol.Available);
        Assert.Contains("0.155", oldSol.AvailabilityNote);
        Assert.False(Assert.Single(old.Models, m => m.Id == ModelIds.Gpt6Luna).Available);

        var current = CodexModelDiscovery.WithKnownButUnavailableModels(
            new CliModelCatalog { Models = parsed }, "0.155.0");
        var sol = Assert.Single(current.Models, m => m.Id == ModelIds.Gpt6Sol);
        Assert.True(sol.Available);
        Assert.Equal(["low", "medium", "high"], sol.ThinkingLevels);
        Assert.Equal("medium", sol.DefaultThinkingLevel);
        Assert.False(Assert.Single(current.Models, m => m.Id == ModelIds.Gpt6Luna).Available);
    }

    [Fact]
    public void Gpt61Sol_RequiresVerifiedCliVersionAndLiveDiscovery()
    {
        const string output = """
        {"models":[{"slug":"gpt-6.1-sol","display_name":"GPT-6.1-Sol","visibility":"list","priority":1,
          "supported_reasoning_levels":[{"effort":"minimal"},{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"}],
          "default_reasoning_level":"xhigh"}]}
        """;
        var parsed = CodexModelDiscovery.ParseDebugModelsJson(output);
        var old = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = parsed }, "0.154.0");
        Assert.False(Assert.Single(old.Models, m => m.Id == ModelIds.Gpt61Sol).Available);

        var absent = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = [] }, "0.166.0");
        Assert.False(Assert.Single(absent.Models, m => m.Id == ModelIds.Gpt61Sol).Available);

        var belowFloor = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = parsed }, "0.165.0");
        Assert.False(Assert.Single(belowFloor.Models, m => m.Id == ModelIds.Gpt61Sol).Available);

        var current = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = parsed }, "0.166.0");
        var sol = Assert.Single(current.Models, m => m.Id == ModelIds.Gpt61Sol);
        Assert.True(sol.Available);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh"], sol.ThinkingLevels);
        Assert.Equal("xhigh", sol.DefaultThinkingLevel);
        Assert.Equal(1_050_000, ModelMetadataRegistry.ContextWindowFor(sol.Id));
        Assert.Equal(ModelIds.Gpt62Sol, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    // Shape of `codex debug models` on codex-cli 0.166.0 (runner-01, 2026-11-17).
    private const string Gpt62DebugModels = """
        {"models":[
          {"slug":"gpt-6.2-sol","display_name":"GPT-6.2-Sol","visibility":"list","priority":1,
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"},{"effort":"max"},{"effort":"ultra"}],
           "default_reasoning_level":"low"},
          {"slug":"gpt-6.2-astra","display_name":"GPT-6.2-Astra","visibility":"list","priority":2,
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"},{"effort":"max"},{"effort":"ultra"}],
           "default_reasoning_level":"low"},
          {"slug":"gpt-6-sol","display_name":"GPT-6-Sol","visibility":"list","priority":7,
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"}],
           "default_reasoning_level":"medium"}]}
        """;

    [Theory]
    [InlineData(ModelIds.Gpt62Sol, 500_000)]
    [InlineData(ModelIds.Gpt62Astra, 272_000)]
    public void Gpt62_IsListedWithItsFullCliLadder_AtTheVerifiedCliFloor(string model, long context)
    {
        var parsed = CodexModelDiscovery.ParseDebugModelsJson(Gpt62DebugModels);

        var belowFloor = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = parsed }, "0.165.0");
        var old = Assert.Single(belowFloor.Models, m => m.Id == model);
        Assert.False(old.Available);
        Assert.Contains("0.166", old.AvailabilityNote);

        var absent = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = [] }, "0.166.0");
        Assert.False(Assert.Single(absent.Models, m => m.Id == model).Available);

        var current = CodexModelDiscovery.WithKnownButUnavailableModels(new CliModelCatalog { Models = parsed }, "0.166.0");
        var listed = Assert.Single(current.Models, m => m.Id == model);
        Assert.True(listed.Available);
        Assert.Null(listed.AvailabilityNote);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], listed.ThinkingLevels);
        Assert.Equal("low", listed.DefaultThinkingLevel);
        Assert.Equal(context, ModelMetadataRegistry.ContextWindowFor(model));
    }

    [Fact]
    public void Sol_is_the_standard_route_with_or_without_a_local_catalogue()
    {
        var current = CodexModelDiscovery.WithKnownButUnavailableModels(
            new CliModelCatalog { Models = CodexModelDiscovery.ParseDebugModelsJson(Gpt62DebugModels) }, "0.166.0");

        Assert.Equal(ModelIds.Gpt62Sol, CodexModelDiscovery.PickDetectedDefault(current));
        Assert.Equal(ModelIds.Gpt62Sol, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
        Assert.True(ModelMetadataRegistry.Find(ModelIds.Gpt62Sol)!.IsDefault);
        Assert.False(ModelMetadataRegistry.Find(ModelIds.Gpt62Astra)!.IsDefault);
    }

    [Theory]
    [InlineData(ModelIds.Gpt62Sol)]
    [InlineData(ModelIds.Gpt62Astra)]
    public void Gpt62_StoredLevelSurvives_BeforeAndAfterLiveDiscovery(string model)
    {
        // Codex labels low as "Light" and xhigh as "Extra High"; Studio stores the CLI value.
        string[] ladder = ["low", "medium", "high", "xhigh", "max", "ultra"];
        Assert.Equal(ladder, ModelMetadataRegistry.ThinkingLevelsFor(CliTypes.Codex, model));
        Assert.Equal("medium", ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Codex, model));
        foreach (var level in ladder)
            Assert.Equal(level, ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Codex, model, level));
        Assert.Equal("ultra", ModelMetadataRegistry.NormalizeThinkingLevel(CliTypes.Codex, model, "ULTRA"));
        Assert.Equal("low", ModelMetadataRegistry.NormalizeThinkingLevel(CliTypes.Codex, model, "none"));

        ModelMetadataRegistry.SetDetectedCodexLadders(CodexModelDiscovery.ParseDebugModelsJson(Gpt62DebugModels));
        Assert.False(ModelMetadataRegistry.UsesLiveDiscoveredThinkingLadder(model));
        foreach (var level in ladder)
            Assert.Equal(level, ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Codex, model, level));
    }

    [Fact]
    public void Gpt61Terra_stays_out_of_Studio_catalogue_until_priced()
    {
        const string output = """
        {"models":[{"slug":"gpt-6.1-terra","display_name":"GPT-6.1 Terra","visibility":"list","priority":2,
          "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"}],"default_reasoning_level":"medium"}]}
        """;
        var parsed = CodexModelDiscovery.ParseDebugModelsJson(output);
        var old = CodexModelDiscovery.WithKnownButUnavailableModels(
            new CliModelCatalog { Models = parsed }, "0.165.0");
        Assert.DoesNotContain(old.Models, m => m.Id == AgentStudio.TaskServer.Contracts.CodexCliRelease.Gpt61Terra);

        var current = CodexModelDiscovery.WithKnownButUnavailableModels(
            new CliModelCatalog { Models = parsed }, "0.166.0");
        Assert.DoesNotContain(current.Models, m => m.Id == AgentStudio.TaskServer.Contracts.CodexCliRelease.Gpt61Terra);
        Assert.Null(ModelMetadataRegistry.Find(AgentStudio.TaskServer.Contracts.CodexCliRelease.Gpt61Terra));
        Assert.Equal(ModelIds.Gpt62Sol, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    [Fact]
    public void Gpt61Sol_UsesCliDefault_WhenItsFutureLadderChanges()
    {
        ModelMetadataRegistry.SetDetectedCodexLadders([
            new CliModelInfo
            {
                Id = ModelIds.Gpt61Sol,
                ThinkingLevels = ["low", "medium", "high"],
                DefaultThinkingLevel = "medium"
            }
        ]);

        Assert.Equal(["low", "medium", "high"], ModelMetadataRegistry.ThinkingLevelsFor(CliTypes.Codex, ModelIds.Gpt61Sol));
        Assert.Equal("medium", ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Codex, ModelIds.Gpt61Sol));
        Assert.Equal("medium", ModelMetadataRegistry.NormalizeThinkingLevel(CliTypes.Codex, ModelIds.Gpt61Sol, "unknown"));
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
        var price = TokenTestServices.Pricing().Estimate(astra.Id, 0, 0, 0, 0).PriceBasis;
        Assert.NotNull(price);
        Assert.Equal(10.00m, price.InputPerMillion);
        Assert.Equal(50.00m, price.OutputPerMillion);
        // The product default is unchanged by onboarding astra.
        Assert.Equal(ModelIds.Gpt62Sol, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    [Fact]
    public void Registry_OnboardsTerraAndLuna_AndRetainsMiniAsRetiredHistory()
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
            Assert.Equal(id == ModelIds.Gpt54Mini, metadata.Deprecated);
        }

        // gpt-5.6-sol still deliberately has no registry entry (AGT-2025): the
        // flagship's availability stays purely detection-driven.
        Assert.Null(ModelMetadataRegistry.Find(ModelIds.Gpt56Sol));

        // The whole gpt-5.6 family stays detection-only: terra/luna's registry
        // baseline is Available:false so a total CLI-probe failure never assumes
        // one is offered. Retired Mini is also unavailable.
        Assert.False(ModelMetadataRegistry.Find(ModelIds.Gpt56Terra)!.Available);
        Assert.False(ModelMetadataRegistry.Find(ModelIds.Gpt56Luna)!.Available);
        Assert.False(ModelMetadataRegistry.Find(ModelIds.Gpt54Mini)!.Available);

        // The product default and ladder/default resolution are unchanged by
        // onboarding any of these three (regression coverage for the ladder side
        // lives in CodexDetectedDefaultTests.Gpt56Ladder_And_Default_StayByteForByte_WhenCliReportsADifferentOne).
        Assert.Equal(ModelIds.Gpt62Sol, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
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
    public void PickDetectedDefault_ReturnsNull_ForAnOlderCatalogue()
    {
        var cat = Catalog(
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-5.6-terra", isDefault: false),
            Model("gpt-5.5", isDefault: false));

        Assert.Null(CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_ReturnsNull_WhenAnOlderModelIsFlaggedDefault()
    {
        var cat = Catalog(
            Model("gpt-5.6-terra", isDefault: true),
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-5.5", isDefault: false));

        Assert.Null(CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_FindsSolEvenWhenAstraIsFlaggedDefault()
    {
        var cat = Catalog(
            Model(ModelIds.Gpt62Astra, isDefault: true),
            Model(ModelIds.Gpt62Sol, isDefault: false),
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-6-sol", isDefault: false),
            Model("gpt-6-luna", isDefault: false));

        Assert.Equal(ModelIds.Gpt62Sol, CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_SkipsAnUnavailableGpt62Sol()
    {
        var cat = Catalog(
            Model(ModelIds.Gpt62Sol, isDefault: false) with { Available = false },
            Model("gpt-5.6-sol", isDefault: false));

        Assert.Null(CodexModelDiscovery.PickDetectedDefault(cat));
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
    public void FallbackCatalog_IdentifiesDefaultWithoutClaimingLiveAvailability()
    {
        var catalog = CodexModelDiscovery.FallbackCatalog();

        Assert.NotEmpty(catalog.Models);
        Assert.All(catalog.Models, m => Assert.Equal("openai", m.Vendor));
        Assert.Contains(catalog.Models, m => m.Id == ModelIds.Gpt55);
        Assert.DoesNotContain(catalog.Models, m => m.Id.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase));
        var model = Assert.Single(catalog.Models, candidate => candidate.IsDefault);
        Assert.Equal(ModelIds.Gpt62Sol, model.Id);
        Assert.False(model.Available);
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
