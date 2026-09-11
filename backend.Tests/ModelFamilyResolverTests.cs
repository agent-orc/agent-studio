using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Serializes every test that reads or writes the process-global vendor
/// availability publication (<see cref="ModelMetadataRegistry.SetDetectedVendorAvailability"/>),
/// same rationale as <see cref="CodexDetectedDefaultCollection"/>.
/// </summary>
[CollectionDefinition(ModelFamilyResolverCollection.Name, DisableParallelization = true)]
public sealed class ModelFamilyResolverCollection
{
    public const string Name = "ModelFamilyResolver";
}

/// <summary>
/// Coverage for <see cref="ModelFamilyResolver"/> (AGT-2716): resolves the
/// newest available model per family from live-discovered vendor availability,
/// falling back to the static registry's Available/Deprecated flags and
/// declared (newest-first) generation order when discovery has not run.
/// Fixture catalogs mirror the 2026-09-06 fact check against the installed
/// Claude Code 2.1.263 /model picker and codex-cli: Opus 5 (default),
/// Fable 5.1, Sonnet 5, Haiku 4.5 for Claude; gpt-5.5 baseline / gpt-5.6-sol
/// once detected for Codex.
/// </summary>
[Collection(ModelFamilyResolverCollection.Name)]
public sealed class ModelFamilyResolverTests : IDisposable
{
    public ModelFamilyResolverTests() => ModelMetadataRegistry.ClearDetectedVendorAvailability();

    public void Dispose()
    {
        ModelMetadataRegistry.ClearDetectedVendorAvailability();
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
    }

    [Fact]
    public void ClaudeHaiku_ResolvesToOnlyMember_NoNewerGenerationExists()
    {
        // 2026-09-06 fact check: there is no Haiku 5. "Latest in family" keeps
        // claude-haiku-4-5 today; this is the whole reason the migration
        // catalog carries no haiku entry.
        Assert.Equal(ModelIds.ClaudeHaiku45, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku));
    }

    [Fact]
    public void ClaudeSonnet_ResolvesToNewestGeneration_FromStaticRegistry_WhenNothingDetected()
    {
        Assert.Equal(ModelIds.ClaudeSonnet5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet));
    }

    [Fact]
    public void ClaudeOpus_ResolvesToNewestGeneration_FromStaticRegistry_WhenNothingDetected()
    {
        Assert.Equal(ModelIds.ClaudeOpus5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus));
    }

    [Fact]
    public void ClaudeSonnet_FollowsLiveCatalog_WhenNewestGenerationIsMissingFromIt()
    {
        // Simulate a live Claude catalog (e.g. a stale CLI) that has not
        // caught up to sonnet-5 yet: discovery only reports the older members.
        ModelMetadataRegistry.SetDetectedVendorAvailability(
            "anthropic", [ModelIds.ClaudeOpus5, ModelIds.ClaudeSonnet46, ModelIds.ClaudeSonnet45, ModelIds.ClaudeHaiku45]);

        Assert.Equal(ModelIds.ClaudeSonnet46, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet));
    }

    [Fact]
    public void ClaudeOpus_FollowsLiveCatalog_OnceItReportsTheNewestGeneration()
    {
        ModelMetadataRegistry.SetDetectedVendorAvailability(
            "anthropic", [ModelIds.ClaudeOpus5, ModelIds.ClaudeSonnet5, ModelIds.ClaudeHaiku45]);

        Assert.Equal(ModelIds.ClaudeOpus5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus));
    }

    [Fact]
    public void ClaudeOpus_FallsBackToNewestDeclaredMember_WhenLiveCatalogReportsNoFamilyMemberAtAll()
    {
        // Discovery ran (vendor key present) but somehow reported nothing for
        // this family (e.g. a transient parse failure) - the resolver must
        // still return a usable id rather than throwing or returning null.
        ModelMetadataRegistry.SetDetectedVendorAvailability("anthropic", [ModelIds.ClaudeSonnet5]);

        Assert.Equal(ModelIds.ClaudeOpus5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus));
    }

    [Fact]
    public void GptMini_ResolvesToItsSoleMember()
    {
        Assert.Equal(ModelIds.Gpt54Mini, ModelFamilyResolver.Resolve(ModelFamilies.GptMini));
    }

    [Fact]
    public void GptFlagship_FallsBackToGpt55_WhenNothingDetected()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        Assert.Equal(ModelIds.Gpt55, ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship));
    }

    [Fact]
    public void GptFlagship_FollowsLiveCodexDetection_SameAsDefaultForCli()
    {
        // gpt-flagship is a thin alias over the already-live Codex detection
        // layer, so it must stay in lockstep with DefaultForCli rather than
        // duplicating the mechanism.
        ModelMetadataRegistry.SetDetectedCodexDefault(ModelIds.Gpt56Sol);
        Assert.Equal(ModelIds.Gpt56Sol, ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship));
        Assert.Equal(
            ModelMetadataRegistry.DefaultForCli(CliTypes.Codex),
            ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship));
    }

    [Fact]
    public void Resolve_UnknownFamily_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModelFamilyResolver.Resolve("claude-fable"));
    }

    [Fact]
    public void RuntimeDefaultsThatUsedToHardcodeHaiku_NowResolveViaTheFamily()
    {
        // Regression guard for the AGT-2716 literal sweep: every former
        // ModelIds.ClaudeHaiku45 / ModelIds.Gpt54Mini runtime default now
        // reads through the family resolver and lands on the same id today.
        Assert.Equal(ModelIds.ClaudeHaiku45, OrchestratorRunner.DefaultModel);
        Assert.Equal(ModelIds.ClaudeSonnet5, AgentStudio.Docs.WikiMaintenanceModelService.DefaultModel);
        Assert.Equal(ModelIds.ClaudeOpus5, AgentStudio.Cli.GenericCliExecutionService.DefaultOpusModel);
        Assert.Equal(ModelIds.Gpt54Mini, AgentStudio.Pipeline.PipelineStepModelDefaults.SupportModel);
        Assert.Equal(ModelIds.Gpt54Mini, AgentStudio.Drift.DriftPostStepRunner.DefaultModel);
    }
}
