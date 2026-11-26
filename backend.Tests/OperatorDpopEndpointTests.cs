using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

[TestCaseOrderer(SharedHostTestOrder.TypeName, SharedHostTestOrder.AssemblyName)]
public sealed partial class OperatorDpopEndpointTests : IClassFixture<NetworkedSecurityEndpointTests.PrivateEdgeHost>
{
    private readonly NetworkedSecurityEndpointTests.PrivateEdgeHost _host;
    public OperatorDpopEndpointTests(NetworkedSecurityEndpointTests.PrivateEdgeHost host) { _host = host; host.Reset(); }

    [Fact]
    public async Task Copied_session_file_is_refused_on_every_mapped_route_before_handlers_and_cookie_fallback()
    {
        var (factory, store, ownerToken, agentId, token, key, clock) = Setup();
        using var keyLifetime = key;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        var root = Path.Combine(Path.GetTempPath(), "dpop-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = Path.Combine(root, "operator-session");
            File.WriteAllText(original, token);
            var copied = Path.Combine(root, "another-machine-session");
            File.Copy(original, copied);
            var stolen = File.ReadAllText(copied);
            var routes = factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => (Method: method, Path: Regex.Replace(endpoint.RoutePattern.RawText ?? "/", @"\{[^}]+\}", "probe"))))
                .Append((Method: "GET", Path: "/healthz")).Append((Method: "GET", Path: "/unmapped-route"))
                .Distinct().ToList();
            Assert.True(routes.Count > 100);
            foreach (var route in routes)
            {
                using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + stolen);
                request.Headers.Add("Cookie", AccessSecurityStore.SessionCookieName + "=" + ownerToken);
                request.Headers.Add("Origin", "https://studio.test");
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, route.Method + " " + route.Path);
                Assert.Equal("dpop-required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
            }
            if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { } results)
                File.WriteAllText(Path.Combine(results, "copied-session-routes.txt"),
                    $"Copied operator-session only, without signing agent: all {routes.Count} mapped method/route pairs plus health and unmatched route refused with 401 dpop-required. A valid Owner cookie was also presented.\n");
        }
        finally { Directory.Delete(root, true); }

        var refusals = store.ControlPlaneAudit.ReadNewest(entry => entry.Outcome == "401", 10);
        Assert.NotEmpty(refusals);
        Assert.All(refusals, entry => Assert.Equal(ControlPlaneAuditStreams.OperatorAgent, entry.Stream));

        async Task<HttpResponseMessage> Send(string? proof)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operator/decisions?limit=1");
            request.Headers.Add("Authorization", "Bearer " + token);
            if (proof is not null) request.Headers.Add("DPoP", proof);
            return await client.SendAsync(request);
        }
        var uri = "https://studio.test/api/operator/decisions";
        var proof = OperatorDpopTests.Proof(key, token, "GET", uri, clock.GetUtcNow());
        using (var accepted = await Send(proof)) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var replayed = await Send(proof)) Assert.Equal("dpop-replayed", (await replayed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        foreach (var invalid in new[] {
            OperatorDpopTests.Proof(key, token, "POST", uri, clock.GetUtcNow()),
            OperatorDpopTests.Proof(key, token, "GET", uri + "/elsewhere", clock.GetUtcNow()),
            OperatorDpopTests.Proof(key, token, "GET", uri, clock.GetUtcNow().AddSeconds(-61)) })
        {
            using var refused = await Send(invalid);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        var legacy = store.StartOperatorSession(agentId, new StartOperatorSessionRequest("Settings", 1), store.AuthenticateSession(ownerToken)!.User.Id);
        using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/operator/decisions");
        legacyRequest.Headers.Add("Authorization", "Bearer " + legacy.Secret);
        using var legacyAnswer = await client.SendAsync(legacyRequest);
        Assert.Equal(HttpStatusCode.OK, legacyAnswer.StatusCode);
        Assert.Null(store.ToOperatorSessionResponse(agentId, legacy.Session).Device);
    }
    private (WebApplicationFactory<Program> Factory, AccessSecurityStore Store, string OwnerToken, string AgentId,
        string Token, ECDsa Key, FakeTimeProvider Clock) Setup()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var factory = _host.Ready("198.51.100.7", clock);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var human = store.AuthenticateSession(owner.SessionToken)!;
        var totp = store.BeginTotpEnrollment(human);
        store.ConfirmTotpEnrollment(human, SecondFactorCodes.CurrentTotp(totp, clock.GetUtcNow().UtcDateTime));
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("bound console", null, [new IamGrantRequest(IamRoles.OperatorAgent, IamScope.Workspace)]), owner.User.Id);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        var start = store.StartOperatorSignIn(new StartOperatorSignInRequest(new OperatorDevicePublicKey("EC", "P-256",
            OperatorDevicePublicKey.Base64Url(point.X!), OperatorDevicePublicKey.Base64Url(point.Y!)), "agent@test", agent.Id, []), "198.51.100.7", "test");
        clock.Advance(TimeSpan.FromSeconds(30));
        store.VerifyStepUp(human, SecondFactorCodes.CurrentTotp(totp, clock.GetUtcNow().UtcDateTime));
        store.ConfirmOperatorSignIn(new ConfirmOperatorSignInRequest(start.UserCode, agent.Id, [], 8), human, "198.51.100.7");
        var token = store.PollOperatorSignIn(start.PollHandle).Token!;

        return (factory, store, owner.SessionToken, agent.Id, token, key, clock);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Median_added_server_time_over_one_thousand_requests_is_below_two_milliseconds()
    {
        var (factory, store, ownerToken, agentId, token, key, clock) = Setup();
        using var keyLifetime = key;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var legacy = store.StartOperatorSession(agentId, new StartOperatorSessionRequest("Settings baseline", 1), store.AuthenticateSession(ownerToken)!.User.Id);
        async Task<double> Measure(bool bound)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operator/decisions?limit=1");
            request.Headers.Add("Authorization", "Bearer " + (bound ? token : legacy.Secret));
            if (bound) request.Headers.Add("DPoP", OperatorDpopTests.Proof(key, token, "GET", "https://studio.test/api/operator/decisions", clock.GetUtcNow()));
            var start = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return elapsed;
        }
        var baseline = new List<double>();
        var boundTimes = new List<double>();
        for (var i = 0; i < 1050; i++)
        {
            var first = await Measure(i % 2 == 0);
            var second = await Measure(i % 2 != 0);
            if (i < 50) continue;
            baseline.Add(i % 2 == 0 ? second : first);
            boundTimes.Add(i % 2 == 0 ? first : second);
        }
        baseline.Sort(); boundTimes.Sort();
        var baseMedian = (baseline[499] + baseline[500]) / 2;
        var boundMedian = (boundTimes[499] + boundTimes[500]) / 2;
        var added = boundMedian - baseMedian;
        var report = $"1000 bound and 1000 unbound requests after 50 warmup pairs, alternating order, same networked pipeline and route, signing excluded.\nUnbound median: {baseMedian:F6} ms\nBound median: {boundMedian:F6} ms\nAdded median: {added:F6} ms (budget < 2 ms).\n";
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { } folder)
            File.WriteAllText(Path.Combine(folder, "dpop-request-performance.txt"), report);
        Assert.True(added < 2, report);
    }

}
