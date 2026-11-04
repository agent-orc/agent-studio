using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-4 standing grants in the identity store (decision D7
/// option A): only an active Owner issues and renews a grant, each after a
/// fresh step-up, and revokes it without one; a grant covers T1 operations
/// only, lasts seven days by default and at most thirty, stops admitting at
/// its expiry and on the first request after its revocation, counts its
/// hourly rate across a restart, and every change lands in the IAM audit.
/// </summary>
public sealed class StandingGrantTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private static readonly Func<string, string, bool> Same = (bound, requested)
        => string.Equals(bound, requested, StringComparison.OrdinalIgnoreCase);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "standing-grants-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly AccessSecurityStore _store;
    private readonly string _ownerId;
    private readonly string _ownerSession;
    private readonly string _totpSecret;
    private readonly string _agentId;
    private static readonly OperatorOperation Requeue = OperatorOperationCatalog.Find(OperatorOperationIds.CardRequeue)!;
    private static readonly OperatorGrantTarget Alpha = new("alpha");

    public StandingGrantTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var bootstrap = _store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        (_ownerId, _ownerSession) = (bootstrap.User.Id, bootstrap.SessionToken);
        var owner = _store.AuthenticateSession(_ownerSession)!;
        _totpSecret = _store.BeginTotpEnrollment(owner);
        _store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_totpSecret, Now));
        _agentId = _store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null), _ownerId).Id;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void A_grant_lasts_seven_days_by_default_at_most_thirty_and_stops_admitting_at_expiry()
    {
        var grant = Issue();
        Assert.Equal(grant.IssuedAt.AddDays(7), grant.ExpiresAt);
        Assert.Equal((_ownerId, OperatorTier.T1), (grant.IssuedBy, grant.Tier));
        Assert.Equal("invalid-lifetime", Refused(() => Issue(days: 31)).Code);
        var longest = Issue(days: 30);
        Assert.Equal(longest.IssuedAt.AddDays(30), longest.ExpiresAt);
        _store.RevokeStandingGrant(longest.Id, new RevokeStandingGrantRequest(null), _ownerId);

        Assert.True(Admit().Admitted);
        _clock.Advance(grant.ExpiresAt - Now - TimeSpan.FromSeconds(1));
        Assert.Equal(grant.Id, Admit().GrantId);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(OperatorPolicyReasons.GrantRequired, Admit().Reason);
        Assert.Equal(StandingGrantStates.Expired, _store.ToStandingGrantResponse(_store.ListStandingGrants().Single(item => item.Id == grant.Id)).State);
        Assert.Equal("standing-grant-ended", Refused(() =>
            _store.RenewStandingGrant(grant.Id, new RenewStandingGrantRequest(null), SteppedUpOwner())).Code);
    }

    [Fact]
    public void A_revoked_grant_refuses_the_next_request_and_a_renewal_extends_a_live_one()
    {
        var grant = Issue(days: 2);
        Assert.True(Admit().Admitted);
        _clock.Advance(TimeSpan.FromDays(1));
        var renewed = _store.RenewStandingGrant(grant.Id, new RenewStandingGrantRequest(10), SteppedUpOwner());
        Assert.Equal((Now.AddDays(10), _ownerId), (renewed.ExpiresAt, renewed.RenewedBy));
        Assert.Equal("invalid-lifetime", Refused(() =>
            _store.RenewStandingGrant(grant.Id, new RenewStandingGrantRequest(31), SteppedUpOwner())).Code);

        var revoked = _store.RevokeStandingGrant(grant.Id, new RevokeStandingGrantRequest("Done with the overnight run"), _ownerId);
        Assert.Equal((Now, "Done with the overnight run"), (revoked.RevokedAt!.Value, revoked.RevokeReason));
        // No clock advance: the very next request after the revocation is refused.
        Assert.Equal(OperatorPolicyReasons.GrantRequired, Admit().Reason);
        Assert.Equal("standing-grant-ended", Refused(() =>
            _store.RenewStandingGrant(grant.Id, new RenewStandingGrantRequest(null), SteppedUpOwner())).Code);
    }

    [Fact]
    public void The_hourly_rate_is_enforced_and_survives_a_restart()
    {
        var grant = Issue(rate: 2);
        Assert.True(Admit().Admitted);
        Assert.True(Admit().Admitted);
        Assert.Equal(OperatorPolicyReasons.GrantRateExceeded, Admit().Reason);

        var restarted = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        Assert.Equal(OperatorPolicyReasons.GrantRateExceeded,
            restarted.AdmitOperatorGrant(_agentId, Requeue, Alpha, Same).Reason);
        Assert.Equal(2, restarted.ToStandingGrantResponse(restarted.ListStandingGrants().Single()).UsesLastHour);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(grant.Id, restarted.AdmitOperatorGrant(_agentId, Requeue, Alpha, Same).GrantId);
    }

    [Fact]
    public void Only_an_active_owner_changes_grants_and_issue_and_renew_need_a_fresh_step_up()
    {
        var admin = _store.InviteUser(new InviteUserRequest("workspace.admin", "Admin", "", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), _ownerId).User;
        var adminPrincipal = new HumanPrincipal(admin, new StudioSession
        {
            Id = "ses_admin", UserId = admin.Id, TokenHash = "", CsrfHash = "", CreatedAt = Now, LastSeenAt = Now,
            ExpiresAt = Now.AddHours(1), AbsoluteExpiresAt = Now.AddHours(1),
        });
        Assert.Equal("owner-required", Refused(() => _store.CreateStandingGrant(Request(), adminPrincipal)).Code);

        var withoutStepUp = _store.AuthenticateSession(_ownerSession)!;
        Assert.Equal("step-up-required", Refused(() => _store.CreateStandingGrant(Request(), withoutStepUp)).Code);
        var owner = SteppedUpOwner();
        var grant = _store.CreateStandingGrant(Request(), owner);
        // The step-up is single use.
        Assert.Equal("step-up-required", Refused(() => _store.CreateStandingGrant(Request(), owner)).Code);
        Assert.Equal("step-up-required", Refused(() =>
            _store.RenewStandingGrant(grant.Id, new RenewStandingGrantRequest(null), owner)).Code);

        Assert.Equal("owner-required", Refused(() =>
            _store.RevokeStandingGrant(grant.Id, new RevokeStandingGrantRequest(null), admin.Id)).Code);
        Assert.NotNull(_store.RevokeStandingGrant(grant.Id, new RevokeStandingGrantRequest(null), _ownerId).RevokedAt);
    }

    [Theory]
    [InlineData(OperatorOperationIds.SecretsChange, "grant-operation-invalid")]
    [InlineData(OperatorOperationIds.GateBypass, "grant-operation-invalid")]
    [InlineData(OperatorOperationIds.Observe, "grant-operation-invalid")]
    [InlineData("shell.exec", "grant-operation-invalid")]
    [InlineData(OperatorOperationIds.ServiceRestart, "grant-scope-mismatch")]
    public void A_grant_covers_only_t1_operations_its_scope_can_reach(string operation, string code)
        => Assert.Equal(code, Refused(() => _store.CreateStandingGrant(Request(operations: [operation]), SteppedUpOwner())).Code);

    [Fact]
    public void Rate_window_scope_and_reason_are_validated()
    {
        Assert.Equal("invalid-rate", Refused(() => _store.CreateStandingGrant(Request(rate: 0), SteppedUpOwner())).Code);
        Assert.Equal("invalid-window", Refused(() => _store.CreateStandingGrant(
            Request(window: new StandingGrantWindow("25:00", "06:00")), SteppedUpOwner())).Code);
        Assert.Equal("invalid-scope", Refused(() => _store.CreateStandingGrant(
            Request(scope: new StandingGrantScope(OperatorScopeKinds.Environment, "alpha", "qa")), SteppedUpOwner())).Code);
        Assert.Equal("reason-required", Refused(() => _store.CreateStandingGrant(Request(reason: " "), SteppedUpOwner())).Code);
    }

    [Fact]
    public void Grant_changes_appear_in_the_iam_audit_and_disabling_the_agent_revokes_its_grants()
    {
        var start = _store.ControlPlaneAudit.HeadSequence;
        var grant = Issue();
        _store.RenewStandingGrant(grant.Id, new RenewStandingGrantRequest(3), SteppedUpOwner());
        _store.RevokeStandingGrant(grant.Id, new RevokeStandingGrantRequest("Rotation"), _ownerId);
        var second = Issue();
        _store.DisableOperatorAgent(_agentId, _ownerId);

        Assert.NotNull(_store.ListStandingGrants().Single(item => item.Id == second.Id).RevokedAt);
        Assert.Equal(OperatorPolicyReasons.GrantRequired, Admit().Reason);
        var entries = _store.ControlPlaneAudit.Read(start + 1, 100)
            .Where(entry => entry.Stream == ControlPlaneAuditStreams.Iam && entry.Target?.StartsWith("standing-grant:") == true)
            .Select(entry => (entry.Operation, entry.Target, entry.Actor)).ToList();
        var owner = $"human:{_ownerId}";
        Assert.Equal(
        [
            ("standing-grant.create", $"standing-grant:{grant.Id}", owner),
            ("standing-grant.renew", $"standing-grant:{grant.Id}", owner),
            ("standing-grant.revoke", $"standing-grant:{grant.Id}", owner),
            ("standing-grant.create", $"standing-grant:{second.Id}", owner),
            ("standing-grant.revoke", $"standing-grant:{second.Id}", owner),
        ], entries);
        Assert.True(_store.ControlPlaneAudit.VerifyChain().Ok);
    }

    private OperatorPolicyDecision Admit() => _store.AdmitOperatorGrant(_agentId, Requeue, Alpha, Same);

    private StandingGrant Issue(int? days = null, int rate = 10)
        => _store.CreateStandingGrant(Request(days: days, rate: rate), SteppedUpOwner());

    private CreateStandingGrantRequest Request(
        string[]? operations = null, StandingGrantScope? scope = null, int rate = 10, StandingGrantWindow? window = null,
        int? days = null, string reason = "Requeue stalled cards")
        => new(_agentId, operations ?? [OperatorOperationIds.CardRequeue],
            scope ?? new StandingGrantScope(OperatorScopeKinds.Project, "alpha"), rate, window, days, reason);

    /// <summary>A fresh Owner session right after a second-factor step-up; the fake clock may have outrun older sessions.</summary>
    private HumanPrincipal SteppedUpOwner()
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        var session = _store.Login("first.owner", Password, "standing-grant-tests", SecondFactorCodes.CurrentTotp(_totpSecret, Now)).SessionToken;
        var owner = _store.AuthenticateSession(session)!;
        _store.VerifyStepUp(owner, SecondFactorCodes.CurrentTotp(_totpSecret, Now));
        return owner;
    }

    private static SecurityOperationException Refused(Action action) => Assert.Throws<SecurityOperationException>(action);

    private static SecurityOperationException Refused<T>(Func<T> action) => Assert.Throws<SecurityOperationException>(() => action());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
