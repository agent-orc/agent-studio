using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The migration contract: which pins are offered an update, which of those
/// Studio may apply on its own, and what the audit trail looks like when it
/// does. The repository-baseline catalog is the fixture wherever the rules under
/// test are the shipped ones, so the tests fail if that file drifts.
/// </summary>
public class ModelMigrationTests
{
    private static ModelMigrationCatalogDocument Baseline()
        => ModelMigrationCatalogService.ReadEmbedded();

    private static readonly List<CliModelInfo> LiveClaude =
    [
        new() { Id = ModelIds.ClaudeOpus5, Label = "Claude Opus 5", Available = true,
                ThinkingLevels = ["low", "medium", "high", "xhigh", "max"], DefaultThinkingLevel = "high" },
        new() { Id = ModelIds.ClaudeSonnet5, Label = "Claude Sonnet 5", Available = true },
        new() { Id = ModelIds.ClaudeHaiku45, Label = "Claude Haiku 4.5", Available = true },
    ];

    // ── Proposal detection ────────────────────────────────────────────────

    [Fact]
    public void Propose_OffersTheTokenEconomyValueTierForAPinnedHaiku45()
    {
        // Haiku 4.5 IS the newest Haiku, so the family rule has nothing to say.
        // The offer exists only because the Token Economy catalog asserts a
        // cross-family move - and a family change is never automatic.
        var proposal = ModelMigrationPlanner.Propose(ModelIds.ClaudeHaiku45, Baseline(), LiveClaude);

        Assert.NotNull(proposal);
        Assert.Equal(ModelIds.ClaudeHaiku45, proposal!.From.ModelId);
        Assert.Equal(ModelIds.ClaudeSonnet5, proposal.To.ModelId);
        Assert.Equal("token-economy-value-tier", proposal.Rule);
        Assert.False(proposal.SafeAuto);
        Assert.Equal("catalog", proposal.SafeAutoBlockedBy);
        Assert.Equal(Baseline().Version, proposal.CatalogVersion);
    }

    [Fact]
    public void Propose_DerivesTheSupersededOpusPinFromTheFamilyRule()
    {
        var proposal = ModelMigrationPlanner.Propose(ModelIds.ClaudeOpus48, Baseline(), LiveClaude);

        Assert.NotNull(proposal);
        Assert.Equal(ModelIds.ClaudeOpus5, proposal!.To.ModelId);
        Assert.Equal("same-family-newer-generation", proposal.Rule);
        // The diff the UI renders comes from the proposal, not from a second
        // client-side lookup: both ladders are present.
        Assert.Contains("xhigh", proposal.To.ThinkingLevels);
    }

    [Fact]
    public void Propose_ResolvesAnAliasedPinToItsCanonicalFamilyMember()
        => Assert.Equal(
            ModelIds.ClaudeOpus5,
            ModelMigrationPlanner.Propose("claude-opus-4.8", Baseline(), LiveClaude)?.To.ModelId);

    [Fact]
    public void Propose_ReturnsNothingForTheNewestMemberOfItsFamily()
        => Assert.Null(ModelMigrationPlanner.Propose(ModelIds.ClaudeOpus5, Baseline(), LiveClaude));

    [Fact]
    public void Propose_ReturnsNothingForAModelOutsideEveryKnownFamily()
        => Assert.Null(ModelMigrationPlanner.Propose(ModelIds.Gemini25Pro, Baseline(), LiveClaude));

    [Fact]
    public void Propose_BlocksTheAutomaticPathWhenTheTargetLosesAReasoningLevel()
    {
        // A target that cannot do what the current pin does is an offer, never
        // an automatic change: silently dropping xhigh would spend correctness
        // margin without an operator seeing it.
        var narrowed = new List<CliModelInfo>
        {
            new() { Id = ModelIds.ClaudeOpus5, Label = "Claude Opus 5", Available = true,
                    ThinkingLevels = ["low", "medium"] },
            new() { Id = ModelIds.ClaudeOpus48, Label = "Claude Opus 4.8", Available = true,
                    ThinkingLevels = ["low", "medium", "high", "xhigh"] },
        };

        var proposal = ModelMigrationPlanner.Propose(ModelIds.ClaudeOpus48, Baseline(), narrowed);

        Assert.NotNull(proposal);
        Assert.False(proposal!.LadderCompatible);
        Assert.False(proposal.SafeAuto);
        Assert.Equal("reasoning-ladder", proposal.SafeAutoBlockedBy);
    }

    // ── Admission matrix ──────────────────────────────────────────────────

    [Theory]
    // A superseded, non-explicit model with the switch on: the one case that applies.
    [InlineData(ModelIds.ClaudeOpus48, false, true, true)]
    // An explicit pin is never touched, however superseded it is.
    [InlineData(ModelIds.ClaudeOpus48, true, true, false)]
    // The workspace switch turns automatic application off; the offer remains.
    [InlineData(ModelIds.ClaudeOpus48, false, false, false)]
    // Offer-only migrations (cross-family) never apply themselves.
    [InlineData(ModelIds.ClaudeHaiku45, false, true, false)]
    // Nothing to migrate.
    [InlineData(ModelIds.ClaudeOpus5, false, true, false)]
    [InlineData("", false, true, false)]
    public void Decide_AppliesOnlyToUnpinnedSupersededModelsWithTheSwitchOn(
        string model, bool modelExplicit, bool autoApply, bool expectApplied)
    {
        var decision = ModelMigrationAdmission.Decide(
            model, modelExplicit, autoApply, Baseline(), LiveClaude);

        Assert.Equal(expectApplied, decision != null);
    }

    // ── Catalog loading ───────────────────────────────────────────────────

    [Fact]
    public void Load_PrefersTheTokenEconomyCatalogAndReportsItsVersion()
    {
        using var workspace = new TempDir();
        var catalogPath = Path.Combine(workspace.Path, "te-migrations.json");
        File.WriteAllText(catalogPath, """
        {
          "version": "te-2026-09-06",
          "familyRule": { "id": "same-family-newer-generation", "safeAuto": true },
          "migrations": []
        }
        """);

        var service = BuildCatalogService(("TokenEconomy:MigrationCatalogPath", catalogPath));
        var (catalog, source) = service.Load();

        Assert.Equal("te-2026-09-06", catalog.Version);
        Assert.Equal(ModelMigrationCatalogService.TokenEconomySource, source);
    }

    [Fact]
    public void Load_FallsBackToTheRepositoryBaselineWhenTheTokenEconomyFileIsUnusable()
    {
        using var workspace = new TempDir();
        var catalogPath = Path.Combine(workspace.Path, "broken.json");
        File.WriteAllText(catalogPath, "{ not json");

        var service = BuildCatalogService(("TokenEconomy:MigrationCatalogPath", catalogPath));
        var (catalog, source) = service.Load();

        Assert.Equal(Baseline().Version, catalog.Version);
        Assert.Equal(ModelMigrationCatalogService.EmbeddedSource, source);
    }

    [Fact]
    public void Validate_RejectsAMigrationThatPointsAtItself()
        => Assert.Throws<InvalidOperationException>(() =>
            ModelMigrationCatalogService.Validate(new ModelMigrationCatalogDocument
            {
                Version = "x",
                Migrations = [new() { From = "a", To = "a", Rule = "r" }],
            }));

    // ── Application and audit trail ───────────────────────────────────────

    [Fact]
    public void Apply_MigratesTheCardKeepsItNonExplicitAndWritesTheTimelineEvent()
    {
        using var workspace = new TempDir();
        var job = CreateJob(workspace.Path, ModelIds.ClaudeOpus48, modelExplicit: false);
        var applier = BuildApplier(workspace.Path);

        var migrated = applier.Apply(job, project: "PROJ");

        Assert.Equal(ModelIds.ClaudeOpus5, migrated.Model);
        Assert.Equal(ModelIds.ClaudeOpus5, ReadCardField(job.FolderPath, "model"));
        // The card stays policy-routed: migrating a default must not turn it
        // into an operator pin, or the next migration would be refused.
        Assert.False(ReadCardBool(job.FolderPath, "modelExplicit"));

        var events = Timeline.ReadAll(job.FolderPath);
        var migrationEvent = Assert.Single(events, e => e.Kind == TimelineEventKinds.ModelMigrated);
        Assert.Equal(TimelineActors.Orchestrator, migrationEvent.Actor);
        Assert.Equal(ModelIds.ClaudeOpus48, migrationEvent.Details?["from"]);
        Assert.Equal(ModelIds.ClaudeOpus5, migrationEvent.Details?["to"]);
        Assert.Equal("same-family-newer-generation", migrationEvent.Details?["rule"]);
        Assert.Equal(Baseline().Version, migrationEvent.Details?["catalogVersion"]);
        Assert.Equal(ModelMigrationCatalogService.EmbeddedSource, migrationEvent.Details?["catalogSource"]);
    }

    [Fact]
    public void Apply_LeavesAnExplicitPinAloneAndWritesNoEvent()
    {
        using var workspace = new TempDir();
        var job = CreateJob(workspace.Path, ModelIds.ClaudeOpus48, modelExplicit: true);
        var applier = BuildApplier(workspace.Path);

        var unchanged = applier.Apply(job, project: "PROJ");

        Assert.Equal(ModelIds.ClaudeOpus48, unchanged.Model);
        Assert.Equal(ModelIds.ClaudeOpus48, ReadCardField(job.FolderPath, "model"));
        Assert.DoesNotContain(
            Timeline.ReadAll(job.FolderPath), e => e.Kind == TimelineEventKinds.ModelMigrated);
    }

    [Fact]
    public void Apply_HonoursTheWorkspaceSwitchThatTurnsAutomaticApplicationOff()
    {
        using var workspace = new TempDir();
        var job = CreateJob(workspace.Path, ModelIds.ClaudeOpus48, modelExplicit: false);
        var state = BuildStateStore(workspace.Path);
        state.SetAutoApplyModelMigrations(false);

        var unchanged = BuildApplier(workspace.Path, state).Apply(job, project: "PROJ");

        Assert.Equal(ModelIds.ClaudeOpus48, unchanged.Model);
        Assert.DoesNotContain(
            Timeline.ReadAll(job.FolderPath), e => e.Kind == TimelineEventKinds.ModelMigrated);
    }

    [Fact]
    public void SetEconomyMode_DoesNotResetTheAutoApplySwitch()
    {
        using var workspace = new TempDir();
        var state = BuildStateStore(workspace.Path);
        state.SetAutoApplyModelMigrations(false);

        state.SetEconomyMode(true);

        Assert.True(state.EconomyMode);
        Assert.False(state.AutoApplyModelMigrations);
        // And the value survives a reload from disk, not just the in-memory copy.
        Assert.False(BuildStateStore(workspace.Path).AutoApplyModelMigrations);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────

    private static readonly TimelineLog Timeline = new(NullLogger<TimelineLog>.Instance);

    private static ModelMigrationCatalogService BuildCatalogService(params (string Key, string Value)[] settings)
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build(),
            NullLogger<ModelMigrationCatalogService>.Instance);

    private static ModelRoutingPolicyStateStore BuildStateStore(string workspaceRoot)
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("TaskRepository", workspaceRoot)])
                .Build(),
            NullLogger<ModelRoutingPolicyStateStore>.Instance);

    private static ModelMigrationApplier BuildApplier(
        string workspaceRoot,
        ModelRoutingPolicyStateStore? state = null)
        => new(
            // No TokenEconomy:MigrationCatalogPath, and the workspace has no
            // .metadata catalog, so the repository baseline is what applies.
            BuildCatalogService(("TaskRepository", workspaceRoot)),
            state ?? BuildStateStore(workspaceRoot),
            NullLogger<ModelMigrationApplier>.Instance,
            Timeline);

    private static TaskInfo CreateJob(string workspaceRoot, string model, bool modelExplicit)
    {
        var folder = Path.Combine(workspaceRoot, "3-progress", "AGT-1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new { id = "AGT-1", model, modelExplicit }));
        return new TaskInfo
        {
            Id = "AGT-1",
            FolderPath = folder,
            Model = model,
            ModelExplicit = modelExplicit,
        };
    }

    private static string? ReadCardField(string folder, string field)
        => ReadCard(folder).GetProperty(field).GetString();

    private static bool ReadCardBool(string folder, string field)
        => ReadCard(folder).GetProperty(field).GetBoolean();

    private static JsonElement ReadCard(string folder)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json"))).RootElement;

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "agt-2716-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception) { /* a leaked temp dir must not fail a green test */ }
        }
    }
}
