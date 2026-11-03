using System.Net;
using System.Net.Http.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed partial class RemoteRunnerEndToEndTests
{
    /// <summary>SM-11: the monolith review plane refuses a verdict from the delivery's coding executor.</summary>
    [Fact]
    public async Task Review_report_from_the_delivery_coding_executor_is_refused()
    {
        const string executorId = "coder-reviewing-itself";
        const string instance = "shared-host:self-review";
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        SeedTask(TaskStates.AutoReview, TaskKey, "Self review", "The coder may not review its own delivery.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(TaskKey, "PROJ-SELF", null, executorId, "coding-host", 30, "self-review-source").RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "self-review-source-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                "PROJ-SELF", run.AttemptId, "4136f00d4136f00d4136f00d4136f00d4136f00d", resultSha,
                "refs/heads/self-review-result", null, new string('a', 64)),
        }).Accepted);
        Assert.True(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey, "PROJ-SELF", resultSha, run.AttemptId, "requirements", "policy", [], "self-review-create")).Accepted);
        await RegisterReviewExecutorAsync(http, executorId, instance);
        var claimed = await http.PostAsJsonAsync($"/api/v1/runners/{executorId}/review-claims",
            new Contract.ReviewClaimRequest(executorId, instance, 30, AvailableSlots: 1));
        claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
        Assert.Equal("claimed", claim.Status);

        var report = await http.PostAsJsonAsync($"/api/v1/reviews/attempts/{claim.Attempt!.AttemptId}/report",
            InfrastructureReport(claim, executorId, instance, "self-review-report"));

        Assert.Equal(HttpStatusCode.Conflict, report.StatusCode);
        Assert.Equal(Contract.ReviewPrincipalSeparationPolicy.CoderCode,
            (await report.Content.ReadFromJsonAsync<Contract.ApiError>())!.Code);
        Assert.Empty(authority.GetReview(claim.Attempt.AttemptId)!.Reports);
    }
}
