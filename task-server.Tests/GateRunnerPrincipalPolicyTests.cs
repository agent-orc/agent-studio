using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace TaskServer.Tests;

public sealed class GateRunnerPrincipalPolicyTests
{
    [Theory]
    [InlineData("gate-a", "gate-a", true)]
    [InlineData("gate-a", "gate-b", false)]
    [InlineData("gate-a", "Gate-a", false)]
    public void Runner_cannot_use_another_executors_fence(
        string principalRunnerId, string authorityExecutorId, bool allowed)
    {
        var principal = new TaskServerPrincipal("runner", TaskServerPrincipalKinds.Runner,
            new HashSet<string> { TaskServerScopes.ReviewsWrite }, principalRunnerId);

        Assert.Equal(allowed, GateRunnerPrincipalPolicy.Matches(principal, authorityExecutorId));
    }

    [Fact]
    public void Service_principal_can_use_its_scoped_gate_authority()
    {
        var principal = new TaskServerPrincipal("engine", TaskServerPrincipalKinds.Engine,
            new HashSet<string> { TaskServerScopes.ReviewsWrite }, null);

        Assert.True(GateRunnerPrincipalPolicy.Matches(principal, "gate-a"));
    }

    [Fact]
    public void Runner_without_a_bound_runner_id_is_denied()
    {
        var principal = new TaskServerPrincipal("runner", TaskServerPrincipalKinds.Runner,
            new HashSet<string> { TaskServerScopes.ReviewsWrite }, null);

        Assert.False(GateRunnerPrincipalPolicy.Matches(principal, "gate-a"));
    }

    [Fact]
    public void Mismatch_returns_a_typed_forbidden_response()
    {
        var result = GateRunnerPrincipalPolicy.Denied();

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("runner-identity-mismatch",
            Assert.IsType<ApiError>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value).Code);
    }
}
