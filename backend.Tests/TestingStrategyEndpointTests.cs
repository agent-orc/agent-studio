using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3965: the settings routes return <c>testingStrategy</c> with fast for a
/// project without a stored value, keep the former switches readable as
/// derived values, and accept only the strategy as a write.
/// </summary>
public sealed class TestingStrategyEndpointTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "testing-strategy-api-" + Guid.NewGuid().ToString("N"));

    public TestingStrategyEndpointTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task SettingsSnapshotAndTestingRoutesReturnTheStrategyAndAcceptOnlyAStrategyWrite()
    {
        var watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var lane in AgentStudio.Shared.TaskStates.All) Directory.CreateDirectory(Path.Combine(watchPath, lane));
        // A stored project without a testing section: the settings list names it.
        await File.WriteAllTextAsync(Path.Combine(_workspace, "project-settings.json"),
            "{\"demo\":{\"AutoCommit\":false}}");
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "local",
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = watchPath,
                // No checkout: the snapshot's own branch lookup needs a repository and is not under test here.
                ["WatchPaths:0:RootPath"] = Path.Combine(_workspace, "no-checkout"),
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/projects/settings")).GetProperty(Project);
        Assert.Equal("fast", listed.GetProperty("testingStrategy").GetString());
        Assert.Equal("affected", listed.GetProperty("testingPerCard").GetString());
        Assert.Equal("post-merge", listed.GetProperty("testingMode").GetString());

        using var snapshotResponse = await client.GetAsync($"/api/projects/{Project}/snapshot");
        Assert.True(snapshotResponse.IsSuccessStatusCode, await snapshotResponse.Content.ReadAsStringAsync());
        var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fast", snapshot.GetProperty("settings").GetProperty("testingStrategy").GetString());
        Assert.Equal(3, snapshot.GetProperty("settings").GetProperty("testingStrategies").GetArrayLength());

        var testing = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{Project}/testing");
        Assert.Equal("fast", testing.GetProperty("strategy").GetString());
        Assert.False(testing.GetProperty("stored").GetBoolean());
        Assert.Equal(["fast", "balanced", "strict"], testing.GetProperty("strategies").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()));

        // Saving the default is still an explicit stored choice, without a gate transition.
        using (var same = await client.PutAsJsonAsync($"/api/projects/{Project}/testing", new { strategy = "FAST" }))
        {
            same.EnsureSuccessStatusCode();
            var saved = await same.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(saved.GetProperty("stored").GetBoolean());
            Assert.Equal("fast", saved.GetProperty("transition").GetProperty("strategy").GetString());
            Assert.Empty(saved.GetProperty("transition").GetProperty("cards").EnumerateArray());
        }

        using (var legacy = await client.PutAsJsonAsync($"/api/projects/{Project}/testing", new { perCard = "full" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
            Assert.Contains("derived from testing.strategy", await legacy.Content.ReadAsStringAsync());
        }
        using (var unknown = await client.PutAsJsonAsync($"/api/projects/{Project}/testing", new { strategy = "post-merge" }))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var strict = await client.PutAsJsonAsync($"/api/projects/{Project}/testing", new { strategy = "strict" });
        strict.EnsureSuccessStatusCode();
        var written = await strict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("strict", written.GetProperty("strategy").GetString());
        Assert.True(written.GetProperty("stored").GetBoolean());
        Assert.Equal("full", written.GetProperty("perCard").GetString());
        Assert.Equal("pre-merge", written.GetProperty("mode").GetString());
        Assert.Equal("not-expected", written.GetProperty("postMergeCoverage").GetProperty("state").GetString());

        listed = (await client.GetFromJsonAsync<JsonElement>("/api/projects/settings")).GetProperty(Project);
        Assert.Equal("strict", listed.GetProperty("testingStrategy").GetString());
        Assert.Equal("full", listed.GetProperty("testingPerCard").GetString());
        Assert.Equal("pre-merge", listed.GetProperty("testingMode").GetString());
    }
}
