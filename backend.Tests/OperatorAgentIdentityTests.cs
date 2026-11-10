using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-1: the operator agent is a service account with child
/// sessions. The Owner starts each session and alone renews it, a session
/// lasts at most eight hours, its requests are attributed to
/// <c>operator-agent:&lt;id&gt;/session:&lt;sid&gt;</c> delegated by the Owner, and
/// it never reaches access administration, the audit trail, or grants.
/// </summary>
public sealed class OperatorAgentIdentityTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-agent-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly AccessSecurityStore _store;
    private readonly string _ownerId;
    private readonly string _ownerSession;
    private string? _totpSecret;

    public OperatorAgentIdentityTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var bootstrap = _store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        (_ownerId, _ownerSession) = (bootstrap.User.Id, bootstrap.SessionToken);
    }

    /// <summary>The Owner's session after a fresh second-factor step-up, as a grant change requires.</summary>
    private HumanPrincipal SteppedUpOwner()
    {
        var owner = _store.AuthenticateSession(_ownerSession)!;
        if (_totpSecret is null)
        {
            _totpSecret = _store.BeginTotpEnrollment(owner);
            _store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_totpSecret, _clock.GetUtcNow().UtcDateTime));
            owner = _store.AuthenticateSession(_ownerSession)!;
        }
        _clock.Advance(TimeSpan.FromSeconds(30));
        _store.VerifyStepUp(owner, SecondFactorCodes.CurrentTotp(_totpSecret, _clock.GetUtcNow().UtcDateTime));
        return owner;
    }

    [Fact]
    public void A_session_lasts_at_most_eight_hours_and_ends_on_expiry()
    {
        var agent = NewAgent();
        Assert.Equal(ServiceAccountOrigins.OperatorAgent, agent.Origin);
        var started = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest("console", null), _ownerId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddHours(8), started.Session.ExpiresAt);
        Assert.StartsWith(AccessSecurityStore.OperatorSessionTokenPrefix + ".", started.Secret);

        var tooLong = Assert.Throws<SecurityOperationException>(() =>
            _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, 9), _ownerId));
        Assert.Equal((400, "invalid-lifetime"), (tooLong.Status, tooLong.Code));
        Assert.Throws<SecurityOperationException>(() =>
            _store.RenewOperatorSession(agent.Id, started.Session.Id, new RenewOperatorSessionRequest(9), _ownerId));

        var principal = _store.AuthenticateServiceAccount(started.Secret);
        Assert.NotNull(principal);
        Assert.Equal($"operator-agent:{agent.Id}/session:{started.Session.Id}", principal!.ActorId);
        Assert.Equal($"human:{_ownerId}", principal.Operator!.DelegatedBy);

        _clock.Advance(TimeSpan.FromHours(8));
        Assert.Null(_store.AuthenticateServiceAccount(started.Secret));
        var ended = Assert.Throws<SecurityOperationException>(() =>
            _store.RenewOperatorSession(agent.Id, started.Session.Id, new RenewOperatorSessionRequest(null), _ownerId));
        Assert.Equal("operator-session-ended", ended.Code);
    }

    [Fact]
    public void Only_an_active_owner_starts_or_renews_a_session()
    {
        var agent = NewAgent();
        var admin = _store.InviteUser(new InviteUserRequest("workspace.admin", "Admin", "", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), _ownerId).User.Id;
        var started = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, 2), _ownerId);

        Assert.Equal("owner-required", Assert.Throws<SecurityOperationException>(() =>
            _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), admin)).Code);
        Assert.Equal("owner-required", Assert.Throws<SecurityOperationException>(() =>
            _store.RenewOperatorSession(agent.Id, started.Session.Id, new RenewOperatorSessionRequest(null), admin)).Code);
        Assert.Equal("owner-required", Assert.Throws<SecurityOperationException>(() =>
            _store.CreateOperatorAgent(new CreateOperatorAgentRequest("other", null), admin)).Code);

        _clock.Advance(TimeSpan.FromHours(1));
        var renewed = _store.RenewOperatorSession(agent.Id, started.Session.Id, new RenewOperatorSessionRequest(null), _ownerId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddHours(8), renewed.ExpiresAt);
        Assert.Equal(_ownerId, renewed.RenewedBy);
        // The operator principal holds no service-account token of its own.
        Assert.Equal("operator-agent-account", Assert.Throws<SecurityOperationException>(() =>
            _store.CreateServiceAccountToken(agent.Id, new CreateServiceAccountTokenRequest(null, null), _ownerId)).Code);
    }

    [Fact]
    public void Revocation_disable_and_a_lost_delegation_end_the_session()
    {
        var agent = NewAgent();
        var first = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId);
        var second = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId);

        _store.RevokeOperatorSession(agent.Id, first.Session.Id, _ownerId);
        Assert.Null(_store.AuthenticateServiceAccount(first.Secret));
        Assert.NotNull(_store.AuthenticateServiceAccount(second.Secret));

        _store.DisableOperatorAgent(agent.Id, _ownerId);
        Assert.Null(_store.AuthenticateServiceAccount(second.Secret));
        Assert.All(_store.ListOperatorAgents().Single().Sessions!, session => Assert.NotNull(session.RevokedAt));
    }

    [Fact]
    public async Task Requests_are_attributed_to_the_session_never_to_the_owner()
    {
        var agent = NewAgent(new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));
        var session = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId);
        var actor = $"operator-agent:{agent.Id}/session:{session.Session.Id}";
        var start = _store.ControlPlaneAudit.HeadSequence;

        // SM-4: the write is a routine stage deploy of a gate-verified commit
        // under the Owner's standing grant.
        var grant = _store.CreateStandingGrant(new CreateStandingGrantRequest(agent.Id, [OperatorOperationIds.StageDeploy],
            new StandingGrantScope(OperatorScopeKinds.Environment, "alpha", IamEnvironments.Stage), 5, null, null, "Stage deploys"),
            SteppedUpOwner());
        string? ambientActor = null;
        var write = await Run("POST", "/api/projects/{projectName}/environments/releases", "/api/projects/alpha/environments/releases",
            session.Secret, "{\"artifact\":\"sha256:aa\",\"commit\":\"c0ffee\"}", _ => ambientActor = OperatorActorScope.Current?.Actor,
            new GateEvidence("c0ffee"));
        Assert.True(write.Called);
        Assert.Equal(actor, ambientActor);
        Assert.Equal(actor, write.Context.Items["ClientId"]);
        Assert.Equal(actor, AccessSecurityMiddleware.ActorId(write.Context, "local-operator"));
        Assert.Equal(actor, AgentStudio.Tasks.TaskCrudEndpoints.OperatorActor(write.Context));
        Assert.Equal(actor, TimelineActors.Human(actor));
        Assert.False(TimelineActors.IsHuman(actor));
        var synthetic = Assert.IsType<HumanPrincipal>(write.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]);
        Assert.Equal(actor, synthetic.User.Id);
        Assert.Null(OperatorActorScope.Current);

        var entry = Assert.Single(_store.ControlPlaneAudit.Read(start + 1, 10), item => item.Stream == ControlPlaneAuditStreams.OperatorAgent);
        Assert.Equal((ControlPlaneAuditStreams.OperatorAgent, actor, session.Session.Id, $"human:{_ownerId}"),
            (entry.Stream, entry.Actor, entry.Session, entry.DelegatedBy));
        Assert.Equal(("POST /api/projects/alpha/environments/releases", "project:alpha"), (entry.Operation, entry.Target));
        var details = entry.Details!.Value;
        Assert.Equal((OperatorOperationIds.StageDeploy, "T1", grant.Id),
            (details.GetProperty("operation").GetString(), details.GetProperty("tier").GetString(), details.GetProperty("grantId").GetString()));
        Assert.StartsWith("sha256:", entry.ParameterDigest);
        Assert.DoesNotContain(session.Secret, entry.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operator_session_cannot_administer_access_write_audit_or_create_grants()
    {
        // Even a workspace Admin binding grants an operator session none of these.
        var agent = NewAgent(new IamGrantRequest(IamRoles.Admin, IamScope.Workspace));
        var token = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId).Secret;

        var refused = new[]
        {
            await Run("GET", "/api/auth/iam", "/api/auth/iam", token),
            await Run("POST", "/api/auth/iam/bindings", "/api/auth/iam/bindings", token,
                "{\"principalKind\":\"service-account\",\"principalId\":\"" + agent.Id + "\",\"role\":\"admin\",\"scope\":{\"kind\":\"workspace\"}}"),
            await Run("POST", "/api/auth/iam/operator-agents/{id}/sessions/{sessionId}/renew",
                $"/api/auth/iam/operator-agents/{agent.Id}/sessions/x/renew", token),
            await Run("POST", "/api/auth/audit/verify", "/api/auth/audit/verify", token),
            await Run("GET", "/api/auth/audit/operator-activity", "/api/auth/audit/operator-activity", token),
            await Run("GET", "/api/auth/audit/backups", "/api/auth/audit/backups", token),
            await Run("POST", "/api/auth/invitations", "/api/auth/invitations", token),
            await Run("POST", "/api/projects/path-import", "/api/projects/path-import", token),
            await Run("GET", "/api/v1/{**path}", "/api/v1/runners", token),
        };
        Assert.All(refused, outcome => Assert.Equal((403, false), (outcome.Status, outcome.Called)));
        // SM-4: access changes are T2 and the path import is not catalogued, so the
        // policy step refuses both before the access floor; reads still meet the floor.
        Assert.Equal(OperatorPolicyReasons.ApprovalRequired, refused[1].Error);
        Assert.Equal(OperatorPolicyReasons.NotCatalogued, refused[7].Error);
        Assert.Equal("token-route-denied", refused[0].Error);
        Assert.True((await Run("GET", "/api/tags", "/api/tags", token)).Called);
    }

    private OperatorAgentIdentityTestsAgent NewAgent(params IamGrantRequest[] grants)
    {
        var account = _store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null, grants), _ownerId);
        return new OperatorAgentIdentityTestsAgent(account.Id, account.Origin);
    }

    [Fact]
    public async Task Break_glass_admits_the_host_certificate_policy_step_only_during_its_window()
    {
        var agent = NewAgent(new IamGrantRequest(IamRoles.Admin, IamScope.Workspace));
        var session = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId);
        const string pattern = "/api/hosts/{id}/certificates";
        const string path = "/api/hosts/sample-host/certificates";

        var withoutWindow = await Run("POST", pattern, path, session.Secret, "{}");
        Assert.Equal(OperatorPolicyReasons.GrantRequired, withoutWindow.Error);

        var credential = _store.IssueBreakGlassCredential(SteppedUpOwner());
        _store.OpenBreakGlassWindow(SteppedUpOwner(), credential, [session.Session.Id], 2, "sample-project");
        var duringWindow = await Run("POST", pattern, path, session.Secret, "{}");
        Assert.True(duringWindow.Called);
        Assert.True(Assert.IsType<OperatorPolicyDecision>(
            duringWindow.Context.Items[AccessSecurityMiddleware.OperatorPolicyItem]).Admitted);

        _clock.Advance(TimeSpan.FromHours(2));
        var afterWindow = await Run("POST", pattern, path, session.Secret, "{}");
        Assert.Equal(OperatorPolicyReasons.GrantRequired, afterWindow.Error);
    }

    private async Task<Outcome> Run(string method, string pattern, string path, string token, string? body = null,
        Action<HttpContext>? handler = null, IOperatorGateEvidence? evidence = null)
    {
        var called = false;
        var middleware = new AccessSecurityMiddleware(context =>
        {
            called = true;
            handler?.Invoke(context);
            return Task.CompletedTask;
        }, _config, _store, clock: _clock);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("studio.test");
        context.Request.Headers.Authorization = new StringValues("Bearer " + token);
        if (evidence is not null)
            context.RequestServices = new ServiceCollection().AddSingleton(evidence).BuildServiceProvider();
        if (body is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        object[] metadata = pattern.StartsWith("/api/v1/", StringComparison.Ordinal)
                            || pattern.StartsWith("/api/hosts/", StringComparison.Ordinal)
            ? [new MethodPermissionMetadata(IamPermissions.WorkspaceRead, IamPermissions.WorkspaceOperate)]
            : [];
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0,
            new EndpointMetadataCollection([new HttpMethodMetadata([method]), .. metadata]), pattern));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return new Outcome(context, context.Response.StatusCode, ErrorCode(context), called);
    }

    private static string? ErrorCode(HttpContext context)
    {
        if (context.Response.Body.Length == 0) return null;
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private sealed record Outcome(HttpContext Context, int Status, string? Error, bool Called);

    /// <summary>Full gate evidence for exactly one commit.</summary>
    private sealed class GateEvidence(string commit) : IOperatorGateEvidence
    {
        public bool HasFullGateEvidence(string project, string candidate) => candidate == commit;
        public string? ReleaseCommit(string project, string? releaseId, string? artifact) => null;
    }

    private sealed record OperatorAgentIdentityTestsAgent(string Id, string? Origin);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
