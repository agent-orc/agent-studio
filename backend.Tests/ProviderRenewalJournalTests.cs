using System.Net;
using System.Net.Http.Json;
using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixed observation dates are compared only with the receipt deadline.

public sealed class ProviderRenewalJournalTests
{
    [Theory]
    [InlineData("R3", "codex", "native-cli-store")]
    [InlineData("R8", "claude", "environment-file")]
    public async Task Completion_requires_distinct_coding_and_review_advertisements_with_real_requests(
        string method, string provider, string source)
    {
        var observedAt = new DateTime(2026, 10, 8, 12, 2, 0, DateTimeKind.Utc);
        var receipt = new ProviderRenewalReceiptDto("renewal_fixture", "installation", "host",
            "credential", "generation-a", method, "signin-fixture", "operator",
            observedAt.AddMinutes(13), "installed", null, null, [], false, [], observedAt);
        var posted = new List<AdvanceProviderRenewalRequest>();
        var reviewHasRealRequest = false;
        var handler = new ReplyHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var capabilities = new[]
                {
                    Snapshot("coding", CapabilityProtocol.CodingExecutor, observedAt, provider, source),
                    Snapshot("review", CapabilityProtocol.ReviewExecutor,
                        reviewHasRealRequest ? observedAt : null, provider, source),
                };
                return JsonResponse(capabilities);
            }
            var step = request.Content!.ReadFromJsonAsync<AdvanceProviderRenewalRequest>().GetAwaiter().GetResult()!;
            posted.Add(step);
            return JsonResponse(receipt with { Step = step.Step,
                ObservedGeneration = step.ObservedGeneration ?? "generation-b",
                EffectiveSource = step.EffectiveSource ?? source,
                RealRequestSucceeded = true });
        });
        var journal = new ProviderRenewalTaskServerJournal(new FixedClientFactory(handler));

        Assert.Equal("installed", (await journal.TryVerifyAsync(receipt, default)).Step);
        Assert.Empty(posted);
        reviewHasRealRequest = true;
        var completed = await journal.TryVerifyAsync(receipt, default);

        Assert.Equal("complete", completed.Step);
        Assert.Equal(["verified", "retired", "complete"], posted.Select(step => step.Step));
        Assert.Equal(["agent-runner.service", "agent-runner-review.service"], posted[0].VerifiedUnits);
        Assert.True(posted[0].RealRequestSucceeded);
    }

    private static object Snapshot(string role, string roleKey, DateTime? observedAt,
        string provider, string source) => new
    {
        runnerId = $"runner-{role}", hostId = "host", status = "active",
        capabilities = new object[]
        {
            new { key = roleKey },
            new
            {
                key = CapabilityProtocol.ProviderAuthentication(provider),
                isFresh = true, credentialGeneration = "generation-b",
                lastRealSuccessAt = observedAt, effectiveSource = source,
                healthOutcome = "healthy",
            },
        },
    };

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://task-server.local") };
    }
}
