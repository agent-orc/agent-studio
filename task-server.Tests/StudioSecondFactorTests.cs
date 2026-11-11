using AgentStudio.Security;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioSecondFactorTests
{
    [Fact]
    public async Task Owner_session_administration_consumes_a_second_factor_step_up()
    {
        using var temp = new TempDirectory();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var password = "owner pass phrase " + Guid.NewGuid().ToString("N");
        var bootstrap = await store.BootstrapStudioAuthAsync(new StudioBootstrapRequest("owner", password), false, default);
        var secret = await store.BeginStudioTotpAsync(bootstrap.SessionToken, default);
        await store.ConfirmStudioTotpAsync(bootstrap.SessionToken, SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime), default);
        clock.Now = clock.Now.AddSeconds(30);
        var ownerId = bootstrap.Status.User!.UserId;
        Assert.Equal("step-up-required", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.RevokeStudioSessionsAsync(bootstrap.SessionToken, ownerId, null, default))).Code);
        await store.VerifyStudioStepUpAsync(bootstrap.SessionToken, SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime), default);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal("step-up-required", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.RevokeStudioSessionsAsync(bootstrap.SessionToken, ownerId, null, default))).Code);
        await store.VerifyStudioStepUpAsync(bootstrap.SessionToken, SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime), default);
        await store.RevokeStudioSessionsAsync(bootstrap.SessionToken, ownerId, null, default);
        Assert.Equal("step-up-required", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.RevokeStudioSessionsAsync(bootstrap.SessionToken, ownerId, null, default))).Code);
    }

    [Fact]
    public async Task Passkey_registration_login_and_step_up_verify_signed_challenges()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new MutableClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)));
        await store.InitializeAsync();
        var password = "owner pass phrase " + Guid.NewGuid().ToString("N");
        var bootstrap = await store.BootstrapStudioAuthAsync(new StudioBootstrapRequest("owner", password), false, default);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string rpId = "studio.invalid";
        const string origin = "https://studio.invalid";
        var id = RandomNumberGenerator.GetBytes(20);
        var credentialId = WebAuthnProofs.Encode(id);
        var challenge = await store.BeginStudioPasskeyRegistrationAsync(bootstrap.SessionToken, default);
        var registration = new PasskeyRegistrationProof(credentialId, ClientData("webauthn.create", challenge, origin),
            WebAuthnProofs.Encode(RegistrationData(key.ExportParameters(false), id, rpId)), "Test passkey");
        await store.RegisterStudioPasskeyAsync(bootstrap.SessionToken, registration, origin, rpId, default);

        var loginChallenge = store.BeginStudioPasskeyAssertion("owner", "client");
        var loginProof = Assertion(key, credentialId, loginChallenge, origin, rpId, 1);
        var login = await store.LoginStudioAsync(new StudioLoginRequest("owner", password, Passkey: loginProof),
            default, "client", passkey: loginProof, origin: origin, rpId: rpId);
        Assert.False(login.Status.SecondFactorEnrollmentRequired);
        var stepChallenge = await store.BeginStudioPasskeyStepUpAsync(login.SessionToken, default);
        await store.VerifyStudioPasskeyStepUpAsync(login.SessionToken,
            Assertion(key, credentialId, stepChallenge, origin, rpId, 2), origin, rpId, default);
        await store.RevokeStudioSessionsAsync(login.SessionToken, bootstrap.Status.User!.UserId, null, default);
        Assert.Equal("step-up-required", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.RevokeStudioSessionsAsync(login.SessionToken, bootstrap.Status.User!.UserId, null, default))).Code);
    }

    [Fact]
    public async Task Owner_login_requires_totp_or_one_use_recovery_and_sessions_expire()
    {
        using var temp = new TempDirectory();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var password = "owner pass phrase " + Guid.NewGuid().ToString("N");
        var bootstrap = await store.BootstrapStudioAuthAsync(new StudioBootstrapRequest("owner", password), false, default);
        Assert.True(bootstrap.Status.SecondFactorEnrollmentRequired);
        var secret = await store.BeginStudioTotpAsync(bootstrap.SessionToken, default);
        var codes = await store.ConfirmStudioTotpAsync(bootstrap.SessionToken, SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime), default);
        clock.Now = clock.Now.AddSeconds(30);

        var missing = await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.LoginStudioAsync(new StudioLoginRequest("owner", password), default, "client"));
        Assert.Equal("second-factor-required", missing.Code);
        var login = await store.LoginStudioAsync(new StudioLoginRequest("owner", password,
            SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime)), default, "client", "Test device");
        Assert.False(login.Status.SecondFactorEnrollmentRequired);
        Assert.Equal(2, (await store.ListStudioSessionsAsync(bootstrap.SessionToken, null, default)).Count);
        var sessions = await store.ListStudioSessionsAsync(bootstrap.SessionToken, null, default);
        var other = Assert.Single(sessions, session => !session.Current);
        await store.RevokeStudioSessionsAsync(bootstrap.SessionToken, null, other.SessionId, default);
        Assert.False((await store.GetStudioAuthStatusAsync(login.SessionToken, default)).Authenticated);

        var recoveryLogin = await store.LoginStudioAsync(new StudioLoginRequest("owner", password, codes[0]), default, "client");
        Assert.True(recoveryLogin.Status.Authenticated);
        Assert.Equal("invalid-second-factor", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.LoginStudioAsync(new StudioLoginRequest("owner", password, codes[0]), default, "client"))).Code);
        await store.RevokeStudioSessionsAsync(bootstrap.SessionToken, null, null, default);
        Assert.False((await store.GetStudioAuthStatusAsync(recoveryLogin.SessionToken, default)).Authenticated);
        clock.Now = clock.Now.AddSeconds(30);
        var refreshed = await store.LoginStudioAsync(new StudioLoginRequest("owner", password,
            SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime)), default, "client");
        clock.Now = clock.Now.AddHours(8).AddSeconds(1);
        Assert.False((await store.GetStudioAuthStatusAsync(refreshed.SessionToken, default)).Authenticated);

        var longLived = await store.LoginStudioAsync(new StudioLoginRequest("owner", password,
            SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime)), default, "client");
        for (var dayPart = 0; dayPart < 23; dayPart++)
        {
            clock.Now = clock.Now.AddHours(7);
            Assert.True((await store.GetStudioAuthStatusAsync(longLived.SessionToken, default)).Authenticated);
        }
        clock.Now = clock.Now.AddHours(7).AddSeconds(1);
        Assert.False((await store.GetStudioAuthStatusAsync(longLived.SessionToken, default)).Authenticated);
    }

    [Fact]
    public async Task Totp_step_is_one_use_for_login_and_step_up_after_store_reopens()
    {
        using var temp = new TempDirectory();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        var password = "owner pass phrase " + Guid.NewGuid().ToString("N");
        var bootstrap = await store.BootstrapStudioAuthAsync(new StudioBootstrapRequest("owner", password), false, default);
        var secret = await store.BeginStudioTotpAsync(bootstrap.SessionToken, default);
        await store.ConfirmStudioTotpAsync(bootstrap.SessionToken,
            SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime), default);
        clock.Now = clock.Now.AddSeconds(30);
        var code = SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime);
        var login = await store.LoginStudioAsync(new StudioLoginRequest("owner", password, code), default, "client");

        store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        Assert.Equal("invalid-second-factor", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.LoginStudioAsync(new StudioLoginRequest("owner", password, code), default, "client"))).Code);
        Assert.Equal("invalid-second-factor", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.VerifyStudioStepUpAsync(login.SessionToken, code, default))).Code);

        clock.Now = clock.Now.AddSeconds(30);
        await store.VerifyStudioStepUpAsync(login.SessionToken,
            SecondFactorCodes.CurrentTotp(secret, clock.Now.UtcDateTime), default);
        Assert.Equal("invalid-second-factor", (await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
            store.LoginStudioAsync(new StudioLoginRequest("owner", password, code), default, "client"))).Code);
    }

    [Fact]
    public async Task Password_failures_lock_client_for_fifteen_minutes()
    {
        using var temp = new TempDirectory();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var password = "owner pass phrase " + Guid.NewGuid().ToString("N");
        await store.BootstrapStudioAuthAsync(new StudioBootstrapRequest("owner", password), false, default);
        for (var attempt = 0; attempt < 10; attempt++)
            await Assert.ThrowsAsync<StudioAuthenticationException>(() =>
                store.LoginStudioAsync(new StudioLoginRequest("owner", "wrong password"), default, "client"));
        await Assert.ThrowsAsync<StudioThrottledException>(() =>
            store.LoginStudioAsync(new StudioLoginRequest("owner", password), default, "client"));
        clock.Now = clock.Now.AddMinutes(15).AddSeconds(1);
        Assert.True((await store.LoginStudioAsync(new StudioLoginRequest("owner", password), default, "client"))
            .Status.SecondFactorEnrollmentRequired);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string ClientData(string type, string challenge, string origin)
        => WebAuthnProofs.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin })));

    private static PasskeyAssertionProof Assertion(ECDsa key, string credentialId, string challenge,
        string origin, string rpId, uint count)
    {
        var clientData = ClientData("webauthn.get", challenge, origin);
        var authenticatorData = AssertionData(rpId, count);
        var signed = authenticatorData.Concat(SHA256.HashData(WebAuthnProofs.Decode(clientData))).ToArray();
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new PasskeyAssertionProof(credentialId, clientData, WebAuthnProofs.Encode(authenticatorData),
            WebAuthnProofs.Encode(signature));
    }

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
