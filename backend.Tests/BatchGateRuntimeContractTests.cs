using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateRuntimeContractTests : IDisposable
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "batch-runtime-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("docs/operations/gates/index.html", true)]
    [InlineData("docs/start/README.md", true)]
    [InlineData(".agents/skills/task-api/SKILL.md", true)]
    [InlineData("README.md", true)]
    [InlineData("AGENTS.md", true)]
    [InlineData("backend/Features/Pipeline/BatchGate/BatchGateRuntime.cs", false)]
    [InlineData("frontend/src/app/app.component.ts", false)]
    [InlineData("docs/operations/gates/audit.py", false)]
    [InlineData("docs/app/schemas/pipeline-definition.schema.json", false)]
    [InlineData(".agents/skills/task-api/scripts/probe.sh", false)]
    public void PilotAdmitsOnlyDocumentationPaths(string path, bool expected)
        => Assert.Equal(expected, BatchGateRoutingPolicy.IsDocumentationPath(path));

    [Fact]
    public void FullBatchSuiteKeepsCommandsOutsideTheDocumentationDiff()
    {
        var frontendBuild = new VerifyCommand(VerifyEcosystem.Node,
            VerifyCommandKind.Build, "frontend", "npm run build");
        var docsOnly = new[] { "docs/operations/gates/index.html" };
        var ordinary = new BuildTestGateRequest("repo", Sha, "test");
        var batch = ordinary with { ForceFullSuite = true, BypassVerdictCache = true };

        Assert.False(BuildTestGateRunner.ShouldRunForRequest(ordinary,
            frontendBuild, docsOnly));
        Assert.True(BuildTestGateRunner.ShouldRunForRequest(batch,
            frontendBuild, docsOnly));
        Assert.Equal(TestExecutionLevels.Full,
            TestSelectionPlanner.ResolveLevel(null, TaskStates.AutoReview,
                batch.ForceFullSuite ? TestExecutionLevels.Full : batch.RequiredTestLevel));
        Assert.True(batch.BypassVerdictCache);
    }

    [Fact]
    public void DeferRequiresOptInAndAProvenDocumentationOnlyDiff()
    {
        var enabled = new BatchGateFormationOptions(Enabled: true);
        Assert.True(BatchGateRoutingPolicy.ShouldDefer(enabled,
            ["docs/operations/gates/index.html", "README.md"]));
        Assert.False(BatchGateRoutingPolicy.ShouldDefer(new BatchGateFormationOptions(),
            ["docs/operations/gates/index.html"]));
        Assert.False(BatchGateRoutingPolicy.ShouldDefer(enabled, []));
        Assert.False(BatchGateRoutingPolicy.ShouldDefer(enabled,
            ["docs/operations/gates/index.html", "backend/Program.cs"]));
    }

    [Fact]
    public void SettledReviewCarriesOneExplicitDeferredBuildTestAspect()
    {
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("check", "completion", "git", ["status"])],
            ["completion"], BuildTestDeferredToBatch: true);
        var report = new Contract.ReviewReportRequest("executor", "instance", "lease",
            1, "key", "Pass", null, null, null!, null!, [], [], []);
        var first = BatchGateRoutingPolicy.RecordDeferredAspect(report, plan);
        var second = BatchGateRoutingPolicy.RecordDeferredAspect(first, plan);
        Assert.Single(second.Verdicts, verdict =>
            verdict.Aspect == "build-tests"
            && verdict.Status == "deferred-to-batch");
        Assert.Empty(BatchGateRoutingPolicy.RecordDeferredAspect(report,
            plan with { BuildTestDeferredToBatch = false }).Verdicts);
        Assert.False(BatchGateRoutingPolicy.ValidDeferredAspect(report with
        {
            Verdicts = [new Contract.ReviewVerdictDto("build-tests", "pass", "CommandPassed", "green")],
        }, plan));
    }

    [Fact]
    public void PendingSubjectIsDurableAndCannotSilentlyChangeIdentity()
    {
        var store = new BatchGateStore(_root);
        var subject = new BatchGateSubject("AGT-1", "project", "repo", "develop",
            "full", "digest", "v1", "refs/heads/agent-studio/results/run/sha",
            Sha, "run-1", 1, 1, Sha, true, true, true, true, false,
            null, true, DateTimeOffset.UtcNow, 1);
        var pending = new BatchGatePendingDelivery(subject, "review-1", "/task",
            "/repo", DateTimeOffset.UtcNow);
        store.Queue(pending);
        store.Queue(pending);
        Assert.Single(store.Pending());
        Assert.Throws<InvalidDataException>(() => store.Queue(pending with
        {
            Subject = subject with { ResultSha = new string('b', 40) },
        }));
        store.Claim(pending, "batch-1");
        Assert.Equal("batch-1", store.PendingOwner(pending));
        store.ReleaseClaim(pending, "batch-1");
        Assert.Null(store.PendingOwner(pending));
        store.CompletePending(pending, "per-task-fallback");
        Assert.Empty(store.Pending());
    }

    [Fact]
    public void LaneReleaseRequiresTheCurrentFencedRunAndMatchingDeferredReview()
    {
        var envelope = new Contract.ImmutableResultEnvelope("repo", "run-1", Sha, Sha,
            "refs/heads/agent-studio/results/run-1", null, new string('c', 64));
        var run = new RunAttemptDto("run-1", "AGT-1", "repo", null,
            AttemptLifecycleState.Completed, null, 7, 3, DateTime.UtcNow,
            DateTime.UtcNow, Sha, "Done", null, [], envelope,
            Contract.ResultEnvelopeDigest.Compute(envelope));
        var plan = new Contract.ReviewPlanDto([], [], BuildTestDeferredToBatch: true);
        var review = new ReviewAttemptDto("review-1", "AGT-1", "repo", "run-1",
            null, new ReviewSubjectDto("subject", "repo", Sha, "run-1", "task",
                "policy", [], DateTime.UtcNow, Plan: plan),
            AttemptLifecycleState.Completed, null, 1, 3, DateTime.UtcNow,
            DateTime.UtcNow, ReviewTerminalOutcome.Pass, null, Sha, null, []);
        var subject = new BatchGateSubject("AGT-1", "project", "repo", "develop",
            "full", "digest", "v1", envelope.ImmutableRemoteRef!, Sha,
            "run-1", 3, 7, Sha, true, true, true, true, false, null,
            true, DateTimeOffset.UtcNow, 1);
        var pending = new BatchGatePendingDelivery(subject, "review-1", "/task",
            "/repo", DateTimeOffset.UtcNow);

        Assert.True(BatchGateRuntime.IsCurrentGeneration(pending, TaskStates.AutoReview,
            run, review));
        Assert.False(BatchGateRuntime.IsCurrentGeneration(pending, TaskStates.AutoReview,
            run with { AuthorityEpoch = 4 }, review));
        Assert.False(BatchGateRuntime.IsCurrentGeneration(pending, TaskStates.AutoReview,
            run with { LastFence = 8 }, review));
        Assert.False(BatchGateRuntime.IsCurrentGeneration(pending, TaskStates.AutoReview,
            run, review with { TestedResultSha = new string('b', 40) }));
        Assert.False(BatchGateRuntime.IsCurrentGeneration(pending, TaskStates.AutoReview,
            run, review with { Subject = review.Subject with
            {
                Plan = plan with { BuildTestDeferredToBatch = false },
            } }));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task SharedRefLeaseAdvancesFenceAfterRelease()
    {
        var leases = new ProjectRefMutationLeaseService(_root);
        long firstFence;
        using (var first = await leases.AcquireAsync("project", "repo", "develop", CancellationToken.None))
        {
            Assert.True(first.IsCurrent);
            firstFence = first.Fence;
        }
        using var second = await leases.AcquireAsync("project", "repo", "develop", CancellationToken.None);
        Assert.True(second.IsCurrent);
        Assert.Equal(firstFence + 1, second.Fence);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task CanonicalRepositoryIdentityMakesSeparateCheckoutsContendOnOneRefLease()
    {
        var leases = new ProjectRefMutationLeaseService(_root);
        var firstKey = Contract.RepositoryIdentityContract.FromUrl(
            "https://example.test/project.git")!;
        var secondKey = Contract.RepositoryIdentityContract.FromUrl(
            "https://example.test/project.git/")!;
        Assert.Equal(firstKey, secondKey);
        using var first = await leases.AcquireAsync("project", firstKey,
            "develop", CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            leases.AcquireAsync("project", secondKey, "develop", timeout.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
