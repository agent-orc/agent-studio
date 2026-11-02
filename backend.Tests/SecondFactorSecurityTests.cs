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
            var totp = store.Login("owner", password, "client", SecondFactorCodes.CurrentTotp(secret, TestUtcNow),
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
            var code = SecondFactorCodes.CurrentTotp(secret, TestUtcNow);
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
            store.VerifyStepUp(owner, SecondFactorCodes.CurrentTotp(secret, TestUtcNow));
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
            var bootstrap = store.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
            var principal = store.AuthenticateSession(bootstrap.SessionToken)!;
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
            var challenge = store.BeginPasskeyAssertion("owner", "client");
            var clientData = ClientData("webauthn.get", challenge, origin);
            var authData = AssertionData(rpId, 1);
            var signed = authData.Concat(SHA256.HashData(WebAuthnProofs.Decode(clientData))).ToArray();
            var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var proof = new PasskeyAssertionProof(idEncoded, clientData, WebAuthnProofs.Encode(authData), WebAuthnProofs.Encode(signature));
            var login = store.Login("owner", password, "client", requireSecondFactor: true, passkey: proof, origin: origin, rpId: rpId);
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
                () => store.Login("owner", password, "client", requireSecondFactor: true, passkey: proof, origin: origin, rpId: rpId)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AccessSecurityStore NewStore(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = root,
            ["Security:Profile"] = SecurityProfiles.Networked
        }).Build();
        return new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance,
            new FakeTimeProvider(new DateTimeOffset(TestUtcNow)));
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
