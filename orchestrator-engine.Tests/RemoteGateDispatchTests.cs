using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class RemoteGateDispatchTests
{
    [Fact]
    public async Task Engine_restart_replays_subject_request_and_consumes_terminal_gate()
    {
        var server = new GatePlane();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://localhost") };
        using var client = new EngineTaskServerClient(http);
        var options = new EngineOptions
        {
            ServerUrl = "http://localhost",
            ClientId = "engine-a",
            RemoteGateEnabled = true,
        };
        var run = new OrchestrationRunDto("orun-a", "project-a", "task-a", 1,
            "leased", OrchestrationStage.GateDispatch,
            "{\"reviewSubjectId\":\"review-a\"}", 0,
            DateTime.UtcNow, DateTime.UtcNow, null);

        await Assert.ThrowsAsync<GatePendingException>(() =>
            new GateDispatchLoop(options, client).ExecuteAsync(run, default));
        var afterRestart = await new GateDispatchLoop(options, client).ExecuteAsync(run, default);

        Assert.Equal(OrchestrationAction.Continue, afterRestart.Action);
        Assert.Equal(2, server.Creates.Count);
        Assert.Equal(server.Creates[0].SourceRunId, server.Creates[1].SourceRunId);
        Assert.Equal(server.Creates[0].PlanHash, server.Creates[1].PlanHash);
        Assert.Equal("post-build-test-gate", server.Creates[0].Plan.GateId);
        Assert.Contains("subject-a", afterRestart.OutputJson, StringComparison.Ordinal);
    }

    private sealed class GatePlane : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly ReviewSubjectDto _review = new(
            "review-a", "task-a", "run-a", "repo-a", "https://example.invalid/repo.git",
            new string('a', 40), "refs/heads/result", null, null, null,
            "policy-a", new ReviewPlanDto(
                [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-c", "exit 0"],
                    TimeoutSeconds: 60)], ["build-tests"]), DateTime.UtcNow);
        private GateSubject? _subject;
        public List<CreateGateSubjectRequest> Creates { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/reviews/subjects/review-a")
                return JsonResponse(_review);
            if (request.Method == HttpMethod.Post && path == "/api/v1/gates/subjects")
            {
                var input = await request.Content!.ReadFromJsonAsync<CreateGateSubjectRequest>(Json, cancellationToken)
                    ?? throw new InvalidDataException("Gate subject request is empty.");
                Creates.Add(input);
                _subject ??= new GateSubject("subject-a", input.TaskId, input.SourceRunId,
                    input.RepositoryId, input.RepositoryUrl, input.ExpectedSha,
                    input.ResultRef, input.SourceBundleId, input.SourceBundleSha256,
                    input.PlanHash, input.PolicyHash, input.PipelineDefinitionVersion,
                    input.TestSelectionAuditDigest, input.Plan,
                    input.DispatchDeadline, input.RetryBudget, DateTime.UtcNow);
                return JsonResponse(_subject);
            }
            if (request.Method == HttpMethod.Get && path == "/api/v1/gates/subjects/subject-a")
            {
                var state = Creates.Count == 1 ? GateStates.Queued : GateStates.Passed;
                var attempt = new GateAttempt("attempt-a", "subject-a", 1, state,
                    "gate-a", "host-a", null, state == GateStates.Passed ? "passed" : null,
                    DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
                return JsonResponse(new GateStatusView(_subject!,
                    [new GateAttemptView(attempt, null, null)], TimeSpan.Zero,
                    "host-a", state, 1, DateTime.UtcNow.AddMinutes(1),
                    state == GateStates.Passed ? _subject!.ExpectedSha : null,
                    state == GateStates.Passed ? "passed" : null,
                    state == GateStates.Passed));
            }
            throw new InvalidOperationException("Unexpected gate API request: " + path);
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
