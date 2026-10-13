using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorChatWorkingDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "orchestrator-chat-cwd-" + Guid.NewGuid().ToString("N"));

    public OrchestratorChatWorkingDirectoryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolveWorkingDirectory_PrefersWatchEntryRootPath()
    {
        var watchPath = Directory.CreateDirectory(Path.Combine(_root, "tasks")).FullName;
        var configuredRoot = Directory.CreateDirectory(Path.Combine(_root, "configured-root")).FullName;
        var config = BuildConfiguration(watchPath, configuredRoot);
        var service = BuildService(config, projects: null, NullLogger<OrchestratorChatService>.Instance);

        var resolved = service.ResolveWorkingDirectory("project-a", watchPath);

        Assert.Equal(configuredRoot, resolved);
    }

    [Fact]
    public void ResolveWorkingDirectory_WithoutRootPath_UsesRegistryRepositoryPath()
    {
        var watchPath = Directory.CreateDirectory(Path.Combine(_root, "registry-tasks")).FullName;
        var repositoryPath = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        var config = BuildConfiguration(watchPath, rootPath: null);
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(
            watchPath, "project-a", DefaultWorkspace.Id);
        projects.SetRepositoryPath(project.Id, repositoryPath);
        var service = BuildService(config, projects, NullLogger<OrchestratorChatService>.Instance);

        var resolved = service.ResolveWorkingDirectory("project-a", watchPath);

        Assert.Equal(repositoryPath, resolved);
    }

    [Fact]
    public void ResolveWorkingDirectory_WithoutConfiguredPaths_LogsTempFallback()
    {
        var watchPath = Directory.CreateDirectory(Path.Combine(_root, "fallback-tasks")).FullName;
        var config = BuildConfiguration(watchPath, rootPath: null);
        var logger = new CapturingLogger();
        var service = BuildService(config, projects: null, logger);

        var resolved = service.ResolveWorkingDirectory("project-a", watchPath);

        Assert.Equal(Path.GetTempPath(), resolved);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("project=project-a", warning);
        Assert.Contains("missing-watch-root-and-registry-repository-path", warning);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private IConfiguration BuildConfiguration(string watchPath, string? rootPath)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = rootPath,
            })
            .Build();

    private static OrchestratorChatService BuildService(
        IConfiguration config,
        ProjectRegistry? projects,
        ILogger<OrchestratorChatService> logger)
    {
        var summary = new AgentStudio.Review.SummaryGenerationService(
            NullLogger<AgentStudio.Review.SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            summary,
            projectRegistry: projects);
        return new OrchestratorChatService(TokenTestServices.Pricing(),
            chat: null!,
            runner: null!,
            sessionStore: null!,
            bootstrap: null!,
            scanner,
            config,
            logger,
            projects: projects);
    }

    private sealed class CapturingLogger : ILogger<OrchestratorChatService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
