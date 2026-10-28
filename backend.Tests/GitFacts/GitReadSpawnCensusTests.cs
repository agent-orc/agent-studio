using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Registry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Focused runtime census of the known Git read violators (AGT-3321, Git facts
/// S1, step 3). Each scenario boots a fresh host over the shared 3,000-card
/// <see cref="GitFactsBoardFixture"/>, clears the process-wide toplevel cache,
/// and issues the requests the Studio sends for that family. A per-host
/// <see cref="IGitReadRequestObserver"/> captures every GET with its route
/// template and Git commands.
///
/// <para>The guard runs strict here, so a request on a route without a frozen
/// exception, or past its ceiling, fails with
/// <see cref="GitProcessOnReadRequestException"/>. The scenarios additionally
/// assert that every observed route is declared by its scenario, that routes
/// the census marks <c>measured-zero</c> start no Git, and that every frozen
/// exception route is exercised. Set <c>GIT_READ_CENSUS_OUT</c> to a file to
/// append one JSON line of evidence per request (method, route template,
/// scenario, spawns, commands, owner, removal slice).</para>
/// </summary>
[Collection(GitFactsCollection.Name)]
public sealed class GitReadSpawnCensusTests(GitFactsBoardFixture board, ITestOutputHelper output)
{
    public static TheoryData<string> Scenarios
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var scenario in GitReadSpawnScenarios.All) data.Add(scenario.Name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Known_violator_stays_within_its_frozen_ceiling(string name)
    {
        var scenario = GitReadSpawnScenarios.All.Single(candidate => candidate.Name == name);
        GitService.InvalidateToplevelCache();
        await using var host = new GitReadCensusHost(board);
        await scenario.Run(host);
        GitReadCensusEvidence.Append(scenario, host.Records);

        var census = Architecture.GitReadRouteCensusTests.ReadCensus();
        Assert.NotEmpty(host.Records);
        foreach (var record in host.Records)
        {
            output.WriteLine($"{record.StatusCode} {record.RouteTemplate} spawns={record.Spawns} [{string.Join(",", record.Commands)}]");
            Assert.Contains(record.RouteTemplate, scenario.Routes);
            Assert.NotEqual(500, record.StatusCode);
            Assert.Equal(GitReadSpawnVerdict.Allowed, record.Verdict);
            if (census.TryGetValue(record.RouteTemplate, out var route) && route.Status is "measured-zero" or "-")
                Assert.True(record.Spawns == 0,
                    $"{record.RouteTemplate} is Git-free in the census but started [{string.Join(",", record.Commands)}]");
        }
    }

    [Fact]
    public void Every_frozen_exception_has_a_scenario()
    {
        var exercised = GitReadSpawnScenarios.Routes;
        var missing = GitReadSpawnExceptionManifest.Entries
            .Where(entry => !exercised.Contains(entry.RouteTemplate)
                && !GitReadSpawnScenarios.ExistingTestEvidence.ContainsKey(entry.RouteTemplate))
            .Select(entry => entry.RouteTemplate)
            .ToList();
        Assert.Empty(missing);
        var root = AgentStudio.TestSupport.RepositoryRoot.Find();
        Assert.All(GitReadSpawnScenarios.ExistingTestEvidence.Values.SelectMany(classes => classes), name =>
            Assert.True(File.Exists(Path.Combine(root, "backend.Tests", name + ".cs")), $"evidence class {name} is gone"));
    }
}

/// <summary>One family's requests and the route templates they may reach.</summary>
public sealed record GitReadSpawnScenario(
    string Name, string Family, IReadOnlySet<string> Routes, Func<GitReadCensusHost, Task> Run);

/// <summary>
/// The measured scenarios. Their declared routes are the census's evidence for
/// every <c>frozen</c> and <c>measured-zero</c> route.
/// </summary>
public static class GitReadSpawnScenarios
{
    private const string Tasks = "/api/tasks/{jobId}";
    private const string Wiki = "/api/projects/{projectName}";

    public static readonly IReadOnlyList<GitReadSpawnScenario> All =
    [
        Scenario("R4 provenance: cold, warm, moved ref, pending card", "R4",
            [Tasks + "/provenance"],
            async host =>
            {
                var review = host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "provenance");
                await host.Get(review);
                await host.Get(review);
                host.Board.MoveDevelop("provenance");
                await host.Get(review);
                await host.Get(host.TaskUrl(GitFactsBoardFixture.EscalatedBare, "provenance"));
                foreach (var lane in new[] { TaskStates.Completed, TaskStates.Archive })
                    await host.Get(host.TaskUrl(host.Board.PlainCard(lane), "provenance"));
            }),
        Scenario("R5 commit aggregate and default diff: review, completed, archived, escalated; cold, warm", "R5",
            [Tasks + "/commits", Tasks + "/commits/files", Tasks + "/commits/diff", Tasks + "/commit", Tasks + "/commit/diff"],
            async host =>
            {
                var cards = new[]
                {
                    GitFactsBoardFixture.IntegratedReview, host.Board.PlainCard(TaskStates.Completed),
                    host.Board.PlainCard(TaskStates.Archive), GitFactsBoardFixture.EscalatedWithFiles,
                };
                // The commit list opened cold on its own host (it greps the run
                // trailer and walks the run range); then the Studio's order,
                // aggregate files and default diff first.
                await host.GetCold(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "commits"));
                for (var pass = 0; pass < 2; pass++)
                foreach (var card in cards)
                foreach (var resource in new[] { "commits/files", "commits/diff", "commits", "commit", "commit/diff" })
                    await host.Get(host.TaskUrl(card, resource));
            }),
        Scenario("R5 single commit views", "R5",
            [Tasks + "/commits/{sha}/files", Tasks + "/commits/{sha}/diff", Tasks + "/commits/{sha}/file"],
            async host =>
            {
                var sha = host.Board.ReviewCommits[0];
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, $"commits/{sha}/files"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, $"commits/{sha}/diff"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, $"commits/{sha}/file", "&path=src/Facts/Review.cs"));
            }),
        Scenario("R7 run views: cold, warm", "R7",
            [Tasks + "/runs/{index:int}/commits", Tasks + "/runs/{index:int}/files", Tasks + "/runs/{index:int}/diff"],
            async host =>
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    foreach (var resource in new[] { "runs/1/commits", "runs/1/files", "runs/1/diff" })
                        await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, resource));
                }
            }),
        Scenario("R8 worktree status, diff, file, hygiene: cold, warm", "R8",
            [Tasks + "/git/status", Tasks + "/git/diff", Tasks + "/git/file", Tasks + "/git/hygiene", Tasks + "/git/withheld-candidates"],
            async host =>
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.InProgress, "git/status"));
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.InProgress, "git/diff"));
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.InProgress, "git/file", "&path=src/Facts/InProgress.cs"));
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.InProgress, "git/hygiene"));
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.InProgress, "git/withheld-candidates"));
                }
            }),
        Scenario("R11 card files: follow-up present and absent, status history, fixed SHA, missing file", "R11",
            [Tasks + "/files/{**path}"],
            async host =>
            {
                await host.Get(host.TaskUrl(GitFactsBoardFixture.EscalatedWithFiles, "files/orchestrator-follow-up.md"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.EscalatedBare, "files/orchestrator-follow-up.md"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "files/results/status.md"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "files/results/status.md/history"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "files/code-review.md",
                    "&at=" + host.Board.FirstStatusVersion));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "files/missing.md/history"));
            }),
        Scenario("R11 decision files, workspace source: present and absent", "R11",
            [Tasks + "/files/{**path}"],
            async host =>
            {
                foreach (var id in new[] { GitFactsBoardFixture.EscalatedWithFiles, GitFactsBoardFixture.EscalatedBare })
                {
                    await host.Get(host.TaskUrl(id, "files/results/decision.html", "&scope=workspace"));
                    await host.Get(host.TaskUrl(id, "files/results/decision.json", "&scope=workspace"));
                }
            }),
        Scenario("R11 checkout files: live, fixed SHA, history, missing file", "R11",
            [Tasks + "/checkout/{**path}"],
            async host =>
            {
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "checkout/src/Facts/Review.cs"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "checkout/src/Facts/Review.cs",
                    "&at=" + host.Board.ReviewCommits[0]));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "checkout/src/Facts/Review.cs/history"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "checkout/src/Facts/Missing.cs"));
            }),
        Scenario("R6 result history after AGT-3086", "R6",
            [Tasks + "/result-history"],
            async host =>
            {
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "result-history"));
                await host.Get(host.TaskUrl(GitFactsBoardFixture.EscalatedBare, "result-history"));
            }),
        Scenario("R6 result version warms from persisted facts", "R6",
            [Tasks + "/result-history/{versionId}"],
            async host =>
            {
                await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview,
                    "result-history/local-0001"));
            }),
        Scenario("R12 project integration view: cold, warm, moved ref", "R12",
            ["/api/git/integration"],
            async host =>
            {
                var url = "/api/git/integration?project=" + GitFactsBoardFixture.ProjectName;
                await host.Get(url);
                await host.Get(url);
                host.Board.MoveDevelop("integration-view");
                await host.Get(url);
            }),
        Scenario("R13 Wiki in checkout mode", "R13",
            [Wiki + "/wiki/recent", Wiki + "/wiki/pulse", Wiki + "/wiki/history/{**relPath}",
             Wiki + "/wiki/revisions/{sha}/{**relPath}", Wiki + "/wiki", Wiki + "/wiki/tree", Wiki + "/wiki/home",
             Wiki + "/wiki/files/{**relPath}", Wiki + "/wiki/folder/{**relPath}"],
            async host =>
            {
                var project = Wiki.Replace("{projectName}", GitFactsBoardFixture.ProjectName, StringComparison.Ordinal);
                var page = GitFactsBoardFixture.WikiPage["docs/".Length..];
                // Each snapshot read is measured as the first Wiki read of a
                // fresh host: an invalidation refills eagerly, so only a new
                // host shows the cold fill a request pays.
                foreach (var resource in new[] { "wiki", "wiki/tree", "wiki/home", "wiki/files/" + page, "wiki/folder/guide" })
                    await host.GetCold($"{project}/{resource}");
                for (var pass = 0; pass < 2; pass++)
                {
                    await host.Get($"{project}/wiki/recent");
                    await host.Get($"{project}/wiki/pulse");
                    await host.Get($"{project}/wiki/history/{page}");
                    await host.Get($"{project}/wiki/revisions/{host.Board.ReviewCommits[1]}/{page}");
                }
            }),
        Scenario("R13 Wiki pinned to a fixed branch commit", "R13",
            [Wiki + "/wiki/recent", Wiki + "/wiki/history/{**relPath}", Wiki + "/wiki/revisions/{sha}/{**relPath}"],
            async host =>
            {
                var registry = host.Services.GetRequiredService<ProjectRegistry>();
                var record = registry.FindByStorageLocation(host.Board.WatchPath)!;
                registry.SetWikiSourceBranch(record.Id, "develop");
                host.Services.GetRequiredService<AgentStudio.Docs.ProjectDocsService>()
                    .InvalidateWikiContent(GitFactsBoardFixture.ProjectName);
                try
                {
                    var project = Wiki.Replace("{projectName}", GitFactsBoardFixture.ProjectName, StringComparison.Ordinal);
                    var page = GitFactsBoardFixture.WikiPage["docs/".Length..];
                    for (var pass = 0; pass < 2; pass++)
                    {
                        await host.Get($"{project}/wiki/recent");
                        await host.Get($"{project}/wiki/history/{page}");
                        await host.Get($"{project}/wiki/revisions/{host.Board.ReviewCommits[1]}/{page}");
                    }
                }
                finally { registry.SetWikiSourceBranch(record.Id, null); }
            }),
        Scenario("R14 delivery claim: integrated Human Review, integrated and pending escalated", "R14",
            [Tasks + "/delivery-claim", "/api/projects/{projectId}/delivery-claims",
             "/api/projects/{projectId}/completed-lane/integration-pending"],
            async host =>
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "delivery-claim"));
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.EscalatedWithFiles, "delivery-claim"));
                    await host.Get(host.TaskUrl(GitFactsBoardFixture.EscalatedBare, "delivery-claim"));
                    foreach (var lane in new[] { TaskStates.Completed, TaskStates.Archive })
                        await host.Get(host.TaskUrl(host.Board.PlainCard(lane), "delivery-claim"));
                }
                await host.Get($"/api/projects/{host.ProjectId}/delivery-claims");
                // The project sweep and the pending listing read published
                // verdicts too (Git facts S3): a cold host starts no Git.
                await host.GetCold($"/api/projects/{host.ProjectId}/delivery-claims");
                await host.GetCold($"/api/projects/{host.ProjectId}/completed-lane/integration-pending");
            }),
        Scenario("Project Git views and maintenance previews", "Inventory boundary: project views",
            ["/api/git/summary", "/api/git/hygiene", "/api/git/inventory", "/api/git/history",
             "/api/git/project-commit/files", "/api/git/project-commit/diff", "/api/git/cleanup/plan"],
            async host =>
            {
                var project = "?project=" + GitFactsBoardFixture.ProjectName;
                var sha = host.Board.ReviewCommits[1];
                await host.Get("/api/git/summary");
                await host.Get("/api/git/hygiene" + project);
                await host.Get("/api/git/inventory" + project);
                await host.Get("/api/git/history" + project);
                await host.Get($"/api/git/project-commit/files{project}&sha={sha}");
                await host.Get($"/api/git/project-commit/diff{project}&sha={sha}");
                await host.Get("/api/git/cleanup/plan" + project);
            }),
        Scenario("Publishing reads: status, panel, stored-run probe", "Inventory boundary: publishing",
            ["/api/projects/{project}/publish-status", "/api/projects/{project}/publish/{targetId}/panel",
             "/api/projects/{project}/publish/{targetId}/run"],
            async host =>
            {
                var root = "/api/projects/" + GitFactsBoardFixture.ProjectName;
                var status = await host.GetJson(root + "/publish-status");
                var target = status is { } body ? FirstTargetId(body) : null;
                await host.Get(root + "/publish-status");
                Assert.NotNull(target);
                // The panel derives targets itself on a cold cache.
                host.Services.GetRequiredService<AgentStudio.Publishing.PublishTargetService>().InvalidateCache();
                await host.Get($"{root}/publish/{target}/panel");
                await host.Get($"{root}/publish/{target}/panel");
                await host.Get($"{root}/publish/{target}/run");
            }),
        Scenario("Other project reads that existing tests caught starting Git", "Inventory boundary: other reads",
            ["/api/admin/prompts/", "/api/admin/prompts/{name}", "/api/orchestrator/context/global",
             "/api/orchestrator/context/project:{projectId}", "/api/orchestrator/context/task:{projectId}/{taskKey}",
             "/api/parked-cards", "/api/projects/pipeline-catalogue", "/api/projects/{project}/test-runs",
             "/api/projects/{projectName}/deploy/status", "/api/projects/{projectName}/deploy/releases/page",
             "/api/projects/{projectName}/deploy/deployments/page", "/api/projects/{projectName}/deploy/runbooks",
             "/api/projects/{project}/secret-bindings/{environment}",
             "/api/runner/project:{projectId}/orchestrator-chat",
             "/api/runner/task:{projectId}/{taskKey}/orchestrator-chat",
             "/api/projects/{projectName}/wiki/assets/{**relPath}"],
            async host =>
            {
                var project = GitFactsBoardFixture.ProjectName;
                var key = GitFactsBoardFixture.IntegratedReview.ToUpperInvariant();
                await host.Get("/api/admin/prompts/");
                await host.Get("/api/admin/prompts/coding");
                await host.Get("/api/orchestrator/context/global");
                await host.Get($"/api/orchestrator/context/project:{host.ProjectId}");
                await host.Get($"/api/orchestrator/context/task:{host.ProjectId}/{key}");
                await host.Get("/api/parked-cards");
                await host.Get("/api/projects/pipeline-catalogue");
                await host.Get($"/api/projects/{project}/test-runs");
                foreach (var resource in new[] { "deploy/status", "deploy/releases/page", "deploy/deployments/page", "deploy/runbooks" })
                    await host.Get($"/api/projects/{project}/{resource}");
                await host.Get($"/api/projects/{project}/secret-bindings/production");
                await host.Get($"/api/runner/project:{host.ProjectId}/orchestrator-chat");
                await host.Get($"/api/runner/task:{host.ProjectId}/{key}/orchestrator-chat");
                await host.Get($"/api/projects/{project}/wiki/assets/guide/missing.png");
            }),
        Scenario("R1 to R3 board, core and detail resources", "R1-R3",
            ["/api/tasks/", "/api/tasks/grouped", Tasks + "/core", Tasks + "/details/overview", Tasks + "/details/git",
             Tasks + "/details/review", Tasks + "/details/documents", Tasks + "/details/history", Tasks],
            async host =>
            {
                await host.Get("/api/tasks/");
                await host.Get("/api/tasks/grouped");
                foreach (var id in new[] { GitFactsBoardFixture.IntegratedReview, GitFactsBoardFixture.EscalatedWithFiles })
                {
                    await host.Get($"/api/tasks/{id}/core?project={host.ProjectId}");
                    foreach (var resource in new[] { "overview", "git", "review", "documents", "history" })
                        await host.Get($"/api/tasks/{id}/details/{resource}?project={host.ProjectId}");
                    await host.Get(host.TaskUrl(id, ""));
                }
            }),
    ];

    /// <summary>
    /// Routes whose frozen ceiling also rests on the census ledger run of the
    /// existing host tests (GIT_READ_GUARD_LEDGER_OUT over every test class that
    /// boots a backend host). The classes named here request the route; the
    /// scenarios above could not drive these routes into their Git path on the
    /// board (missing environments, workbenches or chats answer 404 first).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ExistingTestEvidence =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["/api/admin/prompts/{name}"] = ["PromptAdminEndpointsTests"],
            ["/api/orchestrator/context/project:{projectId}"] = ["OrchestratorContextEndpointsTests"],
            ["/api/orchestrator/context/task:{projectId}/{taskKey}"] = ["OrchestratorContextEndpointsTests"],
            ["/api/orchestrator/context/workbench:{projectId}/{workbenchKey}"] = ["OrchestratorContextEndpointsTests"],
            ["/api/parked-cards"] = ["ParkedCardEndpointsTests"],
            ["/api/projects/pipeline-catalogue"] = ["IamHandlerAccessTests"],
            ["/api/projects/{projectName}/deploy/deployments/page"] = ["DeployServiceTests"],
            ["/api/projects/{projectName}/deploy/releases/page"] = ["DeployServiceTests"],
            ["/api/projects/{projectName}/deploy/runbooks"] = ["IamHandlerAccessTests"],
            ["/api/projects/{projectName}/deploy/status"] = ["DeployServiceTests", "IamHandlerAccessTests"],
            ["/api/projects/{projectName}/environments/{environmentId}/telemetry/metrics/names"] = ["IamHandlerAccessTests"],
            ["/api/projects/{projectName}/wiki/assets/{**relPath}"] = ["NetworkedEdgeHardeningTests"],
            ["/api/projects/{projectName}/workbenches/{key}/references"] = ["WaitsOnEndpointsTests"],
            ["/api/projects/{project}/secret-bindings/{environment}"] = ["ProjectEnvironmentServiceTests", "IamHandlerAccessTests"],
            ["/api/projects/{project}/test-runs"] = ["TestRunServiceTests"],
            ["/api/runner/project:{projectId}/orchestrator-chat"] = ["OrchestratorContextChatEndpointsTests"],
            ["/api/runner/task:{projectId}/{taskKey}/orchestrator-chat"] = ["OrchestratorContextChatEndpointsTests"],
            ["/api/runner/workbench:{projectId}/{workbenchKey}/orchestrator-chat"] = ["OrchestratorContextChatEndpointsTests"],
        };

    /// <summary>Every route template a scenario exercises.</summary>
    public static readonly IReadOnlySet<string> Routes =
        All.SelectMany(scenario => scenario.Routes).ToHashSet(StringComparer.Ordinal);

    private static GitReadSpawnScenario Scenario(string name, string family, string[] routes, Func<GitReadCensusHost, Task> run)
        => new(name, family, routes.ToHashSet(StringComparer.Ordinal), run);

    private static string? FirstTargetId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("targets", out var targets)) return null;
        foreach (var target in targets.EnumerateArray())
            if (target.TryGetProperty("id", out var id)) return id.GetString();
        return null;
    }
}

/// <summary>A backend host over the board that records every GET's Git spawns.</summary>
public sealed class GitReadCensusHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly RecordingObserver _observer = new();

    public GitReadCensusHost(GitFactsBoardFixture board, bool keepGitStateIndex = false, IndexRunLog? indexRuns = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        Func<Func<List<TaskInfo>>, Func<List<TaskInfo>>>? wrapIndexScan = null)
    {
        Board = board;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (indexRuns is not null) builder.ConfigureLogging(logging => logging.AddProvider(indexRuns));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(board.HostSettings());
                if (settings is not null) config.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                // Hosted services reconcile lanes and warm caches in the
                // background; the census measures requests on a quiet host.
                services.RemoveAll<IHostedService>();
                if (keepGitStateIndex)
                    services.AddHostedService(provider => provider.GetRequiredService<GitStateIndexService>());
                services.AddSingleton<IGitReadRequestObserver>(_observer);
                if (wrapIndexScan is not null)
                    services.AddSingleton(provider =>
                    {
                        var scanner = provider.GetRequiredService<TaskScannerService>();
                        return new TaskIndexCache(scanner, provider.GetRequiredService<ILogger<TaskIndexCache>>(),
                            provider.GetRequiredService<IConfiguration>(), wrapIndexScan(scanner.ScanAllJobsRaw));
                    });
            });
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        ProjectId = Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(board.WatchPath)?.Id
            ?? GitFactsBoardFixture.ProjectName;
    }

    public GitFactsBoardFixture Board { get; }
    public IServiceProvider Services => _factory.Services;
    public HttpClient Client => _client;
    public string ProjectId { get; }
    public IReadOnlyList<GitReadRequestRecord> Records => _observer.Records.ToArray();

    public string TaskUrl(string id, string resource, string extraQuery = "")
        => $"/api/tasks/{id}{(resource.Length == 0 ? "" : "/" + resource)}{Board.Query}{extraQuery}";

    public async Task<HttpResponseMessage> Get(string url)
    {
        var response = await _client.GetAsync(url);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>Issues <paramref name="url"/> as the first request of a fresh host and keeps its evidence.</summary>
    public async Task GetCold(string url)
    {
        GitService.InvalidateToplevelCache();
        await using var fresh = new GitReadCensusHost(Board);
        await fresh.Get(url);
        await fresh.Get(url);
        foreach (var record in fresh.Records) _observer.Records.Enqueue(record);
    }

    public async Task<JsonElement?> GetJson(string url)
    {
        using var response = await Get(url);
        if (response.StatusCode != HttpStatusCode.OK) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed class RecordingObserver : IGitReadRequestObserver
    {
        public ConcurrentQueue<GitReadRequestRecord> Records { get; } = new();
        public void OnCompleted(GitReadRequestRecord record) => Records.Enqueue(record);
    }
}

/// <summary>Writes scenario evidence when <c>GIT_READ_CENSUS_OUT</c> names a file.</summary>
internal static class GitReadCensusEvidence
{
    private static readonly Lock Gate = new();

    public static void Append(GitReadSpawnScenario scenario, IReadOnlyList<GitReadRequestRecord> records)
    {
        var path = Environment.GetEnvironmentVariable("GIT_READ_CENSUS_OUT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var lines = records.Select(record =>
        {
            var exception = GitReadSpawnExceptionManifest.Find(record.Method, record.RouteTemplate);
            return JsonSerializer.Serialize(new
            {
                scenario = scenario.Name,
                family = scenario.Family,
                method = record.Method,
                route = record.RouteTemplate,
                status = record.StatusCode,
                spawns = record.Spawns,
                commands = record.Commands,
                ceiling = exception?.SpawnCeiling,
                owner = exception?.Owner,
                removal = exception?.RemovalSlice,
            });
        });
        lock (Gate) File.AppendAllLines(path, lines);
    }
}

[CollectionDefinition(Name)]
public sealed class GitFactsCollection : ICollectionFixture<GitFactsBoardFixture>
{
    public const string Name = "Git facts board";
}
