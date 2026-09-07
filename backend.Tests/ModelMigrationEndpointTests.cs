using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The wire contract three UI surfaces share. The card badge, the project
/// pipeline rows, and CLI Management all resolve an update by looking a pinned
/// model id up in this payload, so its shape - not just the planner behind it -
/// is what has to stay stable.
/// </summary>
public sealed class ModelMigrationEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agt-2716-endpoints-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a leaked temp dir must not fail a green test */ }
    }

    [Fact]
    public async Task Get_ReturnsTheCatalogVersionTheSwitchAndProposalsKeyedByPinnedModel()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();

        var view = await client.GetFromJsonAsync<MigrationView>("/api/cli/model-migrations");

        Assert.NotNull(view);
        Assert.Equal(ModelMigrationCatalogService.ReadEmbedded().Version, view!.CatalogVersion);
        Assert.Equal(ModelMigrationCatalogService.EmbeddedSource, view.CatalogSource);
        // On by default: a superseded default must not survive a new release.
        Assert.True(view.AutoApply);

        // The pin an operator would actually be holding, keyed by its own id.
        var haiku = Assert.Contains(ModelIds.ClaudeHaiku45, view.Proposals);
        Assert.Equal(ModelIds.ClaudeSonnet5, haiku.To.ModelId);
        Assert.False(haiku.SafeAuto);
        // The diff is served, not recomputed client-side.
        Assert.NotEmpty(haiku.From.Label);
        Assert.NotEmpty(haiku.CostClass);

        // A current model is absent rather than present-and-empty, so a lookup
        // miss is the only "nothing to offer" signal a surface has to handle.
        Assert.DoesNotContain(ModelIds.ClaudeOpus5, view.Proposals.Keys);
    }

    [Fact]
    public async Task AutoApplySwitch_RoundTripsAndSurvivesAReadBack()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            "/api/cli/model-migrations/auto-apply", new { autoApply = false });
        response.EnsureSuccessStatusCode();

        var view = await client.GetFromJsonAsync<MigrationView>("/api/cli/model-migrations");
        Assert.False(view!.AutoApply);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
                }));
        });

    private sealed record MigrationView
    {
        public string CatalogVersion { get; init; } = "";
        public string CatalogSource { get; init; } = "";
        public bool AutoApply { get; init; }

        public Dictionary<string, ModelMigrationProposal> Proposals { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
