using AgentStudio.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SecondFactorSecurityTests
{
    private static readonly DateTime TestUtcNow = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(IamRoles.Owner, true)]
    [InlineData(IamRoles.Admin, true)]
    [InlineData(IamRoles.Developer, true)]
    [InlineData(IamRoles.Approver, true)]
    [InlineData(IamRoles.Contributor, false)]
    [InlineData(IamRoles.Viewer, true)]
    public void Human_roles_enroll_when_permissions_require_it_and_registered_totp_guards_every_login(
        string role, bool enrollmentRequired)
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var owner = store.Bootstrap(new BootstrapRequest("owner", "proper pass phrase!", "Owner"));
            var scope = role switch
            {
                IamRoles.Owner or IamRoles.Admin => IamScope.Workspace,
                IamRoles.Approver => IamScope.ForEnvironment("alpha", "production"),
                _ => IamScope.ForProject("alpha")
            };
            var username = "user." + role;
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var invitation = store.InviteUser(new InviteUserRequest(username, role, "", null,
                [new IamGrantRequest(role, scope)]), owner.User.Id);
            var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, password), publicEntry: true);

            Assert.Equal(enrollmentRequired,
                store.AuthenticateSession(accepted.SessionToken)!.Session.SecondFactorEnrollmentOnly);
            var beforeEnrollment = store.Login(username, password, username + "-client", requireSecondFactor: true);
            Assert.Equal(enrollmentRequired,
                store.AuthenticateSession(beforeEnrollment.SessionToken)!.Session.SecondFactorEnrollmentOnly);

            var principal = store.AuthenticateSession(accepted.SessionToken)!;
            var secret = store.BeginTotpEnrollment(principal);
            store.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(secret, TestUtcNow));

            Assert.Equal("second-factor-required", Assert.Throws<SecurityOperationException>(() =>
                store.Login(username, password, username + "-missing", requireSecondFactor: true)).Code);
            Assert.Equal("second-factor-required", Assert.Throws<SecurityOperationException>(() =>
                store.Login(username, password, username + "-store-call")).Code);
            var wrong = Assert.Throws<SecurityOperationException>(() =>
                store.Login(username, password, username + "-wrong", "000000", requireSecondFactor: true));
            Assert.Equal("invalid-second-factor", wrong.Code);
            Assert.Equal("The second factor is invalid.", wrong.Message);
            var signedIn = store.Login(username, password, username + "-valid",
                SecondFactorCodes.CurrentTotp(secret, TestUtcNow), requireSecondFactor: true);
            Assert.False(store.AuthenticateSession(signedIn.SessionToken)!.Session.SecondFactorEnrollmentOnly);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Group_grant_requires_enrollment_even_when_the_legacy_summary_is_contributor()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var owner = store.Bootstrap(new BootstrapRequest("owner", "proper pass phrase!", "Owner"));
            var group = store.CreateGroup(new CreateGroupRequest("Operators", null, null), owner.User.Id);
            store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Developer,
                IamScope.ForProject("alpha")), owner.User.Id);
            var invitation = store.InviteUser(new InviteUserRequest("group.product", "Group product", "", null,
                [new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("alpha"))]), owner.User.Id);
            store.UpdateGroup(group.Id, new UpdateGroupRequest(null, null, [invitation.User.Id]), owner.User.Id);

            var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, "safe group pass phrase!"),
                publicEntry: true);

            Assert.Equal(StudioRoles.Contributor, accepted.User.Role);
            Assert.True(store.AuthenticateSession(accepted.SessionToken)!.Session.SecondFactorEnrollmentOnly);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Owner_password_alone_cannot_create_a_networked_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
                ["Security:Profile"] = SecurityProfiles.Networked
            }).Build();
            var store = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));

            var login = store.Login("owner", password, "client", requireSecondFactor: true);
            Assert.True(store.AuthenticateSession(login.SessionToken)!.Session.SecondFactorEnrollmentOnly);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Totp_and_one_use_recovery_codes_guard_privileged_login_and_step_up()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var bootstrap = store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
            var principal = store.AuthenticateSession(bootstrap.SessionToken)!;
            var secret = store.BeginTotpEnrollment(principal);
            var recovery = store.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(secret, TestUtcNow));

            Assert.Equal("second-factor-required", Assert.Throws<SecurityOperationException>(
                () => store.Login("owner", password, "client", requireSecondFactor: true)).Code);
            var totp = store.Login("owner", password, "client", SecondFactorCodes.CurrentTotp(secret, TestUtcNow.AddSeconds(30)),
                requireSecondFactor: true);
            Assert.False(store.AuthenticateSession(totp.SessionToken)!.Session.SecondFactorEnrollmentOnly);
            Assert.Equal("second-factor-required", Assert.Throws<SecurityOperationException>(
                () => store.VerifyStepUp(store.AuthenticateSession(totp.SessionToken)!, null)).Code);
            store.VerifyStepUp(store.AuthenticateSession(totp.SessionToken)!, recovery[0]);
            Assert.Equal("invalid-second-factor", Assert.Throws<SecurityOperationException>(
                () => store.VerifyStepUp(store.AuthenticateSession(totp.SessionToken)!, recovery[0])).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Totp_step_is_one_use_across_sign_in_and_step_up_and_survives_reload()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(TestUtcNow));
            var store = NewStore(root, clock);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var bootstrap = store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
            var owner = store.AuthenticateSession(bootstrap.SessionToken)!;
            var secret = store.BeginTotpEnrollment(owner);
            store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(secret, TestUtcNow));

            clock.Advance(TimeSpan.FromSeconds(30));
            var code = SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime);
            var login = store.Login("owner", password, "client", code, requireSecondFactor: true);
            store = NewStore(root, clock);
            var replay = Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", password, "client", code, requireSecondFactor: true));
            Assert.Equal((401, "invalid-second-factor"), (replay.Status, replay.Code));
            Assert.Equal(1, store.LoginStateCounts.Clients);
            Assert.Equal("invalid-second-factor", Assert.Throws<SecurityOperationException>(() =>
                store.VerifyStepUp(store.AuthenticateSession(login.SessionToken)!, code)).Code);

            clock.Advance(TimeSpan.FromSeconds(30));
            store.VerifyStepUp(store.AuthenticateSession(login.SessionToken)!,
                SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
            Assert.Equal("invalid-second-factor", Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", password, "client", code, requireSecondFactor: true)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Revoke_all_other_sessions_invalidates_them_immediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var bootstrap = store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
            var principal = store.AuthenticateSession(bootstrap.SessionToken)!;
            var secret = store.BeginTotpEnrollment(principal);
            store.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(secret, TestUtcNow));
            var code = SecondFactorCodes.CurrentTotp(secret, TestUtcNow.AddSeconds(30));
            var other = store.Login("owner", password, "client", code, requireSecondFactor: true,
                device: "Test browser", address: "192.0.2.1");
            Assert.Contains(store.ListSessions(principal), x => x.Device == "Test browser" && x.Address == "192.0.2.1");
            store.RevokeOtherSessions(principal);
            Assert.Null(store.AuthenticateSession(other.SessionToken));
            Assert.NotNull(store.AuthenticateSession(bootstrap.SessionToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Owner_session_administration_requires_a_fresh_second_factor_for_each_action()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var bootstrap = store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
            var owner = store.AuthenticateSession(bootstrap.SessionToken)!;
            var secret = store.BeginTotpEnrollment(owner);
            store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(secret, TestUtcNow));
            Assert.Equal("step-up-required", Assert.Throws<SecurityOperationException>(
                () => store.RevokeOtherSessions(owner, owner.User.Id)).Code);
            store.VerifyStepUp(owner, SecondFactorCodes.CurrentTotp(secret, TestUtcNow.AddSeconds(30)));
            store.RevokeOtherSessions(owner, owner.User.Id);
            Assert.Equal("step-up-required", Assert.Throws<SecurityOperationException>(
                () => store.RevokeOtherSessions(owner, owner.User.Id)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Every_step_up_requires_a_registered_factor_even_for_a_viewer()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var owner = store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
            var created = store.CreateUser(new CreateUserRequest("viewer", "Viewer", StudioRoles.Viewer, [], null), owner.User.Id);
            var viewer = store.Login("viewer", created.TemporaryPassword, "viewer-client", requireSecondFactor: true);
            Assert.Equal("second-factor-required", Assert.Throws<SecurityOperationException>(
                () => store.VerifyStepUp(store.AuthenticateSession(viewer.SessionToken)!, null)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Passkey_assertion_checks_challenge_signature_and_counter()
    {
        var root = Path.Combine(Path.GetTempPath(), "second-factor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var password = "long pass phrase " + Guid.NewGuid().ToString("N");
            var bootstrap = store.Bootstrap(new BootstrapRequest("owner", "proper pass phrase!", "Owner"));
            var invitation = store.InviteUser(new InviteUserRequest("viewer", "Viewer", "", null,
                [new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))]), bootstrap.User.Id);
            var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, password), publicEntry: true);
            var principal = store.AuthenticateSession(accepted.SessionToken)!;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            const string rpId = "studio.invalid";
            const string origin = "https://studio.invalid";
            var id = RandomNumberGenerator.GetBytes(20);
            var idEncoded = WebAuthnProofs.Encode(id);
            var registerChallenge = store.BeginPasskeyRegistration(principal);
            var registrationData = RegistrationData(key.ExportParameters(false), id, rpId);
            var registration = new PasskeyRegistrationProof(idEncoded,
                ClientData("webauthn.create", registerChallenge, origin), WebAuthnProofs.Encode(registrationData), "Test passkey");
            store.RegisterPasskey(principal, registration, origin, rpId);
            Assert.Equal("second-factor-required", Assert.Throws<SecurityOperationException>(() =>
                store.Login("viewer", password, "missing-passkey", requireSecondFactor: true)).Code);
            var challenge = store.BeginPasskeyAssertion("viewer", "client");
            var clientData = ClientData("webauthn.get", challenge, origin);
            var authData = AssertionData(rpId, 1);
            var signed = authData.Concat(SHA256.HashData(WebAuthnProofs.Decode(clientData))).ToArray();
            var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var proof = new PasskeyAssertionProof(idEncoded, clientData, WebAuthnProofs.Encode(authData), WebAuthnProofs.Encode(signature));
            var login = store.Login("viewer", password, "client", requireSecondFactor: true, passkey: proof, origin: origin, rpId: rpId);
            Assert.False(store.AuthenticateSession(login.SessionToken)!.Session.SecondFactorEnrollmentOnly);
            var stepChallenge = store.BeginPasskeyStepUp(store.AuthenticateSession(login.SessionToken)!);
            var stepClientData = ClientData("webauthn.get", stepChallenge, origin);
            var stepAuthenticatorData = AssertionData(rpId, 2);
            var stepSigned = stepAuthenticatorData.Concat(SHA256.HashData(WebAuthnProofs.Decode(stepClientData))).ToArray();
            var stepSignature = key.SignData(stepSigned, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            store.VerifyPasskeyStepUp(store.AuthenticateSession(login.SessionToken)!,
                new PasskeyAssertionProof(idEncoded, stepClientData, WebAuthnProofs.Encode(stepAuthenticatorData),
                    WebAuthnProofs.Encode(stepSignature)), origin, rpId);
            store.RevokeOtherSessions(store.AuthenticateSession(login.SessionToken)!, login.User.Id);
            Assert.Equal("step-up-required", Assert.Throws<SecurityOperationException>(
                () => store.RevokeOtherSessions(store.AuthenticateSession(login.SessionToken)!, login.User.Id)).Code);
            Assert.Equal("passkey-challenge-expired", Assert.Throws<SecurityOperationException>(
                () => store.Login("viewer", password, "client", requireSecondFactor: true, passkey: proof, origin: origin, rpId: rpId)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AccessSecurityStore NewStore(string root, TimeProvider? clock = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = root,
            ["Security:Profile"] = SecurityProfiles.Networked
        }).Build();
        return new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance,
            clock ?? new FakeTimeProvider(new DateTimeOffset(TestUtcNow)));
    }

    private static string ClientData(string type, string challenge, string origin)
        => WebAuthnProofs.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin })));

    private static byte[] AssertionData(string rpId, uint count)
    {
        var data = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId)).CopyTo(data, 0);
        data[32] = 0x05;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), count);
        return data;
    }

    private static byte[] RegistrationData(ECParameters key, byte[] id, string rpId)
    {
        var data = AssertionData(rpId, 0);
        data[32] |= 0x40;
        var cose = new byte[] { 0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20 }
            .Concat(key.Q.X!).Concat(new byte[] { 0x22, 0x58, 0x20 }).Concat(key.Q.Y!).ToArray();
        var result = new byte[55 + id.Length + cose.Length];
        data.CopyTo(result, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(53), (ushort)id.Length);
        id.CopyTo(result, 55);
        cose.CopyTo(result, 55 + id.Length);
        return result;
    }
}
