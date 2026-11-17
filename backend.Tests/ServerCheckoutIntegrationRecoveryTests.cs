using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Security;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3810 (playthrough AGT-3795): a Remote delivery of a project without a
/// server checkout failed with <c>server-checkout-missing</c>, but the card
/// never showed the AGT-3673 remedy. The Git state index skips a project that
/// names no repository, so every cache-only card read carried no integration
/// verdict and lost the recorded failure code. After the owner configured the
/// checkout, only a reissue (a full new coding run) moved the card on.
/// <para>
/// These tests drive the real merge runner exactly as the Remote delivery
/// integration does, then read the card the way the board and the detail read
/// it, and replay the same delivery through the operator "Retry integration".
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class ServerCheckoutIntegrationRecoveryTests : IDisposable
{
    private const string Project = "Tally";
    private const string Id = "AGT-3810-card";
    private static readonly DateTimeOffset DeliveredAt = new(2026, 11, 15, 21, 40, 26, TimeSpan.Zero);
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public ServerCheckoutIntegrationRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "server-checkout-recovery-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Server Checkout Recovery Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        // The server checkout tracks the project's origin, where a Remote
        // runner publishes its immutable result ref.
        var origin = Path.Combine(_root, "origin.git");
        Git(_root, "init", "-q", "--bare", "-b", "develop", origin);
        Git(_repo, "remote", "add", "origin", origin);
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");
    }

    [Fact]
    public async Task RemoteDelivery_WithoutServerCheckout_KeepsTheFailureCodeOnTheCard()
    {
        var stack = Build(withCheckout: false);
        var delivery = SeedEscalatedDelivery(stack);
        var job = stack.Scanner.FindJob(Id, _watchPath)!;

        var result = await stack.Runner.RunAsync(Project, job.Id, job.FolderPath, job.WatchPath,
            "develop", CancellationToken.None, reviewedResultSha: delivery);

        Assert.Equal(AcceptedIntegrationFailureCodes.ServerCheckoutMissing, result.FailureCode);
        // The Git state index has no repository to read for this project, so
        // the cache-only projection the board and the detail fold is empty.
        Assert.Empty(new TaskListGitProjectionCache().ReadCacheOnly([job]).Integration);

        var card = Assert.Single(new[] { job }.WithRecordedIntegrationFailure(stack.Integration));
        Assert.Equal(IntegrationStatuses.ConflictSkipped, card.Integration?.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.ServerCheckoutMissing, card.Integration!.Failure?.Code);
        // Without a checkout there is nothing to replay yet; setup is the remedy.
        Assert.False(card.Integration.Failure!.ReplayAvailable);

        var refused = await stack.Retries.RetryNowAsync(job);
        Assert.Equal(GateEnvironmentRetryStatus.NotApplicable, refused.Status);
        Assert.False(IsAncestor(delivery, "develop"));
    }

    [Fact]
    public async Task RetryIntegration_AfterTheCheckoutExists_MergesTheSameDeliveryWithoutANewRun()
    {
        var missing = Build(withCheckout: false);
        var delivery = SeedEscalatedDelivery(missing);
        var failed = missing.Scanner.FindJob(Id, _watchPath)!;
        await missing.Runner.RunAsync(Project, failed.Id, failed.FolderPath, failed.WatchPath,
            "develop", CancellationToken.None, reviewedResultSha: delivery);

        // The owner configures the checkout in Project Settings.
        var stack = Build(withCheckout: true);
        var job = stack.Scanner.FindJob(Id, _watchPath)!;
        var status = stack.Integration.BuildLookup([job])[job.TaskKey];
        Assert.Equal(AcceptedIntegrationFailureCodes.ServerCheckoutMissing, status.Failure?.Code);
        Assert.True(status.Failure!.ReplayAvailable);

        // A missing checkout is not a gate-environment fault: the automatic
        // ladder leaves it to the owner who configured the checkout.
        var sweep = await stack.Retries.RunOnceAsync();
        Assert.Equal(0, sweep.Candidates);

        var projection = stack.Authority.GetTaskProjection(TaskKey(stack));
        var runsBefore = projection.RunAttempts.Count;
        var reviewsBefore = projection.ReviewAttempts.Count;

        var result = await stack.Retries.RetryNowAsync(job);

        Assert.Equal(GateEnvironmentRetryStatus.Replayed, result.Status);
        Assert.Equal(delivery, result.DeliverySha);
        var merge = stack.Integration.ReadLatestMergeStep(stack.Scanner.FindJob(Id, _watchPath)!);
        Assert.True(result.Outcome!.Value.IsSuccessfulIntegration(),
            $"{result.Outcome}: {merge?.FailureCode} {merge?.Reason}");
        Assert.True(IsAncestor(delivery, "develop"), "the replay did not merge the reviewed delivery");
        var after = stack.Authority.GetTaskProjection(TaskKey(stack));
        Assert.Equal(runsBefore, after.RunAttempts.Count);
        Assert.Equal(reviewsBefore, after.ReviewAttempts.Count);
    }

    [Fact]
    public async Task Retry_endpoint_moves_only_an_integrated_escalated_card_to_human_review()
    {
        var missing = Build(withCheckout: false);
        var delivery = SeedEscalatedDelivery(missing);
        var job = missing.Scanner.FindJob(Id, _watchPath)!;
        await missing.Runner.RunAsync(Project, job.Id, job.FolderPath, job.WatchPath,
            "develop", CancellationToken.None, reviewedResultSha: delivery);

        var failing = Build(withCheckout: true, gateFails: true);
        string sessionToken;
        using (var factory = EndpointFactory(failing, withCheckout: true))
        using (var client = factory.CreateClient())
        {
            sessionToken = factory.Services.GetRequiredService<AccessSecurityStore>()
                .Bootstrap(new BootstrapRequest("retry.owner", "correct horse battery staple!", null)).SessionToken;
            client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
            client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={sessionToken}");
            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
            using var refused = await client.PostAsync(RetryUrl(), null);
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var failedBody = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("failed", failedBody.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, failedBody.GetProperty("movedTo").ValueKind);
            Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, Id)));
        }

        var configured = Build(withCheckout: true);
        using var readyFactory = EndpointFactory(configured, withCheckout: true);
        using var readyClient = readyFactory.CreateClient();
        readyClient.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        readyClient.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={sessionToken}");
        readyClient.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var response = await readyClient.PostAsync(RetryUrl(), null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("integrated", body.GetProperty("status").GetString());
        Assert.Equal(TaskStates.HumanReview, body.GetProperty("movedTo").GetString());
        Assert.Equal(TaskStates.HumanReview,
            readyFactory.Services.GetRequiredService<TaskScannerService>().FindJob(Id, _watchPath)!.State);
    }

    private string RetryUrl() => $"/api/tasks/{Id}/integration/retry?watchPath={Uri.EscapeDataString(_watchPath)}";

    private WebApplicationFactory<Program> EndpointFactory(Stack stack, bool withCheckout)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "local",
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = withCheckout ? _repo : null,
                ["WatchPaths:0:RepositoryPath"] = withCheckout ? _repo : null,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GateEnvironmentRetryService>();
                services.AddSingleton(stack.Retries);
                services.RemoveAll<TaskIntegrationStatusService>();
                services.AddSingleton(stack.Integration);
            });
        });

    private string SeedEscalatedDelivery(Stack stack)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + Id, "develop");
        File.WriteAllText(Path.Combine(_repo, "mean.txt"), "mean\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: mean helper");
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "push", "-q", "origin", "task/" + Id);
        Git(_repo, "checkout", "-q", "develop");

        var folder = Path.Combine(_watchPath, TaskStates.Escalated, Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = Id,
            key = Id,
            title = "Add mean() helper",
            state = TaskStates.Escalated,
            order = 1,
            agent = "claude",
            cliType = "claude",
            mode = TaskModes.Coding,
            projectName = Project,
            ownerClientId = DefaultClientIdentity.Id,
            commit = Commit(deliverySha),
            commits = new[] { Commit(deliverySha) },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Add a mean() helper.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: delivered.\n");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = Id,
            RunAttemptId = "run-" + Id,
            Project = Project,
            Repository = _repo,
            ResultSha = deliverySha,
            ResultRef = "task/" + Id,
            // A Remote delivery integrates its immutable result ref.
            ImmutableResultRef = "task/" + Id,
            AttemptChainId = "chain-" + Id,
            IntegrationBranch = "develop",
            CompletedAtUtc = DeliveredAt,
        });
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, Id);
        SeedPassedReview(stack, deliverySha);
        return deliverySha;
    }

    private static void SeedPassedReview(Stack stack, string deliverySha)
    {
        var taskKey = TaskKey(stack);
        var run = stack.Authority.AcquireRun(
            taskKey, "PROJ-002", null, "runner", "host", 600, "run-create-" + Id).RunAttempt!;
        stack.Authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete-" + Id),
            Outcome = "done",
            ResultSha = deliverySha,
        });
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-002", deliverySha, run.AttemptId,
            "requirements", "policy", [], "review-create-" + Id)).ReviewAttempt!;
        var claimed = stack.Authority.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 600, "review-claim-" + Id).ReviewAttempt!;
        var settled = stack.Authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle-" + Id),
            deliverySha,
            ReviewTerminalOutcome.Pass));
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
    }

    private static string TaskKey(Stack stack)
    {
        var job = stack.Scanner.FindJob(Id, stack.WatchPath)!;
        return job.Key ?? job.TaskKey;
    }

    private Stack Build(bool withCheckout, bool gateFails = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["TaskRepository"] = _root,
        };
        if (withCheckout)
        {
            values["WatchPaths:0:RootPath"] = _repo;
            values["WatchPaths:0:RepositoryPath"] = _repo;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var runner = new MergeIntoDevelopRunner(
            git,
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: new IntegrationPushQueue(),
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateFails ? new FailingGate() : new PassingGate()),
            batchReservations: new BatchIntegrationReservationService(Path.Combine(_root, "reservations")),
            refMutationLeases: new RefMutationLeaseService(Path.Combine(_root, "ref-leases")));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var authority = new AttemptAuthorityService(configuration, NullLogger<AttemptAuthorityService>.Instance);
        var retries = new GateEnvironmentRetryService(
            scanner,
            settings,
            integration,
            pipeline,
            runner,
            timeline,
            authority,
            new TaskProvenanceService(git, settings, mutations, NullLogger<TaskProvenanceService>.Instance),
            configuration,
            NullLogger<GateEnvironmentRetryService>.Instance);
        return new Stack(scanner, pipeline, integration, authority, runner, retries, _watchPath);
    }

    private bool IsAncestor(string sha, string branch)
        => Run(_repo, "merge-base", "--is-ancestor", sha, GitService.IntegrationLaneRef(branch)).Code == 0;

    private static object Commit(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = DeliveredAt,
        attribution = "automatic",
        confidence = 1,
    };

    private static string Git(string cwd, params string[] args)
    {
        var (stdout, stderr, code) = Run(cwd, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    private static (string Out, string Err, int Code) Run(string cwd, params string[] args)
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
        return (stdout, stderr, process.ExitCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Server checkout recovery test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        PipelineExecutionLog Pipeline,
        TaskIntegrationStatusService Integration,
        AttemptAuthorityService Authority,
        MergeIntoDevelopRunner Runner,
        GateEnvironmentRetryService Retries,
        string WatchPath);

    private sealed class PassingGate : IBuildTestGateRunner
    {
        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
            => Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, 5, string.Empty, "gate passed", true, false)
            {
                ExpectedSha = request.ExpectedSha,
            });
    }

    private sealed class FailingGate : IBuildTestGateRunner
    {
        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request, IReadOnlyList<string>? changedFiles,
            BuildProfile? profile, PostStepMode mode, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Fail, 1, 5, string.Empty, "gate environment failed", false, false)
            {
                ExpectedSha = request.ExpectedSha,
                FailureKind = BuildTestGateFailureKind.Environment,
            });
    }
}
