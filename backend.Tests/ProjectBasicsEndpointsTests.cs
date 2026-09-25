using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[CollectionDefinition(ProjectRegistryApiCollection.Name, DisableParallelization = true)]
public sealed class ProjectRegistryApiCollection
{
    public const string Name = "Project registry API";
}

[Collection(ProjectRegistryApiCollection.Name)]
public sealed class ProjectBasicsEndpointsTests
{
    [Fact]
    public async Task PutProject_UpdatesAndPersistsAllOnboardingBasics_WithoutMovingTaskStorage()
    {
        var taskRepository = TempPath("project-basics-store");
        var productRepository = TempPath("project-basics-repo");
        Directory.CreateDirectory(taskRepository);
        Directory.CreateDirectory(Path.Combine(productRepository, ".git"));
        Directory.CreateDirectory(Path.Combine(productRepository, "src"));
        try
        {
            await using var factory = BuildFactory(taskRepository);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var createdResponse = await client.PostAsJsonAsync("/api/projects", new
            {
                displayName = "Before Edit",
                shortCode = "BEF",
                workspaceId = DefaultWorkspace.Id,
            });
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
            var created = (await createdResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
            var stableStorage = created.StorageLocation;

            var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
            var clients = factory.Services.GetRequiredService<ClientIdentityStore>();
            var oldRunner = clients.Register(new RegisterClientRequest
            {
                DisplayName = "runner-old",
                Kind = ClientIdentityKinds.Service,
            });
            var newRunner = clients.Register(new RegisterClientRequest
            {
                DisplayName = "runner-new",
                Kind = ClientIdentityKinds.Service,
            });
            settings.SetAutoCommit(created.DisplayName, false);
            settings.SetExecutionRunner(created.DisplayName, oldRunner.Id, remoteExecutionEnabled: false);

            var updateResponse = await client.PutAsJsonAsync($"/api/projects/{created.Id}", new
            {
                displayName = "After Edit",
                shortCode = "aft",
                color = "#123456",
                repositoryPath = productRepository,
                rootPath = Path.Combine(productRepository, "src"),
                repositoryUrl = "https://example.test/org/product.git",
                cliDefault = "codex",
                modelDefault = "gpt-test",
                executionRunner = newRunner.Id,
            });

            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            var updated = (await updateResponse.Content.ReadFromJsonAsync<ProjectSummary>())!;
            Assert.Equal("After Edit", updated.DisplayName);
            Assert.Equal("AFT", updated.ShortCode);
            Assert.Equal("#123456", updated.Color);
            Assert.Equal(productRepository, updated.RepositoryPath);
            Assert.Equal(Path.Combine(productRepository, "src"), updated.RootPath);
            Assert.Equal("https://example.test/org/product.git", updated.RepositoryUrl);
            Assert.Equal("codex", updated.CliDefault);
            Assert.Equal("gpt-test", updated.ModelDefault);
            Assert.Equal(stableStorage, updated.StorageLocation);

            var settingsSnapshot = settings.GetAll();
            Assert.DoesNotContain("Before Edit", settingsSnapshot.Keys);
            Assert.False(settingsSnapshot["After Edit"].AutoCommit);
            Assert.Equal(newRunner.Id, settingsSnapshot["After Edit"].ExecutionRunner);
            Assert.True(settingsSnapshot["After Edit"].RemoteExecutionEnabled);

            var reloadedRegistry = new ProjectRegistry(
                BuildConfiguration(taskRepository), NullLogger<ProjectRegistry>.Instance);
            var persisted = reloadedRegistry.FindById(created.Id)!;
            Assert.Equal(stableStorage, persisted.StorageLocation);
            Assert.Equal(productRepository, persisted.RepositoryPath);
            Assert.Equal(Path.Combine(productRepository, "src"), persisted.RootPath);
            Assert.Equal("https://example.test/org/product.git",
                persisted.Urls.Single(url => url.Id == "repo").Url);

            var reloadedSettings = new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance, BuildConfiguration(taskRepository));
            Assert.False(reloadedSettings.Get("After Edit").AutoCommit);
            Assert.Equal(newRunner.Id, reloadedSettings.Get("After Edit").ExecutionRunner);
            Assert.DoesNotContain("Before Edit", reloadedSettings.GetAll().Keys);

            Assert.True(Directory.Exists(stableStorage));
            Assert.False(Directory.Exists(Path.Combine(productRepository, "tasks")));
            Assert.False(Directory.Exists(Path.Combine(productRepository, ".orchestrator", "jobs")));
        }
        finally
        {
            DeleteBestEffort(taskRepository);
            DeleteBestEffort(productRepository);
        }
    }

    [Fact]
    public async Task PutProject_RejectsCollisionsAndInvalidLaterFields_WithoutPartialPersistence()
    {
        var taskRepository = TempPath("project-basics-atomic");
        Directory.CreateDirectory(taskRepository);
        try
        {
            await using var factory = BuildFactory(taskRepository);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var first = await CreateProject(client, "First Project", "FST");
            var second = await CreateProject(client, "Second Project", "SND");
            var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
            var clients = factory.Services.GetRequiredService<ClientIdentityStore>();
            var originalRunner = clients.Register(new RegisterClientRequest
            {
                DisplayName = "runner-original",
                Kind = ClientIdentityKinds.Service,
            });
            var futureRunner = clients.Register(new RegisterClientRequest
            {
                DisplayName = "runner-must-not-persist",
                Kind = ClientIdentityKinds.Service,
            });
            settings.SetExecutionRunner(first.DisplayName, originalRunner.Id, remoteExecutionEnabled: true);

            var collision = await client.PutAsJsonAsync($"/api/projects/{first.Id}", new
            {
                displayName = "Must Not Persist",
                shortCode = second.ShortCode,
                executionRunner = futureRunner.Id,
            });
            Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

            var displayNameCollision = await client.PutAsJsonAsync($"/api/projects/{first.Id}", new
            {
                displayName = "second project",
            });
            Assert.Equal(HttpStatusCode.Conflict, displayNameCollision.StatusCode);

            var invalidUrl = await client.PutAsJsonAsync($"/api/projects/{first.Id}", new
            {
                displayName = "Also Must Not Persist",
                repositoryUrl = "ftp://example.test/repo",
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidUrl.StatusCode);

            var invalidShortCode = await client.PutAsJsonAsync($"/api/projects/{first.Id}", new
            {
                shortCode = "1BAD",
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidShortCode.StatusCode);

            var current = await client.GetFromJsonAsync<ProjectRecord>($"/api/projects/{first.Id}");
            Assert.NotNull(current);
            Assert.Equal("First Project", current.DisplayName);
            Assert.Equal("FST", current.ShortCode);
            Assert.Equal(first.StorageLocation, current.StorageLocation);
            Assert.Equal(originalRunner.Id, settings.Get("First Project").ExecutionRunner);
            Assert.DoesNotContain("Must Not Persist", settings.GetAll().Keys);

            var persisted = new ProjectRegistry(
                BuildConfiguration(taskRepository), NullLogger<ProjectRegistry>.Instance)
                .FindById(first.Id)!;
            Assert.Equal("First Project", persisted.DisplayName);
            Assert.Equal("FST", persisted.ShortCode);
            Assert.Equal(first.StorageLocation, persisted.StorageLocation);
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }

    [Fact]
    public async Task PutProject_ValidatesRemoteRunnerAndNormalizesLocalToNull()
    {
        var taskRepository = TempPath("project-basics-runner");
        Directory.CreateDirectory(taskRepository);
        try
        {
            await using var factory = BuildFactory(taskRepository);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var created = await CreateProject(client, "Runner Project", "RUN");
            var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
            var clients = factory.Services.GetRequiredService<ClientIdentityStore>();
            var runner = clients.Register(new RegisterClientRequest
            {
                DisplayName = "registered-project-runner",
                Kind = ClientIdentityKinds.Service,
            });

            var assign = await client.PutAsJsonAsync($"/api/projects/{created.Id}", new
            {
                executionRunner = runner.Id,
            });
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
            Assert.Equal(runner.Id, settings.Get(created.DisplayName).ExecutionRunner);

            var rejectUnknown = await client.PutAsJsonAsync($"/api/projects/{created.Id}", new
            {
                displayName = "Must Not Rename",
                executionRunner = "missing-runner",
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejectUnknown.StatusCode);
            Assert.Equal(created.DisplayName,
                (await client.GetFromJsonAsync<ProjectRecord>($"/api/projects/{created.Id}"))!.DisplayName);
            Assert.Equal(runner.Id, settings.Get(created.DisplayName).ExecutionRunner);

            var selectLocal = await client.PutAsJsonAsync($"/api/projects/{created.Id}", new
            {
                executionRunner = "local",
            });
            Assert.Equal(HttpStatusCode.OK, selectLocal.StatusCode);
            Assert.Null(settings.Get(created.DisplayName).ExecutionRunner);
            Assert.Null(new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance,
                BuildConfiguration(taskRepository)).Get(created.DisplayName).ExecutionRunner);
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }

    [Fact]
    public async Task PutProject_SettingsPersistFailure_RollsBackRegistryAndSettings()
    {
        var taskRepository = TempPath("project-basics-cross-store-rollback");
        Directory.CreateDirectory(taskRepository);
        var writer = new ControllableAtomicJsonFileWriter();
        try
        {
            await using var factory = BuildFactory(taskRepository, writer);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var created = await CreateProject(client, "Before Failed Rename", "BFR");
            var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
            settings.SetAutoCommit(created.DisplayName, false);
            var clients = factory.Services.GetRequiredService<ClientIdentityStore>();
            var runner = clients.Register(new RegisterClientRequest
            {
                DisplayName = "rollback-test-runner",
                Kind = ClientIdentityKinds.Service,
            });
            var settingsPath = Path.Combine(taskRepository, "project-settings.json");
            writer.ShouldFail = (path, _) =>
                string.Equals(path, settingsPath, StringComparison.OrdinalIgnoreCase);

            var response = await client.PutAsJsonAsync($"/api/projects/{created.Id}", new
            {
                displayName = "After Failed Rename",
                executionRunner = runner.Id,
            });

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var current = await client.GetFromJsonAsync<ProjectRecord>($"/api/projects/{created.Id}");
            Assert.NotNull(current);
            Assert.Equal(created.DisplayName, current.DisplayName);
            Assert.False(settings.Get(created.DisplayName).AutoCommit);
            Assert.DoesNotContain("After Failed Rename", settings.GetAll().Keys);

            var reloadedRegistry = new ProjectRegistry(
                BuildConfiguration(taskRepository), NullLogger<ProjectRegistry>.Instance);
            Assert.Equal(created.DisplayName, reloadedRegistry.FindById(created.Id)!.DisplayName);
            var reloadedSettings = new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance, BuildConfiguration(taskRepository));
            Assert.False(reloadedSettings.Get(created.DisplayName).AutoCommit);
            Assert.DoesNotContain("After Failed Rename", reloadedSettings.GetAll().Keys);
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }

    [Fact]
    public async Task PostProject_AppendPersistFailure_RemovesFreshCentralStoreFolder()
    {
        var taskRepository = TempPath("project-basics-create-cleanup");
        Directory.CreateDirectory(taskRepository);
        var writer = new ControllableAtomicJsonFileWriter();
        var projectsPath = RegistryPaths.ProjectsFilePath(taskRepository);
        writer.ShouldFail = (path, writeNumber) =>
            string.Equals(path, projectsPath, StringComparison.OrdinalIgnoreCase)
            && writeNumber == 2;
        try
        {
            await using var factory = BuildFactory(taskRepository, writer);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var response = await client.PostAsJsonAsync("/api/projects", new
            {
                displayName = "Create Must Roll Back",
                shortCode = "CMR",
                workspaceId = DefaultWorkspace.Id,
            });

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(taskRepository, "projects", "PROJ-001")));
            Assert.Empty(await client.GetFromJsonAsync<List<ProjectSummary>>("/api/projects") ?? []);
            Assert.Empty(new ProjectRegistry(
                BuildConfiguration(taskRepository), NullLogger<ProjectRegistry>.Instance).List());
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }

    [Fact]
    public async Task PostProject_OmittedShortCode_AlwaysGeneratesAValidCode()
    {
        var taskRepository = TempPath("project-basics-generated-short-code");
        Directory.CreateDirectory(taskRepository);
        try
        {
            await using var factory = BuildFactory(taskRepository);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            foreach (var displayName in new[] { "A", "123 Project" })
            {
                var response = await client.PostAsJsonAsync("/api/projects", new
                {
                    displayName,
                    workspaceId = DefaultWorkspace.Id,
                });

                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var created = (await response.Content.ReadFromJsonAsync<ProjectSummary>())!;
                Assert.True(ShortCodeGenerator.ValidateFormat(created.ShortCode),
                    $"Expected a valid generated code for '{displayName}', got '{created.ShortCode}'.");
            }
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }


    private static async Task<ProjectSummary> CreateProject(HttpClient client, string displayName, string shortCode)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            displayName,
            shortCode,
            workspaceId = DefaultWorkspace.Id,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProjectSummary>())!;
    }

    private static WebApplicationFactory<Program> BuildFactory(
        string taskRepository,
        IAtomicJsonFileWriter? writer = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = taskRepository,
                        ["Logging:BackendFile:LogDirectory"] = Path.Combine(taskRepository, "logs"),
                    }));
                if (writer != null)
                {
                    builder.ConfigureTestServices(services =>
                        services.AddSingleton<IAtomicJsonFileWriter>(writer));
                }
            });

    private static IConfiguration BuildConfiguration(string taskRepository) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = taskRepository,
            })
            .Build();

    private static string TempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteBestEffort(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
