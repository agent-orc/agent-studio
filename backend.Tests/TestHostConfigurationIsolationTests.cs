using AgentStudio.Diagnostics;
using AgentStudio.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestHostConfigurationIsolationTests
{
    [Fact]
    public async Task LocalFileBodyLimit_ReachesKestrelAndArtifactLimits()
    {
        var contentRoot = TempPath("local-body-limit");
        Directory.CreateDirectory(contentRoot);
        try
        {
            File.WriteAllText(Path.Combine(contentRoot, "appsettings.Local.json"),
                "{\"Security\":{\"MaxRequestBodyBytes\":134217728}}");
            var builder = NewBuilder(contentRoot, "Production");
            HostStartupConfiguration.LoadLocalSettings(builder, [], underTestHost: false);
            HostStartupConfiguration.ConfigureRequestBodyLimit(builder);

            await using var app = builder.Build();
            Assert.Equal(134217728, app.Services.GetRequiredService<ArtifactRequestLimits>().MaxRequestBodyBytes);
            Assert.Equal(134217728,
                app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        }
        finally
        {
            DeleteBestEffort(contentRoot);
        }
    }

    [Theory]
    [InlineData("Test", false)]
    [InlineData("Production", true)]
    public async Task TestHostSkipsLocalFileBeforeBodyLimitIsRead(string environment, bool underTestHost)
    {
        var contentRoot = TempPath("isolated-body-limit");
        Directory.CreateDirectory(contentRoot);
        try
        {
            File.WriteAllText(Path.Combine(contentRoot, "appsettings.Local.json"),
                "{\"Security\":{\"MaxRequestBodyBytes\":134217728}}");
            var builder = NewBuilder(contentRoot, environment);
            HostStartupConfiguration.LoadLocalSettings(builder, [], underTestHost);
            HostStartupConfiguration.ConfigureRequestBodyLimit(builder);

            await using var app = builder.Build();
            Assert.Equal(25L * 1024 * 1024,
                app.Services.GetRequiredService<ArtifactRequestLimits>().MaxRequestBodyBytes);
            Assert.Equal(25L * 1024 * 1024,
                app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        }
        finally
        {
            DeleteBestEffort(contentRoot);
        }
    }

    private static WebApplicationBuilder NewBuilder(string contentRoot, string environment) =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            EnvironmentName = environment,
        });

    [Fact]
    public async Task FreshTestHost_DoesNotLoadLocalConfiguration_AndStartsWithEmptyProjectRegistry()
    {
        var taskRepository = TempPath("test-host-empty-registry");
        Directory.CreateDirectory(taskRepository);
        try
        {
            await using var factory = BuildFactory(taskRepository);

            var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();
            Assert.DoesNotContain(configuration.Providers, IsLocalConfigurationProvider);

            var projects = factory.Services.GetRequiredService<ProjectRegistry>();
            Assert.Empty(projects.List());
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }

    [Fact]
    public async Task TestHost_WithDefaultEnvironment_StillDoesNotLoadLocalConfiguration()
    {
        var taskRepository = TempPath("default-environment-test-host");
        Directory.CreateDirectory(taskRepository);
        try
        {
            await using var factory = BuildFactory(taskRepository, useTestEnvironment: false);

            var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();
            Assert.DoesNotContain(configuration.Providers, IsLocalConfigurationProvider);

            var projects = factory.Services.GetRequiredService<ProjectRegistry>();
            Assert.Empty(projects.List());
        }
        finally
        {
            DeleteBestEffort(taskRepository);
        }
    }

    [Fact]
    public async Task TestHost_SeedsOnlyTheExplicitWatchPathFixture()
    {
        var taskRepository = TempPath("test-host-fixture-registry");
        var watchPath = TempPath("test-host-fixture-project");
        Directory.CreateDirectory(taskRepository);
        Directory.CreateDirectory(watchPath);
        try
        {
            await using var factory = BuildFactory(taskRepository, new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture Project",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
                ["WatchPaths:0:RepositoryPath"] = watchPath,
            });

            var projects = factory.Services.GetRequiredService<ProjectRegistry>();
            var project = Assert.Single(projects.List());
            Assert.Equal("Fixture Project", project.DisplayName);
            Assert.Equal(watchPath, project.StorageLocation);
        }
        finally
        {
            DeleteBestEffort(taskRepository);
            DeleteBestEffort(watchPath);
        }
    }

    private static WebApplicationFactory<Program> BuildFactory(
        string taskRepository,
        IReadOnlyDictionary<string, string?>? fixture = null,
        bool useTestEnvironment = true) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                if (useTestEnvironment)
                {
                    builder.UseEnvironment("Test");
                }
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = taskRepository,
                        ["Logging:BackendFile:LogDirectory"] = Path.Combine(taskRepository, "logs"),
                    };
                    if (fixture != null)
                    {
                        foreach (var pair in fixture)
                        {
                            values[pair.Key] = pair.Value;
                        }
                    }

                    configuration.AddInMemoryCollection(values);
                });
            });

    private static bool IsLocalConfigurationProvider(IConfigurationProvider provider) =>
        provider is JsonConfigurationProvider json
        && string.Equals(
            Path.GetFileName(json.Source.Path),
            "appsettings.Local.json",
            StringComparison.OrdinalIgnoreCase);

    private static string TempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteBestEffort(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
