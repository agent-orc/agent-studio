using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ReviewReClaimEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-reclaim-endpoint-" + Guid.NewGuid().ToString("N"));

    private DateTime _now = new(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Re_claim_rejects_missing_authority_fields_before_registry_lookup()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();

        var valid = new Contract.ReviewReClaimRequest(
            "reviewer",
            "replacement-instance",
            "lease-1",
            17,
            "reclaim-1");
        var invalidRequests = new[]
        {
            valid with { ExecutorId = " " },
            valid with { InstanceId = " " },
            valid with { PreviousLeaseId = " " },
            valid with { PreviousFence = 0 },
            valid with { IdempotencyKey = " " },
        };

        foreach (var request in invalidRequests)
        {
            using var response = await http.PostAsJsonAsync(
                "/api/v1/reviews/attempts/attempt-1/reclaim",
                request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<Contract.ApiError>();
            Assert.Equal("invalid-request", error!.Code);
        }
    }

    [Fact]
    public async Task Re_claim_uses_the_callers_idempotency_key()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var claimed = CreateClaimedReview(authority);
        _now = _now.AddSeconds(31);

        var registration = await http.PutAsJsonAsync(
            "/api/v1/runners/reviewer",
            new Contract.RegisterRunnerRequest(
                "reviewer",
                "review-host",
                "replacement-instance",
                "test",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.ReviewExecutor]));
        registration.EnsureSuccessStatusCode();

        var firstRequest = new Contract.ReviewReClaimRequest(
            "reviewer",
            "replacement-instance",
            claimed.Lease!.LeaseId,
            claimed.LastFence,
            "caller-reclaim-1",
            120);
        var first = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claimed.AttemptId}/reclaim",
            firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The caller's exact key replays the minted fence.
        var replay = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claimed.AttemptId}/reclaim",
            firstRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var firstClaim = await first.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>();
        var replayedClaim = await replay.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>();
        Assert.Equal(firstClaim!.Lease!.Fence, replayedClaim!.Lease!.Fence);

        // A different key is a new mutation request. It must validate against
        // the now-current fence instead of replaying an endpoint-derived key.
        var differentKey = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claimed.AttemptId}/reclaim",
            firstRequest with { IdempotencyKey = "caller-reclaim-2" });
        Assert.Equal(HttpStatusCode.Conflict, differentKey.StatusCode);
        var error = await differentKey.Content.ReadFromJsonAsync<Contract.ApiError>();
        Assert.Equal(nameof(AttemptWriteStatus.StaleFence), error!.Code);
    }

    private ReviewAttemptDto CreateClaimedReview(AttemptAuthorityService authority)
    {
        var sha = new string('a', 40);
        var run = authority.AcquireRun(
            "AGT-2753",
            "example/repository",
            null,
            "coding-runner",
            "coding-host",
            60,
            "run-create").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "run-settle"),
            Outcome = "done",
            ResultSha = sha,
        });
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-2753",
            "example/repository",
            sha,
            run.AttemptId,
            "requirements",
            "policy",
            [],
            "review-create")).ReviewAttempt!;
        return authority.ClaimReview(
            review.AttemptId,
            "reviewer",
            "review-host",
            30,
            "review-claim",
            "original-instance").ReviewAttempt!;
    }

    private WebApplicationFactory<Program> BuildFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AttemptAuthorityService>();
                services.AddSingleton(provider => new AttemptAuthorityService(
                    provider.GetRequiredService<IConfiguration>(),
                    provider.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                    () => _now,
                    provider.GetRequiredService<IAtomicJsonFileWriter>()));
            });
        });
}
