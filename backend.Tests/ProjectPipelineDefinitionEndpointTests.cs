using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// <c>/api/projects/{projectName}/pipeline-definition</c> against a real git
/// checkout: the read carries the diagram and line-numbered issues, validation
/// and the dry run never write, and the confirmed migration commits project.yml
/// only while both digests of the dry run still hold.
/// </summary>
public sealed class ProjectPipelineDefinitionEndpointTests : IAsyncLifetime
{
    private const string Project = "Website";
    private const string SchemaOne = """
        schemaVersion: 1
        stack: [node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build: [npm run build]
          test: [npm test]
          lint: []
        testSuites:
        cachePaths: []
        capabilities: [git]
        environment:
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pipeline-definition-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "repo");
    private string DefinitionFile => Path.Combine(Repository, ".agent-studio", "project.yml");
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Repository, ".agent-studio"));
        Directory.CreateDirectory(Path.Combine(Repository, "docs", "deployments", "environments"));
        Directory.CreateDirectory(Path.Combine(Repository, ".orchestrator", "jobs"));
        File.WriteAllText(Path.Combine(Repository, ".agent-studio", "prepare"), "#!/bin/sh\n");
        File.WriteAllText(DefinitionFile, SchemaOne);
        File.WriteAllText(Path.Combine(Repository, "docs", "deployments", "environments", "staging.json"), """
            {"id":"staging","name":"Staging","kind":"stage","targetHostId":"web-vm","address":"https://example.test/_staging/",
             "template":"static","destination":"/srv/www/_staging/site","healthUrl":"https://example.test/_staging/"}
            """);
        File.WriteAllText(Path.Combine(Repository, "docs", "deployments", "environments", "production.json"), """
            {"id":"production","name":"Production","kind":"production","targetHostId":"web-vm","address":"https://example.test/",
             "template":"static","destination":"/srv/www/site","healthUrl":"https://example.test/","approval":"person"}
            """);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        Git("add", "-A");
        Git("commit", "-q", "-m", "seed");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:RootPath"] = Repository,
                ["WatchPaths:0:Path"] = Path.Combine(Repository, ".orchestrator", "jobs"),
            })
            .Build();
        var summaries = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance, summaries);
        var registry = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        var environments = new ProjectEnvironmentService(configuration, scanner, git, settings, new FakeTimeProvider());
        var service = new ProjectPipelineDefinitionService(
            scanner,
            registry,
            environments,
            new ManagedRepositoryMutationService(git, logger: NullLogger<ManagedRepositoryMutationService>.Instance),
            NullLogger<ProjectPipelineDefinitionService>.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(environments);
        _app = builder.Build();
        _app.MapProjectPipelineDefinitionEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        TestTempRoot.TryDelete(_root);
    }

    [Fact]
    public async Task Read_returns_the_schema_one_gate_and_offers_the_migration()
    {
        using var body = await GetJson("/api/projects/Website/pipeline-definition");
        var root = body.RootElement;

        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("migrationAvailable").GetBoolean());
        Assert.Equal("commands", root.GetProperty("diagram").GetProperty("gate").GetProperty("source").GetString());
        Assert.Equal(0, root.GetProperty("diagram").GetProperty("environments").GetArrayLength());
    }

    [Fact]
    public async Task Validate_returns_issues_with_line_and_path_without_writing()
    {
        var yaml = SchemaOne.Replace("schemaVersion: 1", "schemaVersion: 2", StringComparison.Ordinal)
                   + "project:\n  id: website\nenvironments:\n  - name: production\n  - name: staging\n";

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/validate", new { yaml });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.RootElement.GetProperty("valid").GetBoolean());
        var order = body.RootElement.GetProperty("issues").EnumerateArray()
            .Single(issue => issue.GetProperty("code").GetString() == "deployment-environment-order-invalid");
        Assert.Equal("environments", order.GetProperty("path").GetString());
        Assert.Equal(Array.IndexOf(yaml.Split('\n'), "environments:") + 1, order.GetProperty("line").GetInt32());
        Assert.Equal(SchemaOne, File.ReadAllText(DefinitionFile));
    }

    [Fact]
    public async Task Validate_checks_deploy_targets_against_the_hosts_the_declarations_register()
    {
        var yaml = SchemaOne.Replace("schemaVersion: 1", "schemaVersion: 2", StringComparison.Ordinal) + "\n" + """
            project:
              id: website
            artifacts:
              - name: site
                type: static
                path: dist
            deploy:
              - step: static-site
                artifact: site
                target: other-vm
                directory: /srv/www/site
                healthUrl: https://example.test/
                healthTimeoutSeconds: 60
            """;

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/validate", new { yaml });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var issue = Assert.Single(body.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal("deploy-target-unregistered", issue.GetProperty("code").GetString());
        Assert.Equal("deploy[0].target", issue.GetProperty("path").GetString());
        Assert.Equal(Array.IndexOf(yaml.Split('\n'), "    target: other-vm") + 1, issue.GetProperty("line").GetInt32());
        Assert.Contains("staging: web-vm", issue.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_accepts_schema_two_target_without_legacy_declarations()
    {
        Directory.Delete(Path.Combine(Repository, "docs", "deployments", "environments"), true);
        var yaml = SchemaOne.Replace("schemaVersion: 1", "schemaVersion: 2", StringComparison.Ordinal) + "\n" + """
            project:
              id: website
            artifacts:
              - name: site
                type: static
                path: dist
            deploy:
              - step: static-site
                artifact: site
                target: other-vm
                directory: /srv/www/site
                healthUrl: https://example.test/
                healthTimeoutSeconds: 60
            """;

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/validate", new { yaml });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.RootElement.GetProperty("valid").GetBoolean(), body.RootElement.GetProperty("issues").ToString());
    }

    [Fact]
    public void Only_the_confirm_route_is_classified_for_the_public_demo_lock()
    {
        var classified = ((IEndpointRouteBuilder)_app!).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.Contains("pipeline-definition", StringComparison.Ordinal))
            .Select(endpoint => (
                Method: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single(),
                endpoint.RoutePattern.RawText,
                Path: endpoint.Metadata.GetMetadata<ExecutionRouteMetadata>()?.Path,
                Expected: endpoint.Metadata.GetMetadata<PublicDemoExecutionExpectationMetadata>()?.ExpectedCode))
            .Where(route => route.Path is not null)
            .ToArray();

        Assert.Equal(
            [("POST", "/api/projects/{projectName}/pipeline-definition/deploy-steps"),
             ("POST", "/api/projects/{projectName}/pipeline-definition/migration")],
            classified.Select(route => (route.Method, route.RawText!)).OrderBy(route => route.Item2, StringComparer.Ordinal));
        Assert.All(classified, route => Assert.Equal(
            (ExecutionAdmissionPath.PostStep, ExecutionAdmissionPolicy.ExecutionDisabledCode), (route.Path, route.Expected)));
    }

    [Fact]
    public async Task Confirm_after_a_committed_change_to_project_yml_is_refused_as_stale()
    {
        using var dryRun = await GetJson("/api/projects/Website/pipeline-definition/migration");
        var changed = SchemaOne.Replace("lint: []", "lint: [npm run lint]", StringComparison.Ordinal);
        File.WriteAllText(DefinitionFile, changed);
        Git("commit", "-q", "-am", "edit lint");

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/migration", new
        {
            baseSha256 = dryRun.RootElement.GetProperty("baseSha256").GetString(),
            proposalSha256 = dryRun.RootElement.GetProperty("proposalSha256").GetString(),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(changed, File.ReadAllText(DefinitionFile));
        Assert.Equal("2", Git("rev-list", "--count", "HEAD"));
    }

    [Fact]
    public async Task Validate_refuses_an_empty_body()
    {
        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/validate", new { yaml = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dry_run_shows_a_valid_proposal_and_leaves_the_file_alone()
    {
        using var body = await GetJson("/api/projects/Website/pipeline-definition/migration");
        var root = body.RootElement;

        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("valid").GetBoolean());
        var proposed = root.GetProperty("proposedYaml").GetString()!;
        Assert.Contains("  - step: static-site", proposed, StringComparison.Ordinal);
        Assert.Contains("    directory: \"/srv/www/_staging/site\"", proposed, StringComparison.Ordinal);
        Assert.Equal(2, root.GetProperty("diagram").GetProperty("environments").GetArrayLength());
        Assert.Equal(SchemaOne, File.ReadAllText(DefinitionFile));
        Assert.Equal("1", Git("rev-list", "--count", "HEAD"));
    }

    [Fact]
    public async Task Unreadable_declarations_report_one_issue_instead_of_unregistered_targets()
    {
        using var migration = await GetJson("/api/projects/Website/pipeline-definition/migration");
        File.WriteAllText(DefinitionFile, migration.RootElement.GetProperty("proposedYaml").GetString());
        File.WriteAllText(Path.Combine(Repository, "docs", "deployments", "environments", "staging.json"), "{");

        using var read = await GetJson("/api/projects/Website/pipeline-definition");
        var issue = Assert.Single(read.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal("declarations-unreadable", issue.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Confirm_with_stale_digests_is_refused_without_writing()
    {
        using var dryRun = await GetJson("/api/projects/Website/pipeline-definition/migration");
        var proposalSha = dryRun.RootElement.GetProperty("proposalSha256").GetString();

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/migration",
            new { baseSha256 = new string('0', 64), proposalSha256 = proposalSha });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SchemaOne, File.ReadAllText(DefinitionFile));
        Assert.Equal("1", Git("rev-list", "--count", "HEAD"));
    }

    [Fact]
    public async Task Confirm_writes_the_dry_run_through_one_managed_commit()
    {
        using var dryRun = await GetJson("/api/projects/Website/pipeline-definition/migration");
        var proposed = dryRun.RootElement.GetProperty("proposedYaml").GetString();

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/migration", new
        {
            baseSha256 = dryRun.RootElement.GetProperty("baseSha256").GetString(),
            proposalSha256 = dryRun.RootElement.GetProperty("proposalSha256").GetString(),
        });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(proposed, File.ReadAllText(DefinitionFile));
        Assert.Equal(body.RootElement.GetProperty("commitSha").GetString(), Git("rev-parse", "HEAD"));
        Assert.Equal(".agent-studio/project.yml", Git("show", "--name-only", "--format=", "HEAD"));
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
        var definition = body.RootElement.GetProperty("definition");
        Assert.Equal(2, definition.GetProperty("schemaVersion").GetInt32());
        Assert.True(definition.GetProperty("valid").GetBoolean());
        Assert.False(definition.GetProperty("migrationAvailable").GetBoolean());
    }

    [Fact]
    public async Task Deploy_step_form_writes_steps_so_schema_two_environments_become_deployable()
    {
        // F5: without JSON declarations the migration writes environments but no deploy steps.
        Directory.Delete(Path.Combine(Repository, "docs", "deployments", "environments"), true);
        Git("commit", "-q", "-am", "drop declarations");
        await ConfirmMigration();
        using (var migrated = await GetJson("/api/projects/Website/pipeline-definition"))
            Assert.True(migrated.RootElement.GetProperty("deployStepsAvailable").GetBoolean());
        Assert.Empty(Environments().DeclarationFiles(Project)!);
        var form = new
        {
            artifactType = "static",
            artifactPath = "dist",
            environments = new[]
            {
                new { name = "staging", target = "web-vm", directory = "/srv/www/_staging/site", healthUrl = "https://example.test/_staging/", healthTimeoutSeconds = 60 },
                new { name = "production", target = "web-vm", directory = "/srv/www/site", healthUrl = "https://example.test/", healthTimeoutSeconds = 120 },
            },
        };
        var commits = Git("rev-list", "--count", "HEAD");

        using var previewResponse = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/deploy-steps/preview", form);
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.True(preview.RootElement.GetProperty("valid").GetBoolean(), preview.RootElement.ToString());
        Assert.Equal(commits, Git("rev-list", "--count", "HEAD"));

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/deploy-steps", new
        {
            form.artifactType, form.artifactPath, form.environments,
            baseSha256 = preview.RootElement.GetProperty("baseSha256").GetString(),
            proposalSha256 = preview.RootElement.GetProperty("proposalSha256").GetString(),
        });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(preview.RootElement.GetProperty("proposedYaml").GetString(), File.ReadAllText(DefinitionFile));
        Assert.Equal(body.RootElement.GetProperty("commitSha").GetString(), Git("rev-parse", "HEAD"));
        Assert.Equal(".agent-studio/project.yml", Git("show", "--name-only", "--format=", "HEAD"));
        Assert.False(body.RootElement.GetProperty("definition").GetProperty("deployStepsAvailable").GetBoolean());
        var environments = Environments().DeclarationFiles(Project)!;
        Assert.Equal(["stage", "production"], environments.Select(file => file.Declaration!.Id));
    }

    [Fact]
    public async Task Deploy_step_confirm_with_changed_form_is_refused_as_stale()
    {
        Directory.Delete(Path.Combine(Repository, "docs", "deployments", "environments"), true);
        Git("commit", "-q", "-am", "drop declarations");
        await ConfirmMigration();
        var environments = new[]
        {
            new { name = "staging", target = "web-vm", directory = "/srv/www/_staging/site", healthUrl = "https://example.test/_staging/", healthTimeoutSeconds = 60 },
            new { name = "production", target = "web-vm", directory = "/srv/www/site", healthUrl = "https://example.test/", healthTimeoutSeconds = 120 },
        };
        using var previewResponse = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/deploy-steps/preview",
            new { artifactType = "static", artifactPath = "dist", environments });
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var before = File.ReadAllText(DefinitionFile);

        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/deploy-steps", new
        {
            artifactType = "static", artifactPath = "build", environments,
            baseSha256 = preview.RootElement.GetProperty("baseSha256").GetString(),
            proposalSha256 = preview.RootElement.GetProperty("proposalSha256").GetString(),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllText(DefinitionFile));
    }

    private async Task ConfirmMigration()
    {
        using var dryRun = await GetJson("/api/projects/Website/pipeline-definition/migration");
        using var response = await _client!.PostAsJsonAsync("/api/projects/Website/pipeline-definition/migration", new
        {
            baseSha256 = dryRun.RootElement.GetProperty("baseSha256").GetString(),
            proposalSha256 = dryRun.RootElement.GetProperty("proposalSha256").GetString(),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private ProjectEnvironmentService Environments() => _app!.Services.GetRequiredService<ProjectEnvironmentService>();

    [Fact]
    public async Task Unknown_project_is_not_found()
    {
        using var response = await _client!.GetAsync("/api/projects/Nope/pipeline-definition");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        using var response = await _client!.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }
}
