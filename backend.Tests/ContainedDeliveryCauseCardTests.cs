using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3002 - when the one verification gate on a contained delivery fails,
/// the integration lane opens a cause card for the branch. Every card stuck on
/// the same branch joins that one cause card instead of opening its own.
/// </summary>
public sealed class ContainedDeliveryCauseCardTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "contained-cause-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _store;

    public ContainedDeliveryCauseCardTests()
    {
        _repo = Path.Combine(_root, "repo");
        _store = Path.Combine(_root, "store");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_store);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "seed");
        Git("add", "-A");
        Git("commit", "-q", "-m", "seed");
        Git("checkout", "-q", "-b", "develop");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RedVerificationGate_OpensOneCauseCardForTheBranch()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _store,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_store, Project, DefaultWorkspace.Id);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var interventions = new FailureInterventionService(mutations, scanner, timeline,
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance),
            NullLogger<FailureInterventionService>.Instance,
            new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance),
            pipelineLog: pipeline);
        var gate = new RedGate();
        var runner = new MergeIntoDevelopRunner(
            new GitService(NullLogger<GitService>.Instance, scanner, config),
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gate),
            preDevelopTimeout: TimeSpan.FromSeconds(30),
            taskScanner: scanner,
            failureInterventions: interventions,
            timeline: timeline);

        // Two deliveries reached develop without a gate this lane can name,
        // the way the operator script pushed them.
        var first = CreateCardMergedWithoutGate(mutations, scanner, pipeline, "first.txt");
        var second = CreateCardMergedWithoutGate(mutations, scanner, pipeline, "second.txt");

        var firstResult = await runner.RunAsync(Project, first.Id, first.FolderPath, _store, "develop", CancellationToken.None);
        var secondResult = await runner.RunAsync(Project, second.Id, second.FolderPath, _store, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, firstResult.Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, secondResult.Outcome);
        Assert.Equal(2, gate.Invocations);
        var cause = Assert.Single(interventions.List(_store));
        Assert.Equal("integration/unverified-branch", cause.FailureClass);
        Assert.Equal(FailureDomains.Product, cause.FailureDomain);
        Assert.Contains(first.Key ?? first.Id, cause.AffectedCards);
        Assert.Contains(second.Key ?? second.Id, cause.AffectedCards);
        Assert.Contains(IntegrationVerificationStore.FileName, cause.EvidencePointers);
        var followUp = scanner.FindJob(cause.FollowUpTaskId, _store);
        Assert.NotNull(followUp);
        Assert.Contains("integration/unverified-branch", File.ReadAllText(Path.Combine(followUp!.FolderPath, "prompt.md")));

        var verification = IntegrationVerificationStore.Read(first.FolderPath);
        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.True(verification?.GateFailed);
    }

    [Fact]
    public async Task UnavailableGate_DoesNotBlameTheBranch()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _store,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_store, Project, DefaultWorkspace.Id);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var interventions = new FailureInterventionService(mutations, scanner, timeline,
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance),
            NullLogger<FailureInterventionService>.Instance,
            new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance),
            pipelineLog: pipeline);
        var runner = new MergeIntoDevelopRunner(
            new GitService(NullLogger<GitService>.Instance, scanner, config),
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: null,
            taskScanner: scanner,
            failureInterventions: interventions,
            timeline: timeline);
        var card = CreateCardMergedWithoutGate(mutations, scanner, pipeline, "unwired.txt");

        var result = await runner.RunAsync(Project, card.Id, card.FolderPath, _store, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, result.Outcome);
        Assert.DoesNotContain(
            interventions.List(_store),
            item => item.FailureClass == "integration/unverified-branch");
        var verification = IntegrationVerificationStore.Read(card.FolderPath);
        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.False(verification?.GateFailed);
    }

    private TaskInfo CreateCardMergedWithoutGate(
        TaskMutationService mutations,
        TaskScannerService scanner,
        PipelineExecutionLog pipeline,
        string file)
    {
        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Delivery " + file,
            WatchPath = _store,
            TargetState = TaskStates.HumanReview,
            PromptMarkdown = "Deliver " + file,
        });
        Assert.NotNull(id);
        var card = scanner.FindJob(id!, _store)!;
        pipeline.Begin(card.FolderPath, PipelineCatalogue.Standard, Project, card.Id);

        var branch = WorktreeTaskLifecycle.BranchFor(card.Id);
        Git("checkout", "-q", "-b", branch, "develop");
        File.WriteAllText(Path.Combine(_repo, file), file);
        Git("add", "-A");
        Git("commit", "-q", "-m", "feat: " + file);
        Git("checkout", "-q", "develop");
        Git("merge", "-q", "--no-ff", "--no-edit", branch);
        return card;
    }

    private void Git(params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = _repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private sealed class RedGate : IBuildTestGateRunner
    {
        public int Invocations { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Fail,
                1,
                10,
                "FAILED Integration.Tests.BranchTree",
                "1 test failed on the pushed tree",
                true,
                false)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
                FailureKind = BuildTestGateFailureKind.Code,
            });
        }
    }
}
