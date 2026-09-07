using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Serializes every test that reads or writes the process-global Codex
/// detected-default (<see cref="ModelMetadataRegistry.SetDetectedCodexDefault"/>).
/// xUnit runs collections in parallel by default, so without this the detection
/// tests could flip the default under the create-time default tests (AGT-2025).
/// </summary>
[CollectionDefinition(CodexDetectedDefaultCollection.Name, DisableParallelization = true)]
public sealed class CodexDetectedDefaultCollection
{
    public const string Name = "CodexDetectedDefault";
}

/// <summary>
/// Unit coverage for the detection-driven Codex default resolver (AGT-2025):
/// as soon as the installed codex CLI advertises a gpt-5.6-* model the product
/// default follows the CLI; with nothing detected the account-valid gpt-5.5
/// baseline (AGT-1941) holds. Reasoning-effort default is the top of the
/// CLI-derived ladder in both cases (gpt-5.6 -> ultra, gpt-5.5 -> xhigh).
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class CodexDetectedDefaultTests : IDisposable
{
    public CodexDetectedDefaultTests() => ModelMetadataRegistry.SetDetectedCodexDefault(null);

    // Never leak a detected default into sibling tests that assume the baseline.
    public void Dispose() => ModelMetadataRegistry.SetDetectedCodexDefault(null);

    [Fact]
    public void DefaultForCli_Codex_FallsBackToGpt55_WhenNothingDetected()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        Assert.Equal(ModelIds.Gpt55, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    [Fact]
    public void DefaultForCli_Codex_ReturnsDetected_WhenCliReportsGpt56()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(ModelIds.Gpt56Sol);
        Assert.Equal(ModelIds.Gpt56Sol, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    [Fact]
    public void DetectedDefault_DoesNotLeakToOtherVendors()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(ModelIds.Gpt56Sol);
        // A codex-only override must never bleed into the Claude/Gemini defaults.
        Assert.Equal(ModelIds.ClaudeOpus5, ModelMetadataRegistry.DefaultForCli(CliTypes.Claude));
        Assert.Equal(ModelIds.Gemini25Pro, ModelMetadataRegistry.DefaultForCli(CliTypes.Gemini));
    }

    [Fact]
    public void SetDetectedCodexDefault_BlankClearsBackToBaseline()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(ModelIds.Gpt56Sol);
        ModelMetadataRegistry.SetDetectedCodexDefault("   ");
        Assert.Null(ModelMetadataRegistry.DetectedCodexDefault);
        Assert.Equal(ModelIds.Gpt55, ModelMetadataRegistry.DefaultForCli(CliTypes.Codex));
    }

    [Fact]
    public void DefaultThinkingLevelForCli_Codex_IsTopOfLadder()
    {
        // gpt-5.6 exposes ultra (CAR-2 ladder); gpt-5.5 tops at xhigh.
        Assert.Equal("ultra", ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Codex, ModelIds.Gpt56Sol));
        Assert.Equal("xhigh", ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Codex, ModelIds.Gpt55));
    }

    [Fact]
    public void DefaultThinkingLevelForCli_Claude_KeepsLadderDefault()
    {
        // The codex-only "biggest reasoning value" policy must not change Claude.
        Assert.Equal(
            CliThinkingLevels.DefaultFor(CliTypes.Claude, ModelIds.ClaudeOpus48),
            ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Claude, ModelIds.ClaudeOpus48));
    }

    [Fact]
    public void ResolveThinkingLevel_HonorsExplicit_ElseTopOfLadder()
    {
        // Explicit choice wins (normalized to the model ladder)...
        Assert.Equal("high", ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Codex, ModelIds.Gpt56Sol, "high"));
        // ...an out-of-ladder explicit request normalizes to the model's own
        // ladder default (medium for gpt-5.5), not the product top-of-ladder.
        Assert.Equal("medium", ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Codex, ModelIds.Gpt55, "ultra"));
        // No request at all => the product default (top of ladder).
        Assert.Equal("ultra", ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Codex, ModelIds.Gpt56Sol, null));
    }

    [Fact]
    public void NormalizeForCli_Codex_RemapsForeignModelToDetectedDefault()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(ModelIds.Gpt56Sol);
        // A claude model requested under codex is remapped to the detected default.
        Assert.Equal(ModelIds.Gpt56Sol, ModelMetadataRegistry.NormalizeForCli(CliTypes.Codex, ModelIds.ClaudeOpus48));
        // An explicitly-requested gpt-5.6 model is preserved (not in the static
        // registry, but vendor-compatible with codex).
        Assert.Equal(ModelIds.Gpt56Sol, ModelMetadataRegistry.NormalizeForCli(CliTypes.Codex, ModelIds.Gpt56Sol));
        // With nothing detected the same foreign model lands on the gpt-5.5 floor.
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        Assert.Equal(ModelIds.Gpt55, ModelMetadataRegistry.NormalizeForCli(CliTypes.Codex, ModelIds.ClaudeOpus48));
    }
}
