using System.Net.Http.Json;
using AgentStudio.Pipeline;
using AgentStudio.Clients;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PostMergeHostEndpointsTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DiscoveryUsesRegisteredBranchesExcludesStrictAndExposesStarvedWindow()
    {
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root, ["Security:Profile"] = "local", ["AllowedHosts"] = "*",
            }));
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(new PostMergeRunStore(Path.Combine(_root, "post-merge"))));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
        var registered = new List<ProjectRecord>();
        // AGT-3965: fast and balanced integrate first and need T3 runs; strict does not.
        foreach (var (name, branch, strategy) in new[] { ("one", "develop", TestingStrategies.Fast),
                     ("two", "trunk", TestingStrategies.Balanced), ("strict", "main", TestingStrategies.Strict) })
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            var project = registry.EnsureProjectForStorage(path, name, DefaultWorkspace.Id, _clock);
            registry.Update(project.Id, new UpdateProjectRequest { RepositoryUrl = $"https://example.invalid/{name}.git" },
                factory.Services.GetRequiredService<WorkspaceRegistry>());
            settings.SetIntegrationBranch(name, branch);
            settings.SetTestingStrategy(name, strategy);
            registered.Add(project);
        }
        var targets = await client.GetFromJsonAsync<PostMergeProjectTarget[]>("/api/runner/post-merge/projects");
        Assert.Equal(["one", "two"], targets!.Select(target => target.Project).Order().ToArray());
        Assert.Equal("trunk", targets.Single(target => target.Project == "two").Branch);
        Assert.Equal("https://example.invalid/one.git", targets.Single(target => target.Project == "one").RepositoryUrl);
        var now = _clock.GetUtcNow();
        var observation = new PostMergeWindowObservation(new string('a', 40), now.AddMinutes(-31), now, 30, "1 occupied slots");
        using var response = await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/window", observation);
        response.EnsureSuccessStatusCode();
        var states = await client.GetFromJsonAsync<DevelopStateSummary[]>("/api/develop-states");
        var pending = states!.Single(state => state.Project == "one").PendingWindow;
        Assert.NotNull(pending);
        Assert.True(pending.Starved);
        Assert.Equal(observation.TipSha, pending.TipSha);
        Assert.Null(states.Single(state => state.Project == "strict").PendingWindow);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
