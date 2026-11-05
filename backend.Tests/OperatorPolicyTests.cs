using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using System.Text;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-4 as a pure decision matrix: the tier alone admits T0,
/// refuses T2 with <c>approval-required</c> and an uncatalogued request with
/// <c>operation-not-catalogued</c>; T1 needs a live grant of the principal
/// that covers the operation, the scope, the time of day, and the hourly rate.
/// </summary>
public sealed class OperatorPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Func<string, string, bool> Same = (bound, requested)
        => string.Equals(bound, requested, StringComparison.OrdinalIgnoreCase);
    private static readonly OperatorOperation Requeue = OperatorOperationCatalog.Find(OperatorOperationIds.CardRequeue)!;
    private static readonly OperatorGrantTarget Alpha = new("alpha");

    [Fact]
    public void Observe_is_admitted_without_a_grant()
    {
        var decision = OperatorPolicy.Decide(OperatorOperationCatalog.Find(OperatorOperationIds.Observe), "opa_1",
            OperatorGrantTarget.None, [], Now, Same);
        Assert.True(decision.Admitted);
        Assert.Null(decision.GrantId);
    }

    [Fact]
    public void A_request_outside_the_catalogue_is_refused()
        => Assert.Equal(OperatorPolicyReasons.NotCatalogued,
            OperatorPolicy.Decide(null, "opa_1", Alpha, [Grant()], Now, Same).Reason);

    [Theory]
    [MemberData(nameof(HighRiskOperations))]
    public void Every_t2_operation_is_refused_with_approval_required_even_under_a_workspace_grant(string operationId)
    {
        var operation = OperatorOperationCatalog.Find(operationId)!;
        var grant = Grant(operations: [operationId], scope: new StandingGrantScope(OperatorScopeKinds.Workspace));
        var decision = OperatorPolicy.Decide(operation, "opa_1", Alpha, [grant], Now, Same);
        Assert.False(decision.Admitted);
        Assert.Equal(OperatorPolicyReasons.ApprovalRequired, decision.Reason);
        Assert.Equal(operationId, decision.Operation?.Id);
    }

    public static TheoryData<string> HighRiskOperations()
    {
        var data = new TheoryData<string>();
        foreach (var operation in OperatorOperationCatalog.Operations.Where(operation => operation.Tier == OperatorTier.T2))
            data.Add(operation.Id);
        return data;
    }

    [Fact]
    public void A_t1_request_without_a_grant_is_refused_with_grant_required()
        => Assert.Equal(OperatorPolicyReasons.GrantRequired, OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [], Now, Same).Reason);

    [Fact]
    public void A_covering_live_grant_admits_and_names_itself()
    {
        var decision = OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [Grant()], Now, Same);
        Assert.True(decision.Admitted);
        Assert.Equal("sgr_1", decision.GrantId);
    }

    [Theory]
    [InlineData("/api/tasks/{}/attention", OperatorOperationIds.CardAttention)]
    [InlineData("/api/operator/log", OperatorOperationIds.OperatorLogAppend)]
    public void Attention_and_log_writes_are_project_scoped_t1_operations(string route, string id)
    {
        var operation = OperatorOperationCatalog.ResolveWrite("POST", route)!;
        Assert.Equal(id, operation.OperationId);
        Assert.Equal(OperatorTier.T1, OperatorOperationCatalog.Find(id)!.Tier);
        Assert.False(OperatorPolicy.Decide(OperatorOperationCatalog.Find(id), "opa_1", Alpha, [], Now, Same).Admitted);
        Assert.True(OperatorPolicy.Decide(OperatorOperationCatalog.Find(id), "opa_1", Alpha,
            [Grant(operations: [id])], Now, Same).Admitted);
    }

    [Fact]
    public async Task Log_write_classification_carries_the_project_into_the_grant_target()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/operator/log";
        context.SetEndpoint(new RouteEndpointBuilder(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/operator/log"), 0).Build());
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"project\":\"alpha\"}"));
        context.Request.ContentLength = context.Request.Body.Length;

        var operation = await OperatorRequestClassifier.ClassifyAsync(context, false, null,
            (_, _) => null, _ => null);
        var target = OperatorRequestClassifier.GrantTarget(operation!, PermissionTarget.Unaddressed,
            key => key == "project" ? context.Items["OperatorLog.Project"] as string : null);

        Assert.Equal(OperatorOperationIds.OperatorLogAppend, operation?.Id);
        Assert.Equal("alpha", target.Project);
    }

    public static TheoryData<string, StandingGrant> NotCovering() => new()
    {
        { "another principal", Grant(principal: "opa_2") },
        { "another operation", Grant(operations: [OperatorOperationIds.CardSteer]) },
        { "another project", Grant(scope: new StandingGrantScope(OperatorScopeKinds.Project, "beta")) },
        { "expired", Grant(expiresAt: Now) },
        { "revoked", Grant(revokedAt: Now.AddSeconds(-1)) },
        { "issued later", Grant(issuedAt: Now.AddMinutes(1)) },
        { "a host scope", Grant(scope: new StandingGrantScope(OperatorScopeKinds.Host, Host: "build-host")) },
    };

    [Theory]
    [MemberData(nameof(NotCovering))]
    public void A_grant_that_does_not_cover_the_request_leaves_it_refused(string because, StandingGrant grant)
        => Assert.True(OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [grant], Now, Same).Reason == OperatorPolicyReasons.GrantRequired,
            because);

    [Fact]
    public void An_exceeded_hourly_rate_is_refused_and_uses_older_than_an_hour_do_not_count()
    {
        var full = Grant(rate: 2, uses: [Now.AddMinutes(-30), Now.AddMinutes(-1)]);
        Assert.Equal(OperatorPolicyReasons.GrantRateExceeded, OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [full], Now, Same).Reason);

        var aged = Grant(rate: 2, uses: [Now.AddMinutes(-61), Now.AddMinutes(-1)]);
        Assert.True(OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [aged], Now, Same).Admitted);
    }

    [Fact]
    public void A_second_grant_with_capacity_admits_when_the_first_is_exhausted()
    {
        var full = Grant(id: "sgr_full", rate: 1, uses: [Now.AddMinutes(-5)]);
        var spare = Grant(id: "sgr_spare", expiresAt: Now.AddDays(3));
        Assert.Equal("sgr_spare", OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [full, spare], Now, Same).GrantId);
    }

    [Theory]
    [InlineData("08:00", "18:00", true)]
    [InlineData("10:00", "10:30", true)]
    [InlineData("10:01", "18:00", false)]
    [InlineData("22:00", "06:00", false)]
    [InlineData("22:00", "10:01", true)]
    public void A_daily_window_admits_only_inside_it(string start, string end, bool admitted)
    {
        var decision = OperatorPolicy.Decide(Requeue, "opa_1", Alpha, [Grant(window: new StandingGrantWindow(start, end))], Now, Same);
        Assert.Equal(admitted, decision.Admitted);
        if (!admitted) Assert.Equal(OperatorPolicyReasons.GrantOutsideWindow, decision.Reason);
    }

    [Fact]
    public void Scope_kinds_cover_by_workspace_project_environment_and_host()
    {
        var deploy = OperatorOperationCatalog.Find(OperatorOperationIds.StageDeploy)!;
        var stage = new OperatorGrantTarget("alpha", IamEnvironments.Stage);
        var production = new OperatorGrantTarget("alpha", IamEnvironments.Production);
        var stageGrant = Grant(operations: [deploy.Id],
            scope: new StandingGrantScope(OperatorScopeKinds.Environment, "alpha", IamEnvironments.Stage));
        Assert.True(OperatorPolicy.Decide(deploy, "opa_1", stage, [stageGrant], Now, Same).Admitted);
        Assert.False(OperatorPolicy.Decide(deploy, "opa_1", production, [stageGrant], Now, Same).Admitted);
        Assert.True(OperatorPolicy.Decide(deploy, "opa_1", production,
            [Grant(operations: [deploy.Id])], Now, Same).Admitted);

        var restart = OperatorOperationCatalog.Find(OperatorOperationIds.ServiceRestart)!;
        var hostGrant = Grant(operations: [restart.Id], scope: new StandingGrantScope(OperatorScopeKinds.Host, Host: "build-host"));
        Assert.True(OperatorPolicy.Decide(restart, "opa_1", new OperatorGrantTarget(Host: "build-host"), [hostGrant], Now, Same).Admitted);
        Assert.False(OperatorPolicy.Decide(restart, "opa_1", new OperatorGrantTarget(Host: "other-host"), [hostGrant], Now, Same).Admitted);
        Assert.True(OperatorPolicy.Decide(restart, "opa_1", new OperatorGrantTarget(Host: "other-host"),
            [Grant(operations: [restart.Id], scope: new StandingGrantScope(OperatorScopeKinds.Workspace))], Now, Same).Admitted);
    }

    [Theory]
    [InlineData(TaskStates.Ready, OperatorOperationIds.CardRequeue)]
    [InlineData(TaskStates.Backlog, OperatorOperationIds.CardRequeue)]
    [InlineData(TaskStates.Completed, OperatorOperationIds.GateBypass)]
    [InlineData(TaskStates.HumanReview, OperatorOperationIds.GateBypass)]
    [InlineData(TaskStates.Progress, null)]
    [InlineData(null, null)]
    public void A_card_move_is_a_requeue_into_a_queue_lane_and_a_gate_bypass_into_a_delivered_lane(string? lane, string? operation)
    {
        var rule = OperatorOperationCatalog.ResolveWrite("POST", "/api/tasks/{}/move")!;
        Assert.Equal(operation, OperatorOperationCatalog.Refine(rule, new OperatorRequestFacts(TargetLane: lane))?.Id);
    }

    [Theory]
    [InlineData("/api/projects/{}/environments/releases", null, true, OperatorOperationIds.StageDeploy)]
    [InlineData("/api/projects/{}/environments/releases", null, false, OperatorOperationIds.GateBypass)]
    [InlineData("/api/projects/{}/environments/promotions/approve", null, true, OperatorOperationIds.ProductionPromote)]
    [InlineData("/api/projects/{}/environments/promotions/approve", null, false, OperatorOperationIds.GateBypass)]
    [InlineData("/api/projects/{}/deploy/releases/{}/deployments", IamEnvironments.Stage, true, OperatorOperationIds.StageDeploy)]
    [InlineData("/api/projects/{}/deploy/releases/{}/deployments", IamEnvironments.Production, true, OperatorOperationIds.ProductionPromote)]
    [InlineData("/api/projects/{}/deploy/releases/{}/deployments", IamEnvironments.Production, false, OperatorOperationIds.GateBypass)]
    [InlineData("/api/projects/{}/deploy/releases/{}/deployments", null, true, null)]
    public void A_deploy_or_promotion_is_routine_only_with_full_gate_evidence(
        string pattern, string? environment, bool evidence, string? operation)
    {
        var rule = OperatorOperationCatalog.ResolveWrite("POST", pattern)!;
        Assert.Equal(operation, OperatorOperationCatalog.Refine(rule,
            new OperatorRequestFacts(EnvironmentKind: environment, FullGateEvidence: evidence))?.Id);
    }

    private static StandingGrant Grant(
        string id = "sgr_1", string principal = "opa_1", string[]? operations = null, StandingGrantScope? scope = null,
        int rate = 10, StandingGrantWindow? window = null, DateTime? issuedAt = null, DateTime? expiresAt = null,
        DateTime? revokedAt = null, DateTime[]? uses = null) => new()
    {
        Id = id, PrincipalId = principal, Operations = [.. operations ?? [OperatorOperationIds.CardRequeue]],
        Scope = scope ?? new StandingGrantScope(OperatorScopeKinds.Project, "alpha"), RatePerHour = rate, Window = window,
        IssuedBy = "usr_owner", IssuedAt = issuedAt ?? Now.AddDays(-1), ExpiresAt = expiresAt ?? Now.AddDays(6),
        RevokedAt = revokedAt, Reason = "Routine requeues", Uses = [.. uses ?? []],
    };
}
