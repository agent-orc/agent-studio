using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using AgentStudio.Docs;
using AgentStudio.Git;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// AGT-3793: the two polled reads that broke the Git read guard on an idle
/// host start no Git for an unchanged repository. On 15 November 2026 the
/// orchestrator feed ran 52 Git processes per request (the live Dossier
/// catalogue resolved the Wiki source with a full working-tree status per
/// project) and the board's Git summary 39 (three per project after a
/// three-second expiry). Both routes now read published snapshots, so their
/// ceiling is zero: neither has an exception in
/// <see cref="GitReadSpawnExceptionManifest"/>, and the strict test guard
/// fails the request at the first spawn.
/// </summary>
[Trait("Category", "Guard")]
public sealed class IdleReadRouteGitSpawnGuardTests : IAsyncLifetime
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);
    private const string ProjectName = "IdleGuard";
    private const string Feed = "/api/runner/orchestrator-feed";
    private const string Summary = "/api/git/summary";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "idle-read-guard-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingObserver _observer = new();
    private string Repo => Path.Combine(_root, "repo");
    private string WatchPath => Path.Combine(_root, "workspace", "projects", ProjectName.ToLowerInvariant());
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private bool _summaryRequested;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(WatchPath);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "guard@example.invalid");
        await Git("config", "user.name", "Guard");
        await Git("config", "commit.gpgsign", "false");
        Write("README.md", "# Idle guard\n");
        Write("docs/guide/start.md", "# Start\n\nGuide.\n");
        Write("docs/concepts/dossiers/choice/index.html", "<html><body><h1>Choice</h1></body></html>");
        Write("docs/concepts/dossiers/choice/workbench.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = "choice",
            // Startup catalogue discovery assigns and commits missing keys even
            // without hosted services. An idle-read fixture must already have one.
            key = "IDLE-W1",
            title = "Choice",
            summary = "A decided Dossier.",
            entrypoint = "index.html",
            status = "archived",
            // clock-independent: Dossier descriptor data; the feed only carries the dates through.
            updatedAt = "2026-11-15T10:00:00Z",
            decision = new
            {
                outcome = "archive",
                state = "succeeded",
                operationId = "operation-0001",
                preparedAt = "2026-11-15T09:00:00Z",
                preparedBy = "owner",
                sourceFingerprint = new string('a', 64),
                confirmedAt = "2026-11-15T09:30:00Z",
                confirmedBy = "owner",
                decidedAt = "2026-11-15T09:30:00Z",
                reason = "Option A",
                spawnedTaskKeys = Array.Empty<string>(),
            },
        }));
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "seed");

        var seedHead = await Git("rev-parse", "HEAD");

        _factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", Path.Combine(_root, "workspace"));
            builder.UseSetting("WatchPaths:0:Name", ProjectName);
            builder.UseSetting("WatchPaths:0:Path", WatchPath);
            builder.UseSetting("WatchPaths:0:RootPath", Repo);
            builder.UseSetting("WatchPaths:0:RepositoryPath", Repo);
            builder.UseSetting("Startup:RepairSweeps", "false");
            builder.UseSetting("Runner:RunLiveness:Enabled", "false");
            builder.UseSetting("Logging:BackendFile:LogDirectory", Path.Combine(_root, "logs"));
            builder.ConfigureTestServices(services =>
            {
                // Disable hosted workers. Direct catalogue startup reads still run.
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.AddSingleton<IGitReadRequestObserver>(_observer);
            });
        });
        _client = _factory.CreateClient();
        _client.Timeout = CompletionTimeout;
        _client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        // Exercise the same catalogue discovery as the startup callback. It must
        // leave both HEAD and the index untouched, regardless of who gets there first.
        Assert.NotNull(Services.GetRequiredService<WorkbenchCatalogueService>().List(ProjectName, includeHistory: true));
        Assert.Equal(seedHead, await Git("rev-parse", "HEAD"));
        Assert.Equal("", await Git("status", "--porcelain=v1"));
    }

    [Fact]
    public async Task Orchestrator_feed_starts_no_git_cold_or_warm_and_serves_published_dossier_decisions()
    {
        await Get(Feed);
        Assert.True(Services.GetRequiredService<ProjectDocsService>().PreloadWikiContent(ProjectName));
        var warm = await Get(Feed);
        Services.GetRequiredService<GitService>().ExpireShortLivedProbesForTesting();
        await Get(Feed);

        AssertNoSpawns(Feed, expectedRequests: 3);
        var items = Services.GetRequiredService<ProjectDocsService>().GetPublishedWikiWorkbenches(ProjectName);
        Assert.True(items.Any(item => item.Id == "choice" && item.Decision is not null),
            "published: " + string.Join("; ", items.Select(item => $"{item.Id} valid={item.Valid} {item.Error}")) + " feed: " + warm);
        using var body = JsonDocument.Parse(warm);
        Assert.Contains(body.RootElement.GetProperty("ownerEntries").EnumerateArray(), entry =>
            entry.GetProperty("topic").GetString() == "dossier-decision"
            && entry.GetProperty("dossierId").GetString() == "choice");
    }

    [Fact]
    public async Task Git_summary_starts_no_git_on_the_request_and_publishes_the_background_refresh()
    {
        var git = Services.GetRequiredService<GitService>();
        await Get(Summary);
        await git.RefreshSummariesAsync().WaitAsync(CompletionTimeout);
        var warm = await Get(Summary);
        git.ExpireShortLivedProbesForTesting();
        await Get(Summary);

        AssertNoSpawns(Summary, expectedRequests: 3);
        using var body = JsonDocument.Parse(warm);
        var project = Assert.Single(body.RootElement.EnumerateArray());
        Assert.True(project.GetProperty("isRepo").GetBoolean());
        Assert.Equal("main", project.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task Git_summary_refreshes_after_the_observed_head_moves()
    {
        var git = Services.GetRequiredService<GitService>();
        await git.RefreshSummariesAsync().WaitAsync(CompletionTimeout);
        Write("src/change.txt", "change\n");
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "move head");
        Write("src/dirty.txt", "dirty\n");
        await Git("add", "src/dirty.txt");

        Assert.Equal("A  src/dirty.txt", await Git("status", "--porcelain=v1"));
        var movedHead = await Git("rev-parse", "HEAD");

        // The request sees the moved ref and index files and queues the refresh.
        await Get(Summary);
        var refreshed = await git.RefreshSummariesAsync().WaitAsync(CompletionTimeout);

        Assert.Equal(1, Assert.Single(refreshed).FilesChanged);
        Assert.Equal(movedHead, await Git("rev-parse", "HEAD"));
        Assert.Equal("A  src/dirty.txt", await Git("status", "--porcelain=v1"));
        AssertNoSpawns(Summary, expectedRequests: 1);
    }

    private IServiceProvider Services => _factory!.Services;

    private async Task<string> Get(string url)
    {
        if (url == Summary) _summaryRequested = true;
        using var response = await _client!.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)response.StatusCode}: {body}");
        // TestServer can deliver the body before the middleware's finally has
        // published the request evidence. Wait for that evidence, not a delay.
        await _observer.WaitForCompletionAsync(url);
        return body;
    }

    private void AssertNoSpawns(string route, int expectedRequests)
    {
        var records = _observer.Records.Where(record => record.RouteTemplate == route).ToList();
        Assert.Equal(expectedRequests, records.Count);
        Assert.Null(GitReadSpawnExceptionManifest.Find("GET", route));
        Assert.All(records, record => Assert.True(record.Spawns == 0,
            $"{route} started Git on an unchanged repository: [{string.Join(",", record.Commands)}]"));
    }

    private void Write(string rel, string content)
    {
        var path = Path.Combine(Repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<string> Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(CompletionTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(CompletionTimeout);
            throw;
        }
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {await error}");
        return (await output).TrimEnd('\r', '\n');
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            try
            {
                // A request can queue a refresh even if its assertion fails.
                // Join it before disposing services or deleting its repository.
                if (_summaryRequested)
                    await Services.GetRequiredService<GitService>().RefreshSummariesAsync().WaitAsync(CompletionTimeout);
            }
            finally
            {
                await _factory.DisposeAsync();
            }
        }
        GitService.InvalidateToplevelCache(_root);
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class RecordingObserver : IGitReadRequestObserver
    {
        public ConcurrentQueue<GitReadRequestRecord> Records { get; } = new();
        private readonly Channel<GitReadRequestRecord> _completed = Channel.CreateUnbounded<GitReadRequestRecord>();

        public void OnCompleted(GitReadRequestRecord record)
        {
            Records.Enqueue(record);
            _completed.Writer.TryWrite(record);
        }

        public async Task WaitForCompletionAsync(string route)
        {
            using var timeout = new CancellationTokenSource(CompletionTimeout);
            while ((await _completed.Reader.ReadAsync(timeout.Token)).RouteTemplate != route) { }
        }
    }
}
