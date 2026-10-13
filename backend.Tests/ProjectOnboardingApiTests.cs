using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectOnboardingApiTests
{
    [Fact]
    public void ProjectSource_RemainsAnInternalLocalFolderBackcompatDefault()
    {
        Assert.Equal(ProjectSourceTypes.LocalFolder, new ProjectRecord().SourceType);
    }

    [Fact]
    public void ProjectSummary_PreservesConfiguredSourceType()
    {
        var summary = ProjectSummary.From(new ProjectRecord
        {
            Id = "PROJ-001",
            SourceType = ProjectSourceTypes.LocalFolder,
            DisplayName = "Demo",
        });

        Assert.Equal(ProjectSourceTypes.LocalFolder, summary.SourceType);
    }

    [Fact]
    public async Task ProjectSources_IsNotAPublicProductEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "project-source-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = root,
                            ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        }));
                });
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/project-sources");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PostProject_RejectsNonLocalBackcompatSourceValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "project-source-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = root,
                            ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        }));
                });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var response = await client.PostAsJsonAsync("/api/projects", new
            {
                displayName = "Unsupported Source",
                shortCode = "UNS",
                workspaceId = DefaultWorkspace.Id,
                sourceType = "remote-git",
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await client.GetFromJsonAsync<List<ProjectSummary>>("/api/projects") ?? []);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PostProject_CreatesCentralStore_IsImmediatelyListed_AndShortCodeIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "project-onboard-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = root,
                            ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        }));
                });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
            var request = new
            {
                displayName = "API Test Project",
                shortCode = "APT",
                workspaceId = DefaultWorkspace.Id,
                repositoryUrl = "https://github.com/example/api-test-project",
            };

            var createdResponse = await client.PostAsJsonAsync("/api/projects", request);

            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
            var created = await createdResponse.Content.ReadFromJsonAsync<ProjectSummary>();
            Assert.NotNull(created);
            Assert.Equal(Path.Combine(root, "projects", created.Id, "tasks"), created.StorageLocation);
            Assert.True(Directory.Exists(created.StorageLocation));
            Assert.Contains(created.Urls, url => url.Id == "repo" && url.Url == request.repositoryUrl);

            var listed = await client.GetFromJsonAsync<List<ProjectSummary>>("/api/projects");
            Assert.Contains(listed!, project => project.Id == created.Id);

            var duplicate = await client.PostAsJsonAsync("/api/projects", request);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

            var deleted = await client.DeleteAsync($"/api/projects/{created.Id}");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(root, "projects", created.Id)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
