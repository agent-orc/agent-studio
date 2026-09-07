using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class EngineContractTests
{
    [Fact]
    public void Engine_env_contract_resolves_identity_credential_and_stage_caps()
    {
        var values = new Dictionary<string, string?>
        {
            ["SERVER_URL"] = "https://tasks.example.test",
            ["CLIENT_ID"] = "engine-a",
            ["CLIENT_CREDENTIAL"] = "secret",
            ["REVIEW_CONCURRENCY"] = "7",
            ["COUNCIL_CONCURRENCY"] = "6",
            ["POST_PROCESSING_CONCURRENCY"] = "5",
            ["GATE_DISPATCH_CONCURRENCY"] = "3",
            ["COMPLETION_JUDGE_CONCURRENCY"] = "2",
        };

        var options = EngineOptions.Parse(key => values.GetValueOrDefault(key));

        Assert.Equal("https://tasks.example.test", options.ServerUrl);
        Assert.Equal("engine-a", options.ClientId);
        Assert.Equal("secret", options.ClientCredential);
        Assert.Equal(7, options.ReviewConcurrency);
        Assert.Equal(6, options.CouncilConcurrency);
        Assert.Equal(5, options.PostProcessingConcurrency);
        Assert.Equal(3, options.GateDispatchConcurrency);
        Assert.Equal(2, options.CompletionJudgeConcurrency);
    }

    [Fact]
    public void Non_loopback_server_requires_https_and_credential()
    {
        var insecure = new Dictionary<string, string?>
        {
            ["SERVER_URL"] = "http://tasks.example.test",
            ["CLIENT_ID"] = "engine-a",
            ["CLIENT_CREDENTIAL"] = "secret",
        };
        var anonymous = new Dictionary<string, string?>
        {
            ["SERVER_URL"] = "https://tasks.example.test",
            ["CLIENT_ID"] = "engine-a",
        };

        Assert.Contains("HTTPS", Assert.Throws<ArgumentException>(
            () => EngineOptions.Parse(key => insecure.GetValueOrDefault(key))).Message);
        Assert.Contains("CLIENT_CREDENTIAL", Assert.Throws<ArgumentException>(
            () => EngineOptions.Parse(key => anonymous.GetValueOrDefault(key))).Message);
    }

    [Fact]
    public void Contained_compose_http_requires_explicit_opt_in_and_still_requires_credential()
    {
        var contained = new Dictionary<string, string?>
        {
            ["SERVER_URL"] = "http://task-server:5071",
            ["CLIENT_ID"] = "compose-engine",
            ["CLIENT_CREDENTIAL"] = "scenario-token",
            ["ALLOW_INSECURE_HTTP"] = "1",
        };

        var options = EngineOptions.Parse(key => contained.GetValueOrDefault(key));

        Assert.Equal("http://task-server:5071", options.ServerUrl);
        contained.Remove("CLIENT_CREDENTIAL");
        Assert.Contains("CLIENT_CREDENTIAL", Assert.Throws<ArgumentException>(
            () => EngineOptions.Parse(key => contained.GetValueOrDefault(key))).Message);
        contained["SERVER_URL"] = "ftp://task-server:21";
        Assert.Contains("HTTP or HTTPS", Assert.Throws<ArgumentException>(
            () => EngineOptions.Parse(key => contained.GetValueOrDefault(key))).Message);
    }

    [Fact]
    public void Version_surface_contains_release_and_git_sha()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VERSION")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var repositoryVersion = File.ReadAllText(Path.Combine(directory.FullName, "VERSION")).Trim();
        Assert.StartsWith($"orchestrator-engine {repositoryVersion} (", EngineVersion.Display);
        Assert.EndsWith(")", EngineVersion.Display);
    }

    [Fact]
    public async Task Flow_definition_is_server_data_and_all_five_engine_stages_reach_completion()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, TimeProvider.System);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Engine", "wsp-engine"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Engine", "ENG", "prj-engine"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Run engine flow", State: "4-auto-review", TaskId: "tsk-engine"),
            "test",
            default);
        var stages = Enum.GetValues<OrchestrationStage>();
        var definition = await store.UpsertFlowDefinitionAsync(
            project.ProjectId,
            new UpsertFlowDefinitionRequest(null, stages),
            "test",
            default);
        var run = await store.CreateOrchestrationRunAsync(
            project.ProjectId,
            new CreateOrchestrationRunRequest(task.TaskId, """{"agentOutcome":"done"}""", "flow-1"),
            "test",
            default);

        Assert.Equal(1, definition.Version);
        foreach (var stage in stages)
        {
            var claim = await store.ClaimOrchestrationAsync(
                new OrchestrationClaimRequest("engine-a", "instance-a", [stage]),
                "engine-a",
                default);
            Assert.Equal("claimed", claim.Status);
            Assert.Equal(run.RunId, claim.Run!.RunId);
            var action = stage == OrchestrationStage.CompletionJudge
                ? OrchestrationAction.Complete
                : OrchestrationAction.Continue;
            run = await store.CompleteOrchestrationStageAsync(
                run.RunId,
                new CompleteOrchestrationStageRequest(
                    "engine-a",
                    "instance-a",
                    claim.Lease!.LeaseId,
                    claim.Lease.Fence,
                    stage,
                    action,
                    """{"ok":true}""",
                    $"settle-{stage}"),
                "engine-a",
                default);
        }

        Assert.Equal("completed", run.Status);
        Assert.Equal(5, run.StageResults!.Count);
        Assert.Equal("5-human-review", (await store.GetTaskAsync(
            project.ProjectId, task.TaskId, default))!.State);
    }

    [Fact]
    public async Task Engine_restart_reclaims_the_server_owned_run_after_lease_expiry()
    {
        using var temp = new TempDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock, minimumLeaseSeconds: 30, maximumLeaseSeconds: 60);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Restart", "wsp-restart"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Restart", "RST", "prj-restart"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Survive restart", State: "4-auto-review", TaskId: "tsk-restart"),
            "test",
            default);
        await store.UpsertFlowDefinitionAsync(
            project.ProjectId,
            new UpsertFlowDefinitionRequest(null, [OrchestrationStage.ReviewDecision]),
            "test",
            default);
        var run = await store.CreateOrchestrationRunAsync(
            project.ProjectId,
            new CreateOrchestrationRunRequest(task.TaskId, "{}", "restart-1"),
            "test",
            default);
        var beforeRestart = await store.ClaimOrchestrationAsync(
            new OrchestrationClaimRequest(
                "engine-a", "process-1", [OrchestrationStage.ReviewDecision], 30),
            "engine-a",
            default);

        clock.Advance(TimeSpan.FromSeconds(31));
        var afterRestart = await store.ClaimOrchestrationAsync(
            new OrchestrationClaimRequest(
                "engine-a", "process-2", [OrchestrationStage.ReviewDecision], 30),
            "engine-a",
            default);

        Assert.Equal("claimed", afterRestart.Status);
        Assert.Equal(run.RunId, afterRestart.Run!.RunId);
        Assert.True(afterRestart.Lease!.Fence > beforeRestart.Lease!.Fence);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CompleteOrchestrationStageAsync(
                run.RunId,
                new CompleteOrchestrationStageRequest(
                    "engine-a",
                    "process-1",
                    beforeRestart.Lease.LeaseId,
                    beforeRestart.Lease.Fence,
                    OrchestrationStage.ReviewDecision,
                    OrchestrationAction.Complete,
                    "{}",
                    "stale-completion"),
                "engine-a",
                default));
        Assert.Equal("stale-fence", stale.Code);
    }

    [Fact]
    public async Task Generic_orchestration_reissues_are_bounded_across_attempts()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, TimeProvider.System);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Reissue", "wsp-reissue"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Reissue", "REI", "prj-reissue"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Bound remote reissue", State: "4-auto-review", TaskId: "tsk-reissue"),
            "test",
            default);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                task = (await store.UpdateTaskAsync(
                    project.ProjectId,
                    task.TaskId,
                    new UpdateTaskRequest(null, null, "4-auto-review", task.Version),
                    "coding-run",
                    default))!;
            }

            var run = await store.CreateOrchestrationRunAsync(
                project.ProjectId,
                new CreateOrchestrationRunRequest(
                    task.TaskId,
                    """{"agentOutcome":"needs-input"}""",
                    $"review-{attempt}"),
                "review-executor",
                default);
            var claim = await store.ClaimOrchestrationAsync(
                new OrchestrationClaimRequest(
                    "engine-a", $"instance-{attempt}", [OrchestrationStage.ReviewDecision]),
                "engine-a",
                default);
            run = await store.CompleteOrchestrationStageAsync(
                run.RunId,
                new CompleteOrchestrationStageRequest(
                    "engine-a",
                    $"instance-{attempt}",
                    claim.Lease!.LeaseId,
                    claim.Lease.Fence,
                    OrchestrationStage.ReviewDecision,
                    OrchestrationAction.Reissue,
                    "{}",
                    $"reissue-{attempt}"),
                "engine-a",
                default);
            task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;

            Assert.Equal(attempt, run.ReissueAttempts);
            if (attempt <= OrchestrationDefaults.MaxReissueAttempts)
            {
                Assert.Equal("reissued", run.Status);
                Assert.Equal("2-ready", task.State);
            }
            else
            {
                Assert.Equal("escalated", run.Status);
                Assert.Equal("5e-escalated", task.State);
            }
        }
    }

    [Fact]
    public async Task Task_version_change_supersedes_stale_decision_without_overwriting_the_operator()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, TimeProvider.System);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Supersede", "wsp-supersede"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(
                workspace.WorkspaceId, "Supersede", "SUP", "prj-supersede"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest(
                "Original review subject", State: "4-auto-review", TaskId: "tsk-supersede"),
            "test",
            default);
        var run = await store.CreateOrchestrationRunAsync(
            project.ProjectId,
            new CreateOrchestrationRunRequest(task.TaskId, "{}", "supersede-run"),
            "review-executor",
            default);
        task = (await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest("Changed while decision was pending", null, null, task.Version),
            "operator",
            default))!;
        var claim = await store.ClaimOrchestrationAsync(
            new OrchestrationClaimRequest(
                "engine-a", "instance-a", [OrchestrationStage.ReviewDecision]),
            "engine-a",
            default);

        run = await store.CompleteOrchestrationStageAsync(
            run.RunId,
            new CompleteOrchestrationStageRequest(
                "engine-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                OrchestrationStage.ReviewDecision,
                OrchestrationAction.Continue,
                "{}",
                "superseded-settlement"),
            "engine-a",
            default);

        Assert.Equal("superseded", run.Status);
        var preserved = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        Assert.Equal("4-auto-review", preserved.State);
        Assert.Equal("Changed while decision was pending", preserved.Title);
    }

    [Fact]
    public void Engine_project_is_a_pure_contract_api_client()
    {
        var root = RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "orchestrator-engine", "OrchestratorEngine.csproj"));
        Assert.Contains("TaskServer.Contracts.csproj", project);
        Assert.DoesNotContain("TaskServer.csproj", project);
        Assert.DoesNotContain("OrchestratorApi.csproj", project);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", project);

        var sources = Directory.EnumerateFiles(
            Path.Combine(root, "orchestrator-engine"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("TaskScanner", text);
            Assert.DoesNotContain("TaskServerStore", text);
            Assert.DoesNotContain("Microsoft.Data.Sqlite", text);
            Assert.DoesNotContain("File.", text);
            Assert.DoesNotContain("Directory.", text);
        }
    }

    private static TaskServerStore Store(
        string path,
        TimeProvider clock,
        int minimumLeaseSeconds = 30,
        int maximumLeaseSeconds = 600)
        => new(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = path,
                MinimumLeaseSeconds = minimumLeaseSeconds,
                MaximumLeaseSeconds = maximumLeaseSeconds,
            }),
            clock);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "agent-taskboard.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"orchestrator-engine-tests-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
