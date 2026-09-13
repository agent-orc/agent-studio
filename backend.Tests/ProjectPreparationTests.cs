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
    public void V1_compatibility_existing_definitions_remain_valid()
    {
        var v1Minimal = Definition(".agent-studio/prepare");
        var parsed = ProjectDefinitionReader.Parse(v1Minimal);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(1, parsed.Definition!.SchemaVersion);
        Assert.Null(parsed.Definition.Project);
        Assert.Null(parsed.Definition.Quality);
    }

    [Fact]
    public void V1_definition_rejects_v2_keys()
    {
        var v1WithV2Keys = Definition(".agent-studio/prepare") + """

            project:
              id: test-project
              components:
                - id: component1
                  path: .
            """;
        var parsed = ProjectDefinitionReader.Parse(v1WithV2Keys);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Issues, issue => issue.Code == "v1-does-not-support-project");
    }

    [Fact]
    public void V2_schema_requires_project_section()
    {
        var v2WithoutProject = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            """;
        var parsed = ProjectDefinitionReader.Parse(v2WithoutProject);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Issues, issue => issue.Code == "v2-project-required");
    }

    [Fact]
    public void V2_schema_with_project_section_is_valid()
    {
        var v2Valid = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            project:
              id: test-project
              components:
                - id: frontend
                  path: frontend
            """;
        var parsed = ProjectDefinitionReader.Parse(v2Valid);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(2, parsed.Definition!.SchemaVersion);
        Assert.NotNull(parsed.Definition.Project);
        Assert.Equal("test-project", parsed.Definition.Project!.Id);
    }

    [Fact]
    public void V2_product_properties_preserve_unknown_state()
    {
        var v2WithProperties = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            project:
              id: test-project
              properties:
                public-facing: true
              components:
                - id: api
                  path: api
                - id: frontend
                  path: frontend
            """;
        var parsed = ProjectDefinitionReader.Parse(v2WithProperties);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(2, parsed.Definition!.SchemaVersion);
        Assert.NotNull(parsed.Definition.Project);
        Assert.True(parsed.Definition.Project!.Properties?.PublicFacing);
        Assert.Null(parsed.Definition.Project.Properties?.HtmlUi);
        var apiComponent = parsed.Definition.Project.Components.First(c => c.Id == "api");
        Assert.Equal("api", apiComponent.Id);
        Assert.Equal("api", apiComponent.Path);
    }

    [Fact]
    public void V2_unsupported_schema_version_is_rejected()
    {
        var v3Definition = Definition(".agent-studio/prepare")
            .Replace("schemaVersion: 1", "schemaVersion: 3", StringComparison.Ordinal);
        var parsed = ProjectDefinitionReader.Parse(v3Definition);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Issues, issue => issue.Code == "unsupported-version");
    }

    [Fact]
    public void V2_quality_applicability_is_parsed_when_present()
    {
        var v2WithQuality = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            project:
              id: test-project
              components:
                - id: frontend
                  path: frontend
            quality:
              applicability:
                - id: frontend-seo
                  componentScope: [frontend]
                  selector:
                    allOf: [public-facing, html-ui]
                  domains: [seo]
            """;
        var parsed = ProjectDefinitionReader.Parse(v2WithQuality);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.NotNull(parsed.Definition!.Quality);
        Assert.Single(parsed.Definition.Quality!.Applicability);
        Assert.Equal("frontend-seo", parsed.Definition.Quality.Applicability[0].Id);
    }

    [Fact]
    public void Repository_definition_validates_both_release_identity_rule_kinds()
    {
        var lockRule = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare") + """

            release:
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  source: backend/packages.lock.json
              restore:
                - dotnet restore backend/OrchestratorApi.csproj --locked-mode
            """);
        Assert.True(lockRule.IsValid, string.Join(Environment.NewLine, lockRule.Issues.Select(issue => issue.Message)));

        var exactRule = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare") + """

            release:
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  version: 0.7.0
                  integrity: sha512-packagehash
              restore:
                - restore exact registry package
            """);
        Assert.True(exactRule.IsValid, string.Join(Environment.NewLine, exactRule.Issues.Select(issue => issue.Message)));

        var ambiguous = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare") + """

            release:
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  source: backend/packages.lock.json
                  version: 0.7.0
                  integrity: sha512-packagehash
              restore:
                - dotnet restore
            """);
        Assert.Contains(ambiguous.Issues, issue => issue.Code == "release-rule-kind-invalid");
    }

    [Fact]
    public void Agent_studio_project_definition_resolves_its_release_identity_sources()
    {
        var read = ProjectDefinitionReader.ReadWorkspace(FindRepositoryRoot());

        Assert.True(read.IsValid, string.Join(Environment.NewLine, read.Issues.Select(issue => issue.Message)));
        Assert.NotNull(read.Definition?.Release);
        Assert.Contains(read.Definition!.Release!.Identity,
            identity => identity.Package == "CodingAgentRunner"
                        && identity.Source == "backend/packages.lock.json");
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
