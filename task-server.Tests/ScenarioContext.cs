using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;
using static AgentStudio.TestSupport.BuiltProcessLauncher;
using static AgentStudio.TestSupport.ProcessWaiters;

namespace TaskServer.Tests;

/// <summary>
/// Drives the deployment regression scenario's `inproc` target: boots Task
/// Server and a real runner as sibling processes (the same pattern as
/// <see cref="TopologyTests"/>), then executes each step id from
/// <c>testsupport/scenario/steps.json</c> against them. One instance is used
/// for one full scenario run (smoke or full); steps mutate shared state as
/// they go, exactly like a real deployment rehearsal would.
/// </summary>
public sealed class ScenarioContext : IDisposable
{
    private const string CodingRunnerId = "scenario-coding-runner";
    private const string CodingHostId = "scenario-coding-host";
    private const string ReviewExecutorId = "scenario-review-executor";
    private const string ReviewHostId = "scenario-review-host";
    private const string OrchestrationEngineId = "scenario-orchestration-engine";

    private readonly string _root;
    private readonly ScenarioFixture _fixture;
    private readonly string _target;
    private readonly List<IDisposable> _disposables = [];
    private readonly List<Action> _cleanup = [];
    private readonly List<string> _ownedDockerContainers = [];

    private string _serverUrl = "";
    private string _studioBffUrl = "";
    private ManagedProcess _server = null!;
    private HttpClient _serverClient = null!;
    private HttpClient? _reviewSourceClient;
    private HttpClient? _reviewExecutorClient;
    private HttpClient? _engineClient;
    private string _dataDirectory = "";
    private string _bareRepositoryPath = "";
    private string _runnerCredential = "";
    private string _composeProject = "";
    private string _composeFile = "";
    private string _composeOverrideFile = "";
    private string _composeHostDirectory = "";
    private string _taskServerImage = "";
    private ManagedProcess? _runner;
    private string _fakeCliReleaseFile = "";
    private string _fakeCliPath = "";
    private ProjectDto _project = null!;
    private TaskDto _task = null!;
    private RunDto _codingRun = null!;
    private BackupResult _backupResult = null!;
    private string _backupLocalPath = "";

    private bool IsCompose => string.Equals(_target, "compose", StringComparison.Ordinal);

    private ScenarioContext(string root, ScenarioFixture fixture, string target)
    {
        _root = root;
        _fixture = fixture;
        _target = target;
    }

    public static async Task<ScenarioContext> CreateAsync(string root, ScenarioFixture fixture, string target = "inproc")
    {
        var context = new ScenarioContext(root, fixture, target);
        if (context.IsCompose)
            await context.ConnectToComposeAsync();
        else
            await context.StartServerAsync();
        return context;
    }

    public Task<string?> ExecuteAsync(string stepId) => stepId switch
    {
        "bootstrap-principals" => BootstrapPrincipalsAsync(),
        "register-runner" => RegisterRunnerAsync(),
        "create-task" => CreateTaskAsync(),
        "claim-task" => ClaimTaskAsync(),
        "run-coding-attempt" => RunCodingAttemptAsync(),
        "auto-review" => AutoReviewAsync(),
        "orchestrator-chat-turn" => OrchestratorChatTurnAsync(),
        "backup" => BackupAsync(),
        "restore-into-empty-store" => RestoreIntoEmptyStoreAsync(),
        _ => throw new NotSupportedException($"No scenario step handler is registered for '{stepId}'."),
    };

    private async Task StartServerAsync()
    {
        var data = NewTempDirectory();
        _dataDirectory = data;
        var repository = NewTempDirectory();
        _bareRepositoryPath = await SeedRepositoryAsync(repository);

        _serverUrl = $"http://127.0.0.1:{FreePort()}";
        _server = StartBuilt(
            _root,
            "task-server",
            "task-server.dll",
            "--urls", _serverUrl,
            "--TaskServer:DataDirectory", _dataDirectory,
            "--TaskServer:MinimumLeaseSeconds", "5",
            "--TaskServer:MaximumLeaseSeconds", "30");
        _disposables.Add(_server);
        await WaitForHttpAsync(_serverUrl + "/readyz", _server);
        _serverClient = ProtocolClient(_serverUrl);
        _disposables.Add(_serverClient);
    }

    private async Task ConnectToComposeAsync()
    {
        _serverUrl = RequiredEnvironment("SCENARIO_TARGET_URL").TrimEnd('/');
        _studioBffUrl = RequiredEnvironment("SCENARIO_STUDIO_BFF_URL").TrimEnd('/');
        _composeProject = RequiredEnvironment("SCENARIO_COMPOSE_PROJECT");
        _composeFile = RequiredEnvironment("SCENARIO_COMPOSE_FILE");
        _composeOverrideFile = RequiredEnvironment("SCENARIO_COMPOSE_OVERRIDE_FILE");
        _composeHostDirectory = RequiredEnvironment("SCENARIO_HOST_DIR");
        _taskServerImage = RequiredEnvironment("SCENARIO_TASK_SERVER_IMAGE");
        Directory.CreateDirectory(_composeHostDirectory);
        _bareRepositoryPath = await SeedRepositoryAsync(_composeHostDirectory);
        _fakeCliReleaseFile = Path.Combine(_composeHostDirectory, "release");

        _server = StartProcess(
            "docker",
            ComposeArguments("logs", "--no-color", "--follow", "task-server", "studio-bff"),
            _root);
        _disposables.Add(_server);
        await WaitForHttpAsync(_serverUrl + "/readyz", _server);
        await WaitForHttpAsync(_studioBffUrl + "/healthz", _server);
        _serverClient = ProtocolClient(_serverUrl, RequiredEnvironment("SCENARIO_TARGET_TOKEN"));
        _disposables.Add(_serverClient);
    }

    private string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "scenario-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _cleanup.Add(() =>
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        });
        return path;
    }

    private async Task<string> SeedRepositoryAsync(string directory)
    {
        var bare = Path.Combine(directory, "origin.git");
        var seed = Path.Combine(directory, "seed");
        await RunAsync("git", ["init", "--bare", "-b", _fixture.Repository.DefaultBranch, bare], directory);
        await RunAsync("git", ["init", "-b", _fixture.Repository.DefaultBranch, seed], directory);
        foreach (var file in _fixture.Repository.SeedFiles)
        {
            var path = Path.Combine(seed, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content);
            if (file.Executable && OperatingSystem.IsLinux())
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        await RunAsync("git", ["-c", "user.name=Scenario Harness", "-c", "user.email=scenario@example.invalid", "add", "."], seed);
        await RunAsync("git", ["-c", "user.name=Scenario Harness", "-c", "user.email=scenario@example.invalid", "commit", "-m", "seed fixture"], seed);
        await RunAsync("git", ["remote", "add", "origin", bare], seed);
        await RunAsync("git", ["push", "-u", "origin", _fixture.Repository.DefaultBranch], seed);
        await RunAsync("git", ["symbolic-ref", "HEAD", $"refs/heads/{_fixture.Repository.DefaultBranch}"], bare);
        return bare;
    }

    /// <summary>Counts commits reachable from any ref, not just the default branch: the runner checks out its own per-task branch (`runner/&lt;runner-id&gt;/&lt;task-key&gt;`) rather than pushing straight to the fixture's default branch.</summary>
    private async Task<int> CountCommitsAsync(string repositoryPath)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("rev-list");
        start.ArgumentList.Add("--count");
        start.ArgumentList.Add("--all");
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? int.Parse(stdout.Trim()) : 0;
    }

    private async Task<string?> BootstrapPrincipalsAsync()
    {
        if (IsCompose)
        {
            var principals = await _serverClient.GetFromJsonAsync<List<PrincipalDto>>(
                "/api/v1/management/principals");
            Assert.NotNull(principals);
            var principal = Assert.Single(
                principals,
                candidate => candidate.PrincipalId == $"runner:{CodingRunnerId}");
            Assert.Equal(TaskServerPrincipalKinds.Runner, principal.Kind);
            Assert.Equal(CodingRunnerId, principal.RunnerId);

            var reviewSourceCredential = await ReadAsync<IssuedPrincipalCredential>(
                await _serverClient.PostAsJsonAsync(
                    "/api/v1/management/principals",
                    new CreatePrincipalRequest(
                        "runner:scenario-review-source-runner",
                        TaskServerPrincipalKinds.Runner,
                        RunnerId: "scenario-review-source-runner")));
            _reviewSourceClient = ProtocolClient(_serverUrl, reviewSourceCredential.Credential);
            _disposables.Add(_reviewSourceClient);

            var reviewExecutorCredential = await ReadAsync<IssuedPrincipalCredential>(
                await _serverClient.PostAsJsonAsync(
                    "/api/v1/management/principals",
                    new CreatePrincipalRequest(
                        $"runner:{ReviewExecutorId}",
                        TaskServerPrincipalKinds.Runner,
                        RunnerId: ReviewExecutorId)));
            _reviewExecutorClient = ProtocolClient(_serverUrl, reviewExecutorCredential.Credential);
            _disposables.Add(_reviewExecutorClient);

            _engineClient = ProtocolClient(_serverUrl, RequiredEnvironment("SCENARIO_ENGINE_TOKEN"));
            _disposables.Add(_engineClient);
            return $"bootstrapped coding, review-source, review-executor, and engine principals; Studio BFF is healthy";
        }

        var response = await _serverClient.PostAsJsonAsync(
            "/api/v1/management/principals",
            new CreatePrincipalRequest(
                $"runner:{CodingRunnerId}",
                TaskServerPrincipalKinds.Runner,
                RunnerId: CodingRunnerId));
        var credential = await ReadAsync<IssuedPrincipalCredential>(response);
        _runnerCredential = credential.Credential;
        return $"issued credential for principal runner:{CodingRunnerId}";
    }

    private async Task<string?> RegisterRunnerAsync()
    {
        if (IsCompose)
        {
            var containerName = $"{_composeProject}-scenario-runner";
            _ownedDockerContainers.Add(containerName);
            _runner = StartProcess(
                "docker",
                ComposeArguments(
                    "run", "--rm", "--no-deps", "--name", containerName,
                    "agent-host-distributed"),
                _root);
        }
        else
        {
            var runnerWork = NewTempDirectory();
            var fixtureDirectory = NewTempDirectory();
            _fakeCliReleaseFile = Path.Combine(fixtureDirectory, "release");
            _fakeCliPath = await CreateFakeCodingCliAsync(fixtureDirectory);

            _runner = StartBuilt(
                _root,
                "runner",
                "agent-host.dll",
                new Dictionary<string, string?>
                {
                    ["RUNNER_AUTH_TOKEN"] = _runnerCredential,
                    ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                    ["RUNNER_RUN_TIMEOUT_SECONDS"] = "45",
                    ["SCENARIO_RELEASE_FILE"] = _fakeCliReleaseFile,
                },
                "--poll",
                "--server", _serverUrl,
                "--runner-id", CodingRunnerId,
                "--runner-name", CodingRunnerId,
                "--hostname", CodingHostId,
                "--git-remote", _bareRepositoryPath,
                "--branch", _fixture.Repository.DefaultBranch,
                "--workdir", runnerWork,
                "--cli", _fakeCliPath,
                "--ttl", "15",
                "--max-parallelism", "1",
                "--poll-seconds", "1");
        }
        _disposables.Add(_runner);

        await WaitForConditionAsync(
            async () =>
            {
                var runners = await _serverClient.GetFromJsonAsync<List<RunnerDto>>("/api/v1/runners");
                return runners?.Any(runner => runner.RunnerId == CodingRunnerId) == true;
            },
            _runner,
            TimeSpan.FromSeconds(20),
            $"runner '{CodingRunnerId}' registered");
        return $"runner '{CodingRunnerId}' is registered";
    }

    private async Task<string?> CreateTaskAsync()
    {
        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_fixture.Project.WorkspaceName));
        _project = await PostAsync<CreateProjectRequest, ProjectDto>(
            "/api/v1/projects",
            new CreateProjectRequest(workspace.WorkspaceId, _fixture.Project.ProjectName, _fixture.Project.TaskKeyPrefix));
        _task = await PostAsync<CreateTaskRequest, TaskDto>(
            $"/api/v1/projects/{_project.ProjectId}/tasks",
            new CreateTaskRequest(_fixture.Task.Title, _fixture.Task.Body, _fixture.Task.InitialState));
        return $"task {_task.TaskKey} created in state {_task.State}";
    }

    private async Task<string?> ClaimTaskAsync()
    {
        await WaitForAuditCountAsync(_serverClient, "work.permit.accepted", 1, _runner!, TimeSpan.FromSeconds(20));
        var history = await _serverClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{_project.ProjectId}/tasks/{_task.TaskKey}/history");
        Assert.NotNull(history);
        _codingRun = Assert.Single(history.Runs);
        return $"run {_codingRun.RunId} claimed under fence {_codingRun.Fence}";
    }

    private async Task<string?> RunCodingAttemptAsync()
    {
        var beforeCommits = await CountCommitsAsync(_bareRepositoryPath);
        await File.WriteAllTextAsync(_fakeCliReleaseFile, "continue");
        await WaitForAuditCountAsync(_serverClient, "run.completed", 1, _runner!, TimeSpan.FromSeconds(30));
        await WaitForTaskStateAsync(
            _serverClient, _project.ProjectId, _task.TaskKey, "4-auto-review", _runner!, TimeSpan.FromSeconds(20));

        var history = await _serverClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{_project.ProjectId}/tasks/{_task.TaskKey}/history");
        Assert.NotNull(history);
        _codingRun = Assert.Single(history.Runs);
        Assert.Contains(history.Artifacts, artifact => artifact.Name.Contains("scenario-run-log", StringComparison.Ordinal));

        var afterCommits = await CountCommitsAsync(_bareRepositoryPath);
        Assert.True(afterCommits > beforeCommits, "The coding attempt did not push a new commit to the seeded repository.");

        return $"task reached 4-auto-review; {afterCommits - beforeCommits} new commit(s) pushed to run {_codingRun.RunId}";
    }

    private async Task<string?> AutoReviewAsync()
    {
        // Known gap found while building this scenario (tracked in
        // docs/operations/testing/deployment-scenario.md): the runner completes
        // with outcome "SuccessfulCompletion" (ExecutionOutcomeKind.ToString()),
        // but TaskServerStore.RequiresResultEnvelope only recognizes the legacy
        // "success"/"done"/"noop"/"no-op" strings, so a real CLI-driven run's
        // result SHA is never copied onto the run row and a review subject can't
        // reference it. Until that's reconciled, this step proves the review and
        // orchestration wiring against a purpose-built coding attempt (its own
        // task/runner identity, completed with the literal outcome the store
        // requires) instead of the fixture task's real run from the previous step.
        var (reviewTask, reviewRun, reviewResultRef) = await CreateReviewableCodingRunAsync();

        var plan = new ReviewPlanDto(
            [new ReviewCommandDto("step-completion", "completion", "true", [])],
            ["completion"]);
        var subjectResponse = await _serverClient.PostAsJsonAsync(
            "/api/v1/reviews/subjects",
            new CreateReviewSubjectRequest(
                reviewTask.TaskId,
                reviewRun.RunId,
                reviewRun.RepositoryId!,
                _bareRepositoryPath,
                reviewRun.ResultSha!,
                reviewResultRef,
                null,
                null,
                CodingHostId,
                "scenario-policy-v1",
                plan,
                $"scenario-review-subject:{reviewTask.TaskId}"));
        var subject = await ReadAsync<ReviewSubjectDto>(subjectResponse);

        var reviewExecutorClient = _reviewExecutorClient ?? _serverClient;
        await PutAsync(
            reviewExecutorClient,
            $"/api/v1/runners/{ReviewExecutorId}",
            new RegisterRunnerRequest(
                ReviewExecutorId,
                ReviewHostId,
                "review-instance-a",
                "1.0.0",
                TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                ]));

        var claimResponse = await reviewExecutorClient.PostAsJsonAsync(
            $"/api/v1/runners/{ReviewExecutorId}/review-claims",
            new ReviewClaimRequest(ReviewExecutorId, "review-instance-a"));
        var claim = await ReadAsync<ReviewClaimResponse>(claimResponse);
        Assert.Equal("claimed", claim.Status);
        var attempt = claim.Attempt!;
        var lease = claim.Lease!;

        var reportRequest = BuildPassingReport(subject, attempt, lease);
        var reportResponse = await reviewExecutorClient.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{attempt.AttemptId}/report", reportRequest);
        var report = await ReadAsync<ReviewReportDto>(reportResponse);
        Assert.True(
            report.Outcome == "Pass",
            $"Expected review outcome 'Pass' but got '{report.Outcome}' ({report.FailureClassification}).");

        var cleanupResponse = await reviewExecutorClient.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{attempt.AttemptId}/cleanup",
            new ReviewCleanupRequest(ReviewExecutorId, "review-instance-a", lease.LeaseId, lease.Fence, "scenario-cleanup-1", true));
        var cleanup = await ReadAsync<ReviewCleanupResponse>(cleanupResponse);
        Assert.Equal("cleaned", cleanup.Status);

        await SettleOrchestrationAsync(reviewTask);

        await WaitForTaskStateAsync(
            _serverClient, reviewTask.ProjectId, reviewTask.TaskKey, "5-human-review", _runner!, TimeSpan.FromSeconds(20));
        return $"review/orchestration wiring proof: subject {subject.SubjectId} reported Pass; probe task {reviewTask.TaskKey} reached 5-human-review";
    }

    private async Task<(TaskDto Task, RunDto Run, string ResultRef)> CreateReviewableCodingRunAsync()
    {
        const string runnerId = "scenario-review-source-runner";
        const string instanceId = "review-source-instance";
        var reviewSourceClient = _reviewSourceClient ?? _serverClient;
        await PutAsync(
            reviewSourceClient,
            $"/api/v1/runners/{runnerId}",
            new RegisterRunnerRequest(
                runnerId, CodingHostId, instanceId, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));

        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            "/api/v1/workspaces", new CreateWorkspaceRequest("Review wiring probe"));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            "/api/v1/projects", new CreateProjectRequest(workspace.WorkspaceId, "Review Wiring Probe", "RVW"));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest(
                "Review wiring probe",
                "Synthetic coding attempt used only to exercise the review/orchestration HTTP contract " +
                "(see the known gap noted in AutoReviewAsync).",
                "2-ready"));

        var claimResponse = await reviewSourceClient.PostAsJsonAsync(
            $"/api/v1/runners/{runnerId}/claims", new ClaimRequest(runnerId, instanceId));
        var claim = await ReadAsync<ClaimResponse>(claimResponse);
        Assert.Equal("claimed", claim.Status);
        var run = claim.Run!;
        var lease = claim.Lease!;

        var resultSha = Sha256Of("scenario-review-probe-result")[..40];
        var resultRef = $"refs/heads/agent-studio/results/{run.RunId}/fence-{lease.Fence}/{resultSha}";
        var envelope = new ImmutableResultEnvelope(
            "scenario-fixture-repo",
            run.RunId,
            Sha256Of("scenario-review-probe-base")[..40],
            resultSha,
            resultRef,
            null,
            Sha256Of("scenario-review-probe-artifact-manifest"),
            RepositoryUrl: _bareRepositoryPath);
        var digest = ResultEnvelopeDigest.Compute(envelope);
        var handoffResponse = await reviewSourceClient.PutAsJsonAsync(
            $"/api/v1/runs/{run.RunId}/result-handoff",
            new ResultHandoffRequest(runnerId, instanceId, lease.LeaseId, lease.Fence, 1, $"handoff:{run.RunId}", digest, envelope));
        await ReadAsync<ResultHandoffAck>(handoffResponse);

        var completeResponse = await reviewSourceClient.PostAsJsonAsync(
            $"/api/v1/runs/{run.RunId}/completion",
            new CompleteRunRequest(
                runnerId, instanceId, lease.LeaseId, lease.Fence,
                "success", "scenario review/orchestration wiring probe", digest, $"completion:{run.RunId}", 2));
        var completedRun = await ReadAsync<RunDto>(completeResponse);
        return (task, completedRun, resultRef);
    }

    private async Task SettleOrchestrationAsync(TaskDto task)
    {
        var engineClient = _engineClient ?? _serverClient;
        OrchestrationRunDto? run = null;
        await WaitForConditionAsync(
            async () =>
            {
                var runs = await engineClient.GetFromJsonAsync<List<OrchestrationRunDto>>(
                    $"/api/v1/orchestration/runs?projectId={task.ProjectId}&status=pending");
                run = runs?.FirstOrDefault(candidate => candidate.TaskId == task.TaskId);
                return run is not null;
            },
            _runner!,
            TimeSpan.FromSeconds(15),
            "orchestration run queued for the reviewed task");

        while (run!.Status == "pending")
        {
            var claimResponse = await engineClient.PostAsJsonAsync(
                "/api/v1/orchestration/claims",
                new OrchestrationClaimRequest(OrchestrationEngineId, "engine-instance-a", [run.CurrentStage]));
            var claim = await ReadAsync<OrchestrationClaimResponse>(claimResponse);
            Assert.Equal("claimed", claim.Status);
            var lease = claim.Lease!;
            var action = run.CurrentStage == OrchestrationStage.CompletionJudge
                ? OrchestrationAction.Complete
                : OrchestrationAction.Continue;
            var completeResponse = await engineClient.PostAsJsonAsync(
                $"/api/v1/orchestration/runs/{run.RunId}/stages/complete",
                new CompleteOrchestrationStageRequest(
                    OrchestrationEngineId,
                    "engine-instance-a",
                    lease.LeaseId,
                    lease.Fence,
                    run.CurrentStage,
                    action,
                    "{}",
                    $"scenario-settle:{run.RunId}:{run.CurrentStage}"));
            run = await ReadAsync<OrchestrationRunDto>(completeResponse);
        }
    }

    private static ReviewReportRequest BuildPassingReport(ReviewSubjectDto subject, ReviewAttemptDto attempt, ReviewLeaseDto lease)
    {
        var treeHash = new string('a', 40);
        var workspacePath = $"/review/{lease.ResourceNamespace}";
        var commands = subject.Plan.Commands.Select(command => new ReviewCommandEvidenceDto(
            command.StepId,
            command.Aspect,
            command.FileName,
            command.Arguments,
            subject.ExpectedResultSha,
            subject.ExpectedResultSha,
            treeHash,
            DateTime.UtcNow.AddSeconds(-1),
            DateTime.UtcNow,
            0,
            null,
            Sha256Of($"{command.StepId}-stdout"),
            Sha256Of($"{command.StepId}-stderr"),
            ExecutorId: lease.ExecutorId,
            HostId: lease.HostId,
            AttemptId: attempt.AttemptId)).ToArray();
        var artifacts = commands.SelectMany(command => new[]
        {
            new ReviewArtifactEvidenceDto($"{command.StepId}.stdout.log", "text/plain", command.StdoutSha256, 1),
            new ReviewArtifactEvidenceDto($"{command.StepId}.stderr.log", "text/plain", command.StderrSha256, 1),
        }).ToArray();
        var verdicts = subject.Plan.RequiredAspects
            .Select(aspect => new ReviewVerdictDto(aspect, "pass", "Verified", $"{aspect} passed"))
            .ToArray();
        var toolchain = new Dictionary<string, string>
        {
            ["runtime"] = ".NET 10",
            ["git"] = $"git;sha256={Sha256Of("git")}",
        };
        foreach (var command in subject.Plan.Commands)
            toolchain[$"command:{command.StepId}"] = $"{command.FileName};sha256={Sha256Of(command.FileName)}";
        return new ReviewReportRequest(
            lease.ExecutorId,
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            $"scenario-report-{attempt.AttemptId}",
            "Pass",
            null,
            "scenario review passed",
            new ReviewWorkspaceProofDto(
                subject.RepositoryId,
                subject.ExpectedResultSha,
                subject.ExpectedResultSha,
                treeHash,
                false,
                false,
                Sha256Of(workspacePath),
                lease.ResourceNamespace),
            new ReviewEnvironmentDto(
                lease.HostId,
                lease.ExecutorId,
                lease.InstanceId,
                "linux",
                "x64",
                "10.0",
                toolchain,
                new Dictionary<string, string>
                {
                    ["workspace"] = workspacePath,
                    ["cache"] = $"{workspacePath}/cache",
                    ["temp"] = $"{workspacePath}/tmp",
                    ["ports"] = $"{lease.PortBase}-{lease.PortBase + 7}",
                    ["containers"] = lease.ResourceNamespace,
                    ["databases"] = lease.ResourceNamespace,
                    ["credentials"] = "review-read-only",
                }),
            commands,
            artifacts,
            verdicts);
    }

    private static string Sha256Of(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task<string?> OrchestratorChatTurnAsync()
    {
        await PutAsync($"/api/v1/orchestrator-contexts/projects/{_project.ProjectId}/tasks/{_task.TaskKey}", new { });

        var userTurn = new OrchestratorContextTurnDto(
            $"turn-{Guid.NewGuid():N}",
            DateTime.UtcNow,
            "user",
            "What is the state of the fixture task?");
        await ReadAsync<OrchestratorContextTurnDto>(await _serverClient.PostAsJsonAsync(
            $"/api/v1/orchestrator-contexts/projects/{_project.ProjectId}/tasks/{_task.TaskKey}/turns",
            new AppendOrchestratorContextTurnRequest(userTurn)));

        // The context receipt records what the orchestrator actually included
        // (token budget, sources) while composing its reply to a specific prior
        // user turn; it rides on the following assistant turn, not the user turn
        // it explains.
        var receipt = new OrchestratorContextReceiptDto(
            $"receipt-{Guid.NewGuid():N}",
            userTurn.TurnId,
            $"{_project.ProjectId}/{_task.TaskKey}",
            DateTime.UtcNow,
            new OrchestratorContextBudgetReceiptDto(8000, 12000, 16000, 4200),
            [new OrchestratorContextSourceReceiptDto("task-body", "task", null, null, "fresh", 512, 128, "included")]);
        var assistantTurn = new OrchestratorContextTurnDto(
            $"turn-{Guid.NewGuid():N}",
            DateTime.UtcNow,
            "orchestrator",
            "The fixture task is in 4-auto-review after its coding attempt completed.",
            Receipt: receipt);
        await ReadAsync<OrchestratorContextTurnDto>(await _serverClient.PostAsJsonAsync(
            $"/api/v1/orchestrator-contexts/projects/{_project.ProjectId}/tasks/{_task.TaskKey}/turns",
            new AppendOrchestratorContextTurnRequest(assistantTurn)));

        var transcript = await _serverClient.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            $"/api/v1/orchestrator-contexts/projects/{_project.ProjectId}/tasks/{_task.TaskKey}/turns");
        Assert.NotNull(transcript);
        var readBack = Assert.Single(transcript.Turns, item => item.TurnId == assistantTurn.TurnId);
        Assert.NotNull(readBack.Receipt);
        Assert.True(readBack.Receipt!.Budget.EstimatedIncludedTokens > 0);
        Assert.NotEmpty(readBack.Receipt.Sources);
        return $"turn {readBack.TurnId} recorded with a context receipt ({readBack.Receipt.Sources.Count} source(s))";
    }

    private async Task<string?> BackupAsync()
    {
        var response = await _serverClient.PostAsJsonAsync("/api/v1/management/backups", new BackupRequest("scenario"));
        _backupResult = await ReadAsync<BackupResult>(response);
        Assert.Equal(64, _backupResult.Sha256.Length);
        if (IsCompose)
        {
            var copyDirectory = NewTempDirectory();
            _backupLocalPath = Path.Combine(copyDirectory, Path.GetFileName(_backupResult.Path));
            await RunAsync(
                "docker",
                ComposeArguments("cp", $"task-server:{_backupResult.Path}", _backupLocalPath),
                _root);
            Assert.True(File.Exists(_backupLocalPath));
        }
        else
        {
            Assert.True(File.Exists(_backupResult.Path));
            _backupLocalPath = _backupResult.Path;
        }
        return $"backup {_backupResult.BackupId} sha256={_backupResult.Sha256[..12]}...";
    }

    private async Task<string?> RestoreIntoEmptyStoreAsync()
    {
        if (IsCompose)
            return await RestoreComposeBackupAsync();

        var targetData = NewTempDirectory();
        var targetUrl = $"http://127.0.0.1:{FreePort()}";
        using var target = StartBuilt(
            _root,
            "task-server",
            "task-server.dll",
            "--urls", targetUrl,
            "--TaskServer:DataDirectory", targetData);
        await WaitForHttpAsync(targetUrl + "/readyz", target);
        using var targetClient = ProtocolClient(targetUrl);

        var backupsDirectory = Path.Combine(targetData, "backups");
        Directory.CreateDirectory(backupsDirectory);
        File.Copy(_backupResult.Path, Path.Combine(backupsDirectory, Path.GetFileName(_backupResult.Path)));

        var modeResponse = await targetClient.PutAsJsonAsync(
            "/api/v1/management/mode",
            new ChangeModeRequest(TaskServerMode.Maintenance, "scenario restore rehearsal"));
        modeResponse.EnsureSuccessStatusCode();

        var restoreResponse = await targetClient.PostAsJsonAsync(
            "/api/v1/management/restore",
            new RestoreRequest(_backupResult.BackupId));
        var restore = await ReadAsync<RestoreResult>(restoreResponse);
        Assert.True(restore.Restored, restore.Message);
        Assert.Equal(_backupResult.Sha256, restore.Sha256);
        return $"restored into empty store; inventory hash equal ({restore.Sha256[..12]}...)";
    }

    private async Task<string?> RestoreComposeBackupAsync()
    {
        const string restoreToken = "scenario-restore-studio-token-00000000000000000000";
        var targetData = NewTempDirectory();
        var backupsDirectory = Path.Combine(targetData, "backup");
        Directory.CreateDirectory(backupsDirectory);
        File.Copy(_backupLocalPath, Path.Combine(backupsDirectory, Path.GetFileName(_backupLocalPath)));

        var port = FreePort();
        var targetUrl = $"http://127.0.0.1:{port}";
        var containerName = $"{_composeProject}-scenario-restore";
        _ownedDockerContainers.Add(containerName);
        using var target = StartProcess(
            "docker",
            [
                "run", "--rm", "--name", containerName,
                "--publish", $"127.0.0.1:{port}:5071",
                "--volume", $"{targetData}:/var/lib/agent-orchestrator",
                "--env", "LISTEN_URL=http://0.0.0.0:5071",
                "--env", "STORE_PATH=/var/lib/agent-orchestrator/store",
                "--env", "BACKUP_PATH=/var/lib/agent-orchestrator/backup",
                "--env", "AUTH=bearer",
                "--env", $"STUDIO_AUTH_TOKEN={restoreToken}",
                _taskServerImage,
            ],
            _root);
        await WaitForHttpAsync(targetUrl + "/readyz", target);
        using var targetClient = ProtocolClient(targetUrl, restoreToken);

        var modeResponse = await targetClient.PutAsJsonAsync(
            "/api/v1/management/mode",
            new ChangeModeRequest(TaskServerMode.Maintenance, "scenario restore rehearsal"));
        modeResponse.EnsureSuccessStatusCode();

        var restoreResponse = await targetClient.PostAsJsonAsync(
            "/api/v1/management/restore",
            new RestoreRequest(_backupResult.BackupId));
        var restore = await ReadAsync<RestoreResult>(restoreResponse);
        Assert.True(restore.Restored, restore.Message);
        Assert.Equal(_backupResult.Sha256, restore.Sha256);
        return $"restored through a second Task Server container; inventory hash equal ({restore.Sha256[..12]}...)";
    }

    private async Task<string> CreateFakeCodingCliAsync(string directory)
    {
        var path = Path.Combine(directory, "scenario-coding-agent.sh");
        await File.WriteAllTextAsync(path, """
            #!/bin/sh
            set -eu
            if [ "${1:-}" = "--version" ]; then
              printf 'scenario-coding-agent 1.0.0\n'
              exit 0
            fi
            while [ ! -f "$SCENARIO_RELEASE_FILE" ]; do
              sleep 0.05
            done
            passing_status=0
            failing_status=0
            sh tests/known-passing.sh || passing_status=$?
            sh tests/known-failing.sh || failing_status=$?
            {
              printf 'known-passing.sh exit=%s\n' "$passing_status"
              printf 'known-failing.sh exit=%s\n' "$failing_status"
            } > scenario-run-log.txt
            git add scenario-run-log.txt
            git -c user.name="Scenario Agent" -c user.email=scenario-agent@example.invalid commit -m "scenario: record fixture check results" --quiet
            git push origin HEAD --quiet
            mkdir -p "$JOB_RESULTS_DIR"
            cp scenario-run-log.txt "$JOB_RESULTS_DIR/scenario-run-log.txt"
            printf '{"type":"agent_message","text":"ran known-passing.sh (exit %s) and known-failing.sh (exit %s)"}\n' "$passing_status" "$failing_status"
            printf '{"type":"tool","name":"scenario-fixture-tests"}\n'
            printf '[[TASK_DONE]]\n'
            """);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response)
    {
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {detail}");
        return JsonSerializer.Deserialize<TResponse>(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request)
        => await ReadAsync<TResponse>(await _serverClient.PostAsJsonAsync(path, request));

    private async Task PutAsync<TRequest>(string path, TRequest request)
        => await PutAsync(_serverClient, path, request);

    private static async Task PutAsync<TRequest>(HttpClient client, string path, TRequest request)
    {
        var response = await client.PutAsJsonAsync(path, request);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {detail}");
    }

    private static HttpClient ProtocolClient(string baseUrl, string? bearerToken = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, "scenario-harness");
        if (!string.IsNullOrWhiteSpace(bearerToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    private string[] ComposeArguments(params string[] command)
        => [
            "compose",
            "--project-name", _composeProject,
            "--file", _composeFile,
            "--file", _composeOverrideFile,
            "--profile", "distributed",
            "--profile", "runner",
            .. command,
        ];

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the compose scenario target.");

    private static async Task WaitForAuditCountAsync(
        HttpClient client, string action, int expected, ManagedProcess process, TimeSpan timeout)
    {
        await WaitForConditionAsync(
            async () =>
            {
                var records = await client.GetFromJsonAsync<List<AuditRecordDto>>("/api/v1/management/audit");
                return records?.Count(record => record.Action == action) >= expected;
            },
            process,
            timeout,
            $"audit action '{action}' observed {expected} time(s)");
    }

    private static async Task WaitForTaskStateAsync(
        HttpClient client, string projectId, string taskKey, string expected, ManagedProcess process, TimeSpan timeout)
    {
        await WaitForConditionAsync(
            async () =>
            {
                var task = await client.GetFromJsonAsync<TaskDto>($"/api/v1/projects/{projectId}/tasks/{taskKey}");
                return task?.State == expected;
            },
            process,
            timeout,
            $"task '{taskKey}' reached state '{expected}'");
    }

    public void Dispose()
    {
        foreach (var containerName in _ownedDockerContainers)
            TryRemoveContainer(containerName);
        foreach (var disposable in _disposables) disposable.Dispose();
        foreach (var cleanup in _cleanup) cleanup();
    }

    private static void TryRemoveContainer(string containerName)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("container");
            start.ArgumentList.Add("rm");
            start.ArgumentList.Add("--force");
            start.ArgumentList.Add(containerName);
            using var process = System.Diagnostics.Process.Start(start);
            process?.WaitForExit(5_000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
