using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class OperatorDpopEndpointTests
{
    [Fact]
    public async Task Renewal_endpoint_rotates_the_key_without_touching_idle_and_the_previous_key_is_refused()
    {
        var (factory, store, ownerToken, agentId, token, key, clock) = Setup();
        using var keyLifetime = key;
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = next.ExportParameters(false).Q;
        var newKey = new OperatorDevicePublicKey("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!), OperatorDevicePublicKey.Base64Url(point.Y!));
        var session = store.ListOperatorAgents().Single().Sessions!.Single();
        var issuedAt = clock.GetUtcNow().ToUnixTimeSeconds();
        var signature = OperatorDevicePublicKey.Base64Url(key.SignData(Encoding.UTF8.GetBytes(
            AccessSecurityStore.OperatorRenewalStatement(session.Id, newKey.Thumbprint(), issuedAt)), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        using var renew = new HttpRequestMessage(HttpMethod.Post, OperatorSessionEndpoints.RenewPath)
        { Content = JsonContent.Create(new OperatorSessionRenewalRequest(session.Id, newKey, issuedAt, signature)) };
        renew.Headers.Add("Authorization", "Bearer " + token);
        renew.Headers.Add("DPoP", OperatorDpopTests.Proof(key, token, "POST", "https://studio.test" + OperatorSessionEndpoints.RenewPath, clock.GetUtcNow()));
        using var renewed = await client.SendAsync(renew);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Null((await renewed.Content.ReadFromJsonAsync<OperatorSessionResponse>())!.LastUsedAt);
        foreach (var (signer, expected) in new[] { (key, HttpStatusCode.Unauthorized), (next, HttpStatusCode.OK) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operator/decisions");
            request.Headers.Add("Authorization", "Bearer " + token);
            request.Headers.Add("DPoP", OperatorDpopTests.Proof(signer, token, "GET", "https://studio.test/api/operator/decisions", clock.GetUtcNow()));
            using var answer = await client.SendAsync(request);
            Assert.Equal(expected, answer.StatusCode);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Session_limits_are_a_hard_refusal_for_operator_and_other_tokens(string method)
    {
        var (factory, store, ownerToken, agentId, token, key, clock) = Setup();
        using var keyLifetime = key;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        const string path = "/api/auth/iam/session-limits";
        var ownerId = store.AuthenticateSession(ownerToken)!.User.Id;
        var legacy = store.StartOperatorSession(agentId, new("Settings", 1), ownerId);
        var api = store.CreateApiToken(new CreateApiTokenRequest("tool", StudioRoles.Operator, ["*"], clock.GetUtcNow().UtcDateTime.AddDays(1)), ownerId).Secret;
        var account = store.CreateServiceAccount(new("admin automation", null, [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), ownerId);
        var service = store.CreateServiceAccountToken(account.Id, new("tool", null), ownerId).Secret;
        foreach (var bearer in new[] { token, legacy.Secret, api, service })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new OperatorSessionLimits()) };
            request.Headers.Add("Authorization", "Bearer " + bearer);
            if (bearer == token) request.Headers.Add("DPoP", OperatorDpopTests.Proof(key, token, method, "https://studio.test" + path, clock.GetUtcNow()));
            using var answer = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Empty(store.ListOperatorApprovals());
        }
        Assert.Equal(new OperatorSessionLimits(), store.GetOperatorSessionLimits());
    }
    [Fact]
    public async Task Owner_changes_session_limits_only_after_a_fresh_step_up_through_the_http_route()
    {
        var (factory, store, ownerToken, agentId, token, key, clock) = Setup();
        using var keyLifetime = key;
        var human = store.AuthenticateSession(ownerToken)!;
        clock.Advance(TimeSpan.FromSeconds(30));
        var login = store.Login("first.owner", "correct horse battery staple!", "limits browser",
            SecondFactorCodes.CurrentTotp(human.User.TotpSecret!, clock.GetUtcNow().UtcDateTime));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        async Task<HttpResponseMessage> Put()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/auth/iam/session-limits")
                { Content = JsonContent.Create(new OperatorSessionLimits(24, 168, 24, 12)) };
            request.Headers.Add("Cookie", AccessSecurityStore.SessionCookieName + "=" + login.SessionToken);
            request.Headers.Add("Origin", "https://studio.test");
            request.Headers.Add("X-CSRF-Token", login.CsrfToken);
            return await client.SendAsync(request);
        }
        using (var refused = await Put())
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("step-up-required", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        clock.Advance(TimeSpan.FromSeconds(30));
        store.VerifyStepUp(store.AuthenticateSession(login.SessionToken)!, SecondFactorCodes.CurrentTotp(human.User.TotpSecret!, clock.GetUtcNow().UtcDateTime));
        using (var accepted = await Put()) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var reused = await Put()) Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
        Assert.Equal(12, store.GetOperatorSessionLimits().IdleHours);
    }

}
