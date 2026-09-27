using AgentStudio.Pipeline;
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
    [InlineData("README.md", true)]
    [InlineData("AGENTS.md", true)]
    [InlineData("backend/Features/Pipeline/BatchGate/BatchGateRuntime.cs", false)]
    [InlineData("frontend/src/app/app.component.ts", false)]
    public void PilotAdmitsOnlyDocumentationPaths(string path, bool expected)
        => Assert.Equal(expected, BatchGateRoutingPolicy.IsDocumentationPath(path));

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
