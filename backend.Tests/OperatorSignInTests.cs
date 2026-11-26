using System.Security.Cryptography;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ES-3 (Dossier operator-elevated-session, decision ES-D2 option S2): a
/// terminal starts a sign-in without a credential, only an active Owner with
/// a fresh second factor confirms it, and the next poll receives an
/// <c>oas.*</c> session that records the device. Codes are single use, valid
/// ten minutes, and five wrong entries per Owner within 15 minutes lock code
/// entry for 15 minutes.
/// </summary>
public sealed class OperatorSignInTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private const string Terminal = "198.51.100.7";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-sign-in-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly AccessSecurityStore _store;
    private readonly string _ownerId;
    private readonly string _ownerSession;
    private readonly string _totpSecret;

    public OperatorSignInTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Secrets:KeyFile"] = _root + ".kek",
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var bootstrap = _store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        (_ownerId, _ownerSession) = (bootstrap.User.Id, bootstrap.SessionToken);
        var owner = Owner();
        _totpSecret = _store.BeginTotpEnrollment(owner);
        _store.ConfirmTotpEnrollment(owner, Totp());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (File.Exists(_root + ".kek")) File.Delete(_root + ".kek");
    }

    private HumanPrincipal Owner(string? session = null) => _store.AuthenticateSession(session ?? _ownerSession)!;

    /// <summary>Another browser session of the first Owner, signed in with a fresh authenticator code.</summary>
    private string SecondBrowser(string device)
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        return _store.Login("first.owner", Password, device, Totp()).SessionToken;
    }

    /// <summary>A second person with an accepted invitation and the given workspace role.</summary>
    private (string Id, string Session) Person(string username, string role)
    {
        var invite = _store.InviteUser(new InviteUserRequest(username, username, "", null,
            [new IamGrantRequest(role, IamScope.Workspace)]), _ownerId);
        var accepted = _store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "another correct horse phrase " + Guid.NewGuid().ToString("N")[..8]));
        return (accepted.User.Id, accepted.SessionToken);
    }

    private string Totp() => SecondFactorCodes.CurrentTotp(_totpSecret, _clock.GetUtcNow().UtcDateTime);

    /// <summary>A fresh step-up; the clock moves to the next authenticator step so the code is new.</summary>
    private HumanPrincipal SteppedUp(string? session = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        var owner = Owner(session);
        _store.VerifyStepUp(owner, Totp());
        return owner;
    }

    private static OperatorDevicePublicKey NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        return new OperatorDevicePublicKey("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!), OperatorDevicePublicKey.Base64Url(point.Y!));
    }

    private ServiceAccount NewAgent(string name = "ops console")
        => _store.CreateOperatorAgent(new CreateOperatorAgentRequest(name, null,
            [new IamGrantRequest(IamRoles.OperatorAgent, IamScope.ForProject("alpha"))]), _ownerId);

    private (OperatorSignIn SignIn, string Code, string Poll) Start(OperatorDevicePublicKey? key = null,
        IReadOnlyList<string>? classes = null, string agent = "ops console", string address = Terminal)
        => _store.StartOperatorSignIn(new StartOperatorSignInRequest(key ?? NewKey(), "agent@workstation", agent, classes),
            address, address);

    private PollOperatorSignInResponse Poll(string handle)
    {
        _clock.Advance(TimeSpan.FromSeconds(AccessSecurityStore.OperatorSignInPollIntervalSeconds));
        return _store.PollOperatorSignIn(handle);
    }

    private ConfirmOperatorSignInResponse Confirm(string code, string agentId, IReadOnlyList<string>? classes = null,
        int? hours = null, string? session = null, string? address = "203.0.113.20")
        => _store.ConfirmOperatorSignIn(new ConfirmOperatorSignInRequest(code, agentId, classes, hours), SteppedUp(session), address);

    private IReadOnlyList<string> AuditOperations()
        => _store.ControlPlaneAudit.Read(0, 1000).Select(entry => entry.Operation).ToList();

    private OperatorDeviceCertification Certification(ECDsa key, OperatorSignIn signIn, string kind = "tpm", long? at = null)
    {
        var point = key.ExportParameters(false).Q;
        var publicKey = new OperatorDevicePublicKey("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!), OperatorDevicePublicKey.Base64Url(point.Y!));
        var issuedAt = at ?? _clock.GetUtcNow().ToUnixTimeSeconds();
        var bytes = System.Text.Encoding.UTF8.GetBytes(AccessSecurityStore.DeviceCertificationStatement(signIn.KeyThumbprint, signIn.Id, issuedAt));
        return new(publicKey, kind, issuedAt, OperatorDevicePublicKey.Base64Url(key.SignData(bytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    [Theory]
    [InlineData("tpm")]
    [InlineData("secure-enclave")]
    public void Device_certification_records_binding_and_recognizes_the_same_device_with_a_new_session_key(string kind)
    {
        var agent = NewAgent();
        using var hardware = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        for (var login = 0; login < 2; login++)
        {
            var started = Start();
            var cert = Certification(hardware, started.SignIn, kind);
            _store.PollOperatorSignIn(started.Poll, certification: cert);
            var lookup = _store.LookupOperatorSignIn(started.Code, Owner(), Terminal);
            Assert.Equal(kind, lookup.BindingKind);
            Assert.Equal(cert.PublicKey.Thumbprint(), lookup.DeviceKeyThumbprint);
            Assert.Equal(login == 1, lookup.KnownForAgentIds.Contains(agent.Id));
            Confirm(started.Code, agent.Id);
            Assert.Equal(kind, Poll(started.Poll).BindingKind);
            var session = _store.ListOperatorAgents().Single().Sessions!.Last();
            Assert.Equal(kind, session.Device!.BindingKind);
            Assert.Equal(started.SignIn.KeyThumbprint, session.Device.KeyThumbprint);
            Assert.Equal(cert.PublicKey.Thumbprint(), session.Device.DeviceKeyThumbprint);
            Assert.Equal(kind, _store.ToOperatorSessionResponse(agent.Id, session).Device!.BindingKind);
        }
        Assert.Single(_store.ListOperatorAgents().Single().Devices!);
        Assert.Equal(2, _store.ToOperatorAgentResponse(_store.ListOperatorAgents().Single()).Devices!.Single().ActiveSessions);
        var known = _store.ListOperatorAgents().Single().Devices!.Single();
        _store.ForgetOperatorDevice(agent.Id, known.Id, _ownerId);
        Assert.All(_store.ListOperatorAgents().Single().Sessions!, session => Assert.NotNull(session.RevokedAt));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("session-key")]
    [InlineData("signin-id")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("kind")]
    public void Invalid_certification_from_a_known_device_is_unknown_and_memory_bound(string alteration)
    {
        var agent = NewAgent();
        using var hardware = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = Start();
        _store.PollOperatorSignIn(first.Poll, certification: Certification(hardware, first.SignIn));
        Confirm(first.Code, agent.Id);
        Poll(first.Poll);
        var started = Start();
        var source = alteration == "session-key" ? started.SignIn with { KeyThumbprint = NewKey().Thumbprint() }
            : alteration == "signin-id" ? first.SignIn : started.SignIn;
        var cert = Certification(hardware, source, at: _clock.GetUtcNow().ToUnixTimeSeconds()
            + (alteration == "stale" ? -61 : alteration == "future" ? 61 : 0));
        if (alteration == "signature") cert = cert with { Signature = new string('A', 86) };
        if (alteration == "kind") cert = cert with { BindingKind = "untrusted" };
        _store.PollOperatorSignIn(started.Poll, certification: cert);
        var lookup = _store.LookupOperatorSignIn(started.Code, Owner(), Terminal);
        Assert.Empty(lookup.KnownForAgentIds);
        Assert.Equal("memory", lookup.BindingKind);
        Assert.Null(lookup.DeviceKeyThumbprint);
        Confirm(started.Code, agent.Id);
        Assert.Equal("memory", Poll(started.Poll).BindingKind);
    }

    [Fact]
    public void Certification_is_single_use_and_cannot_be_attached_after_owner_confirmation()
    {
        var agent = NewAgent();
        using var hardware = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var started = Start();
        var cert = Certification(hardware, started.SignIn);
        _store.PollOperatorSignIn(started.Poll, certification: cert);
        Assert.Equal("device-certification-used", Assert.Throws<SecurityOperationException>(() =>
            _store.PollOperatorSignIn(started.Poll, certification: cert)).Code);
        var other = Start();
        Confirm(other.Code, agent.Id);
        Assert.Equal("device-certification-used", Assert.Throws<SecurityOperationException>(() =>
            _store.PollOperatorSignIn(other.Poll, certification: Certification(hardware, other.SignIn))).Code);
    }

    [Fact]
    public void A_session_is_issued_only_after_an_owner_confirms_with_a_fresh_second_factor()
    {
        var agent = NewAgent();
        var key = NewKey();
        var started = Start(key, ["cards.accept-override", "project.testing"]);
        Assert.Equal(8, started.Code.Length);
        Assert.All(started.Code, character => Assert.Contains(character, OperatorSignInCodes.Alphabet));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(10), started.SignIn.ExpiresAt);
        Assert.Equal(key.Thumbprint(), started.SignIn.KeyThumbprint);
        Assert.Equal(OperatorSignInPollStatuses.Pending, Poll(started.Poll).Status);

        var lookup = _store.LookupOperatorSignIn(started.Code, Owner(), "203.0.113.20");
        Assert.Equal(OperatorSignInCodes.Display(started.Code), lookup.UserCode);
        Assert.Equal(("agent@workstation", agent.Id, Terminal, false), (lookup.DeviceLabel, lookup.RequestedAgentId, lookup.RequestedAddress, lookup.AddressMatches));
        Assert.Empty(lookup.KnownForAgentIds);
        Assert.Equal(["cards.accept-override", "project.testing"], lookup.RequestedClasses.Select(entry => entry.Id));
        Assert.Equal(Enumerable.Range(1, 8), lookup.LifetimeHours);

        // Without a fresh step-up the confirmation answers step-up-required and changes nothing.
        var refused = Assert.Throws<SecurityOperationException>(() => _store.ConfirmOperatorSignIn(
            new ConfirmOperatorSignInRequest(started.Code, agent.Id, null, 2), Owner(), "203.0.113.20"));
        Assert.Equal((403, "step-up-required"), (refused.Status, refused.Code));
        Assert.Equal(OperatorSignInPollStatuses.Pending, Poll(started.Poll).Status);
        Assert.Empty(_store.ListOperatorAgents().Single().Sessions!);

        var confirmed = Confirm(started.Code, agent.Id, ["project.testing"], 2);
        var pending = _store.ListOperatorAgents().Single().Sessions!.Single();
        Assert.Equal(OperatorSessionStates.Pending, AccessSecurityStore.OperatorSessionState(pending, _clock.GetUtcNow().UtcDateTime));

        var issued = Poll(started.Poll);
        Assert.Equal(OperatorSignInPollStatuses.Session, issued.Status);
        Assert.StartsWith($"{AccessSecurityStore.OperatorSessionTokenPrefix}.{confirmed.SessionId}.", issued.Token);
        Assert.Equal(["project.testing"], issued.Classes);
        var principal = _store.AuthenticateServiceAccount(issued.Token);
        Assert.NotNull(principal);
        Assert.Equal($"operator-agent:{agent.Id}/session:{confirmed.SessionId}", principal!.ActorId);
        Assert.Equal($"human:{_ownerId}", principal.Operator!.DelegatedBy);

        var session = _store.ListOperatorAgents().Single().Sessions!.Single();
        Assert.Equal(confirmed.ExpiresAt, session.ExpiresAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddHours(2).AddSeconds(-5), session.ExpiresAt);
        Assert.Equal(("agent@workstation", key.Thumbprint(), Terminal, "203.0.113.20", Owner().Session.Id),
            (session.Device!.Label, session.Device.KeyThumbprint, session.Device.RequestedAddress, session.Device.ConfirmedAddress,
                session.Device.ConfirmingSessionId));
        var response = _store.ToOperatorAgentResponse(_store.ListOperatorAgents().Single());
        Assert.Equal(key.Thumbprint(), response.Sessions.Single().Device!.KeyThumbprint);
        Assert.Equal(key.Thumbprint(), Assert.Single(response.Devices!).KeyThumbprint);

        // The bearer is handed out once.
        var again = Poll(started.Poll);
        Assert.Equal(OperatorSignInPollStatuses.Expired, again.Status);
        Assert.Null(again.Token);
        var operations = AuditOperations();
        Assert.Contains("operator-signin.start", operations);
        Assert.Contains("operator-signin.confirm", operations);
        Assert.Contains("operator-signin.issue", operations);
    }

    [Fact]
    public void Expired_reused_and_unknown_codes_are_refused()
    {
        var agent = NewAgent();
        var unknown = Assert.Throws<SecurityOperationException>(() =>
            _store.LookupOperatorSignIn("BCDF-GHJK", Owner(), null));
        Assert.Equal((404, "signin-code-unknown"), (unknown.Status, unknown.Code));

        var used = Start();
        Confirm(used.Code, agent.Id);
        var reused = Assert.Throws<SecurityOperationException>(() => Confirm(used.Code, agent.Id));
        Assert.Equal((409, "signin-code-used"), (reused.Status, reused.Code));

        var late = Start();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var expired = Assert.Throws<SecurityOperationException>(() => Confirm(late.Code, agent.Id));
        Assert.Equal((410, "signin-code-expired"), (expired.Status, expired.Code));
        Assert.Equal(OperatorSignInPollStatuses.Expired, Poll(late.Poll).Status);
        // The confirmed sign-in whose bearer was never collected expired too, and its session ended with it.
        Assert.Equal(OperatorSignInPollStatuses.Expired, Poll(used.Poll).Status);
        Assert.All(_store.ListOperatorAgents().Single().Sessions!, session => Assert.NotNull(session.RevokedAt));
        Assert.Contains("operator-signin.expire", AuditOperations());

        var denied = Start();
        _store.DenyOperatorSignIn(denied.Code, Owner());
        Assert.Equal(OperatorSignInPollStatuses.Denied, Poll(denied.Poll).Status);
        Assert.Equal("signin-code-denied", Assert.Throws<SecurityOperationException>(() => Confirm(denied.Code, agent.Id)).Code);
        Assert.Contains("operator-signin.deny", AuditOperations());
        Assert.Equal("signin-not-found", Assert.Throws<SecurityOperationException>(() =>
            _store.PollOperatorSignIn(denied.Poll[..^4] + "AAAA")).Code);
    }

    [Fact]
    public void The_sixth_wrong_entry_within_fifteen_minutes_locks_code_entry_for_fifteen_minutes()
    {
        var agent = NewAgent();
        var started = Start();
        var wrong = started.Code[0] == 'B' ? "C" + started.Code[1..] : "B" + started.Code[1..];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("signin-code-unknown", Assert.Throws<SecurityOperationException>(() =>
                _store.LookupOperatorSignIn(wrong, Owner(), null)).Code);
        }
        var sixth = Assert.Throws<SecurityOperationException>(() => _store.LookupOperatorSignIn(wrong, Owner(), null));
        Assert.Equal((429, "signin-locked"), (sixth.Status, sixth.Code));
        // While locked, even the right code is refused, in lookup and confirm.
        Assert.Equal("signin-locked", Assert.Throws<SecurityOperationException>(() =>
            _store.LookupOperatorSignIn(started.Code, Owner(), null)).Code);
        Assert.Equal("signin-locked", Assert.Throws<SecurityOperationException>(() => Confirm(started.Code, agent.Id)).Code);

        _clock.Advance(AccessSecurityStore.OperatorSignInLockout);
        var fresh = Start(address: "198.51.100.8");
        Assert.Equal(fresh.SignIn.Id, _store.LookupOperatorSignIn(fresh.Code, Owner(), null).SignInId);
    }

    [Fact]
    public void Wrong_entries_spread_over_more_than_fifteen_minutes_do_not_lock()
    {
        NewAgent();
        var started = Start();
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            _clock.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal("signin-code-unknown", Assert.Throws<SecurityOperationException>(() =>
                _store.LookupOperatorSignIn("BBBB-BBBB", Owner(), null)).Code);
        }
        Assert.NotEqual("signin-locked", Assert.Throws<SecurityOperationException>(() =>
            _store.LookupOperatorSignIn(started.Code, Owner(), null)).Code);
    }

    [Fact]
    public void Only_an_active_owner_confirms_and_classes_are_removable_but_not_addable()
    {
        var agent = NewAgent();
        var admin = Person("workspace.admin", IamRoles.Admin);
        var started = Start(classes: ["cards.archive-override"]);
        var notOwner = Assert.Throws<SecurityOperationException>(() =>
            _store.LookupOperatorSignIn(started.Code, Owner(admin.Session), null));
        Assert.Equal((403, "owner-required"), (notOwner.Status, notOwner.Code));

        var added = Assert.Throws<SecurityOperationException>(() =>
            Confirm(started.Code, agent.Id, ["cards.archive-override", "project.testing"]));
        Assert.Equal("action-class-not-requested", added.Code);
        Assert.Empty(Confirm(started.Code, agent.Id, []).Classes);

        Assert.Equal("action-class-unknown", Assert.Throws<SecurityOperationException>(() => Start(classes: ["access.manage"])).Code);
        Assert.Equal("device-key-invalid", Assert.Throws<SecurityOperationException>(() =>
            Start(new OperatorDevicePublicKey("EC", "P-384", "AA", "BB"))).Code);
        var offCurve = NewKey() with { Y = OperatorDevicePublicKey.Base64Url(new byte[32]) };
        Assert.Equal("device-key-invalid", Assert.Throws<SecurityOperationException>(() => Start(offCurve)).Code);
    }

    [Fact]
    public void Polling_faster_than_the_interval_answers_slow_down_and_starts_are_throttled_per_address()
    {
        NewAgent();
        var started = Start();
        Assert.Equal(OperatorSignInPollStatuses.Pending, _store.PollOperatorSignIn(started.Poll).Status);
        var fast = _store.PollOperatorSignIn(started.Poll);
        Assert.Equal((OperatorSignInPollStatuses.SlowDown, 10), (fast.Status, fast.Interval));
        _clock.Advance(TimeSpan.FromSeconds(10));
        var slower = _store.PollOperatorSignIn(started.Poll);
        Assert.Equal((OperatorSignInPollStatuses.Pending, 10), (slower.Status, slower.Interval));

        for (var start = 1; start <= 10; start++) Start(address: "192.0.2.9");
        Assert.Equal("signin-throttled", Assert.Throws<SecurityOperationException>(() => Start(address: "192.0.2.9")).Code);
        Start(address: "192.0.2.10");
    }

    [Fact]
    public void Revoking_the_confirming_browser_session_a_password_reset_or_disable_ends_the_sessions_it_confirmed()
    {
        var agent = NewAgent();
        var other = SecondBrowser("owner-laptop");
        var secondOwner = Person("second.owner", IamRoles.Owner);

        string Issue(string? session)
        {
            var started = Start();
            Confirm(started.Code, agent.Id, session: session);
            return Poll(started.Poll).Token!;
        }
        var fromLaptop = Issue(other);
        var fromMain = Issue(null);
        var settings = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId).Secret;

        // A sign-out keeps the sessions it confirmed; revoking the browser session from a session list ends them.
        _store.RevokeSession(Owner(), Owner(other).Session.Id);
        Assert.Null(_store.AuthenticateServiceAccount(fromLaptop));
        Assert.NotNull(_store.AuthenticateServiceAccount(fromMain));
        Assert.NotNull(_store.AuthenticateServiceAccount(settings));

        _store.ResetPassword(_ownerId, new PasswordResetRequest("another correct horse phrase!"), secondOwner.Id);
        Assert.Null(_store.AuthenticateServiceAccount(fromMain));
        Assert.Contains(_store.ControlPlaneAudit.Read(0, 1000), entry => entry.Operation == "operator-session.revoke");
    }

    [Fact]
    public void Disabling_the_confirming_owner_and_a_sign_out_behave_as_the_dossier_states()
    {
        var agent = NewAgent();
        var secondOwner = Person("second.owner", IamRoles.Owner);
        var signOut = SecondBrowser("owner-phone");
        var started = Start();
        Confirm(started.Code, agent.Id, session: signOut);
        var token = Poll(started.Poll).Token!;

        _store.Logout(Owner(signOut).Session);
        Assert.NotNull(_store.AuthenticateServiceAccount(token));

        _store.UpdateUser(_ownerId, new UpdateUserRequest(null, null, null, true), secondOwner.Id);
        Assert.Null(_store.AuthenticateServiceAccount(token));
        Assert.NotNull(_store.ListOperatorAgents().Single().Sessions!.Single().RevokedAt);
    }

    [Fact]
    public void A_known_device_is_shown_as_known_until_the_owner_forgets_it()
    {
        var agent = NewAgent();
        var key = NewKey();
        var first = Start(key);
        Confirm(first.Code, agent.Id);
        var token = Poll(first.Poll).Token!;

        var second = Start(key);
        Assert.Equal([agent.Id], _store.LookupOperatorSignIn(second.Code, Owner(), null).KnownForAgentIds);
        var device = Assert.Single(_store.ToOperatorAgentResponse(_store.ListOperatorAgents().Single()).Devices!);

        _store.ForgetOperatorDevice(agent.Id, device.Id, _ownerId);
        Assert.Null(_store.AuthenticateServiceAccount(token));
        Assert.Empty(_store.LookupOperatorSignIn(second.Code, Owner(), null).KnownForAgentIds);
        Assert.Empty(_store.ToOperatorAgentResponse(_store.ListOperatorAgents().Single()).Devices!);
        Assert.Contains("operator-device.forget", AuditOperations());
    }
}
