using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2990 - an unknown project segment on the delivery-claim routes must
/// answer 404 and sweep nothing. Before the fix it resolved to no watch path,
/// which the sweep read as "every project", so a mistyped id ran the repair
/// pass across the whole workspace.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class DeliveryClaimSweepScopeTests : IDisposable
{
    // clock-independent: the commit timestamp is only serialized task history.
    private static readonly DateTimeOffset CommitAt = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Project = "Fixture";
    private const string TaskId = "contained-without-record";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;
    private readonly string _folder;

    public DeliveryClaimSweepScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "delivery-claim-scope-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Delivery Claim Scope Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        File.WriteAllText(Path.Combine(_repo, "delivery.txt"), "delivery\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: contained delivery");
        var sha = Git(_repo, "rev-parse", "HEAD");

        // A contained delivery without an integration record: exactly the
        // shape the reconcile pass repairs, so any sweep that reaches this
        // card leaves a visible record behind.
        _folder = Path.Combine(_watchPath, TaskStates.Completed, TaskId);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = TaskId,
            key = "AGT-9001",
            title = TaskId,
            state = TaskStates.Completed,
            order = 1,
            agent = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
            commits = new[]
            {
                new
                {
                    sha,
                    shortSha = sha[..8],
                    message = "feat: contained delivery",
                    filesChanged = 1,
                    at = CommitAt,
                    attribution = "automatic",
                    confidence = 1,
                },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Implement the fixture.\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Delivery-claim scope test cleanup is best-effort."); }
    }

    [Fact]
    public void UnknownProject_SweepsAndRepairsNothing()
    {
        var (sweep, scanner, _) = BuildSweep();

        Assert.Null(sweep.Run("PROJ-999", repair: true));
        Assert.Null(sweep.Run("PROJ-999", repair: false));

        scanner.InvalidateCache();
        Assert.Empty(scanner.FindJob(TaskId, _watchPath)!.IntegrationRecords);
    }

    [Fact]
    public void KnownProject_StillSweepsAndRepairsItsCards()
    {
        var (sweep, scanner, projects) = BuildSweep();
        var project = projects.EnsureProjectForStorage(_watchPath, Project, DefaultWorkspace.Id);

        var report = sweep.Run(project.Id, repair: true);

        Assert.NotNull(report);
        var row = Assert.Single(report!.Rows);
        Assert.Contains(DeliveryClaimFindings.MissingIntegrationRecord, row.Repairs);
        scanner.InvalidateCache();
        Assert.Single(scanner.FindJob(TaskId, _watchPath)!.IntegrationRecords);
    }

    [Fact]
    public void BlankScope_StillSweepsEveryProject()
    {
        var (sweep, _, _) = BuildSweep();

        var report = sweep.Run(null, repair: false);

        Assert.NotNull(report);
        Assert.Equal("*", report!.Project);
        Assert.Equal(1, report.Scanned);
    }

    [Theory]
    [InlineData("GET", "/api/projects/PROJ-999/delivery-claims")]
    [InlineData("POST", "/api/projects/PROJ-999/delivery-claims/reconcile")]
    public async Task UnknownProject_RouteReturnsNotFound(string method, string route)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repo,
                }));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private (DeliveryClaimSweep Sweep, TaskScannerService Scanner, ProjectRegistry Projects) BuildSweep()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
        }).Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var projects = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            projects,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var integration = new TaskIntegrationStatusService(
            git,
            settings,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<TaskIntegrationStatusService>.Instance);
        var sweep = new DeliveryClaimSweep(
            scanner,
            integration,
            mutations,
            settings,
            projects,
            NullLogger<DeliveryClaimSweep>.Instance,
            git);
        Assert.NotNull(scanner.FindJob(TaskId, _watchPath));
        return (sweep, scanner, projects);
    }

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }
}
