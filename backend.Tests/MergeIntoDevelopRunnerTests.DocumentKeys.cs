using AgentStudio.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3695: the first delivery of a concept card adds a keyless
/// <c>docs/&lt;slug&gt;/workbench.json</c>. Key assignment amends the
/// integration merge on the detached integration HEAD; the lane must follow,
/// or the gate-approved keyed merge is refused as <c>sha-not-on-branch</c>.
/// </summary>
public sealed partial class MergeIntoDevelopRunnerTests
{
    [Fact]
    public async Task RunAsync_KeylessWorkbenchOnExistingLane_PushesTheKeyedMergeToOrigin()
    {
        var (repo, remote) = SeedRepoWithOrigin("document-key-lane");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, $"update-ref {LaneRef("develop")} develop");
        RunGit(repo, "checkout -q -b task/concept-path");
        var descriptor = Path.Combine(repo, "docs", "concept-path", "workbench.json");
        Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
        File.WriteAllText(descriptor,
            "{\"schemaVersion\":1,\"id\":\"concept-path\",\"title\":\"Concept path\"}\n");
        Commit(repo, "docs: add concept path dossier");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var registry = FixtureProjectRegistry(repo);
        var shortCode = registry.FindByIdOrDisplayName("Fixture")!.ShortCode;
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 10, "", "green", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "concept-path");
        var runner = new MergeIntoDevelopRunner(git, log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue, projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            projectRegistry: registry);

        var merged = await runner.RunAsync("Fixture", "concept-path", jobFolder,
            repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, merged.Outcome);
        Assert.Equal(merged.MergedSha, gateRunner.Request!.ExpectedSha);
        Assert.Equal(merged.MergedSha, Lane(repo, "develop"));
        Assert.Contains($"\"key\": \"{shortCode}-W1\"",
            RunGit(repo, $"show {merged.MergedSha}:docs/concept-path/workbench.json").Out);
        Assert.True(queue.Reader.TryRead(out var queued));

        await new IntegrationPushWorker(queue, runner,
            NullLogger<IntegrationPushWorker>.Instance)
            .ProcessAsync(queued!, CancellationToken.None);

        var push = ReadPushStep(log, jobFolder);
        Assert.NotNull(push);
        Assert.Equal(PipelineStepStatus.Passed, push!.Status);
        Assert.Equal(merged.MergedSha, RemoteSha(remote, "develop"));
        Assert.Contains($"\"key\": \"{shortCode}-W1\"",
            RunGit(remote, "-c safe.bareRepository=all show develop:docs/concept-path/workbench.json").Out);
    }

    /// <summary>
    /// A lane AGT-3610 left on the pre-amend merge: the queued push of the
    /// approved keyed merge adopts it onto the lane instead of refusing it.
    /// </summary>
    [Fact]
    public async Task PushIntegrationBranch_LaneOnThePreAmendMerge_PublishesTheApprovedKeyedMerge()
    {
        var (repo, remote, _, log, runner, queue, jobFolder, merged) =
            await MergeConceptDelivery("document-key-stuck-push");
        StrandLaneOnPreAmendMerge(repo, merged);
        Assert.True(queue.Reader.TryRead(out var queued));

        await new IntegrationPushWorker(queue, runner,
            NullLogger<IntegrationPushWorker>.Instance)
            .ProcessAsync(queued!, CancellationToken.None);

        Assert.Equal(PipelineStepStatus.Passed, ReadPushStep(log, jobFolder)!.Status);
        Assert.Equal(merged, RemoteSha(remote, "develop"));
        Assert.Equal(merged, Lane(repo, "develop"));
    }

    /// <summary>
    /// The replay of a card stranded that way (Retry integration) publishes the
    /// green-gated keyed merge, not the ungated merge the lane still names.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReplayOnThePreAmendMerge_PublishesTheGatedKeyedMerge()
    {
        var (repo, remote, _, log, runner, queue, jobFolder, merged) =
            await MergeConceptDelivery("document-key-stuck-replay");
        StrandLaneOnPreAmendMerge(repo, merged);
        Assert.True(queue.Reader.TryRead(out _));

        var replay = await runner.RunAsync("Fixture", "concept-path", jobFolder,
            repo, "develop", CancellationToken.None);
        Assert.True(replay.Outcome.IsSuccessfulIntegration(), replay.Error);
        Assert.True(queue.Reader.TryRead(out var queued));
        await new IntegrationPushWorker(queue, runner,
            NullLogger<IntegrationPushWorker>.Instance)
            .ProcessAsync(queued!, CancellationToken.None);

        Assert.Equal(merged, Lane(repo, "develop"));
        Assert.Equal(merged, RemoteSha(remote, "develop"));
    }

    /// <summary>A key commit that also changes another file is no document-key amend.</summary>
    [Fact]
    public void AdoptDocumentKeyAmend_RefusesAnAmendBeyondWorkbenchDescriptors()
    {
        var (repo, _) = SeedRepoWithOrigin("document-key-foreign-amend");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base");
        Commit(repo, "chore: base");
        RunGit(repo, $"update-ref {LaneRef("develop")} develop");
        var laneTip = Lane(repo, "develop");
        File.WriteAllText(Path.Combine(repo, "README.md"), "changed");
        RunGit(repo, "commit -q -a --amend --no-edit");
        var amended = RunGit(repo, "rev-parse HEAD").Out.Trim();
        var (git, _, _) = BuildWithSettings(repo);

        Assert.False(git.AdoptDocumentKeyAmend(repo, "develop", amended));
        Assert.Equal(laneTip, Lane(repo, "develop"));
    }

    private async Task<(string Repo, string Remote, GitService Git, PipelineExecutionLog Log,
        MergeIntoDevelopRunner Runner, IntegrationPushQueue Queue, string JobFolder, string Merged)>
        MergeConceptDelivery(string name)
    {
        var (repo, remote) = SeedRepoWithOrigin(name);
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, $"update-ref {LaneRef("develop")} develop");
        RunGit(repo, "checkout -q -b task/concept-path");
        var descriptor = Path.Combine(repo, "docs", "concept-path", "workbench.json");
        Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
        File.WriteAllText(descriptor,
            "{\"schemaVersion\":1,\"id\":\"concept-path\",\"title\":\"Concept path\"}\n");
        Commit(repo, "docs: add concept path dossier");
        RunGit(repo, "checkout -q develop");
        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 10, "", "green", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "concept-path");
        var runner = new MergeIntoDevelopRunner(git, log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue, projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            projectRegistry: FixtureProjectRegistry(repo));
        var merged = await runner.RunAsync("Fixture", "concept-path", jobFolder,
            repo, "develop", CancellationToken.None);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, merged.Outcome);
        return (repo, remote, git, log, runner, queue, jobFolder, merged.MergedSha!);
    }

    /// <summary>
    /// Recreates the AGT-3610 state: the lane names the merge as it was before
    /// the key amend (same parents, the delivery's keyless tree).
    /// </summary>
    private static void StrandLaneOnPreAmendMerge(string repo, string keyed)
    {
        var preAmend = RunGit(repo,
            $"commit-tree task/concept-path^{{tree}} -p {keyed}^1 -p {keyed}^2 -m \"Merge task/concept-path\"").Out.Trim();
        RunGit(repo, $"update-ref {LaneRef("develop")} {preAmend}");
        Assert.Equal(preAmend, Lane(repo, "develop"));
    }

    private ProjectRegistry FixtureProjectRegistry(string repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(Path.Combine(_tempDir, "fixture-jobs"), "Fixture",
            DefaultWorkspace.Id);
        return registry;
    }
}
