using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ES-3 through the real networked pipeline: the start and poll routes are
/// anonymous and bounded, the Owner routes need the browser session with its
/// CSRF token, and a confirmation without a fresh step-up answers
/// step-up-required.
/// </summary>
[TestCaseOrderer(SharedHostTestOrder.TypeName, SharedHostTestOrder.AssemblyName)]
public sealed class OperatorSignInEndpointTests : IClassFixture<NetworkedSecurityEndpointTests.PrivateEdgeHost>
{
    private const string Studio = "https://studio.test";
    private readonly NetworkedSecurityEndpointTests.PrivateEdgeHost _host;

    public OperatorSignInEndpointTests(NetworkedSecurityEndpointTests.PrivateEdgeHost host)
    {
        _host = host;
        host.Reset();
    }

    private static object PublicKey(ECDsa key)
    {
        var point = key.ExportParameters(false).Q;
        return new { kty = "EC", crv = "P-256", x = OperatorDevicePublicKey.Base64Url(point.X!), y = OperatorDevicePublicKey.Base64Url(point.Y!) };
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body,
        string? session = null, string? csrf = null, string? proof = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            request.Headers.Add("Origin", Studio);
        }
        if (proof is not null) request.Headers.Add("DPoP", proof);
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_terminal_signs_in_anonymously_and_the_owner_confirms_only_with_a_fresh_step_up()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        var factory = _host.Ready("198.51.100.7", clock);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri(Studio), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var principal = store.AuthenticateSession(owner.SessionToken)!;
        var secret = store.BeginTotpEnrollment(principal);
        store.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null), owner.User.Id);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var started = await SendAsync(client, OperatorSignInEndpoints.StartPath,
            new { publicKey = PublicKey(key), deviceLabel = "agent@workstation", operatorAgent = "ops console", classes = new[] { "project.testing" } });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Contains("no-store", started.Headers.CacheControl!.ToString());
        var start = await Json(started);
        var code = start.GetProperty("userCode").GetString()!;
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$", code);
        Assert.Equal($"{Studio}/#/terminal-sign-in?code={code.Replace("-", "")}", start.GetProperty("verificationUriComplete").GetString());
        Assert.Equal(5, start.GetProperty("interval").GetInt32());
        using var hardware = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedAt = clock.GetUtcNow().ToUnixTimeSeconds();
        var statement = Encoding.UTF8.GetBytes(AccessSecurityStore.DeviceCertificationStatement(
            start.GetProperty("keyThumbprint").GetString()!, start.GetProperty("signInId").GetString()!, issuedAt));
        var certification = new { publicKey = PublicKey(hardware), bindingKind = "tpm", issuedAt,
            signature = OperatorDevicePublicKey.Base64Url(hardware.SignData(statement, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
        var poll = new { pollHandle = start.GetProperty("pollHandle").GetString() };

        Task<HttpResponseMessage> Poll() => SendAsync(client, OperatorSignInEndpoints.PollPath, poll,
            proof: OperatorDpopTests.Proof(key, poll.pollHandle!, "POST", Studio + OperatorSignInEndpoints.PollPath, clock.GetUtcNow()));

        using (var missingProof = await SendAsync(client, OperatorSignInEndpoints.PollPath, poll))
            Assert.Equal("dpop-required", (await Json(missingProof)).GetProperty("error").GetString());
        using (var certify = await SendAsync(client, OperatorSignInEndpoints.PollPath, new { poll.pollHandle, certification },
                   proof: OperatorDpopTests.Proof(key, poll.pollHandle!, "POST", Studio + OperatorSignInEndpoints.PollPath, clock.GetUtcNow())))
        {
            Assert.Equal(HttpStatusCode.OK, certify.StatusCode);
            Assert.Equal("tpm", (await Json(certify)).GetProperty("bindingKind").GetString());
        }

        var confirm = new { code, operatorAgentId = agent.Id, lifetimeHours = 8 };
        using (var anonymous = await SendAsync(client, "/api/auth/iam/operator-sign-ins/confirm", confirm))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var noCsrf = await SendAsync(client, "/api/auth/iam/operator-sign-ins/confirm", confirm, owner.SessionToken))
            Assert.Equal("csrf-invalid", (await Json(noCsrf)).GetProperty("error").GetString());

        using (var lookup = await SendAsync(client, "/api/auth/iam/operator-sign-ins/lookup", new { code }, owner.SessionToken, owner.CsrfToken))
        {
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            var found = await Json(lookup);
            Assert.Equal(("198.51.100.7", true), (found.GetProperty("requestedAddress").GetString(), found.GetProperty("addressMatches").GetBoolean()));
            Assert.Equal(agent.Id, found.GetProperty("requestedAgentId").GetString());
            Assert.Equal("tpm", found.GetProperty("bindingKind").GetString());
        }

        using (var withoutStepUp = await SendAsync(client, "/api/auth/iam/operator-sign-ins/confirm", confirm, owner.SessionToken, owner.CsrfToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, withoutStepUp.StatusCode);
            Assert.Equal("step-up-required", (await Json(withoutStepUp)).GetProperty("error").GetString());
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        store.VerifyStepUp(store.AuthenticateSession(owner.SessionToken)!, SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        using (var confirmed = await SendAsync(client, "/api/auth/iam/operator-sign-ins/confirm", confirm, owner.SessionToken, owner.CsrfToken))
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var issued = await Poll();
        var session = await Json(issued);
        Assert.Equal("session", session.GetProperty("status").GetString());
        Assert.Equal("tpm", session.GetProperty("bindingKind").GetString());
        var token = session.GetProperty("token").GetString()!;
        Assert.StartsWith("oas.", token);
        Assert.NotNull(store.AuthenticateServiceAccount(token));
        var row = store.ToOperatorAgentResponse(store.ListOperatorAgents().Single()).Sessions.Single();
        Assert.Equal("tpm", row.Device!.BindingKind);
        Assert.Equal(("agent@workstation", "198.51.100.7"), (row.Device!.Label, row.Device.RequestedAddress));
    }

    [Fact]
    public async Task The_anonymous_routes_bound_their_body_and_refuse_unknown_handles()
    {
        var factory = _host.Ready("198.51.100.8", null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri(Studio), HandleCookies = false });

        using var large = new HttpRequestMessage(HttpMethod.Post, OperatorSignInEndpoints.StartPath)
        { Content = new StringContent("{\"deviceLabel\":\"" + new string('a', 5000) + "\"}", Encoding.UTF8, "application/json") };
        using (var tooLarge = await client.SendAsync(large))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        using (var noKey = await SendAsync(client, OperatorSignInEndpoints.StartPath, new { deviceLabel = "agent@workstation" }))
            Assert.Equal("device-key-required", (await Json(noKey)).GetProperty("error").GetString());

        using var unknown = await SendAsync(client, OperatorSignInEndpoints.PollPath,
            new { pollHandle = "osp." + new string('a', 32) + "." + new string('b', 43) });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("signin-not-found", (await Json(unknown)).GetProperty("error").GetString());
    }
}
