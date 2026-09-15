using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2842: auto-review postprocessing kept deferring with growing backoff
/// ("awaiting-canonical-review-executor") even while a review executor was
/// registered, active, and idle, because nothing in that path ever asked the
/// registry whether an executor actually existed. These tests pin
/// <see cref="AgentStudio.Runner.V1ReviewExecutorRegistry.EvaluateReviewExecutorAvailability"/>,
/// the coarse, immediate check that fix relies on.
/// </summary>
public sealed class V1ReviewExecutorAvailabilityTests
{
    private const string RunnerId = "agent-runner-01-review";
    private const string Instance = "review-host:1";

    [Fact]
    public void No_registration_reports_no_review_executor()
    {
        var registry = new AgentStudio.Runner.V1ReviewExecutorRegistry();

        var availability = registry.EvaluateReviewExecutorAvailability();

        Assert.False(availability.AnyRegistered);
        Assert.False(availability.Available);
        Assert.Contains("no review executor is registered", availability.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Freshly_registered_idle_executor_is_immediately_available()
    {
        var registry = new AgentStudio.Runner.V1ReviewExecutorRegistry();
        registry.Register(RunnerId, Registration());

        var availability = registry.EvaluateReviewExecutorAvailability();

        Assert.True(availability.AnyRegistered);
        Assert.True(availability.Available);
        Assert.Contains(RunnerId, availability.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_coding_only_registration_is_never_counted_as_a_review_executor()
    {
        var registry = new AgentStudio.Runner.V1ReviewExecutorRegistry();
        registry.Register("agent-runner-01-coding", new Contract.RegisterRunnerRequest(
            "agent-runner-01-coding",
            "coding-host",
            Instance,
            "1.0.0",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor]));

        var availability = registry.EvaluateReviewExecutorAvailability();

        Assert.False(availability.AnyRegistered);
    }

    [Fact]
    public void Stale_heartbeat_past_the_staleness_budget_is_registered_but_not_available()
    {
        // Regression guard for the "heartbeat newer than the review claim
        // interval" failure mode: the staleness budget must be generous
        // relative to the daemon's own re-registration/advertisement cadence,
        // not tied to the (much shorter) claim-poll interval, or ordinary
        // jitter would read as "no executor".
        var registry = new AgentStudio.Runner.V1ReviewExecutorRegistry();
        registry.Register(RunnerId, Registration());

        var justInsideBudget = registry.EvaluateReviewExecutorAvailability(
            DateTime.UtcNow + AgentStudio.Runner.V1ReviewExecutorRegistry.ReviewExecutorHeartbeatStaleAfter
                - TimeSpan.FromSeconds(1));
        Assert.True(justInsideBudget.Available);

        var pastBudget = registry.EvaluateReviewExecutorAvailability(
            DateTime.UtcNow + AgentStudio.Runner.V1ReviewExecutorRegistry.ReviewExecutorHeartbeatStaleAfter
                + TimeSpan.FromSeconds(1));

        Assert.True(pastBudget.AnyRegistered);
        Assert.False(pastBudget.Available);
        Assert.Contains("heartbeat is stale", pastBudget.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Whole_host_capability_drain_is_registered_but_not_available()
    {
        var registry = new AgentStudio.Runner.V1ReviewExecutorRegistry();
        registry.Register(RunnerId, Registration());
        registry.ReportCapabilityFailure(RunnerId, new Contract.CapabilityFailureRequest(
            RunnerId,
            Instance,
            Contract.CapabilityProtocol.Disk,
            "InfrastructureUnavailable",
            "The review workspace disk was unreachable.",
            DateTime.UtcNow,
            "drain-1"));

        var availability = registry.EvaluateReviewExecutorAvailability();

        Assert.True(availability.AnyRegistered);
        Assert.False(availability.Available);
        Assert.Contains("is drained", availability.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capability_key_the_review_role_never_advertises_does_not_block_availability()
    {
        // Regression guard for the "capability snapshot the review role never
        // advertises" failure mode: availability must not require a specific
        // advertised capability key the way coding-admission does, because the
        // review role advertises a different capability set.
        var registry = new AgentStudio.Runner.V1ReviewExecutorRegistry();
        registry.Register(RunnerId, Registration());

        var availability = registry.EvaluateReviewExecutorAvailability();

        Assert.True(availability.Available);
    }

    private static Contract.RegisterRunnerRequest Registration()
        => new(
            RunnerId,
            "review-host",
            Instance,
            "1.0.0",
            Contract.TaskServerProtocol.Current,
            [
                Contract.ReviewCapabilities.ReviewExecutor,
                Contract.ReviewCapabilities.BaselineComparison,
                Contract.ReviewCapabilities.DependencyPreparation,
                Contract.ReviewCapabilities.GitMaterialization,
                Contract.ReviewCapabilities.SemanticReview,
            ]);
}
