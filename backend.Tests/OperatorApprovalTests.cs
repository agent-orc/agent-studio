using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-5 approvals in the identity store and as pure policy
/// (decision SM-D8 option A): only an active Owner decides, each decision
/// consumes a fresh step-up, a request stays decidable for 15 minutes and an
/// approval runs once within 15 minutes of the decision, bound to the
/// session, operation and parameter digest. Disabling the agent or revoking
/// the session cancels open approvals, and one session cannot flood the Owner.
/// </summary>
public sealed class OperatorApprovalTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private const string Digest = "sha256:aa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-approvals-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly AccessSecurityStore _store;
    private readonly string _ownerId;
    private readonly string _totpSecret;
    private readonly string _agentId;
    private readonly string _sessionId;
    private static readonly OperatorOperation Delete = OperatorOperationCatalog.Find(OperatorOperationIds.DataDelete)!;

    public OperatorApprovalTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Secrets:KeyFile"] = _root + ".kek",
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var bootstrap = _store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        _ownerId = bootstrap.User.Id;
        var owner = _store.AuthenticateSession(bootstrap.SessionToken)!;
        _totpSecret = _store.BeginTotpEnrollment(owner);
        _store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_totpSecret, Now));
        _agentId = _store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null), _ownerId).Id;
        _sessionId = _store.StartOperatorSession(_agentId, new StartOperatorSessionRequest(null, null), _ownerId).Session.Id;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public static TheoryData<string, string?> RedeemMatrix => new()
    {
        { "approved", null },
        { "pending", OperatorApprovalReasons.Pending },
        { "rejected", OperatorApprovalReasons.Rejected },
        { "used", OperatorApprovalReasons.Used },
        { "cancelled", OperatorApprovalReasons.Cancelled },
        { "approval-lapsed", OperatorApprovalReasons.Expired },
        { "request-lapsed", OperatorApprovalReasons.Expired },
        { "other-session", OperatorApprovalReasons.NotFound },
        { "other-principal", OperatorApprovalReasons.NotFound },
        { "missing", OperatorApprovalReasons.NotFound },
        { "other-digest", OperatorApprovalReasons.DigestMismatch },
        { "other-operation", OperatorApprovalReasons.DigestMismatch },
    };

    [Theory]
    [MemberData(nameof(RedeemMatrix))]
    public void Redeem_admits_only_an_approved_unused_live_approval_of_the_same_session_and_digest(string scenario, string? expected)
    {
        var now = new DateTime(2026, 11, 4, 9, 10, 0, DateTimeKind.Utc);
        var requested = now.AddMinutes(-5);
        var approval = new OperatorApproval
        {
            Id = "apr_1", PrincipalId = "opa_1", SessionId = "ses_1", Actor = "operator-agent:opa_1/session:ses_1", DelegatedBy = "human:usr_1",
            OperationId = Delete.Id, Method = "DELETE", Path = "/api/tasks/card", ParameterDigest = Digest, InstructionSource = "owner-chat",
            RequestedAt = requested, ExpiresAt = requested + OperatorApprovalPolicy.Lifetime,
            Approved = true, DecidedBy = "usr_1", DecidedAt = now.AddMinutes(-1), ApprovalExpiresAt = now.AddMinutes(14),
        };
        var (principal, session, operation, digest) = ("opa_1", "ses_1", Delete.Id, Digest);
        approval = scenario switch
        {
            "pending" => approval with { Approved = null, DecidedBy = null, DecidedAt = null, ApprovalExpiresAt = null },
            "rejected" => approval with { Approved = false, ApprovalExpiresAt = null },
            "used" => approval with { UsedAt = now.AddSeconds(-5) },
            "cancelled" => approval with { CancelledAt = now.AddSeconds(-5), CancelReason = "operator agent disabled" },
            "approval-lapsed" => approval with { ApprovalExpiresAt = now },
            "request-lapsed" => approval with
            {
                Approved = null, DecidedBy = null, DecidedAt = null, ApprovalExpiresAt = null, ExpiresAt = now,
            },
            _ => approval,
        };
        switch (scenario)
        {
            case "other-session": session = "ses_2"; break;
            case "other-principal": principal = "opa_2"; break;
            case "other-digest": digest = "sha256:bb"; break;
            case "other-operation": operation = OperatorOperationIds.SecretsChange; break;
        }
        Assert.Equal(expected, OperatorApprovalPolicy.RedeemRefusal(scenario == "missing" ? null : approval, principal, session, operation, digest, now));
    }

    [Fact]
    public void A_request_stays_decidable_for_15_minutes_and_an_approval_runs_once_within_15_minutes_of_the_decision()
    {
        var approval = Request();
        Assert.Equal(approval.RequestedAt + TimeSpan.FromMinutes(15), approval.ExpiresAt);
        Assert.Equal(OperatorApprovalStates.Pending, State(approval.Id));

        _clock.Advance(TimeSpan.FromMinutes(10));
        var approved = _store.DecideOperatorApproval(approval.Id, true, new DecideOperatorApprovalRequest(" ok "), SteppedUpOwner());
        Assert.Equal((true, _ownerId, "ok"), (approved.Approved, approved.DecidedBy, approved.DecisionNote));
        Assert.Equal(approved.DecidedAt + TimeSpan.FromMinutes(15), approved.ApprovalExpiresAt);
        // The decision is final.
        Assert.Equal("approval-decided", Refused(() =>
            _store.DecideOperatorApproval(approval.Id, false, new DecideOperatorApprovalRequest(null), SteppedUpOwner())).Code);

        // Still usable after the request's own 15 minutes, because the approval's clock starts at the decision.
        _clock.Advance(TimeSpan.FromMinutes(14));
        var admitted = Redeem(approval.Id);
        Assert.True(admitted.Admitted);
        Assert.Equal(approval.Id, admitted.ApprovalId);
        Assert.Equal(OperatorApprovalReasons.Used, Redeem(approval.Id).Reason);
        _store.RecordOperatorApprovalResult(approval.Id, 200);
        _store.RecordOperatorApprovalResult(approval.Id, 500);
        var stored = _store.ListOperatorApprovals().Single(candidate => candidate.Id == approval.Id);
        Assert.Equal((200, OperatorApprovalStates.Used), (stored.ResultStatus, State(approval.Id)));

        var late = Request(digest: "sha256:cc");
        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(OperatorApprovalStates.Expired, State(late.Id));
        Assert.Equal(OperatorApprovalReasons.Expired, Refused(() =>
            _store.DecideOperatorApproval(late.Id, true, new DecideOperatorApprovalRequest(null), SteppedUpOwner())).Code);
    }

    [Fact]
    public void Only_an_active_owner_decides_and_each_decision_needs_a_fresh_step_up()
    {
        var first = Request();
        var second = Request(digest: "sha256:bb");
        var admin = _store.InviteUser(new InviteUserRequest("workspace.admin", "Admin", "", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), _ownerId).User;
        var adminPrincipal = new HumanPrincipal(admin, new StudioSession
        {
            Id = "ses_admin", UserId = admin.Id, TokenHash = "", CsrfHash = "", CreatedAt = Now, LastSeenAt = Now,
            ExpiresAt = Now.AddHours(1), AbsoluteExpiresAt = Now.AddHours(1),
        });
        Assert.Equal("owner-required", Refused(() =>
            _store.DecideOperatorApproval(first.Id, true, new DecideOperatorApprovalRequest(null), adminPrincipal)).Code);

        var owner = SteppedUpOwner();
        _store.DecideOperatorApproval(first.Id, false, new DecideOperatorApprovalRequest("Not now"), owner);
        Assert.Equal(OperatorApprovalStates.Rejected, State(first.Id));
        Assert.Equal(OperatorApprovalReasons.Rejected, Redeem(first.Id).Reason);
        // Rejecting used the step-up: approving the next request needs a new one.
        Assert.Equal("step-up-required", Refused(() =>
            _store.DecideOperatorApproval(second.Id, true, new DecideOperatorApprovalRequest(null), owner)).Code);
        Assert.Equal(OperatorApprovalStates.Pending, State(second.Id));
        Assert.Equal("invalid-note", Refused(() =>
            _store.DecideOperatorApproval(second.Id, true, new DecideOperatorApprovalRequest(new string('n', 501)), SteppedUpOwner())).Code);
        Assert.Equal(OperatorApprovalReasons.NotFound, Refused(() =>
            _store.DecideOperatorApproval("apr_unknown", true, new DecideOperatorApprovalRequest(null), SteppedUpOwner())).Code);

        var operations = _store.ControlPlaneAudit.Read(1, 10_000).Where(entry => entry.Operation.StartsWith("operator-approval.", StringComparison.Ordinal))
            .Select(entry => (entry.Operation, entry.Actor)).ToList();
        Assert.Contains(("operator-approval.reject", "human:" + _ownerId), operations);
        Assert.Equal(2, operations.Count(entry => entry.Operation == "operator-approval.request"));
    }

    [Fact]
    public void A_retried_request_reuses_its_open_approval_and_one_session_cannot_flood_the_owner()
    {
        var (first, created) = _store.RequestOperatorApproval(Facts(Digest));
        var (again, createdAgain) = _store.RequestOperatorApproval(Facts(Digest));
        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(first.Id, again.Id);

        for (var index = 1; index < AccessSecurityStore.MaxOpenApprovalsPerSession; index++)
            Request(digest: $"sha256:{index:x4}");
        Assert.Equal("approval-queue-full", Refused(() => _store.RequestOperatorApproval(Facts("sha256:ffff"))).Code);
        // Expired requests no longer count.
        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(_store.RequestOperatorApproval(Facts("sha256:ffff")).Created);
    }

    [Fact]
    public void Revoking_the_session_or_disabling_the_agent_cancels_its_open_approvals()
    {
        var pending = Request();
        var approved = Request(digest: "sha256:bb");
        _store.DecideOperatorApproval(approved.Id, true, new DecideOperatorApprovalRequest(null), SteppedUpOwner());
        var otherSession = _store.StartOperatorSession(_agentId, new StartOperatorSessionRequest("second", null), _ownerId).Session.Id;
        var other = _store.RequestOperatorApproval(Facts(Digest, otherSession)).Approval;

        _store.RevokeOperatorSession(_agentId, _sessionId, _ownerId);
        Assert.Equal((OperatorApprovalStates.Cancelled, OperatorApprovalStates.Cancelled, OperatorApprovalStates.Pending),
            (State(pending.Id), State(approved.Id), State(other.Id)));
        Assert.Equal("operator session revoked",
            _store.ListOperatorApprovals().Single(candidate => candidate.Id == approved.Id).CancelReason);
        Assert.Equal(OperatorApprovalReasons.Cancelled, Refused(() =>
            _store.DecideOperatorApproval(pending.Id, true, new DecideOperatorApprovalRequest(null), SteppedUpOwner())).Code);

        _store.DisableOperatorAgent(_agentId, _ownerId);
        Assert.Equal(OperatorApprovalStates.Cancelled, State(other.Id));
        Assert.Equal(3, _store.ControlPlaneAudit.Read(1, 10_000).Count(entry => entry.Operation == "operator-approval.cancel"));
    }

    [Fact]
    public void A_session_kill_cancels_the_approvals_of_its_session_and_ending_all_operator_access_cancels_every_open_one()
    {
        var pending = Request();
        var approved = Request(digest: "sha256:bb");
        _store.DecideOperatorApproval(approved.Id, true, new DecideOperatorApprovalRequest(null), SteppedUpOwner());
        var otherSession = _store.StartOperatorSession(_agentId, new StartOperatorSessionRequest("second", null), _ownerId).Session.Id;
        var other = _store.RequestOperatorApproval(Facts(Digest, otherSession)).Approval;

        var killed = _store.KillOperatorSession(_agentId, _sessionId, _ownerId);

        Assert.Equal([pending.Id, approved.Id], killed.ApprovalIds);
        Assert.Equal((OperatorApprovalStates.Cancelled, OperatorApprovalStates.Cancelled, OperatorApprovalStates.Pending),
            (State(pending.Id), State(approved.Id), State(other.Id)));
        Assert.Equal(OperatorApprovalReasons.Cancelled,
            _store.RedeemOperatorApproval(approved.Id, _agentId, _sessionId, Delete, "sha256:bb").Reason);

        var ended = _store.EndOperatorAccess(null, OperatorKillActor.Owner(_ownerId));

        Assert.Equal([other.Id], ended.ApprovalIds);
        Assert.Equal(OperatorApprovalStates.Cancelled, State(other.Id));
        Assert.Equal("operator kill switch", _store.ListOperatorApprovals().Single(candidate => candidate.Id == other.Id).CancelReason);
        Assert.Equal(3, _store.ControlPlaneAudit.Read(1, 10_000).Count(entry => entry.Operation == "operator-approval.cancel"));
    }

    [Fact]
    public async Task The_out_of_band_kill_cancels_every_open_approval_with_the_api_stopped()
    {
        var pending = Request();

        Assert.Equal(OperatorKillSwitchCommand.ExitOk,
            await OperatorKillSwitchCommand.RunAsync(["--reason", "Studio unreachable"], new StringWriter(), _config, _clock));

        var restarted = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var cancelled = restarted.ListOperatorApprovals().Single(candidate => candidate.Id == pending.Id);
        Assert.Equal(OperatorApprovalStates.Cancelled, OperatorApprovalPolicy.State(cancelled, Now));
        Assert.Equal("operator kill switch", cancelled.CancelReason);
        var kill = restarted.ControlPlaneAudit.ReadNewest(entry => entry.Operation == OperatorKillEvents.All, 1).Single();
        Assert.Equal([pending.Id], kill.Details!.Value.GetProperty("approvalIds").EnumerateArray().Select(id => id.GetString()));
        Assert.Contains(restarted.ControlPlaneAudit.ReadNewest(entry => entry.Operation == "operator-approval.cancel", 10),
            entry => entry.Target == $"operator-approval:{pending.Id}");
    }

    [Fact]
    public void The_reviewed_parameters_mask_secret_like_values_and_keep_the_rest()
    {
        // Built at runtime: no credential-shaped literal in source.
        var secret = "v-" + Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = "DEPLOY_VALUE", value = secret, environment = "stage",
            nested = new { apiToken = secret, labels = new[] { "a", "b" } },
            credentials = new[] { secret },
        });
        var shown = OperatorApprovalParameters.Display(body);
        Assert.DoesNotContain(secret, shown);
        Assert.Contains("\"name\": \"DEPLOY_VALUE\"", shown);
        Assert.Contains("\"environment\": \"stage\"", shown);
        Assert.Contains("\"value\": \"[masked]\"", shown);
        Assert.Contains("\"apiToken\": \"[masked]\"", shown);
        Assert.Contains("\"a\"", shown);
        Assert.Equal("plain text", OperatorApprovalParameters.Display(Encoding.UTF8.GetBytes("plain text")));
    }

    private OperatorApproval Request(string digest = Digest) => _store.RequestOperatorApproval(Facts(digest)).Approval;

    private OperatorApprovalRequestFacts Facts(string digest, string? sessionId = null)
    {
        var session = sessionId ?? _sessionId;
        return new OperatorApprovalRequestFacts(_agentId, session, $"operator-agent:{_agentId}/session:{session}", "human:" + _ownerId,
            Delete, "DELETE", "/api/tasks/alpha-card", "?project=alpha", null, digest, "project:alpha/task:alpha-card",
            OperatorInstructionSources.OwnerChat, "Remove the duplicate card");
    }

    private OperatorPolicyDecision Redeem(string approvalId) => _store.RedeemOperatorApproval(approvalId, _agentId, _sessionId, Delete, Digest);

    private string State(string approvalId)
        => OperatorApprovalPolicy.State(_store.ListOperatorApprovals().Single(candidate => candidate.Id == approvalId), Now);

    /// <summary>A fresh Owner session right after a second-factor step-up.</summary>
    private HumanPrincipal SteppedUpOwner()
    {
        var session = _store.Login("first.owner", Password, "operator-approval-tests", SecondFactorCodes.CurrentTotp(_totpSecret, Now)).SessionToken;
        var owner = _store.AuthenticateSession(session)!;
        _store.VerifyStepUp(owner, SecondFactorCodes.CurrentTotp(_totpSecret, Now));
        return owner;
    }

    private static SecurityOperationException Refused<T>(Func<T> action) => Assert.Throws<SecurityOperationException>(() => action());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        File.Delete(_root + ".kek");
    }
}
