using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public class TaskServerClientHealthTests
{
    [Fact]
    public async Task Monolith_capability_plane_registers_and_advertises_before_legacy_claim()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath;
            var content = path switch
            {
                "/api/v1/protocol/compatibility" => """
                    {
                      "supported": true,
                      "server": {
                        "current": 2,
                        "minimumSupported": 1,
                        "maximumSupported": 2,
                        "serverVersion": "1.0.0",
                        "serverId": "orchestrator-monolith",
                        "clientKinds": ["runner"],
                        "capabilities": ["review-plane", "capability-advertisement"]
                      }
                    }
                    """,
                "/api/clients/register" => """
                    {"id":"legacy-client","displayName":"runner","kind":"service"}
                    """,
                "/api/runner/claim" => """{"status":"empty"}""",
                _ => "{}",
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner",
            RunnerName = "runner",
            Hostname = "host",
            BackendName = "test",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CliBin = "claude",
            CliArgs = "",
        };
        using var client = new TaskServerClient(
            http,
            "runner",
            usesDurableTaskServer: false,
            options: options);

        await client.EnsureCompatibleAsync(CancellationToken.None);
        await client.RegisterAsync("runner", "service", CancellationToken.None);
        await client.AdvertiseCapabilitiesAsync(
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
            null,
            1,
            CancellationToken.None);
        await client.ClaimAsync(
            new RunnerClaimRequest("runner", "runner", "host", 1, "test"),
            CancellationToken.None);

        Assert.False(client.UsesDurableTaskServer);
        Assert.Equal(
            [
                "/api/v1/protocol/compatibility",
                "/api/v1/runners/runner",
                "/api/clients/register",
                "/api/v1/runners/runner/capabilities",
                "/api/runner/claim",
            ],
            handler.Requests.Select(request => request.PathAndQuery));
    }

    [Fact]
    public async Task Register_uses_and_verifies_the_configured_client_identity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"identity":{"id":"agent-runner-01","kind":"service"}}""")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "agent-runner-01");

        var adopted = await client.RegisterAsync("ignored-display-name", "service", CancellationToken.None);

        Assert.Equal("agent-runner-01", adopted);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/clients/agent-runner-01", request.PathAndQuery);
        Assert.Equal("agent-runner-01", request.ClientId);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task Register_does_not_create_a_replacement_when_the_configured_identity_is_unknown()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent("client-not-found")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "missing-runner");

        var error = await Assert.ThrowsAsync<TaskServerException>(
            () => client.RegisterAsync("would-create-a-different-id", "service", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Contains("will not create a replacement identity", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Register_rejects_a_retired_configured_identity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"identity":{"id":"agent-runner-01","kind":"retired"}}""")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "agent-runner-01");

        var error = await Assert.ThrowsAsync<TaskServerException>(
            () => client.RegisterAsync("ignored-display-name", "service", CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("identity is retired", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Register_rejects_a_malformed_success_response()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "agent-runner-01");

        var error = await Assert.ThrowsAsync<TaskServerException>(
            () => client.RegisterAsync("ignored-display-name", "service", CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("different or empty identity", error.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// An unreachable server (a closed loopback port stands in for a dropped
    /// reverse tunnel) must be reported as a clean, non-null health reason and
    /// must never throw - that is what lets the runner surface "connection lost"
    /// once instead of a transport-exception cascade through register/lease.
    /// </summary>
    [Fact]
    public async Task Probe_reports_a_reason_when_the_server_is_unreachable()
    {
        // Port 1 is reserved and never listening: connect fails fast (refused).
        var (options, _, _, _) = RunnerOptions.Parse(["--health-check", "--server", "http://127.0.0.1:1"]);
        using var client = new TaskServerClient(options);

        var reason = await client.ProbeHealthAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task Probe_propagates_a_real_shutdown_rather_than_masking_it_as_a_health_failure()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["--health-check", "--server", "http://127.0.0.1:1"]);
        using var client = new TaskServerClient(options);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ProbeHealthAsync(cancelled.Token));
    }

    [Fact]
    public async Task Durable_task_server_does_not_probe_legacy_project_chat()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("The legacy endpoint must not be called."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true);

        var claim = await client.ClaimProjectChatWorkAsync(
            new RemoteChatWorkClaimRequest("runner-v1", "Runner v1", "host-a"),
            CancellationToken.None);

        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, claim.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Durable_registration_adopts_the_server_owned_host_capacity()
    {
        var now = DateTime.UtcNow.ToString("O");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {
                  "runnerId":"runner-v1",
                  "name":"Runner v1",
                  "hostId":"host-a",
                  "instanceId":"host-a:1",
                  "runnerVersion":"1.0.0",
                  "protocolVersion":3,
                  "status":"active",
                  "registeredAt":"{{now}}",
                  "lastSeenAt":"{{now}}",
                  "runtimeCapacity":{
                    "hostId":"host-a",
                    "maxParallelism":7,
                    "targetLoadPercent":80,
                    "rampStrategy":"balanced",
                    "version":1,
                    "updatedAt":"{{now}}"
                  }
                }
                """),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        var (options, _, _, _) = RunnerOptions.Parse(
            ["--poll", "--server", "http://127.0.0.1", "--runner-id", "runner-v1", "--max-parallelism", "2"]);
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true,
            options: options);

        await client.RegisterAsync("Runner v1", "service", CancellationToken.None);

        Assert.Equal(7, client.HostMaxParallelism);
        Assert.Equal(1, client.RuntimeCapacityVersion);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Durable_empty_claim_refreshes_capacity_without_restarting_the_daemon()
    {
        var now = DateTime.UtcNow.ToString("O");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {
                  "status":"empty",
                  "message":"No task available.",
                  "runtimeCapacity":{
                    "hostId":"host-a",
                    "maxParallelism":5,
                    "targetLoadPercent":85,
                    "rampStrategy":"conservative",
                    "version":2,
                    "updatedAt":"{{now}}"
                  }
                }
                """),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        var (options, _, _, _) = RunnerOptions.Parse(
            ["--poll", "--server", "http://127.0.0.1", "--runner-id", "runner-v1", "--max-parallelism", "2"]);
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true,
            options: options);

        var claim = await client.ClaimAsync(
            new RunnerClaimRequest(
                "runner-v1",
                "Runner v1",
                "host-a",
                1,
                "remote"),
            CancellationToken.None);

        Assert.Equal(RunnerClaimStatus.Empty, claim.Status);
        Assert.Equal(5, client.HostMaxParallelism);
        Assert.Equal(2, client.RuntimeCapacityVersion);
    }

    [Fact]
    public async Task Durable_capacity_survives_a_restart_without_the_task_server()
    {
        var state = Directory.CreateTempSubdirectory("runner-capacity-cache-");
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "runnerId":"runner-v1",
                      "name":"Runner v1",
                      "hostId":"host-a",
                      "instanceId":"host-a:1",
                      "runnerVersion":"1.0.0",
                      "protocolVersion":3,
                      "status":"active",
                      "registeredAt":"{{now}}",
                      "lastSeenAt":"{{now}}",
                      "runtimeCapacity":{
                        "hostId":"host-a",
                        "maxParallelism":7,
                        "targetLoadPercent":80,
                        "rampStrategy":"balanced",
                        "version":4,
                        "updatedAt":"{{now}}"
                      }
                    }
                    """),
            });
            using var onlineHttp = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://task-server"),
            };
            var options = CapacityOptions(state.FullName);
            using (var online = new TaskServerClient(
                       onlineHttp,
                       "runner-v1",
                       usesDurableTaskServer: true,
                       options: options))
            {
                await online.RegisterAsync("Runner v1", "service", CancellationToken.None);
                Assert.Equal(7, online.HostMaxParallelism);
                Assert.Equal(4, online.RuntimeCapacityVersion);
            }

            using var offlineHttp = new HttpClient(new RecordingHandler(_ =>
                throw new InvalidOperationException("The offline cache read must not use HTTP.")))
            {
                BaseAddress = new Uri("http://task-server"),
            };
            using var restarted = new TaskServerClient(
                offlineHttp,
                "runner-v1",
                usesDurableTaskServer: true,
                options: options);

            Assert.Equal(7, restarted.HostMaxParallelism);
            Assert.Equal(4, restarted.RuntimeCapacityVersion);
        }
        finally
        {
            state.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Durable_task_server_claim_without_repository_registration_uses_runner_git_fallback()
    {
        var now = DateTime.UtcNow;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {
                  "status": "claimed",
                  "run": {
                    "runId": "run-v1",
                    "taskId": "task-v1",
                    "status": "running",
                    "runnerId": "runner-v1",
                    "fence": 7,
                    "createdAt": "{{now:o}}",
                    "startedAt": "{{now:o}}",
                    "finishedAt": null,
                    "resultSha": null,
                    "repositoryId": null
                  },
                  "task": {
                    "taskId": "task-v1",
                    "projectId": "project-v1",
                    "taskKey": "RTS-21",
                    "title": "Restart replay",
                    "state": "3-progress",
                    "version": 2,
                    "createdAt": "{{now:o}}",
                    "updatedAt": "{{now:o}}",
                    "body": "hold the detached worker"
                  },
                  "lease": {
                    "leaseId": "lease-v1",
                    "runId": "run-v1",
                    "taskId": "task-v1",
                    "runnerId": "runner-v1",
                    "instanceId": "instance-v1",
                    "fence": 7,
                    "acquiredAt": "{{now:o}}",
                    "expiresAt": "{{now.AddMinutes(2):o}}",
                    "status": "active"
                  },
                  "mechanicalDelta": {
                    "baseSha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "deliveryRef": "refs/heads/result",
                    "deliverySha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "conflictPaths": ["src/Feature.cs"],
                    "steer": "Resolve the conflict.",
                    "verificationPlan": "Run focused tests."
                  },
                  "mechanicalFreshRoute": {
                    "cliType": "codex",
                    "model": "gpt-5.6-terra",
                    "thinkingLevel": "medium",
                    "reason": "pending-mechanical-continuation"
                  }
                }
                """)
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-v1",
            RunnerName = "runner-v1",
            Hostname = "host-v1",
            BackendName = "test",
            GitRemote = "/tmp/fallback-origin.git",
            WorkDir = "/tmp/runner-v1",
            BaseBranch = "main",
            CliBin = "/bin/sh",
            CliArgs = "fixture.sh",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            RunTimeoutSeconds = 120,
            HostMaxParallelism = 1,
            PollSeconds = 1,
        };
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true,
            options: options);

        var claim = await client.ClaimAsync(
            new RunnerClaimRequest(
                "runner-v1",
                "runner-v1",
                "host-v1",
                42,
                "test",
                AvailableSlots: 1),
            CancellationToken.None);

        Assert.Equal(RunnerClaimStatus.Claimed, claim.Status);
        Assert.Equal("project-v1", claim.ProjectName);
        Assert.Equal(
            TaskServerClient.RepositoryIdentity("/tmp/fallback-origin.git"),
            claim.ProjectId);
        Assert.Equal("/tmp/fallback-origin.git", claim.RepositoryUrl);
        Assert.Equal("main", claim.DefaultBranch);
        Assert.Equal("run-v1", claim.RunId);
        Assert.Equal("lease-v1", claim.Lease!.LeaseId);
        Assert.Equal("gpt-5.6-terra", claim.RunSpec?.Model);
        Assert.Equal("medium", claim.RunSpec?.ThinkingLevel);
        Assert.Equal("clean", claim.RunSpec?.ContextMode);
        Assert.NotNull(claim.MechanicalDelta);
    }

    [Fact]
    public async Task Both_server_topologies_return_the_same_provider_refusal_continuation_claim()
    {
        var now = DateTime.UtcNow;
        const string salvageRef =
            "refs/heads/agent-studio/salvage/runner/AGT-2874/run-1/fence-1/1111111";
        const string salvageSha = "1111111111111111111111111111111111111111";
        var legacyJson = $$"""
            {
              "status":"claimed",
              "taskKey":"AGT-2874",
              "jobId":"task-v1",
              "projectName":"project-v1",
              "runId":"run-v1",
              "leaseInstanceId":"instance-v1",
              "lease":{
                "taskKey":"AGT-2874","runnerId":"runner-v1","runnerName":"Runner v1",
                "hostname":"host-v1","pid":42,"backendName":"test","leaseId":"lease-v1",
                "fencingToken":7,"acquiredAt":"{{now:o}}","expiresAt":"{{now.AddMinutes(2):o}}",
                "attemptId":"run-v1"
              },
              "runSpec":{"cliType":"codex","model":"gpt-5.6-sol","thinkingLevel":"high","contextMode":"clean"},
              "continuationBaseRef":"{{salvageRef}}",
              "continuationBaseSha":"{{salvageSha}}"
            }
            """;
        var durableJson = $$"""
            {
              "status":"claimed",
              "run":{"runId":"run-v1","taskId":"task-v1","status":"running","runnerId":"runner-v1","fence":7,"createdAt":"{{now:o}}","startedAt":"{{now:o}}"},
              "task":{"taskId":"task-v1","projectId":"project-v1","taskKey":"AGT-2874","title":"Continue refused run","state":"3-progress","version":2,"createdAt":"{{now:o}}","updatedAt":"{{now:o}}"},
              "lease":{"leaseId":"lease-v1","runId":"run-v1","taskId":"task-v1","runnerId":"runner-v1","instanceId":"instance-v1","fence":7,"acquiredAt":"{{now:o}}","expiresAt":"{{now.AddMinutes(2):o}}","status":"active"},
              "modelFallback":{"from":"gpt-6-astra","to":"gpt-5.6-sol","reason":"provider-refusal-sibling","cliType":"codex","thinkingLevel":"high"},
              "continuationBaseRef":"{{salvageRef}}",
              "continuationBaseSha":"{{salvageSha}}"
            }
            """;

        using var legacyHttp = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(legacyJson),
        })) { BaseAddress = new Uri("http://task-server") };
        using var durableHttp = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(durableJson),
        })) { BaseAddress = new Uri("http://task-server") };
        var options = CapacityOptions(
            Path.Combine(Path.GetTempPath(), "claim-parity-" + Guid.NewGuid().ToString("N")));
        using var legacy = new TaskServerClient(legacyHttp, "runner-v1", usesDurableTaskServer: false, options: options);
        using var durable = new TaskServerClient(durableHttp, "runner-v1", usesDurableTaskServer: true, options: options);
        var request = new RunnerClaimRequest("runner-v1", "Runner v1", "host-v1", 42, "test");

        var legacyClaim = await legacy.ClaimAsync(request, CancellationToken.None);
        var durableClaim = await durable.ClaimAsync(request, CancellationToken.None);

        Assert.Equal(legacyClaim.TaskKey, durableClaim.TaskKey);
        Assert.Equal(legacyClaim.RunSpec, durableClaim.RunSpec);
        Assert.Equal(legacyClaim.ContinuationBaseRef, durableClaim.ContinuationBaseRef);
        Assert.Equal(legacyClaim.ContinuationBaseSha, durableClaim.ContinuationBaseSha);
        Assert.Equal(salvageRef, durableClaim.ContinuationBaseRef);
        Assert.Equal(salvageSha, durableClaim.ContinuationBaseSha);
    }

    [Fact]
    public void Restored_run_outbox_keeps_the_original_attempt_instance()
    {
        using var http = new HttpClient(new RecordingHandler(_ =>
            throw new InvalidOperationException("No HTTP request is expected.")))
        {
            BaseAddress = new Uri("http://task-server"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true);
        var now = DateTime.UtcNow;
        var lease = new RunLeaseInfoDto(
            "RTS-21",
            "runner-v1",
            "Runner v1",
            "host-v1",
            42,
            "test",
            "lease-v1",
            7,
            now,
            now.AddMinutes(2),
            "run-v1");

        client.RestoreRunAuthority(
            "RTS-21",
            "run-v1",
            "host-v1:original-process",
            lease);

        var authority = client.OutboxAuthority("RTS-21");
        Assert.Equal("host-v1:original-process", authority.InstanceId);
        Assert.NotEqual(client.RunnerInstanceId, authority.InstanceId);
    }

    [Fact]
    public async Task Durable_direct_completion_reports_mechanical_fallback_as_ready()
    {
        var now = DateTime.UtcNow;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new RunDto(
                "run-v1", "task-v1", "MechanicalFallback", "runner-v1", 7,
                now, now, now)),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "runner-v1", usesDurableTaskServer: true);
        client.RestoreRunAuthority("RTS-21", "run-v1", "instance-v1", new RunLeaseInfoDto(
            "RTS-21", "runner-v1", "Runner v1", "host-v1", 42, "test",
            "lease-v1", 7, now, now.AddMinutes(2), "run-v1"));
        var decision = MechanicalRoundFallbackPolicy.AsTypedDecision(
            ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
                "run-v1", ExecutionAttemptKind.Coding, LaunchFailed: true)),
            "semantic-conflict");

        var response = await client.CompleteRunAsync(new RemoteRunCompletionRequest(
            "RTS-21", "lease-v1", 7, "runner-v1", "MechanicalFallback",
            OutcomeDecision: decision), CancellationToken.None);

        Assert.Equal("2-ready", response?.TargetState);
        Assert.Equal("MechanicalFallback", response?.Outcome);
        Assert.Contains(handler.Requests, request =>
            request.PathAndQuery == "/api/v1/runs/run-v1/completion");
    }

    private static RunnerOptions CapacityOptions(string stateDirectory)
        => new()
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-v1",
            RunnerName = "Runner v1",
            Hostname = "host-a",
            BackendName = "test",
            WorkDir = Path.Combine(stateDirectory, "work"),
            StateDir = stateDirectory,
            BaseBranch = "main",
            CliBin = "claude",
            CliArgs = "-p",
            HostMaxParallelism = 2,
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string? ClientId)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues("X-Client-Id", out var values) ? values.SingleOrDefault() : null));
            return Task.FromResult(respond(request));
        }
    }
}
