using AgentStudio.ExecutionPreparation;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-preparation-" + Guid.NewGuid().ToString("N"));

    public ProjectPreparationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Repository_definition_accepts_shell_operators_and_rejects_yaml_aliases()
    {
        var valid = ProjectDefinitionReader.Parse(Definition("test -f package.json && npm ci"));
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Issues.Select(issue => issue.Message)));

        var invalid = ProjectDefinitionReader.Parse(Definition("*shared"));
        Assert.Contains(invalid.Issues, issue => issue.Code == "yaml-feature-unsupported");

        var unknown = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare")
            .Replace("schemaVersion: 1", "schemaVersion: 1\nmystery: true", StringComparison.Ordinal));
        Assert.Contains(unknown.Issues, issue => issue.Code == "unknown-field");
    }

    [Fact]
    public async Task Successful_prepare_publishes_immutable_cache_and_second_run_hits_it()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            if [ -f "$NPM_CONFIG_CACHE/marker" ]; then printf hit > observed; else printf miss > observed; fi
            printf cache > "$NPM_CONFIG_CACHE/marker"
            """);
        var cache = Path.Combine(_root, "product-cache");

        var first = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "first.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(first.Succeeded, first.Output);
        Assert.Equal("miss", File.ReadAllText(Path.Combine(_root, "observed")));
        Assert.All(first.Manifest!.Caches, entry => Assert.Equal("published", entry.State));

        var second = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "second.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(second.CacheHit);
        Assert.Equal("hit", File.ReadAllText(Path.Combine(_root, "observed")));
        Assert.All(second.Manifest!.Caches, entry => Assert.Equal("hit", entry.State));
    }

    [Fact]
    public async Task Failed_prepare_discards_cache_staging_and_records_failure_signature()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", "mkdir -p \"$NPM_CONFIG_CACHE\"; printf poison > \"$NPM_CONFIG_CACHE/marker\"; exit 7");
        var cache = Path.Combine(_root, "product-cache");

        var result = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "failed.json"), "subject-2", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PreparationFailureKind.Command, result.FailureKind);
        Assert.StartsWith("command:7:", result.FailureSignature);
        Assert.All(result.Manifest!.Caches, entry => Assert.Equal("discarded", entry.State));
        Assert.Empty(Directory.Exists(Path.Combine(cache, "entries"))
            ? Directory.EnumerateFiles(Path.Combine(cache, "entries"), "manifest.json", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task Incomplete_immutable_entry_fails_with_an_evictable_cache_signature()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", "printf cache > \"$NPM_CONFIG_CACHE/marker\"");
        var cache = Path.Combine(_root, "product-cache");
        var first = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "first.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(first.Succeeded);
        var content = Directory.EnumerateDirectories(Path.Combine(cache, "entries", "npm"), "content", SearchOption.AllDirectories).Single();
        Directory.Delete(content, recursive: true);
        Directory.CreateDirectory(content);

        var second = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "second.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal(PreparationFailureKind.Cache, second.FailureKind);
        Assert.Equal("cache:incomplete", second.FailureSignature);
    }

    [Fact]
    public void Generator_proposes_both_repository_files_for_detected_stack()
    {
        Write("Sample.slnx", "<Solution />");
        Write("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}");
        Write("web/package.json", "{\"scripts\":{\"build\":\"ng build\",\"test\":\"ng test\"}}");
        Write("web/package-lock.json", "{\"lockfileVersion\":3}");

        var proposal = ProjectDefinitionGenerator.Generate(_root);

        Assert.Contains("stack: [dotnet, node]", proposal.Definition);
        Assert.Contains("dotnetSdk: global.json", proposal.Definition);
        Assert.Contains("npm --prefix 'web' ci --prefer-offline", proposal.PrepareScript);
        Assert.True(ProjectDefinitionReader.Parse(proposal.Definition).IsValid);
        var failureCard = ProjectDefinitionProposalService.BuildPrompt(proposal, "tool:node:version-mismatch");
        Assert.Contains("tool:node:version-mismatch", failureCard);
        Assert.Contains("## Reasoning", failureCard);
    }

    [Fact]
    public void Execution_override_requires_justification_and_valid_definition()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);

        Assert.Throws<ArgumentException>(() => settings.SetExecutionDefinitionOverride("Pilot", Definition(".agent-studio/prepare"), ""));
        var saved = settings.SetExecutionDefinitionOverride("Pilot", Definition(".agent-studio/prepare"), "Temporary executor exception");

        Assert.Equal("Temporary executor exception", saved.Justification);
        Assert.NotNull(settings.Get("Pilot").ExecutionDefinitionOverride);
        settings.ClearExecutionDefinitionOverride("Pilot");
        Assert.Null(settings.Get("Pilot").ExecutionDefinitionOverride);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Agent_studio_pilot_runs_twice_and_second_run_is_a_cache_hit()
    {
        var repository = FindRepositoryRoot();
        var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR")
                      ?? Path.Combine(_root, "pilot-results");
        var cache = Path.Combine(Path.GetTempPath(), "agent-studio-m1-pilot-cache");
        var subjectSha = await RunGitAsync(repository, "rev-parse", "HEAD");

        var first = await ProjectPreparationExecutor.RunAsync(
            repository, cache, Path.Combine(results, "agent-studio-preparation-first.json"),
            subjectSha, null, TimeSpan.FromMinutes(20), CancellationToken.None);
        Assert.True(first.Succeeded, first.Output);
        var second = await ProjectPreparationExecutor.RunAsync(
            repository, cache, Path.Combine(results, "agent-studio-preparation-second.json"),
            subjectSha, null, TimeSpan.FromMinutes(20), CancellationToken.None);

        Assert.True(second.Succeeded, second.Output);
        Assert.True(second.CacheHit, "The unchanged Agent Studio pilot must hit every technology cache.");
    }

    private static string Definition(string prepare) => $$"""
        schemaVersion: 1
        stack: [node]
        toolVersions:
        commands:
          prepare: {{prepare}}
          build:
          test:
          lint:
        testSuites:
        cachePaths: [node_modules]
        capabilities: [linux]
        environment:
          CI: "true"
        """;

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agent-taskboard.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Agent Studio repository root was not found.");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Git did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await stderr);
        return output;
    }
}
