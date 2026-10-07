using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ModelMigrationProjectEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-migration-http-" + Guid.NewGuid().ToString("N"));
    private string Jobs => Path.Combine(_root, "jobs");

    [Fact]
    public async Task Project_acceptance_updates_only_eligible_explicit_pins_and_reports_the_result()
    {
        Seed("AGT-eligible", TaskStates.Ready, ModelIds.Gpt56Sol, explicitModel: true);
        Seed("AGT-other-model", TaskStates.Ready, ModelIds.Gpt56Luna, explicitModel: true);
        Seed("AGT-implicit", TaskStates.Ready, ModelIds.Gpt56Sol, explicitModel: false);
        Seed("AGT-reviewing", TaskStates.AutoReview, ModelIds.Gpt56Sol, explicitModel: true);

        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .EnsureProjectForStorage(Jobs, "Migration Project", "default");

        using var response = await client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/model-migrations/apply",
            new ApplyProjectModelMigrationRequest(ModelIds.Gpt56Sol));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApplyProjectModelMigrationResult>();
        Assert.NotNull(result);
        Assert.Equal(ModelIds.Gpt56Sol, result.From);
        Assert.Equal(ModelIds.Gpt6Sol, result.To);
        Assert.Equal(["AGT-eligible"], result.UpdatedTaskIds);
        Assert.Empty(result.FailedTaskIds);
        Assert.Equal(ModelIds.Gpt6Sol, ModelIn("AGT-eligible", TaskStates.Ready));
        Assert.Equal(ModelIds.Gpt56Luna, ModelIn("AGT-other-model", TaskStates.Ready));
        Assert.Equal(ModelIds.Gpt56Sol, ModelIn("AGT-implicit", TaskStates.Ready));
        Assert.Equal(ModelIds.Gpt56Sol, ModelIn("AGT-reviewing", TaskStates.AutoReview));

        using var unknownMigration = await client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/model-migrations/apply",
            new ApplyProjectModelMigrationRequest("gpt-6.1-sol"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownMigration.StatusCode);

        using var unknownProject = await client.PostAsJsonAsync(
            "/api/projects/PROJ-unknown/model-migrations/apply",
            new ApplyProjectModelMigrationRequest(ModelIds.Gpt56Sol));
        Assert.Equal(HttpStatusCode.NotFound, unknownProject.StatusCode);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "Migration Project",
                ["WatchPaths:0:Path"] = Jobs,
                ["WatchPaths:0:RootPath"] = _root,
                ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
            }));
        });

    private void Seed(string id, string state, string model, bool explicitModel)
    {
        var folder = Path.Combine(Jobs, state, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = id, title = id, state, order = 1,
            cliType = "codex", model, modelExplicit = explicitModel,
            thinkingLevel = "medium",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "A model migration request.");
    }

    private string? ModelIn(string id, string state)
    {
        using var task = JsonDocument.Parse(File.ReadAllText(Path.Combine(Jobs, state, id, "task.json")));
        return task.RootElement.GetProperty("model").GetString();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* A watcher may finish after the test. */ }
    }
}
