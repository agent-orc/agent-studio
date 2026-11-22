using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3964 (AGT-3951): Dossier placement refused a reviewed concept delivery,
/// and the card then had no product action left: Retry integration answered
/// <c>not-a-gate-environment-failure</c> and the move to Human Review answered
/// <c>integration-dead-end</c>. The operator committed the Dossier unchanged
/// onto develop by hand. Retry integration now places the same reviewed
/// delivery again through Dossier placement, without a new agent round, and
/// the operator-placed copy merges as a no-op.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class ConceptPlacementRetryTests : IDisposable
{
    private const string Project = "Studio";
    private const string Id = "AGT-3951-card";
    private const string Topic = "economy-dial";
    private const string Entrypoint = "docs/" + Topic + "/index.html";
    private static readonly DateTimeOffset DeliveredAt = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public ConceptPlacementRetryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "concept-placement-retry-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Concept Placement Retry Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        var origin = Path.Combine(_root, "origin.git");
        Git(_root, "init", "-q", "--bare", "-b", "develop", origin);
        Git(_repo, "remote", "add", "origin", origin);
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");
    }

    [Fact]
    public async Task RetryIntegration_PlacesTheOperatorPlacedDossierWithoutANewRound()
    {
        var stack = Build();
        var delivery = SeedRefusedConceptDelivery(stack);
        PlaceDossierOnDevelopByHand(delivery);
        var job = stack.Scanner.FindJob(Id, _watchPath)!;
        var refused = stack.Integration.BuildLookup([job])[job.TaskKey];
        Assert.Equal(IntegrationStatuses.ConflictSkipped, refused.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected, refused.Failure?.Code);
        Assert.True(refused.Failure!.ReplayAvailable);

        // A refused placement is not a gate-environment fault: only the
        // operator's Retry integration replays it.
        Assert.Equal(0, (await stack.Retries.RunOnceAsync()).Candidates);
        var projection = stack.Authority.GetTaskProjection(TaskKey(stack));
        var runsBefore = projection.RunAttempts.Count;
        var reviewsBefore = projection.ReviewAttempts.Count;

        var result = await stack.Retries.RetryNowAsync(job);

        var merge = stack.Integration.ReadLatestMergeStep(stack.Scanner.FindJob(Id, _watchPath)!);
        Assert.True(result.Integrated, $"{result.Status} {result.Outcome} {result.Reason}: {merge?.FailureCode} {merge?.Reason}");
        Assert.True(IsAncestor(delivery, "develop"), "the replay did not merge the reviewed delivery");
        var record = ConceptWorkbenchStore.Read(job.FolderPath);
        Assert.Equal("docs/" + Topic, record?.RepoRelativeDirectory);
        Assert.Equal(Entrypoint, record?.RepoRelativeEntrypoint);
        Assert.Equal(SteerPendingKinds.ConceptSightReview, SteerPendingMarker.TryRead(job.FolderPath)?.Kind);
        Assert.NotEqual(IntegrationStatuses.ConflictSkipped,
            stack.Integration.BuildLookup([job])[job.TaskKey].Status);
        Assert.NotNull(stack.Scanner.BuildPromoteConceptPlan(Id, _watchPath));
        var after = stack.Authority.GetTaskProjection(TaskKey(stack));
        Assert.Equal(runsBefore, after.RunAttempts.Count);
        Assert.Equal(reviewsBefore, after.ReviewAttempts.Count);
    }

    [Fact]
    public async Task RetryIntegration_StillRefusesAConceptThatChangedProductCode()
    {
        var stack = Build();
        SeedRefusedConceptDelivery(stack, touchProductCode: true);
        var job = stack.Scanner.FindJob(Id, _watchPath)!;

        var result = await stack.Retries.RetryNowAsync(job);

        Assert.False(result.Integrated);
        Assert.StartsWith(ConceptDeliveryPostSteps.PlacementParkPrefix, result.PostStepParkReason);
        Assert.Contains("src/product.cs", result.PostStepParkReason);
        Assert.Null(ConceptWorkbenchStore.Read(job.FolderPath));
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected,
            stack.Integration.BuildLookup([job])[job.TaskKey].Failure?.Code);
    }

    private string SeedRefusedConceptDelivery(Stack stack, bool touchProductCode = false)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + Id, "develop");
        var dossier = ConceptWorkbenchContract.CreateScaffold(
            _repo, Topic, "Economy dial", "Where the economy dial sits.", Id);
        AddImplementationTask(Path.Combine(dossier, ConceptWorkbenchContract.DescriptorFileName));
        if (touchProductCode)
        {
            Directory.CreateDirectory(Path.Combine(_repo, "src"));
            File.WriteAllText(Path.Combine(_repo, "src", "product.cs"), "// product\n");
        }
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "concept: economy dial Dossier");
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "push", "-q", "origin", "task/" + Id);
        Git(_repo, "checkout", "-q", "develop");

        var folder = Path.Combine(_watchPath, TaskStates.Escalated, Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = Id,
            key = Id,
            title = "Place the economy dial",
            state = TaskStates.Escalated,
            order = 1,
            agent = "claude",
            cliType = "claude",
            mode = TaskModes.Concept,
            projectName = Project,
            ownerClientId = DefaultClientIdentity.Id,
            commit = Commit(deliverySha),
            commits = new[] { Commit(deliverySha) },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Write the economy dial Dossier.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: delivered.\n");
        ConceptDossierContract.WriteReference(folder, Entrypoint);
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = Id,
            RunAttemptId = "run-" + Id,
            Project = Project,
            Repository = _repo,
            ResultSha = deliverySha,
            ResultRef = "task/" + Id,
            ImmutableResultRef = "task/" + Id,
            AttemptChainId = "chain-" + Id,
            IntegrationBranch = "develop",
            CompletedAtUtc = DeliveredAt,
        });
        stack.Pipeline.Begin(folder, PipelineCatalogue.Concept, Project, Id);
        // The refusal the placement check recorded on the delivery (AGT-3951).
        stack.Pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = DeliveredAt.UtcDateTime,
            CompletedAt = DeliveredAt.UtcDateTime,
            Verdict = AcceptedIntegrationFailureCodes.ConceptDossierRejected,
            VerdictSummary = "Dossier placement refused the concept delivery; nothing was merged.",
            Reason = ConceptDeliveryPostSteps.PlacementParkPrefix
                     + "Exactly one concept Dossier is required; found 2 topic directories.",
            FailureCode = AcceptedIntegrationFailureCodes.ConceptDossierRejected,
        });
        SeedPassedReview(stack, deliverySha);
        return deliverySha;
    }

    /// <summary>The operator workaround: the Dossier committed unchanged onto develop.</summary>
    private void PlaceDossierOnDevelopByHand(string deliverySha)
    {
        Git(_repo, "checkout", "-q", deliverySha, "--", "docs/" + Topic);
        Git(_repo, "commit", "-q", "-m", "AGT-3951: place the economy-dial Dossier (operator placement)");
        Git(_repo, "push", "-q", "origin", "develop");
    }

    private static void AddImplementationTask(string descriptorPath)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(File.ReadAllText(descriptorPath), options)!;
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor with
        {
            ImplementationTasks =
            [
                new ConceptImplementationTask
                {
                    Title = "Implement the economy dial",
                    PromptMarkdown = "Add the approved economy dial.",
                },
            ],
        }, options));
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

    private Stack Build()
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
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(new PassingGate()));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var authority = new AttemptAuthorityService(configuration, NullLogger<AttemptAuthorityService>.Instance);
        var concept = new ConceptDeliveryPostSteps(
            git, scanner, pipeline, timeline, NullLogger<ConceptDeliveryPostSteps>.Instance, settings);
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
            NullLogger<GateEnvironmentRetryService>.Instance,
            conceptDelivery: concept);
        return new Stack(scanner, pipeline, integration, authority, retries, _watchPath);
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
        catch (Exception ex) { SilentCatch.Note(ex, "Concept placement retry test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        PipelineExecutionLog Pipeline,
        TaskIntegrationStatusService Integration,
        AttemptAuthorityService Authority,
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
}
