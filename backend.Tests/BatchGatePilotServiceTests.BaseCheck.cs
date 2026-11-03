using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3433: the base check of a red batch candidate. Measured on 2 November
/// 2026 (batch 0d54bfe1): a guard violation stopped the candidate after 2.5
/// minutes, then the full suite ran on the base for 3,599,828 ms although the
/// base was the tip a batch had just published plus one documentation commit.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    private const string FailedTestCommand = "dotnet test Product.Tests.csproj --no-build --filter Category!=MachineBound";
    private const string FailedTest = "Product.Tests.Architecture.LayeringTests.Features_do_not_reference_hosts";

    [Fact]
    public async Task Published_batch_tip_as_base_needs_no_base_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var published = await PublishFirstBatchAsync(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var recorded = store.ReadBaseVerdict(ProjectName, published.TestedCandidateSha,
            Assert.Single(store.ListManifests()).Scope.GateProfileDigest);
        Assert.Equal(BatchGateBaseCheckSources.PublishedBatch, recorded?.Source);
        Assert.Equal("pass", recorded?.Outcome);

        SeedMember(factory, "DOC-ON-PUBLISHED", "docs/on-published.md");
        _gate.Batch = request => GuardRed(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.BaseRuns);
        var verdict = SecondPrimaryVerdict(store, published.TestedCandidateSha);
        Assert.Equal("deterministic-suite-red", verdict.Classification);
        Assert.Equal(BatchGateBaseCheckSources.PublishedBatch, verdict.BaseCheck);
        Assert.Equal(0, verdict.BaseCheckMinutes);
    }

    [Fact]
    public async Task Documentation_commit_on_a_published_tip_needs_no_base_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var published = await PublishFirstBatchAsync(factory);
        // The workbench key commit of the incident: documents only.
        AdvanceOriginDevelop("docs/review-gate-packages/workbench.json");
        var docsBase = RemoteTip();
        Assert.NotEqual(published.TestedCandidateSha, docsBase);

        SeedMember(factory, "DOC-ON-DOCS", "docs/on-docs.md");
        _gate.Batch = request => GuardRed(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.BaseRuns);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var verdict = SecondPrimaryVerdict(store, docsBase);
        Assert.Equal("deterministic-suite-red", verdict.Classification);
        Assert.Equal(BatchGateBaseCheckSources.VerifiedAncestor, verdict.BaseCheck);
        var carried = store.ReadBaseVerdict(ProjectName, docsBase, verdict.GateProfileDigest)!;
        Assert.Equal(published.TestedCandidateSha, carried.VerifiedSha);
        Assert.Equal("pass", carried.Outcome);
    }

    [Fact]
    public async Task Code_commit_on_a_published_tip_checks_the_base_and_a_nameless_failure_runs_the_full_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        await PublishFirstBatchAsync(factory);
        AdvanceOriginDevelop("src/Product.cs");
        var codeBase = RemoteTip();

        SeedMember(factory, "DOC-ON-CODE", "docs/on-code.md");
        var baseRequests = new List<BuildTestGateRequest>();
        _gate.Batch = request => Red(request.ExpectedSha);
        _gate.Base = request =>
        {
            baseRequests.Add(request);
            return Green(request.ExpectedSha);
        };
        _gate.Fallback = request => Red(request.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        var full = Assert.Single(baseRequests);
        Assert.Null(full.NarrowCheck);
        Assert.Equal(TestExecutionLevels.Full, full.RequiredTestLevel);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var verdict = SecondPrimaryVerdict(store, codeBase);
        Assert.Equal("deterministic-suite-red", verdict.Classification);
        Assert.Equal(BatchGateBaseCheckSources.FullSuite, verdict.BaseCheck);
        Assert.Equal(BatchGateBaseCheckSources.FullSuite,
            store.ReadBaseVerdict(ProjectName, codeBase, verdict.GateProfileDigest)?.Source);
    }

    [Fact]
    public async Task Guard_violation_runs_only_the_guard_step_on_the_base_and_blames_the_batch()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-GUARD-RED", "docs/guard-red.md");
        var baseRequests = new List<BuildTestGateRequest>();
        // The incident's numbers: the candidate stopped after 2.5 minutes,
        // the guard step on its own takes 15 seconds.
        _gate.Batch = request => GuardRed(request.ExpectedSha) with { DurationMs = 153_000 };
        _gate.Base = request =>
        {
            baseRequests.Add(request);
            return Green(request.ExpectedSha) with { DurationMs = 15_000 };
        };
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var check = Assert.Single(baseRequests);
        Assert.Equal(GateNarrowCheckKinds.GuardStep, check.NarrowCheck?.Kind);
        var manifest = Assert.Single(store.ListManifests(), item => item.ParentBatchId is null);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        var verdict = store.ReadVerdict(manifest.BatchId, run.BatchRunId)!;
        Assert.Equal("deterministic-suite-red", verdict.Classification);
        Assert.Equal(GateNarrowCheckKinds.GuardStep, verdict.BaseCheck);
        Assert.Equal(0.25, verdict.BaseCheckMinutes);
        // A narrow green is no passing base: a later failure elsewhere asks again.
        Assert.Null(store.ReadBaseVerdict(ProjectName, manifest.BaseSha, run.GateProfileDigest));
        // The blamed member leaves the batch for its own gate, which is red.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, member.Key)));

        var report = pilot.Report(ProjectName);
        var shown = Assert.Single(report.BaseChecks!);
        Assert.Equal(GateNarrowCheckKinds.GuardStep, shown.AnsweredBy);
        Assert.Equal(0.25, shown.Minutes);
        Assert.Equal(0.25, report.BaseCheckMinutes);
        var execution = Assert.Single(store.ListExecutions(ProjectName), fact => fact.NarrowCheck is not null);
        Assert.Equal(manifest.BaseSha, execution.TestedSha);
        Assert.Equal(15_000, execution.DurationMs);
        Assert.Contains("\"answeredBy\":\"guard-step\"",
            await client.GetStringAsync($"/api/projects/{ProjectName}/batch-gate/report"));
    }

    [Fact]
    public async Task Failed_tests_run_only_those_tests_on_the_base_and_a_red_base_is_pre_existing()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-TEST-RED", "docs/test-red.md");
        var baseRequests = new List<BuildTestGateRequest>();
        _gate.Batch = request => TestRed(request.ExpectedSha);
        _gate.Base = request =>
        {
            baseRequests.Add(request);
            return TestRed(request.ExpectedSha) with { DurationMs = 30_000 };
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var check = Assert.Single(baseRequests).NarrowCheck!;
        Assert.Equal(GateNarrowCheckKinds.FailedTests, check.Kind);
        Assert.Equal([FailedTest], check.Tests);
        Assert.Equal(FailedTestCommand, check.Command);
        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal("integration-branch-red",
            AssertPhase(store, manifest.BatchId, BatchPhase.Paused).Reason);
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        var verdict = store.ReadVerdict(manifest.BatchId, run.BatchRunId)!;
        Assert.Equal("pre-existing-base-red", verdict.Classification);
        Assert.Equal(GateNarrowCheckKinds.FailedTests, verdict.BaseCheck);
        Assert.Equal(0.5, verdict.BaseCheckMinutes);
        var recorded = store.ReadBaseVerdict(ProjectName, manifest.BaseSha, run.GateProfileDigest)!;
        Assert.Equal("fail", recorded.Outcome);
        Assert.Equal(GateNarrowCheckKinds.FailedTests, recorded.Source);
        // The shortcut never releases a red member.
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        Assert.Equal(0, _gate.FallbackRuns);
    }

    [Fact]
    public async Task Narrow_check_that_finds_nothing_to_repeat_falls_back_to_the_full_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        SeedMember(factory, "DOC-NARROW-EMPTY", "docs/narrow-empty.md");
        var baseRequests = new List<BuildTestGateRequest>();
        _gate.Batch = request => GuardRed(request.ExpectedSha);
        _gate.Base = request =>
        {
            baseRequests.Add(request);
            return request.NarrowCheck is null
                ? Red(request.ExpectedSha)
                : new BuildTestGateResult(BuildTestGateVerdict.Skipped, null, 0, "",
                    "narrow check guard-step matched no verify command", false, false)
                {
                    TestedSha = request.ExpectedSha,
                };
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(new string?[] { GateNarrowCheckKinds.GuardStep, null },
            baseRequests.Select(request => request.NarrowCheck?.Kind));
        var manifest = Assert.Single(store.ListManifests());
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        var verdict = store.ReadVerdict(manifest.BatchId, run.BatchRunId)!;
        Assert.Equal("pre-existing-base-red", verdict.Classification);
        Assert.Equal(BatchGateBaseCheckSources.FullSuite, verdict.BaseCheck);
    }

    private async Task<BatchGatePublication> PublishFirstBatchAsync(WebApplicationFactory<Program> factory)
    {
        SeedMember(factory, "DOC-FIRST-" + Guid.NewGuid().ToString("N")[..6], "docs/first.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var publication = store.ReadPublication(Assert.Single(store.ListManifests()).BatchId)!;
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        Git(_repo, "fetch", "-q", "origin");
        return publication;
    }

    private static BatchGateRunVerdict SecondPrimaryVerdict(BatchGateStore store, string baseSha)
    {
        var manifest = Assert.Single(store.ListManifests(), item =>
            item.ParentBatchId is null && item.BaseSha == baseSha);
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        return store.ReadVerdict(manifest.BatchId, run.BatchRunId)!;
    }

    private static BuildTestGateResult GuardRed(string? sha)
        => Red(sha) with
        {
            Reason = "guard violation, full suite not run: dotnet test failed",
            GuardViolation = true,
            FailedStepKind = VerifyCommandKind.Test,
        };

    private static BuildTestGateResult TestRed(string? sha)
        => Red(sha) with
        {
            FailedStepKind = VerifyCommandKind.Test,
            Processes =
            [
                new BuildTestGateProcessEvidence
                {
                    Command = FailedTestCommand,
                    ExitCode = 1,
                    StandardOutput = $"  Failed {FailedTest} [12 ms]\n",
                },
            ],
        };
}
