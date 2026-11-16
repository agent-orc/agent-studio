using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3811: a root-level library whose only verify commands come from
/// <c>.agent-studio/project.yml</c>. The merge gate used to skip such a merge,
/// so a delivery that broke the declared test still moved develop. The real
/// gate now runs the declared commands on the exact merged SHA.
/// </summary>
public sealed partial class MergeIntoDevelopRunnerTests
{
    [Fact]
    public async Task RunAsync_DevelopTarget_ProjectYmlTestBreakingMerge_DoesNotMoveDevelop()
    {
        var repo = SeedDeclaredLibrary("declared-library-red");
        RunGit(repo, "checkout -q -b task/81");
        File.WriteAllText(Path.Combine(repo, "lib.js"), "function tally() {}\nfunction tally() {} // BROKEN\n");
        Commit(repo, "feat: duplicate declaration");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var jobFolder = BeginRun(log, repo, jobId: "81");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(HermeticGateRunner()));

        var outcome = await runner.RunAsync("Fixture", "81", jobFolder, repo, "develop", CancellationToken.None);

        // The red gate blocks the merge. Without a parsed test report the
        // failure diagnosis may type it as environment; either way it is red.
        Assert.True(
            outcome.Outcome is MergeIntoIntegrationOutcome.GateFailed
                or MergeIntoIntegrationOutcome.GateEnvironmentFailure,
            $"expected a red merge gate, got {outcome.Outcome}: {outcome.Error}");
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        var lane = RunGit(repo, $"rev-parse --verify -q {LaneRef("develop")}").Out.Trim();
        Assert.True(lane.Length == 0 || lane == developBefore, "the integration lane must not carry the red merge");
        var evidence = File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-develop-build-gate-*.log")));
        Assert.Contains("sh test.sh", evidence);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_ProjectYmlGreenMerge_RecordsCommandsAndTestedSha()
    {
        var repo = SeedDeclaredLibrary("declared-library-green");
        RunGit(repo, "checkout -q -b task/82");
        File.WriteAllText(Path.Combine(repo, "lib.js"), "function tally() { return 2; }\n");
        Commit(repo, "feat: tally returns two");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var jobFolder = BeginRun(log, repo, jobId: "82");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(HermeticGateRunner()));

        var outcome = await runner.RunAsync("Fixture", "82", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        var record = IntegrationVerificationStore.Read(jobFolder);
        Assert.NotNull(record);
        Assert.Equal(IntegrationVerificationEvidence.MergeGate, record!.Evidence);
        Assert.Contains($"on {outcome.MergedSha}", record.Reason);
        Assert.Contains("sh build.sh", record.Reason);
        Assert.Contains("sh test.sh", record.Reason);
    }

    /// <summary>
    /// A develop branch whose project.yml declares a build and a test command
    /// and nothing the convention planners own: no <c>frontend/</c>, no .NET.
    /// The test fails when <c>lib.js</c> carries the marker <c>BROKEN</c>.
    /// </summary>
    private string SeedDeclaredLibrary(string name)
    {
        var repo = SeedRepo(name);
        RunGit(repo, "checkout -q -b develop");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n"
            + "  build: [sh build.sh]\n  test: [sh test.sh]\n  lint: []\n");
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "prepare"), "#!/bin/sh\nset -eu\n");
        File.WriteAllText(Path.Combine(repo, "build.sh"), "test -f lib.js\n");
        File.WriteAllText(Path.Combine(repo, "test.sh"), "if grep -q BROKEN lib.js; then exit 1; fi\n");
        File.WriteAllText(Path.Combine(repo, "lib.js"), "function tally() { return 1; }\n");
        Commit(repo, "chore: declared library");
        return repo;
    }
}
