using System.Net;
using System.Text;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class OneShotJobClientTests
{
    [Fact]
    public async Task Runner_claims_a_bounded_job_and_completes_with_its_claim_token()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server/") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true,
            runnerInstanceId: "instance-a");

        var work = await client.ClaimOneShotJobAsync("runner-a", default);
        Assert.NotNull(work);
        Assert.Equal("hello", work.Request.Prompt);
        Assert.Contains("instance-a", handler.ClaimBody);
        Assert.True(await client.CompleteOneShotJobAsync(new OneShotJobCompletion(
            "runner-a", work.JobId, work.ClaimToken, true, "done", null,
            new(1, 2, 0, 0, "gpt-6-sol")), default));
        Assert.Contains("claim-1", handler.CompletionBody);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string ClaimBody { get; private set; } = string.Empty;
        public string CompletionBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string result;
            if (path == "/api/v1/one-shot-jobs/claim")
            {
                ClaimBody = body;
                result = """{"jobId":"job-1","claimToken":"claim-1","request":{"cli":"codex","model":"gpt-6-sol","prompt":"hello","thinkingLevel":"medium","timeoutSeconds":30,"maxOutputBytes":100}}""";
            }
            else
            {
                Assert.Equal("/api/v1/one-shot-jobs/complete", path);
                CompletionBody = body;
                result = "true";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
