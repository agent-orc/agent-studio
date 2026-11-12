using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class MergeIntoDevelopRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Per_task_fallback_net_change_publication_records_its_passed_gate(bool fenced)
    {
        var fixture = SeedNetChangeFixture($"fallback-net-change-{fenced}", "fallback-net");
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 10, "", "green", true, false));
        var runner = PublicationRunner(fixture, gate);

        var result = await runner.RunAsync("Fixture", "fallback-net", fixture.Folder,
            fixture.WatchPath, "develop", CancellationToken.None,
            publicationFence: fenced ? publish => { publish(); return true; } : null);

        Assert.Equal(MergeIntoIntegrationOutcome.MergedAfterRebase, result.Outcome);
        Assert.Equal(2, RunGit(fixture.Repo, $"rev-list --parents -n 1 {result.MergedSha}").Out.Trim().Split(' ').Length);
        var publication = Assert.Single(PublishedGateEvidenceStore.Read(fixture.WatchPath));
        Assert.Equal(result.MergedSha, publication.TreeSha);
        Assert.Equal(result.MergedSha, publication.PublishedSha);
    }

    [Theory]
    [InlineData(BuildTestGateVerdict.Fail, false)]
    [InlineData(BuildTestGateVerdict.Ok, true)]
    public async Task Per_task_fallback_red_or_cancelled_gate_records_nothing(
        BuildTestGateVerdict verdict, bool cancelled)
    {
        var fixture = SeedNetChangeFixture($"fallback-red-{verdict}-{cancelled}", "fallback-red");
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            cancelled ? BuildTestGateVerdict.Fail : verdict, 1, 10, "", "red", true, false)
        {
            FailureKind = cancelled ? BuildTestGateFailureKind.Cancellation : BuildTestGateFailureKind.None,
        });
        var runner = PublicationRunner(fixture, gate);

        var result = await runner.RunAsync("Fixture", "fallback-red", fixture.Folder,
            fixture.WatchPath, "develop", CancellationToken.None,
            publicationFence: publish => { publish(); return true; });

        Assert.False(result.Outcome.IsFreshMerge());
        Assert.Empty(PublishedGateEvidenceStore.Read(fixture.WatchPath));
    }

    [Fact]
    public void Backfill_adds_an_exact_passed_full_suite_gate_on_a_first_parent_commit_once()
    {
        var fixture = SeedLateGateFixture("backfill-exact", "backfill", publishDelivery: true);
        var published = RunGit(fixture.Repo, "rev-parse develop").Out.Trim();
        WriteReceipt(fixture.Folder, 1, published, "Ok", "Executed", "test-level=full; selected=3; full-suite=map-fallback-and-run");
        var backfill = Backfill(fixture);

        Assert.Equal(1, backfill.Repair("Fixture", "backfill", fixture.Folder, fixture.WatchPath));
        Assert.Equal(0, backfill.Repair("Fixture", "backfill", fixture.Folder, fixture.WatchPath));

        var entry = Assert.Single(PublishedGateEvidenceStore.Read(fixture.WatchPath));
        Assert.Equal(published, entry.TreeSha);
        Assert.Equal(published, entry.PublishedSha);
        Assert.True(entry.Backfilled);
        Assert.Equal("backfill", entry.SourceJobId);
        Assert.Equal("backfill/post-steps/pre-develop-build-gate-1.log", entry.GateLog);
        Assert.Contains("backfilled", entry.Describe());
    }

    [Theory]
    [InlineData("Fail", "Executed", "test-level=full; selected=3; full-suite=required-and-run", false)]
    [InlineData("Ok", "CacheHit", "test-level=full; selected=3; full-suite=required-and-run", false)]
    [InlineData("Ok", "Executed", "test-level=work-package; selected=1; full-suite=not-run; omitted=2", false)]
    [InlineData("Ok", "Executed", "test-level=full; selected=3; full-suite=required-and-run", true)]
    public void Backfill_refuses_red_reused_narrow_and_off_line_gates(
        string verdict, string source, string coverage, bool offLine)
    {
        var fixture = SeedLateGateFixture($"backfill-refused-{verdict}-{source}-{offLine}", "refused",
            publishDelivery: !offLine);
        // Off the line: the delivery commit was never published to develop.
        var sha = offLine
            ? RunGit(fixture.Repo, "rev-parse agent-studio/results/run-refused/result").Out.Trim()
            : RunGit(fixture.Repo, "rev-parse develop").Out.Trim();
        WriteReceipt(fixture.Folder, 1, sha, verdict, source, coverage);

        Assert.Equal(0, Backfill(fixture).Repair("Fixture", "refused", fixture.Folder, fixture.WatchPath));
        Assert.Empty(PublishedGateEvidenceStore.Read(fixture.WatchPath));
    }

    private static PublishedGateEvidenceBackfill Backfill(LateGateFixture fixture)
        => new(fixture.Scanner, fixture.Git,
            new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance),
            NullLogger<PublishedGateEvidenceBackfill>.Instance);

    private static void WriteReceipt(string folder, int index, string sha, string verdict,
        string source, string coverage)
    {
        var dir = Path.Combine(folder, "post-steps");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"pre-develop-build-gate-{index}.log"),
            $"verdict={verdict} exit=0 durationMs=10 failureKind=None\n"
            + $"expectedSha={sha} testedSha={sha}\n"
            + $"reason=Build and tests passed; {coverage}\n"
            + $"verdictSource={source} originalRunId=n/a\n");
    }

    private LateGateFixture SeedNetChangeFixture(string name, string jobId)
    {
        var fixture = SeedLateGateFixture(name, jobId, publishDelivery: false);
        var repo = fixture.Repo;
        var deliveryRef = $"agent-studio/results/run-{jobId}/result";
        RunGit(repo, $"checkout -q {deliveryRef}");
        RunGit(repo, "checkout -q -b side");
        File.WriteAllText(Path.Combine(repo, "side.txt"), "side work");
        Commit(repo, "feat: side work");
        RunGit(repo, $"checkout -q {deliveryRef}");
        RunGit(repo, "merge -q --no-ff --no-edit side");
        var deliverySha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, $"push -q -f origin {deliveryRef}:{deliveryRef}");
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "integration.txt"), "integration work");
        Commit(repo, "chore: develop moved on");
        RunGit(repo, "push -q origin develop");
        var subject = ReviewSubjectStore.Read(fixture.Folder)!;
        ReviewSubjectStore.Write(fixture.Folder, subject with { ResultSha = deliverySha });
        return fixture;
    }
}
