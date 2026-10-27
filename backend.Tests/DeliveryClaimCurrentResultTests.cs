using System.Diagnostics;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using AgentStudio.Pipeline;
using AgentStudio.Registry;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2817 rework - the delivery-claim answer for a card whose integration
/// verdict comes from the current-result shortcut. On Stable, AGT-2822 read
/// <c>integrated: true</c> with detail <c>current-result-ancestor</c>, yet its
/// class was <c>unintegrated-delivery</c>, its only commit
/// <c>onIntegrationBranch: false</c> and <c>released: null</c>, although the
/// commit was in develop and main. The shortcut verdict carried no
/// per-commit membership, and the sweep reads containment from exactly that.
/// </summary>
public sealed class DeliveryClaimCurrentResultTests : IDisposable
{
    private const string Project = "Fixture";
    private const string TaskId = "current-result-delivery";
    private const string RunAttemptId = "run-agt-2822";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;
    private readonly string _sha;

    public DeliveryClaimCurrentResultTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "delivery-claim-current-result-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "main", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Delivery Claim Current Result Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        Git(_repo, "checkout", "-q", "-b", "develop");
        File.WriteAllText(Path.Combine(_repo, "delivery.txt"), "delivery\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: delivered through the current result");
        _sha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "main");
        Git(_repo, "merge", "-q", "--ff-only", "develop");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Delivery-claim current-result test cleanup is best-effort."); }
    }

    [Fact]
    public void Describe_CurrentResultAncestor_IsAnIntegratedAndReleasedDelivery()
    {
        WriteCard(TaskStates.Completed);
        var (sweep, scanner, publish) = BuildSweep();
        publish();

        var answer = sweep.Describe(scanner.FindJob(TaskId, _watchPath)!);

        Assert.Equal("ready", answer.FactsState);
        Assert.True(answer.Integrated);
        Assert.Equal("current-result-ancestor", answer.Detail);
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, answer.Class);
        Assert.DoesNotContain(DeliveryClaimFindings.UnintegratedDelivery, answer.Findings);
        Assert.Contains(DeliveryClaimFindings.StalePendingSupersession, answer.Findings);
        Assert.True(answer.Released);
        var commit = Assert.Single(answer.Commits);
        Assert.Equal(_sha, commit.Sha);
        Assert.True(commit.OnIntegrationBranch);
        Assert.True(commit.OnReleaseBranch);
        Assert.Equal(CommitSupersessionStates.ReplacementPending, commit.Supersession);
    }

    [Fact]
    public void Reconcile_ClearsThePlaceholderOnAContainedCurrentResultDelivery()
    {
        WriteCard(TaskStates.Completed);
        var (sweep, scanner, publish) = BuildSweep();

        var report = sweep.Run(_watchPath, repair: true);

        var row = Assert.Single(report!.Rows);
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, row.Class);
        Assert.True(row.Released);
        Assert.Contains(DeliveryClaimFindings.StalePendingSupersession, row.Findings);
        Assert.Contains(DeliveryClaimFindings.StalePendingSupersession, row.Repairs);
        Assert.Contains(DeliveryClaimFindings.MissingIntegrationRecord, row.Repairs);

        scanner.InvalidateCache();
        var card = scanner.FindJob(TaskId, _watchPath)!;
        Assert.Equal(
            CommitSupersessionStates.Current,
            TaskCommitSupersession.State(Assert.Single(card.Commits)));
        publish();
        var after = sweep.Describe(card);
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, after.Class);
        Assert.Empty(after.Findings);
    }

    [Fact]
    public async Task TaskDeliveryClaimRoute_AnswersIntegratedAndReleased()
    {
        WriteCard(TaskStates.Archive);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.GetAsync(
            $"/api/tasks/{TaskId}/delivery-claim?watchPath={Uri.EscapeDataString(_watchPath)}");

        response.EnsureSuccessStatusCode();
        using var body = await PublishedClaim(client);
        var root = body.RootElement;
        Assert.Equal("ready", root.GetProperty("factsState").GetString());
        Assert.True(root.GetProperty("integrated").GetBoolean());
        Assert.Equal("current-result-ancestor", root.GetProperty("detail").GetString());
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, root.GetProperty("class").GetString());
        Assert.True(root.GetProperty("released").GetBoolean());
        var commit = Assert.Single(root.GetProperty("commits").EnumerateArray());
        Assert.Equal(_sha, commit.GetProperty("sha").GetString());
        Assert.True(commit.GetProperty("onIntegrationBranch").GetBoolean());
        Assert.True(commit.GetProperty("onReleaseBranch").GetBoolean());
    }

    [Fact]
    public async Task ProjectDeliveryClaimsRoute_ReportsTheCardAsIntegratedWithoutRepairing()
    {
        WriteCard(TaskStates.Archive);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var card = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(TaskId, _watchPath)!;
        var integration = factory.Services.GetRequiredService<TaskIntegrationStatusService>()
            .BuildForCompletion(card);
        Assert.Equal(IntegrationStatuses.Integrated, integration.Status);

        // The read-mode sweep joins published card facts (Git facts S3).
        (await PublishedClaim(client)).Dispose();
        using var response = await client.GetAsync($"/api/projects/{Project}/delivery-claims");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.False(root.GetProperty("repair").GetBoolean());
        var row = Assert.Single(root.GetProperty("rows").EnumerateArray());
        Assert.Equal(TaskId, row.GetProperty("jobId").GetString());
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, row.GetProperty("class").GetString());
        Assert.True(row.GetProperty("integrated").GetBoolean());
        Assert.True(row.GetProperty("released").GetBoolean());
        Assert.Contains(
            DeliveryClaimFindings.StalePendingSupersession,
            row.GetProperty("findings").EnumerateArray().Select(finding => finding.GetString()));
        Assert.Empty(row.GetProperty("repairs").EnumerateArray());
    }

    /// <summary>
    /// AGT-3097 - a two-commit attempt integrated through a curated mapping.
    /// The first commit never reaches develop itself, so the sweep must not
    /// read the card as unintegrated, and released follows the mapped
    /// integration commit on main.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sweep_MultiCommitCuratedMapping_IsIntegratedAndAnswersReleasedAgainstMain(bool released)
    {
        Git(_repo, "checkout", "-q", "-b", "task/curated", "develop");
        File.WriteAllText(Path.Combine(_repo, "first.txt"), "first\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: first half");
        var first = Git(_repo, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_repo, "second.txt"), "second\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: second half");
        var source = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "first.txt"), "first, curated\n");
        File.WriteAllText(Path.Combine(_repo, "second.txt"), "second, curated\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "merge(AGT-3097): curated integration");
        var integrated = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "main");
        if (released)
            Git(_repo, "merge", "-q", "--ff-only", "develop");
        WriteCuratedCard(first, source, integrated);
        var (sweep, scanner, publish) = BuildSweep();
        publish();

        var answer = sweep.Describe(scanner.FindJob(TaskId, _watchPath)!);
        var row = Assert.Single(sweep.Run(_watchPath, repair: false)!.Rows);

        Assert.True(answer.Integrated);
        Assert.Equal("current-result-curated-mapping", answer.Detail);
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, answer.Class);
        Assert.DoesNotContain(DeliveryClaimFindings.UnintegratedDelivery, answer.Findings);
        Assert.Equal(released, answer.Released);
        var mapped = Assert.Single(answer.Commits, commit => commit.Sha == source);
        Assert.True(mapped.OnIntegrationBranch);
        Assert.Equal(released, mapped.OnReleaseBranch);
        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, row.Class);
        Assert.Equal(released, row.Released);
    }

    private void WriteCuratedCard(string first, string source, string integrated)
    {
        const string attempt = "run-curated";
        var folder = Path.Combine(_watchPath, TaskStates.Completed, TaskId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = TaskId,
            key = "AGT-3097",
            title = TaskId,
            state = TaskStates.Completed,
            order = 1,
            agent = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
            commits = new[] { first, source }.Select(sha => new
            {
                sha,
                shortSha = sha[..8],
                message = "feat: curated attempt",
                filesChanged = 1,
                at = _clock.GetUtcNow(),
                attribution = "automatic",
                confidence = 1,
                runAttemptId = attempt,
            }).ToArray(),
            integrationRecords = new[]
            {
                new TaskIntegrationRecord
                {
                    Id = "curated-mapping",
                    Classification = IntegrationRecordClasses.CuratedMapping,
                    RecordedAtUtc = _clock.GetUtcNow().UtcDateTime,
                    IntegrationBranch = "develop",
                    SourceSha = source,
                    IntegrationSha = integrated,
                    DeliveryEpoch = attempt,
                },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Implement the fixture.\n");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-3097",
            RunAttemptId = attempt,
            Project = Project,
            Repository = _repo,
            ResultSha = source,
            AttemptChainId = "attempt-agt-3097",
            CompletedAtUtc = _clock.GetUtcNow(),
        });
    }

    /// <summary>
    /// One file-carrying commit, still holding the next-attempt placeholder
    /// (AGT-2821 ab4c1b3), proven by the current review subject. The hosted
    /// endpoint tests use the archive lane: the test host's lane rails reopen
    /// a completed card without verified integration, while archived cards
    /// stay closed, and the sweep covers both lanes.
    /// </summary>
    private void WriteCard(string lane)
    {
        var folder = Path.Combine(_watchPath, lane, TaskId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = TaskId,
            key = "AGT-2822",
            title = TaskId,
            state = lane,
            order = 1,
            agent = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
            commits = new[]
            {
                new
                {
                    sha = _sha,
                    shortSha = _sha[..8],
                    message = "feat: delivered through the current result",
                    filesChanged = 1,
                    at = _clock.GetUtcNow(),
                    attribution = "automatic",
                    confidence = 1,
                    runAttemptId = RunAttemptId,
                    supersededByAttempt = TaskCommitSupersession.PendingAttempt,
                },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Implement the fixture.\n");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-2822",
            RunAttemptId = RunAttemptId,
            Project = Project,
            Repository = _repo,
            ResultSha = _sha,
            AttemptChainId = "attempt-agt-2822",
            CompletedAtUtc = _clock.GetUtcNow(),
        });
    }

    private WebApplicationFactory<Program> BuildFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["ErrorHandling:IncludeExceptionDetails"] = "true",
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repo,
                    ["WatchPaths:0:RepositoryPath"] = _repo,
                }));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(_clock));
        });

    /// <summary>
    /// The claim once the host's Git fact producer published the card's
    /// generation (Git facts S3): until then the answer reads unknown.
    /// </summary>
    private async Task<JsonDocument> PublishedClaim(HttpClient client)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync(
                $"/api/tasks/{TaskId}/delivery-claim?watchPath={Uri.EscapeDataString(_watchPath)}");
            response.EnsureSuccessStatusCode();
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (body.RootElement.GetProperty("factsState").GetString() is "ready" or "restored") return body;
            body.Dispose();
            if (waited.Elapsed > TimeSpan.FromMinutes(1)) throw new TimeoutException("Card facts were not published.");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// The sweep over published card facts, and the publish the Git fact
    /// producer would do: the card's integration verdict from the live lookup
    /// into the projection the claim reads.
    /// </summary>
    private (DeliveryClaimSweep Sweep, TaskScannerService Scanner, Action Publish) BuildSweep()
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
            new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<TaskIntegrationStatusService>.Instance,
            _clock);
        var projection = new TaskListGitProjectionCache();
        var sweep = new DeliveryClaimSweep(
            scanner,
            integration,
            mutations,
            settings,
            projects,
            NullLogger<DeliveryClaimSweep>.Instance,
            projection,
            cardFacts: null,
            _clock);
        Assert.NotNull(scanner.FindJob(TaskId, _watchPath));
        void Publish()
        {
            scanner.InvalidateCache();
            var cards = scanner.ScanAllJobsRaw().ToArray();
            projection.SetSnapshot(_watchPath, TaskListGitProjection.Empty with
            {
                Integration = integration.BuildLookup(cards),
                Signatures = cards.ToDictionary(card => card.TaskKey, TaskGitSignature.For, StringComparer.Ordinal),
            }, _clock.GetUtcNow(), "test");
        }
        return (sweep, scanner, Publish);
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
                CreateNoWindow = true,
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
