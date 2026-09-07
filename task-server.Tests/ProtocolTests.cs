using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Unsupported_runner_is_rejected_before_registration_or_claim()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, "0");

        var response = await client.PutAsJsonAsync(
            "/api/v1/runners/old-runner",
            new RegisterRunnerRequest("old", "host", "instance", "0.8.0", 0));

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("protocol-unsupported", error!.Code);
        Assert.Contains("supported range", error.Message, StringComparison.OrdinalIgnoreCase);

        var claim = await client.PostAsJsonAsync(
            "/api/v1/runners/old-runner/claims",
            new ClaimRequest("old-runner", "old-instance"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, claim.StatusCode);
    }

    [Fact]
    public async Task Versioned_resource_request_without_protocol_header_is_honestly_rejected()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/workspaces");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("protocol-unsupported", error!.Code);
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bearer_mode_protects_resources_but_keeps_handshake_available()
    {
        using var temp = new TempDirectory();
        const string token = "task-server-test-token-000000000000000000000001";
        await using var factory = new TaskServerFactory(
            temp.Path,
            new Dictionary<string, string?>
            {
                ["TaskServer:AuthMode"] = "bearer",
                ["TaskServer:AuthToken"] = token,
            });
        using var client = factory.CreateClient();

        var handshake = await client.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest(
                "runner",
                "1.0.0",
                TaskServerProtocol.Current));
        Assert.Equal(HttpStatusCode.OK, handshake.StatusCode);

        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var denied = await client.GetAsync("/api/v1/management/status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var accepted = await client.GetAsync("/api/v1/management/status");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Supported_mixed_product_versions_negotiate_protocol_v1()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();

        var studio = await client.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest("studio", "1.1.0", 1));
        var runner = await client.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest("runner", "1.0.0", 1));

        Assert.Equal(HttpStatusCode.OK, studio.StatusCode);
        Assert.Equal(HttpStatusCode.OK, runner.StatusCode);
        Assert.True((await runner.Content.ReadFromJsonAsync<ProtocolCompatibilityResponse>())!.Supported);
    }

    [Fact]
    public async Task Orchestrator_engine_negotiates_before_claiming_work()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest(
                TaskServerProtocol.EngineClientKind,
                "0.1.0",
                TaskServerProtocol.Current));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var compatibility = await response.Content.ReadFromJsonAsync<ProtocolCompatibilityResponse>();
        Assert.True(compatibility!.Supported);
        Assert.Contains(TaskServerProtocol.EngineClientKind, compatibility.Server.ClientKinds);
    }

    [Fact]
    public async Task Public_orchestration_api_owns_definition_run_claim_and_settlement()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "engine-api-test");

        (await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Engine API", "wsp-engine-api")))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest(
                "wsp-engine-api", "Engine API", "EAP", "prj-engine-api")))
            .EnsureSuccessStatusCode();
        var taskResponse = await client.PostAsJsonAsync(
            "/api/v1/projects/prj-engine-api/tasks",
            new CreateTaskRequest(
                "API-only orchestration",
                State: "4-auto-review",
                TaskId: "tsk-engine-api"));
        taskResponse.EnsureSuccessStatusCode();
        var definitionResponse = await client.PutAsJsonAsync(
            "/api/v1/orchestration/projects/prj-engine-api/flow-definition",
            new UpsertFlowDefinitionRequest(
                null,
                [OrchestrationStage.ReviewDecision, OrchestrationStage.CompletionJudge]));
        definitionResponse.EnsureSuccessStatusCode();
        var runResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestration/projects/prj-engine-api/runs",
            new CreateOrchestrationRunRequest(
                "tsk-engine-api",
                """{"agentOutcome":"done"}""",
                "api-run-1"));
        runResponse.EnsureSuccessStatusCode();
        var run = (await runResponse.Content.ReadFromJsonAsync<OrchestrationRunDto>())!;

        var claimResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestration/claims",
            new OrchestrationClaimRequest(
                "engine-api-test",
                "instance-1",
                [OrchestrationStage.ReviewDecision]));
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<OrchestrationClaimResponse>())!;
        Assert.Equal(run.RunId, claim.Run!.RunId);

        var settlement = await client.PostAsJsonAsync(
            $"/api/v1/orchestration/runs/{run.RunId}/stages/complete",
            new CompleteOrchestrationStageRequest(
                "engine-api-test",
                "instance-1",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                OrchestrationStage.ReviewDecision,
                OrchestrationAction.Continue,
                """{"decision":"continue"}""",
                "api-stage-1"));
        settlement.EnsureSuccessStatusCode();
        var advanced = (await settlement.Content.ReadFromJsonAsync<OrchestrationRunDto>())!;
        Assert.Equal("pending", advanced.Status);
        Assert.Equal(OrchestrationStage.CompletionJudge, advanced.CurrentStage);
    }

    [Fact]
    public async Task Runtime_capacity_endpoint_reads_and_versions_the_host_policy()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "capacity-api-test");

        var registration = await client.PutAsJsonAsync(
            "/api/v1/runners/runner-capacity",
            new RegisterRunnerRequest(
                "runner-capacity",
                "host-capacity",
                "host-capacity:1",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor],
                BootstrapMaxParallelism: 3));
        registration.EnsureSuccessStatusCode();

        var current = await client.GetFromJsonAsync<RuntimeCapacitySettingsDto>(
            "/api/v1/hosts/host-capacity/runtime-capacity");
        Assert.Equal(3, current!.MaxParallelism);

        var update = await client.PutAsJsonAsync(
            "/api/v1/hosts/host-capacity/runtime-capacity",
            new UpdateRuntimeCapacitySettingsRequest(
                5,
                85,
                "aggressive",
                current.Version));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<RuntimeCapacitySettingsDto>();
        Assert.Equal(5, updated!.MaxParallelism);
        Assert.Equal(current.Version + 1, updated.Version);

        var staleUpdate = await client.PutAsJsonAsync(
            "/api/v1/hosts/host-capacity/runtime-capacity",
            new UpdateRuntimeCapacitySettingsRequest(
                7,
                80,
                "balanced",
                current.Version));
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
        var error = await staleUpdate.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("resource-version-mismatch", error!.Code);
    }

    [Fact]
    public async Task Runtime_capacity_endpoint_can_provision_a_host_before_registration()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "host-provisioner");

        var create = await client.PutAsJsonAsync(
            "/api/v1/hosts/fresh-host/runtime-capacity",
            new UpdateRuntimeCapacitySettingsRequest(8, 80, "balanced", 0));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<RuntimeCapacitySettingsDto>();

        var registration = await client.PutAsJsonAsync(
            "/api/v1/runners/fresh-runner",
            new RegisterRunnerRequest(
                "fresh-runner",
                "fresh-host",
                "fresh-host:1",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor],
                BootstrapMaxParallelism: 2));
        registration.EnsureSuccessStatusCode();
        var registered = await registration.Content.ReadFromJsonAsync<RunnerDto>();

        Assert.Equal(1, created!.Version);
        Assert.Equal(8, registered!.RuntimeCapacity!.MaxParallelism);
    }

    [Fact]
    public async Task Project_policy_endpoint_versions_a_preprovisioned_host_allowlist()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "host-provisioner");
        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Workspace"));
        workspaceResponse.EnsureSuccessStatusCode();
        var workspace = await workspaceResponse.Content.ReadFromJsonAsync<WorkspaceDto>();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest(workspace!.WorkspaceId, "Project", "PRJ"));
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>();

        var create = await client.PutAsJsonAsync(
            "/api/v1/hosts/fresh-host/project-policy",
            new UpdateHostProjectPolicyRequest(false, [project!.ProjectId], 0));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<HostProjectPolicyDto>();
        var read = await client.GetFromJsonAsync<HostProjectPolicyDto>(
            "/api/v1/hosts/fresh-host/project-policy");

        Assert.Equal(1, created!.Version);
        Assert.False(read!.AllowAllProjects);
        Assert.Equal([project.ProjectId], read.AllowedProjectIds);
        var stale = await client.PutAsJsonAsync(
            "/api/v1/hosts/fresh-host/project-policy",
            new UpdateHostProjectPolicyRequest(true, [], 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Published_contract_fixtures_pin_supported_and_unsupported_mixed_versions()
    {
        var root = RepositoryRoot();
        using var supported = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "contracts", "fixtures", "protocol-v1", "supported-mixed-versions.json")));
        using var unsupported = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "contracts", "fixtures", "protocol-v1", "unsupported-runner.json")));

        Assert.True(TaskServerProtocol.Supports(supported.RootElement.GetProperty("runner").GetProperty("protocolVersion").GetInt32()));
        Assert.False(TaskServerProtocol.Supports(unsupported.RootElement.GetProperty("runner").GetProperty("protocolVersion").GetInt32()));
        Assert.Equal("rejected-before-claim", unsupported.RootElement.GetProperty("runner").GetProperty("expected").GetString());
    }

    [Fact]
    public async Task Task_attempt_timeline_api_exposes_typed_outcome_raw_facts_and_recovery()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());

        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Outcome API", "wsp-outcome"));
        workspaceResponse.EnsureSuccessStatusCode();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest("wsp-outcome", "Outcome API", "OUT", "prj-outcome"));
        projectResponse.EnsureSuccessStatusCode();
        var taskResponse = await client.PostAsJsonAsync(
            "/api/v1/projects/prj-outcome/tasks",
            new CreateTaskRequest("Classify this", State: "2-ready", TaskKey: "OUT-1"));
        taskResponse.EnsureSuccessStatusCode();

        var register = await client.PutAsJsonAsync(
            "/api/v1/runners/runner-outcome",
            new RegisterRunnerRequest(
                "runner-outcome",
                "host-a",
                "instance-a",
                "1.0.0",
                1,
                [ReviewCapabilities.CodingExecutor]));
        register.EnsureSuccessStatusCode();
        var claimResponse = await client.PostAsJsonAsync(
            "/api/v1/runners/runner-outcome/claims",
            new ClaimRequest("runner-outcome", "instance-a"));
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>())!;

        var decision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            claim.Run!.RunId,
            ExecutionAttemptKind.Coding,
            FinalAssistantOutput: "[[TASK_BLOCKED:deck-panel-v1-decision-missing]]",
            ExitCode: 0));
        var completion = await client.PostAsJsonAsync(
            $"/api/v1/runs/{claim.Run.RunId}/completion",
            new CompleteRunRequest(
                "runner-outcome",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                decision.Outcome.ToString(),
                IdempotencyKey: $"completion:{claim.Run.RunId}:typed-outcome",
                Sequence: 1,
                OutcomeDecision: decision));
        completion.EnsureSuccessStatusCode();

        var attempts = await client.GetFromJsonAsync<List<ExecutionAttemptTimelineDto>>(
            "/api/v1/projects/prj-outcome/tasks/OUT-1/attempts");
        var attempt = Assert.Single(attempts!);
        Assert.Equal(claim.Run.RunId, attempt.Run.RunId);
        Assert.Equal(ExecutionOutcomeKind.ExplicitAgentBlocker, attempt.OutcomeDecision!.Outcome);
        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, attempt.OutcomeDecision.RecoveryAction);
        Assert.Equal("deck-panel-v1-decision-missing", attempt.OutcomeDecision.Detail);
        Assert.Equal(
            "[[TASK_BLOCKED:deck-panel-v1-decision-missing]]",
            attempt.OutcomeDecision.RawFacts.FinalAssistantOutput);
    }

    [Fact]
    public void Provider_capacity_uses_the_bounded_unknown_limit_retry()
    {
        var observedAt = new DateTimeOffset(2026, 8, 31, 6, 45, 42, TimeSpan.Zero);

        var evidence = ProviderAccessClassifier.Classify(
            1,
            stdout: null,
            stderr: "Selected model is at capacity. Please try a different model.",
            observedAt: observedAt);

        Assert.Equal(ProviderAccessEvidenceKind.RateLimited, evidence.Kind);
        Assert.Equal(observedAt.Add(ProviderAccessClassifier.UnknownLimitRetry), evidence.LimitedUntil);
        Assert.False(evidence.ResetTimeReported);
    }

    internal static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "agent-taskboard.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TaskServerFactory(
        string dataDirectory,
        IReadOnlyDictionary<string, string?>? overrides = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                };
                if (overrides is not null)
                    foreach (var (key, value) in overrides)
                        values[key] = value;
                configuration.AddInMemoryCollection(values);
            });
        }
    }
}
