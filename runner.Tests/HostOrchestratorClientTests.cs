using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostOrchestratorClientTests
{
    private const string CurrentInstance = "host-1:current";

    [Fact]
    public async Task Permit_is_adopted_and_containment_step_is_reported_after_host_report()
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-1", "project-1", "TS-1", "Task", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-1", task.TaskId, "running", "runner-1", 7, now, now, null);
        var lease = new LeaseDto(
            "lease-1", run.RunId, task.TaskId, "runner-1", CurrentInstance, 7,
            now, now.AddMinutes(2), "active");
        var step = new PostStepPlanDto(
            "step-1", run.RunId, "post-worktree-containment", "runner-1", "available");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted", "permit-1", run, task, lease, lease.ExpiresAt, [step],
            MechanicalFreshRoute: new MechanicalFreshRunRoute(
                "codex", "gpt-5.6-sol", "medium", "semantic-conflict"),
            ContinuationBaseRef: "refs/heads/result",
            ContinuationBaseSha: new string('a', 40));
        var handler = new ContractHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/runners/runner-1/reports" => Json(new HostReportResponse(
                "accepted", 1,
                new HostContractRangeDto(HostOrchestratorContract.Current, HostOrchestratorContract.Current),
                1, "active", [new WorkPermitDto("permit-1", task, 1, now.AddMinutes(5))], [])),
            "/api/v1/work-permits/permit-1/accept" => Json(acceptance),
            "/api/v1/runs/run-1/post-steps/step-1/claim" => Json(new PostStepClaimResponse(
                "claimed", step with { Status = "running" }, 7)),
            "/api/v1/runs/run-1/post-steps/step-1/complete" => Json(new PostStepCompleteResponse(
                "completed", step with { Status = "completed" }, "passed", ["evidence-sha"])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        var reportRequest = Report(now);

        var report = await client.ReportHostAsync(reportRequest, default);
        var accepted = await client.AcceptWorkPermitAsync(
            Assert.Single(report.AvailableWork), report.AcceptedSequence, report.PolicyVersion, default);
        var claim = client.AdoptWorkPermit(accepted);
        await client.CompleteHostPostProcessingAsync(task.TaskKey, "evidence-sha", default);

        Assert.Equal("run-1", claim.RunId);
        Assert.Equal("lease-1", claim.Lease!.LeaseId);
        Assert.Equal("gpt-5.6-sol", claim.RunSpec?.Model);
        Assert.Equal("semantic-conflict", claim.FreshRunReason);
        Assert.Equal("refs/heads/result", claim.ContinuationBaseRef);
        Assert.Equal(new string('a', 40), claim.ContinuationBaseSha);
        Assert.Equal(
            [
                "/api/v1/runners/runner-1/reports",
                "/api/v1/work-permits/permit-1/accept",
                "/api/v1/runs/run-1/post-steps/step-1/claim",
                "/api/v1/runs/run-1/post-steps/step-1/complete",
            ],
            handler.Paths);
    }

    [Fact]
    public async Task Result_post_step_retries_summary_only_then_reports_generated_artifact()
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-result", "project-1", "TS-R", "Concept Result", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-result", task.TaskId, "running", "runner-1", 11, now, now, null);
        var lease = new LeaseDto(
            "lease-result", run.RunId, task.TaskId, "runner-1", CurrentInstance, 11,
            now, now.AddMinutes(2), "active");
        var step = new PostStepPlanDto(
            "step-result", run.RunId, HostPostStepIds.ResultFinalization, "runner-1", "available");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted", "permit-result", run, task, lease, lease.ExpiresAt, [step]);
        var finalizationCalls = 0;
        JsonElement completionBody = default;
        var handler = new ContractHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/runs/run-result/result-finalization")
            {
                finalizationCalls++;
                return Json(finalizationCalls == 1
                    ? new ResultFinalizationDto(
                        run.RunId,
                        ResultFinalizationStatus.Retryable,
                        1,
                        3,
                        null,
                        null,
                        "transient summary failure",
                        now)
                    : new ResultFinalizationDto(
                        run.RunId,
                        ResultFinalizationStatus.Ready,
                        2,
                        3,
                        "artifact-status",
                        "status-sha",
                        null,
                        now));
            }
            if (path == "/api/v1/runs/run-result/post-steps/step-result/complete")
            {
                completionBody = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                return Json(new PostStepCompleteResponse(
                    "completed", step with { Status = "completed" }, "passed", ["status-sha"]));
            }
            return path switch
            {
                "/api/v1/runners/runner-1/reports" => Json(new HostReportResponse(
                    "accepted", 1,
                    new HostContractRangeDto(HostOrchestratorContract.Current, HostOrchestratorContract.Current),
                    1, "active", [], [])),
                "/api/v1/runs/run-result/post-steps/step-result/claim" => Json(new PostStepClaimResponse(
                    "claimed", step with { Status = "running" }, 11)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        client.AdoptWorkPermit(acceptance);
        await client.ReportHostAsync(Report(now), default);

        await client.CompleteHostPostProcessingAsync(task.TaskKey, null, default);

        Assert.Equal(2, finalizationCalls);
        Assert.Equal("passed", completionBody.GetProperty("outcome").GetString());
        Assert.Equal("status-sha", Assert.Single(completionBody.GetProperty("artifactHashes").EnumerateArray()).GetString());
        Assert.DoesNotContain(handler.Paths, path => path.EndsWith("/completion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exhausted_result_post_step_reports_degraded_without_repeating_core_completion()
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-degraded", "project-1", "TS-D", "Concept Result", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-degraded", task.TaskId, "running", "runner-1", 12, now, now, null);
        var lease = new LeaseDto(
            "lease-degraded", run.RunId, task.TaskId, "runner-1", CurrentInstance, 12,
            now, now.AddMinutes(2), "active");
        var step = new PostStepPlanDto(
            "step-degraded", run.RunId, HostPostStepIds.ResultFinalization, "runner-1", "available");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted", "permit-degraded", run, task, lease, lease.ExpiresAt, [step]);
        var finalizationCalls = 0;
        JsonElement completionBody = default;
        var handler = new ContractHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/runs/run-degraded/result-finalization")
            {
                finalizationCalls++;
                return Json(new ResultFinalizationDto(
                    run.RunId,
                    finalizationCalls == 1
                        ? ResultFinalizationStatus.Retryable
                        : ResultFinalizationStatus.Degraded,
                    finalizationCalls,
                    2,
                    null,
                    null,
                    "summary unavailable",
                    now));
            }
            if (path == "/api/v1/runs/run-degraded/post-steps/step-degraded/complete")
            {
                completionBody = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                return Json(new PostStepCompleteResponse(
                    "completed", step with { Status = "completed" }, "degraded", []));
            }
            return path switch
            {
                "/api/v1/runners/runner-1/reports" => Json(new HostReportResponse(
                    "accepted", 1,
                    new HostContractRangeDto(HostOrchestratorContract.Current, HostOrchestratorContract.Current),
                    1, "active", [], [])),
                "/api/v1/runs/run-degraded/post-steps/step-degraded/claim" => Json(new PostStepClaimResponse(
                    "claimed", step with { Status = "running" }, 12)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        client.AdoptWorkPermit(acceptance);
        await client.ReportHostAsync(Report(now), default);

        await client.CompleteHostPostProcessingAsync(task.TaskKey, null, default);

        Assert.Equal(2, finalizationCalls);
        Assert.Equal("degraded", completionBody.GetProperty("outcome").GetString());
        Assert.Empty(completionBody.GetProperty("artifactHashes").EnumerateArray());
        Assert.DoesNotContain(handler.Paths, path => path.EndsWith("/completion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registration_declares_host_contract_and_capabilities()
    {
        JsonElement requestBody = default;
        var handler = new ContractHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var now = DateTime.UtcNow;
            return Json(new RunnerDto(
                "runner-1", "Runner", "host-1", CurrentInstance, "1.0.0",
                TaskServerProtocol.Current, "active", now, now));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);

        await client.RegisterAsync("Runner", "service", default);

        Assert.Equal(
            HostOrchestratorContract.Current,
            requestBody.GetProperty("hostOrchestratorMinimum").GetString());
        Assert.Equal(
            HostOrchestratorContract.Current,
            requestBody.GetProperty("hostOrchestratorMaximum").GetString());
        Assert.Equal(
            RunnerReleaseIdentity.Current,
            requestBody.GetProperty("runnerVersion").GetString());
        var capabilities = requestBody.GetProperty("capabilities")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Contains("permits", capabilities);
        Assert.Contains("local-queue", capabilities);
        Assert.Contains("host-post-processing", capabilities);
    }

    [Fact]
    public async Task Replacement_daemon_preserves_attempt_instance_for_reconcile_and_post_step()
    {
        const string attemptInstance = "host-1:attempt";
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-2", "project-1", "TS-2", "Task", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-2", task.TaskId, "running", "runner-1", 8, now, now, null);
        var lease = new LeaseDto(
            "lease-2", run.RunId, task.TaskId, "runner-1", attemptInstance, 8,
            now, now.AddMinutes(2), "active");
        var step = new PostStepPlanDto(
            "step-2", run.RunId, "post-worktree-containment", "runner-1", "available");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted", "permit-2", run, task, lease, lease.ExpiresAt, [step]);
        var bodies = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var handler = new ContractHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Content is not null)
            {
                using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                bodies[path] = document.RootElement.Clone();
            }
            return path switch
            {
                "/api/v1/runners/runner-1/reports" => Json(new HostReportResponse(
                    "accepted", 4,
                    new HostContractRangeDto(
                        HostOrchestratorContract.Current,
                        HostOrchestratorContract.Current),
                    1, "active", [], [])),
                "/api/v1/runs/run-2/reconcile" => Json(new RunReconcileResponse(
                    "reconciled", lease with { ExpiresAt = now.AddMinutes(3) }, 4)),
                "/api/v1/runs/run-2/post-steps/step-2/claim" => Json(new PostStepClaimResponse(
                    "claimed", step with { Status = "running" }, 8)),
                "/api/v1/runs/run-2/post-steps/step-2/complete" => Json(new PostStepCompleteResponse(
                    "completed", step with { Status = "completed" }, "passed", [])),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        client.RestoreHostWorkAuthority(acceptance);

        var report = await client.ReportHostAsync(Report(now), default);
        await client.ReconcileHostRunAsync(acceptance, report.AcceptedSequence, default);
        await client.CompleteHostPostProcessingAsync(task.TaskKey, null, default);

        var reconcile = bodies["/api/v1/runs/run-2/reconcile"];
        Assert.Equal(CurrentInstance, reconcile.GetProperty("instanceId").GetString());
        Assert.Equal(attemptInstance, reconcile.GetProperty("leaseInstanceId").GetString());
        var claim = bodies["/api/v1/runs/run-2/post-steps/step-2/claim"];
        Assert.Equal(CurrentInstance, claim.GetProperty("instanceId").GetString());
        Assert.Equal(attemptInstance, claim.GetProperty("leaseInstanceId").GetString());
        var complete = bodies["/api/v1/runs/run-2/post-steps/step-2/complete"];
        Assert.Equal(CurrentInstance, complete.GetProperty("instanceId").GetString());
        Assert.Equal(attemptInstance, complete.GetProperty("leaseInstanceId").GetString());
    }

    [Fact]
    public async Task Unknown_post_step_fails_closed_before_claiming_it()
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-3", "project-1", "TS-3", "Task", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-3", task.TaskId, "running", "runner-1", 9, now, now, null);
        var lease = new LeaseDto(
            "lease-3", run.RunId, task.TaskId, "runner-1", CurrentInstance, 9,
            now, now.AddMinutes(2), "active");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted",
            "permit-3",
            run,
            task,
            lease,
            lease.ExpiresAt,
            [new PostStepPlanDto("step-3", run.RunId, "post-not-implemented", "runner-1", "available")]);
        var handler = new ContractHandler((_, _) => Json(new HostReportResponse(
            "accepted", 1,
            new HostContractRangeDto(HostOrchestratorContract.Current, HostOrchestratorContract.Current),
            1, "active", [], [])));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        client.AdoptWorkPermit(acceptance);
        await client.ReportHostAsync(Report(now), default);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteHostPostProcessingAsync(task.TaskKey, null, default));

        Assert.Contains("no runner implementation", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/api/v1/runners/runner-1/reports"], handler.Paths);
    }

    private static HostReportRequest Report(DateTime now)
        => new(
            HostOrchestratorContract.Current,
            "host-1",
            CurrentInstance,
            1,
            now,
            new HostCapacityDto(1, 1, 0, 0, 1),
            [],
            [],
            [],
            []);

    private static TaskServerClient Client(HttpClient http)
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-1",
            RunnerName = "Runner",
            Hostname = "host-1",
            BackendName = "test",
            GitRemote = "/tmp/origin.git",
            WorkDir = "/tmp/runner-work",
            StateDir = "/tmp/runner-state",
            BaseBranch = "develop",
            CliBin = "codex",
            CliArgs = "exec",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            RunTimeoutSeconds = 600,
            HostMaxParallelism = 2,
            PollSeconds = 1,
        };
        return new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: CurrentInstance,
            supportsHostOrchestrator: true);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class ContractHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public ContractHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
            : this((request, ct) => Task.FromResult(respond(request, ct)))
        {
        }

        public ContractHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return await _respond(request, cancellationToken);
        }
    }
}
