using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class GateDispatchRestartTests
{
    [Fact]
    public async Task New_engine_instance_replays_the_same_immutable_gate_subject()
    {
        var source = new ReviewSubjectDto("review-1", "task-1", "source-run-1",
            "repo-1", "https://example.invalid/repo.git", new string('a', 40),
            "refs/agent-studio/results/source-run-1", null, null, null, "policy-v1",
            new ReviewPlanDto(
                [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-lc", "dotnet test"],
                    WorkingSubdir: "backend")],
                ["build-tests"]), DateTime.UtcNow);
        var requests = new List<CreateGateSubjectRequest>();
        var handler = new GateApiHandler(source, requests);
        var options = new EngineOptions
        {
            ServerUrl = "http://task-server",
            ClientId = "engine-test",
            RemotePostBuildTestEnabled = true,
            PollSeconds = 1,
        };
        var stageReadyAt = DateTime.UtcNow;
        var run = new OrchestrationRunDto("run-1", "project-1", "task-1", 7,
            "leased", OrchestrationStage.GateDispatch,
            """{"reviewSubjectId":"review-1","gates":[]}""", 0,
            stageReadyAt.AddHours(-2), stageReadyAt, null,
            [new OrchestrationStageResultDto(1, OrchestrationStage.PostProcessing,
                OrchestrationAction.Continue, "{}", stageReadyAt)]);

        using (var first = Client(handler))
            Assert.Equal(OrchestrationAction.Continue,
                (await new GateDispatchLoop(first, options).ExecuteAsync(run, default)).Action);
        using (var restarted = Client(handler))
            Assert.Equal(OrchestrationAction.Continue,
                (await new GateDispatchLoop(restarted, options).ExecuteAsync(run, default)).Action);

        Assert.Equal(2, requests.Count);
        Assert.Equal(JsonSerializer.Serialize(requests[0]), JsonSerializer.Serialize(requests[1]));
        Assert.Equal("source-run-1", requests[0].SourceRunId);
        Assert.Equal(source.ExpectedResultSha, requests[0].ExpectedSha);
        Assert.Contains(CapabilityProtocol.DotNet, requests[0].Plan.RequiredCapabilities);
        Assert.Equal("backend", Assert.Single(requests[0].Plan.Commands).WorkingSubdirectory);
        Assert.Equal(["-lc", "dotnet test"], Assert.Single(requests[0].Plan.Commands).Arguments);
        Assert.Equal(stageReadyAt.AddMinutes(15), requests[0].DispatchDeadline);
    }

    private static EngineTaskServerClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://task-server"),
        });

    private sealed class GateApiHandler(
        ReviewSubjectDto source,
        List<CreateGateSubjectRequest> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath == "/api/v1/gates/review-sources/review-1")
                return Json(source);
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/api/v1/gates/subjects")
            {
                var submitted = await request.Content!.ReadFromJsonAsync<CreateGateSubjectRequest>(
                    cancellationToken: cancellationToken);
                Assert.NotNull(submitted);
                requests.Add(submitted);
                var subject = new GateSubject("gate-1", submitted.TaskId, submitted.SourceRunId,
                    submitted.RepositoryId, submitted.RepositoryUrl, submitted.ExpectedSha,
                    submitted.ResultRef, submitted.SourceBundleArtifactId, submitted.SourceBundleSha256,
                    submitted.PlanHash, submitted.PolicyHash, submitted.PipelineDefinitionVersion,
                    submitted.TestSelectionAuditDigest, submitted.Plan, DateTime.UtcNow,
                    submitted.DispatchDeadline, submitted.MaxAttempts);
                var attempt = new GateAttempt("attempt-1", "gate-1", 1, GateStates.Passed,
                    "gate-host", "host-1", null, GateStates.Passed,
                    DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, 1, null);
                return Json(new GateStatus(subject, [attempt], null, 0, null,
                    GateStates.Passed, 1, null, submitted.ExpectedSha, GateStates.Passed, true));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
    }
}
