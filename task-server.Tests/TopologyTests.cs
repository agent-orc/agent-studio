using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;
using static AgentStudio.TestSupport.BuiltProcessLauncher;
using static AgentStudio.TestSupport.ProcessWaiters;
using RunningProcess = AgentStudio.TestSupport.ManagedProcess;

namespace TaskServer.Tests;

[Trait("Category", "MachineBound")]
[Trait("Category", "ReviewFlaky")]
public sealed class TopologyTests
{
    [Fact(Timeout = 90000)]
    public async Task Remote_concept_run_generates_real_status_without_repeating_core_agent_run()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        using var runnerWork = new TempDirectory();
        using var repository = new TempDirectory();
        using var fixture = new TempDirectory();
        var bareRepository = await CreateBareRepositoryAsync(repository.Path);
        var fakeCli = await CreateFakeCliAsync(fixture.Path);
        var releaseFile = Path.Combine(fixture.Path, "release");
        var invocationCounter = Path.Combine(fixture.Path, "invocations");
        var serverUrl = $"http://127.0.0.1:{FreePort()}";
        var studioUrl = $"http://127.0.0.1:{FreePort()}";

        using var server = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path,
            "--TaskServer:MinimumLeaseSeconds", "5",
            "--TaskServer:MaximumLeaseSeconds", "30");
        await WaitForHttpAsync(serverUrl + "/readyz", server);

        using var studio = StartStudio(root, studioUrl, serverUrl);
        await WaitForHttpAsync(studioUrl + "/healthz", studio);
        using var studioClient = Client(studioUrl);
        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            studioClient,
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Topology proof"));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            studioClient,
            "/api/v1/projects",
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "TOP"));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            studioClient,
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest(
                "Remote concept finalization proof",
                "Produce a decision dossier, publish bounded evidence, and finish with the required terminal sentinel.",
                "2-ready"));

        using var runner = StartBuilt(
            root,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "45",
                ["TOPOLOGY_RELEASE_FILE"] = releaseFile,
                ["TOPOLOGY_INVOCATION_COUNTER"] = invocationCounter,
                ["TOPOLOGY_DONE_ON_FIRST"] = "1",
            },
            "--poll",
            "--server", serverUrl,
            "--runner-id", "topology-runner",
            "--runner-name", "topology-runner",
            "--hostname", "topology-host",
            "--git-remote", bareRepository,
            "--workdir", runnerWork.Path,
            "--cli", fakeCli,
            "--ttl", "15",
            "--max-parallelism", "1",
            "--poll-seconds", "1");

        using var serverClient = ProtocolClient(serverUrl);
        await WaitForAuditCountAsync(serverClient, "work.permit.accepted", 1, runner);
        var activeHistory = await serverClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");
        Assert.NotNull(activeHistory);
        var activeRun = Assert.Single(activeHistory.Runs);
        AssertIndependentParents(studio.Process, server.Process, runner.Process);

        var serverPid = server.Process.Id;
        var runnerPid = runner.Process.Id;
        studio.Stop();
        AssertHealthy(server, serverPid);
        AssertHealthy(runner, runnerPid);

        for (var restart = 0; restart < 3; restart++)
        {
            using var replacementStudio = StartStudio(root, studioUrl, serverUrl);
            await WaitForHttpAsync(studioUrl + "/healthz", replacementStudio);
            using var replacementClient = Client(studioUrl);
            var status = await replacementClient.GetFromJsonAsync<TaskServerStatusDto>("/api/v1/management/status");
            Assert.NotNull(status);
            var replayedActive = await replacementClient.GetFromJsonAsync<TaskHistoryDto>(
                $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");
            Assert.NotNull(replayedActive);
            var replayedRun = Assert.Single(replayedActive.Runs);
            Assert.Equal(activeRun.RunId, replayedRun.RunId);
            Assert.Equal(activeRun.Fence, replayedRun.Fence);
            Assert.Equal("3-progress", replayedActive.Task.State);
            AssertHealthy(server, serverPid);
            AssertHealthy(runner, runnerPid);
            replacementStudio.Stop();
        }

        await File.WriteAllTextAsync(releaseFile, "continue");
        await WaitForAuditCountAsync(serverClient, "run.completed", 1, runner, TimeSpan.FromSeconds(45));
        await WaitForTaskStateAsync(
            serverClient,
            project.ProjectId,
            task.TaskKey,
            "4-auto-review",
            runner,
            TimeSpan.FromSeconds(20));

        using var freshStudio = StartStudio(root, studioUrl, serverUrl);
        await WaitForHttpAsync(studioUrl + "/healthz", freshStudio);
        using var freshClient = Client(studioUrl);
        var history = await freshClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");

        Assert.NotNull(history);
        Assert.Equal(task.TaskId, history.Task.TaskId);
        Assert.Equal("4-auto-review", history.Task.State);
        Assert.Single(history.Runs);
        Assert.Single(history.Events, item =>
            item.Kind == LifecycleEventKinds.AgentMessage
            && item.PayloadJson.Contains("agent_message", StringComparison.Ordinal));
        Assert.Single(history.Events, item => item.Kind == LifecycleEventKinds.ToolTrace);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.RunnerTrace);
        Assert.Single(history.Events, item => item.Kind == LifecycleEventKinds.RunCompleted);
        Assert.Single(history.Events, item => item.Kind == LifecycleEventKinds.PostProcessingCompleted);
        Assert.Single(history.Events, item => item.Kind == LifecycleEventKinds.ResultFinalizationReady);
        Assert.DoesNotContain(history.Events, item => item.Kind == LifecycleEventKinds.Reissued);
        Assert.DoesNotContain(history.Events, item => item.Kind == LifecycleEventKinds.TerminalHandoff);
        Assert.Equal(ResultFinalizationStatus.Ready, history.ResultFinalization!.Status);
        Assert.Equal(2, history.Artifacts.Count);
        Assert.Single(history.Artifacts, item => item.Name.StartsWith("results/proof-attempt-", StringComparison.Ordinal));
        var statusArtifact = Assert.Single(history.Artifacts, item => item.Name == "status.md");
        var statusContent = await serverClient.GetFromJsonAsync<ArtifactContentDto>(
            $"/api/v1/runs/{activeRun.RunId}/artifacts/{statusArtifact.ArtifactId}/content");
        var statusMarkdown = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(statusContent!.ContentBase64));
        Assert.Contains("# Status", statusMarkdown, StringComparison.Ordinal);
        Assert.Contains("- Result: Success", statusMarkdown, StringComparison.Ordinal);
        Assert.Contains("Remote concept finalization proof", statusMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-studio:result-scaffold", statusMarkdown, StringComparison.Ordinal);
        Assert.Equal("1", await File.ReadAllTextAsync(invocationCounter));
        Assert.Equal(history.Events.Max(item => item.Cursor), history.LastCursor);
        Assert.Contains(history.Audit, item => item.Action == "work.permit.accepted");
        Assert.Single(history.Audit, item => item.Action == "run.completed");

        var replayAfterFirstRun = await freshClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history?after=" +
            history.Events.First(item => item.Kind == LifecycleEventKinds.AgentMessage).Cursor);
        Assert.NotNull(replayAfterFirstRun);
        Assert.All(replayAfterFirstRun.Events, item =>
            Assert.True(item.Cursor > history.Events.First(e => e.Kind == LifecycleEventKinds.AgentMessage).Cursor));
        Assert.Contains(replayAfterFirstRun.Events, item => item.Kind == LifecycleEventKinds.RunCompleted);
    }

    [Fact(Timeout = 90000)]
    public async Task Server_outage_and_runner_restart_fail_closed_until_no_overlap_is_proven()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        using var runnerWork = new TempDirectory();
        using var replacementRunnerWork = new TempDirectory();
        using var repository = new TempDirectory();
        using var fixture = new TempDirectory();
        var bareRepository = await CreateBareRepositoryAsync(repository.Path);
        var fakeCli = await CreateFakeCliAsync(fixture.Path);
        var releaseFile = Path.Combine(fixture.Path, "release");
        var invocationCounter = Path.Combine(fixture.Path, "invocations");
        var serverUrl = $"http://127.0.0.1:{FreePort()}";

        using var firstServer = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path,
            "--TaskServer:MinimumLeaseSeconds", "5",
            "--TaskServer:MaximumLeaseSeconds", "15");
        await WaitForHttpAsync(serverUrl + "/readyz", firstServer);
        using var client = ProtocolClient(serverUrl);
        var (project, task) = await SeedReadyTaskAsync(client, "OUT");

        using var originalRunner = StartBuilt(
            root,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "60",
                ["TOPOLOGY_RELEASE_FILE"] = releaseFile,
                ["TOPOLOGY_INVOCATION_COUNTER"] = invocationCounter,
            },
            "--poll",
            "--server", serverUrl,
            "--runner-id", "outage-runner",
            "--runner-name", "outage-runner",
            "--hostname", "outage-host",
            "--git-remote", bareRepository,
            "--workdir", runnerWork.Path,
            "--cli", fakeCli,
            "--ttl", "10",
            "--max-parallelism", "1",
            "--poll-seconds", "1");
        await WaitForAuditCountAsync(client, "work.permit.accepted", 1, originalRunner);

        firstServer.Stop();
        await WaitForOutputAsync(
            originalRunner,
            "renewal safety boundary reached: task-server-unavailable",
            TimeSpan.FromSeconds(15));
        AssertHealthy(originalRunner, originalRunner.Process.Id);

        using var restartedServer = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path,
            "--TaskServer:MinimumLeaseSeconds", "5",
            "--TaskServer:MaximumLeaseSeconds", "15");
        await WaitForHttpAsync(serverUrl + "/readyz", restartedServer);
        using var restartedClient = ProtocolClient(serverUrl);
        var quarantined = await restartedClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");
        Assert.NotNull(quarantined);
        var interruptedRun = Assert.Single(quarantined.Runs);
        Assert.Contains(quarantined.Events, item =>
            item.Kind == LifecycleEventKinds.TaskServerUnavailable);
        Assert.Contains(quarantined.Events, item =>
            item.Kind == LifecycleEventKinds.ProcessUnknown
            && item.PayloadJson.Contains("positive-no-overlap-evidence-required", StringComparison.Ordinal));
        Assert.Equal("3-progress", quarantined.Task.State);

        using (var contender = ProtocolClient(serverUrl))
        {
            await PutAsync(
                contender,
                "/api/v1/runners/contender",
                new RegisterRunnerRequest(
                    "contender",
                    "contender-host",
                    "contender-before-proof",
                    "1.0.0",
                    TaskServerProtocol.Current,
                    [ReviewCapabilities.CodingExecutor]));
            var denied = await PostAsync<ClaimRequest, ClaimResponse>(
                contender,
                "/api/v1/runners/contender/claims",
                new ClaimRequest("contender", "contender-before-proof", 10));
            Assert.Equal("empty", denied.Status);
        }

        originalRunner.Stop();
        Assert.True(originalRunner.Process.HasExited);
        await PostAsync<ResolveUnknownAttemptRequest, LeaseResponse>(
            restartedClient,
            $"/api/v1/management/attempts/{interruptedRun.RunId}/resolve-unknown",
            new ResolveUnknownAttemptRequest(
                $"runner pid {originalRunner.Process.Id} exited and its process tree was reaped",
                "requeue"));

        await File.WriteAllTextAsync(releaseFile, "continue");
        using var replacementRunner = StartBuilt(
            root,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "30",
                ["TOPOLOGY_RELEASE_FILE"] = releaseFile,
                ["TOPOLOGY_INVOCATION_COUNTER"] = invocationCounter,
            },
            "--poll",
            "--server", serverUrl,
            "--runner-id", "replacement-runner",
            "--runner-name", "replacement-runner",
            "--hostname", "replacement-host",
            "--git-remote", bareRepository,
            "--workdir", replacementRunnerWork.Path,
            "--cli", fakeCli,
            "--ttl", "10",
            "--max-parallelism", "1",
            "--poll-seconds", "1");
        await WaitForAuditCountAsync(restartedClient, "run.completed", 1, replacementRunner, TimeSpan.FromSeconds(30));
        await WaitForTaskStateAsync(
            restartedClient,
            project.ProjectId,
            task.TaskKey,
            "4-auto-review",
            replacementRunner,
            TimeSpan.FromSeconds(10));

        var recovered = await restartedClient.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");
        Assert.NotNull(recovered);
        Assert.Equal(2, recovered.Runs.Count);
        Assert.True(recovered.Runs[1].Fence > recovered.Runs[0].Fence);
        Assert.Contains(recovered.Events, item => item.Kind == LifecycleEventKinds.RunnerUnavailable);
        Assert.Contains(recovered.Events, item => item.Kind == LifecycleEventKinds.NoOverlapProven);
        Assert.Contains(recovered.Events, item => item.Kind == LifecycleEventKinds.RunCompleted);
    }

    [Fact(Timeout = 90000)]
    public async Task Brief_runner_transport_interruption_replays_typed_events_idempotently()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        using var runnerWork = new TempDirectory();
        using var repository = new TempDirectory();
        using var fixture = new TempDirectory();
        var bareRepository = await CreateBareRepositoryAsync(repository.Path);
        var fakeCli = await CreateFakeCliAsync(fixture.Path);
        var phaseFile = Path.Combine(fixture.Path, "phase");
        var finishFile = Path.Combine(fixture.Path, "finish");
        var invocationCounter = Path.Combine(fixture.Path, "invocations");
        var serverPort = FreePort();
        var proxyPort = FreePort();
        var serverUrl = $"http://127.0.0.1:{serverPort}";
        var proxyUrl = $"http://127.0.0.1:{proxyPort}";

        using var server = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path,
            "--TaskServer:MinimumLeaseSeconds", "5",
            "--TaskServer:MaximumLeaseSeconds", "30");
        await WaitForHttpAsync(serverUrl + "/readyz", server);
        using var client = ProtocolClient(serverUrl);
        var (project, task) = await SeedReadyTaskAsync(client, "NET");
        var secondTask = await PostAsync<CreateTaskRequest, TaskDto>(
            client,
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest("Must remain unclaimed during partition", null, "2-ready"));

        await using var proxy = new InterruptibleTcpProxy(proxyPort, serverPort);
        await proxy.ResumeAsync();
        using var runner = StartBuilt(
            root,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "45",
                ["TOPOLOGY_PHASE_FILE"] = phaseFile,
                ["TOPOLOGY_FINISH_FILE"] = finishFile,
                ["TOPOLOGY_INVOCATION_COUNTER"] = invocationCounter,
            },
            "--poll",
            "--server", proxyUrl,
            "--runner-id", "transport-runner",
            "--runner-name", "transport-runner",
            "--hostname", "transport-host",
            "--git-remote", bareRepository,
            "--workdir", runnerWork.Path,
            "--cli", fakeCli,
            "--ttl", "20",
            "--max-parallelism", "1",
            "--poll-seconds", "1");
        await WaitForFileAsync(phaseFile, runner, TimeSpan.FromSeconds(20));

        await proxy.PauseAsync();
        await WaitForOutputAsync(runner, "log ingest failed, will retry", TimeSpan.FromSeconds(8));
        var stillReady = await client.GetFromJsonAsync<TaskDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{secondTask.TaskKey}");
        Assert.Equal("2-ready", stillReady!.State);

        await proxy.ResumeAsync();
        await Task.Delay(300);
        await File.WriteAllTextAsync(finishFile, "finish");
        await WaitForTaskStateAsync(
            client,
            project.ProjectId,
            task.TaskKey,
            "4-auto-review",
            runner,
            TimeSpan.FromSeconds(20));

        var history = await client.GetFromJsonAsync<TaskHistoryDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");
        Assert.NotNull(history);
        Assert.Single(history.Events, item =>
            item.Kind == LifecycleEventKinds.AgentMessage
            && item.PayloadJson.Contains("spooled while disconnected", StringComparison.Ordinal));
        Assert.Single(history.Events, item =>
            item.Kind == LifecycleEventKinds.ToolTrace
            && item.PayloadJson.Contains("transport-fixture", StringComparison.Ordinal));
        Assert.Single(history.Events, item => item.Kind == LifecycleEventKinds.RunnerDisconnected);
        Assert.Single(history.Events, item => item.Kind == LifecycleEventKinds.RunnerReconnected);
        Assert.Equal(
            history.Events.Count,
            history.Events.Select(item => item.IdempotencyKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, history.Artifacts.Count);
        Assert.Single(history.Artifacts, item =>
            item.Name.StartsWith("results/proof-attempt-", StringComparison.Ordinal));
        Assert.Single(history.Artifacts, item => item.Name == "status.md");
        Assert.Equal(ResultFinalizationStatus.Ready, history.ResultFinalization!.Status);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.RunCompleted);
    }

    [Fact(Timeout = 90000)]
    public async Task Https_topology_authenticates_studio_and_runner_event_streams()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        using var runnerWork = new TempDirectory();
        using var repository = new TempDirectory();
        using var fixture = new TempDirectory();
        Directory.CreateDirectory(fixture.Path);
        var bareRepository = await CreateBareRepositoryAsync(repository.Path);
        var fakeCli = await CreateFakeCliAsync(fixture.Path);
        var releaseFile = Path.Combine(fixture.Path, "release");
        await File.WriteAllTextAsync(releaseFile, "continue");
        var invocationCounter = Path.Combine(fixture.Path, "invocations");
        var certificatePath = Path.Combine(fixture.Path, "topology-server.pfx");
        const string certificatePassword = "topology-rehearsal";
        using var certificate = CreateServerCertificate();
        await File.WriteAllBytesAsync(
            certificatePath,
            certificate.Export(X509ContentType.Pfx, certificatePassword));
        var certificateSha = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var studioToken = $"studio.{Guid.NewGuid():N}";
        var engineToken = $"engine.{Guid.NewGuid():N}";
        var serverUrl = $"https://127.0.0.1:{FreePort()}";
        var studioUrl = $"http://127.0.0.1:{FreePort()}";

        using var server = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_Kestrel__Certificates__Default__Path"] = certificatePath,
                ["ASPNETCORE_Kestrel__Certificates__Default__Password"] = certificatePassword,
                ["AUTH"] = "bearer",
                ["STUDIO_AUTH_TOKEN"] = studioToken,
                ["ENGINE_AUTH_TOKEN"] = engineToken,
            },
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path);
        await WaitForHttpsAsync(serverUrl + "/readyz", server, certificateSha);

        using var principalHandler = PinnedHandler(certificateSha);
        using var principalClient = new HttpClient(principalHandler)
        {
            BaseAddress = new Uri(serverUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
        principalClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", studioToken);
        principalClient.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var runnerPrincipalResponse = await principalClient.PostAsJsonAsync(
            "/api/v1/management/principals",
            new CreatePrincipalRequest(
                "runner:tls-runner",
                TaskServerPrincipalKinds.Runner,
                RunnerId: "tls-runner"));
        runnerPrincipalResponse.EnsureSuccessStatusCode();
        var runnerToken = (await runnerPrincipalResponse.Content
            .ReadFromJsonAsync<IssuedPrincipalCredential>())!.Credential;

        using var studio = StartBuilt(
            root,
            "studio-bff",
            "agent-studio-bff.dll",
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl,
            "--TaskServer:BearerToken", studioToken,
            "--TaskServer:TlsServerCertificateSha256", certificateSha);
        await WaitForHttpAsync(studioUrl + "/healthz", studio);
        using var studioClient = Client(studioUrl);
        var (project, task) = await SeedReadyTaskAsync(studioClient, "TLS");

        using var anonymousHandler = PinnedHandler(certificateSha);
        using var anonymous = new HttpClient(anonymousHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(serverUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var deniedRead = await anonymous.GetAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRead.StatusCode);

        using var runner = StartBuilt(
            root,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_AUTH_TOKEN"] = runnerToken,
                ["RUNNER_TLS_CERTIFICATE_SHA256"] = certificateSha,
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "30",
                ["TOPOLOGY_RELEASE_FILE"] = releaseFile,
                ["TOPOLOGY_INVOCATION_COUNTER"] = invocationCounter,
            },
            "--poll",
            "--server", serverUrl,
            "--runner-id", "tls-runner",
            "--runner-name", "tls-runner",
            "--hostname", "tls-host",
            "--git-remote", bareRepository,
            "--workdir", runnerWork.Path,
            "--cli", fakeCli,
            "--ttl", "15",
            "--max-parallelism", "1",
            "--poll-seconds", "1");

        await WaitForTaskStateAsync(
            studioClient,
            project.ProjectId,
            task.TaskKey,
            "4-auto-review",
            runner,
            TimeSpan.FromSeconds(30));
        var history = await WaitForTaskEventsAsync(
            studioClient,
            project.ProjectId,
            task.TaskKey,
            runner,
            [LifecycleEventKinds.AgentMessage, LifecycleEventKinds.ToolTrace],
            TimeSpan.FromSeconds(15));
        var run = Assert.Single(history.Runs);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.AgentMessage);
        Assert.Contains(history.Events, item => item.Kind == LifecycleEventKinds.ToolTrace);

        var deniedStream = await anonymous.GetAsync($"/api/v1/runs/{run.RunId}/events");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedStream.StatusCode);
        using var authenticated = new HttpClient(anonymousHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(serverUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
        authenticated.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", studioToken);
        authenticated.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        authenticated.DefaultRequestHeaders.Add(
            TaskServerProtocol.ClientVersionHeaderName,
            "topology-studio");
        var authenticatedEvents = await authenticated.GetFromJsonAsync<List<EventDto>>(
            $"/api/v1/runs/{run.RunId}/events");
        Assert.NotNull(authenticatedEvents);
        Assert.Equal(history.Events.Count, authenticatedEvents.Count);
    }

    [Fact(Timeout = 60000)]
    public async Task Standalone_binary_honors_version_env_migration_and_backup_contracts()
    {
        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        using var backups = new TempDirectory();
        var serverUrl = $"http://127.0.0.1:{FreePort()}";
        var environment = new Dictionary<string, string?>
        {
            ["LISTEN_URL"] = serverUrl,
            ["STORE_PATH"] = data.Path,
            ["BACKUP_PATH"] = backups.Path,
            ["AUTH"] = "none",
        };

        using var version = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            "--version");
        await version.WaitForExitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, version.Process.ExitCode);
        var repositoryVersion = (await File.ReadAllTextAsync(Path.Combine(root, "VERSION"))).Trim();
        Assert.Matches(
            $@"task-server {Regex.Escape(repositoryVersion)}\+sha\.[0-9a-f]{{40}}",
            version.ToString());

        using (var first = StartBuilt(
                   root,
                   "task-server",
                   "task-server.dll",
                   environment))
        {
            await WaitForHttpAsync(serverUrl + "/readyz", first);
            using var client = ProtocolClient(serverUrl);
            var status = await client.GetFromJsonAsync<TaskServerStatusDto>(
                "/api/v1/management/status");
            Assert.Equal(Path.GetFullPath(data.Path), status!.DataDirectory);
            Assert.Contains("+sha.", status.ServerVersion, StringComparison.Ordinal);
        }

        using (var restarted = StartBuilt(
                   root,
                   "task-server",
                   "task-server.dll",
                   environment))
        {
            await WaitForHttpAsync(serverUrl + "/readyz", restarted);
            using var backup = StartBuilt(
                root,
                "task-server",
                "task-server.dll",
                environment,
                "backup",
                "--name",
                "topology");
            await backup.WaitForExitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, backup.Process.ExitCode);
            var json = backup.OutputLines.Last(line => line.StartsWith('{'));
            var result = JsonSerializer.Deserialize<BackupResult>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(result);
            Assert.True(File.Exists(result.Path));
            Assert.StartsWith(Path.GetFullPath(backups.Path), result.Path);
            Assert.Equal(64, result.Sha256.Length);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Offline_import_initializes_a_fresh_store_and_enters_maintenance_from_the_command_line()
    {
        var root = ProtocolTests.RepositoryRoot();
        using var source = new TempDirectory();
        using var data = new TempDirectory();
        using var backups = new TempDirectory();
        var taskDirectory = Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "2-ready", "AGT-1");
        Directory.CreateDirectory(taskDirectory);
        Directory.CreateDirectory(Path.Combine(source.Path, ".metadata"));
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "task.json"),
            "{\"key\":\"AGT-1\",\"title\":\"CLI migration\",\"state\":\"2-ready\"}");
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "attempt-authority.json"), """
            {"authorityEpoch":2,"lastFenceByTask":{"GONE-1":4},"runAttempts":[
              {"attemptId":"run-orphan","taskKey":"GONE-1","repositoryId":"repo","state":2,"lastFence":4,"createdAt":"2026-09-01T10:00:00Z"}
            ],"reviewAttempts":[]}
            """);
        var environment = new Dictionary<string, string?>
        {
            ["STORE_PATH"] = data.Path,
            ["BACKUP_PATH"] = backups.Path,
            ["AUTH"] = "none",
        };

        using var inventoryProcess = StartBuilt(
            root, "task-server", "task-server.dll", environment,
            "inventory", "--source", source.Path, "--workspace", "Workspace");
        await inventoryProcess.WaitForExitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, inventoryProcess.Process.ExitCode);
        var inventory = DeserializeCommandJson<LegacyMigrationInventory>(inventoryProcess);
        Assert.Equal(1, inventory.OrphanedReferences!.CodingAttempts);
        var inventoryPath = Path.Combine(source.Path, "inventory.json");
        await File.WriteAllTextAsync(
            inventoryPath,
            JsonSerializer.Serialize(inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        using (var rejectedImport = StartBuilt(
                   root, "task-server", "task-server.dll", environment,
                   "import", "--source", source.Path, "--inventory", inventoryPath,
                   "--workspace", "Workspace"))
        {
            await rejectedImport.WaitForExitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, rejectedImport.Process.ExitCode);
            Assert.True(rejectedImport.Contains("maintenance-required"), rejectedImport.ToString());
            Assert.True(rejectedImport.Contains("--mode maintenance"), rejectedImport.ToString());
        }

        using var importProcess = StartBuilt(
            root, "task-server", "task-server.dll", environment,
            "import", "--source", source.Path, "--inventory", inventoryPath,
            "--workspace", "Workspace", "--mode", "maintenance");
        await importProcess.WaitForExitAsync(TimeSpan.FromSeconds(20));
        Assert.True(importProcess.Process.ExitCode == 0, importProcess.ToString());
        var imported = DeserializeCommandJson<LegacyMigrationResult>(importProcess);
        Assert.True(imported.Imported);
        Assert.Equal(1, imported.OrphanedReferences!.CodingAttempts);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(data.Path, "task-server.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'mode';";
        Assert.Equal("Maintenance", (string)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT count(*) FROM legacy_migration_orphans WHERE orphaned_task_key = 'GONE-1';";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static T DeserializeCommandJson<T>(RunningProcess process)
    {
        var lines = process.OutputLines;
        var first = Array.FindIndex(lines.ToArray(), line => line.StartsWith('{'));
        Assert.True(first >= 0, $"Command emitted no JSON.{Environment.NewLine}{process}");
        var json = string.Join(Environment.NewLine, lines.Skip(first));
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("Command JSON root was null.");
    }

    private static async Task<(ProjectDto Project, TaskDto Task)> SeedReadyTaskAsync(
        HttpClient client,
        string prefix)
    {
        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            client,
            "/api/v1/workspaces",
            new CreateWorkspaceRequest($"{prefix} workspace"));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            client,
            "/api/v1/projects",
            new CreateProjectRequest(workspace.WorkspaceId, $"{prefix} project", prefix));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            client,
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest(
                "Failure topology proof",
                "Remain active until the harness releases the fixture.",
                "2-ready"));
        return (project, task);
    }

    private static RunningProcess StartStudio(string root, string studioUrl, string serverUrl)
        => StartBuilt(
            root,
            "studio-bff",
            "agent-studio-bff.dll",
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl);

    private static async Task<string> CreateBareRepositoryAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var bare = Path.Combine(directory, "origin.git");
        var seed = Path.Combine(directory, "seed");
        await RunAsync("git", ["init", "--bare", bare], directory);
        await RunAsync("git", ["init", "-b", "main", seed], directory);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "topology fixture\n");
        await RunAsync("git", ["-c", "user.name=Topology Harness", "-c", "user.email=topology@example.invalid", "add", "."], seed);
        await RunAsync("git", ["-c", "user.name=Topology Harness", "-c", "user.email=topology@example.invalid", "commit", "-m", "fixture"], seed);
        await RunAsync("git", ["remote", "add", "origin", bare], seed);
        await RunAsync("git", ["push", "-u", "origin", "main"], seed);
        await RunAsync("git", ["symbolic-ref", "HEAD", "refs/heads/main"], bare);
        return bare;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<string> CreateFakeCliAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "topology-agent.sh");
        await File.WriteAllTextAsync(path, """
            #!/bin/sh
            set -eu
            if [ "${1:-}" = "--version" ]; then
              printf 'topology-agent 1.0.0\n'
              exit 0
            fi
            input_count=0
            if [ -f "$TOPOLOGY_INVOCATION_COUNTER" ]; then
              input_count=$(cat "$TOPOLOGY_INVOCATION_COUNTER")
            fi
            attempt=$((input_count + 1))
            printf '%s' "$attempt" > "$TOPOLOGY_INVOCATION_COUNTER"
            if [ -n "${TOPOLOGY_PHASE_FILE:-}" ]; then
              printf '{"type":"agent_message","text":"spooled while disconnected"}\n'
              printf '{"type":"tool","name":"transport-fixture"}\n'
              printf 'ready\n' > "$TOPOLOGY_PHASE_FILE"
              while [ ! -f "$TOPOLOGY_FINISH_FILE" ]; do
                sleep 0.05
              done
              mkdir -p "$JOB_RESULTS_DIR"
              printf 'transport replay artifact\n' > "$JOB_RESULTS_DIR/proof-attempt-$attempt.txt"
              printf '[[TASK_DONE]]\n'
              exit 0
            fi
            if [ "$attempt" -eq 1 ]; then
              while [ ! -f "$TOPOLOGY_RELEASE_FILE" ]; do
                sleep 0.05
              done
            fi
            mkdir -p "$JOB_RESULTS_DIR"
            printf 'artifact from attempt %s\n' "$attempt" > "$JOB_RESULTS_DIR/proof-attempt-$attempt.txt"
            printf '{"type":"agent_message","text":"attempt %s complete"}\n' "$attempt"
            printf '{"type":"tool","name":"fixture-tool","attempt":%s}\n' "$attempt"
            if [ "${TOPOLOGY_DONE_ON_FIRST:-}" = "1" ] || [ "$attempt" -ne 1 ]; then
              printf '[[TASK_DONE]]\n'
            else
              printf '[[TASK_BLOCKED:bounded-review-reissue]]\n'
            fi
            """);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=agent-studio-topology",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],
                false));
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "copy"),
            "copy",
            X509KeyStorageFlags.Exportable);
    }

    private static void AssertIndependentParents(Process studio, Process server, Process runner)
    {
        Assert.NotEqual(studio.Id, ParentPid(server.Id));
        Assert.NotEqual(studio.Id, ParentPid(runner.Id));
        Assert.NotEqual(server.Id, ParentPid(runner.Id));
        Assert.NotEqual(runner.Id, ParentPid(server.Id));
        Assert.Equal(Environment.ProcessId, ParentPid(studio.Id));
        Assert.Equal(Environment.ProcessId, ParentPid(server.Id));
        Assert.Equal(Environment.ProcessId, ParentPid(runner.Id));
    }

    private static int ParentPid(int pid)
    {
        var line = File.ReadLines($"/proc/{pid}/status")
            .Single(value => value.StartsWith("PPid:", StringComparison.Ordinal));
        return int.Parse(line.AsSpan("PPid:".Length).Trim());
    }

    private static void AssertHealthy(RunningProcess process, int expectedPid)
    {
        Assert.Equal(expectedPid, process.Process.Id);
        Assert.False(process.Process.HasExited, process.ToString());
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {detail}");
        return System.Text.Json.JsonSerializer.Deserialize<TResponse>(
            detail,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task PutAsync<TRequest>(HttpClient client, string path, TRequest request)
    {
        using var response = await client.PutAsJsonAsync(path, request);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {detail}");
    }

    private static async Task WaitForAuditCountAsync(
        HttpClient client,
        string action,
        int expected,
        RunningProcess process,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            AssertHealthy(process, process.Process.Id);
            var records = await client.GetFromJsonAsync<List<AuditRecordDto>>("/api/v1/management/audit");
            if (records?.Count(record => record.Action == action) >= expected) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Audit action '{action}' was not observed {expected} time(s). Process output:{Environment.NewLine}{process}");
    }

    private static async Task WaitForTaskStateAsync(
        HttpClient client,
        string projectId,
        string taskKey,
        string expected,
        RunningProcess process,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            AssertHealthy(process, process.Process.Id);
            var task = await client.GetFromJsonAsync<TaskDto>(
                $"/api/v1/projects/{projectId}/tasks/{taskKey}");
            if (task?.State == expected) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Task '{taskKey}' did not reach '{expected}'. Process output:{Environment.NewLine}{process}");
    }

    private static async Task<TaskHistoryDto> WaitForTaskEventsAsync(
        HttpClient client,
        string projectId,
        string taskKey,
        RunningProcess process,
        IReadOnlyList<string> expectedKinds,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            AssertHealthy(process, process.Process.Id);
            var history = await client.GetFromJsonAsync<TaskHistoryDto>(
                $"/api/v1/projects/{projectId}/tasks/{taskKey}/history");
            if (history is not null
                && expectedKinds.All(kind => history.Events.Any(item => item.Kind == kind)))
                return history;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Task '{taskKey}' did not publish {string.Join(", ", expectedKinds)}. Process output:{Environment.NewLine}{process}");
    }

    private static HttpClient Client(string baseUrl)
        => new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

    private static HttpClient ProtocolClient(string baseUrl)
    {
        var client = Client(baseUrl);
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, "topology-harness");
        return client;
    }

    private sealed class InterruptibleTcpProxy(int listenPort, int targetPort) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<Task> _connections = [];
        private TcpListener? _listener;
        private CancellationTokenSource? _generation;
        private Task? _acceptLoop;

        public Task ResumeAsync()
        {
            lock (_gate)
            {
                if (_listener is not null)
                    return Task.CompletedTask;
                _generation = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, listenPort);
                _listener.Start();
                _acceptLoop = AcceptLoopAsync(_listener, _generation.Token);
            }
            return Task.CompletedTask;
        }

        public async Task PauseAsync()
        {
            TcpListener? listener;
            CancellationTokenSource? generation;
            Task? acceptLoop;
            Task[] connections;
            lock (_gate)
            {
                listener = _listener;
                generation = _generation;
                acceptLoop = _acceptLoop;
                _listener = null;
                _generation = null;
                _acceptLoop = null;
                connections = _connections.ToArray();
                _connections.Clear();
            }

            generation?.Cancel();
            listener?.Stop();
            if (acceptLoop is not null)
                await IgnoreCancellationAsync(acceptLoop);
            if (connections.Length > 0)
                await IgnoreCancellationAsync(Task.WhenAll(connections));
            generation?.Dispose();
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                var connection = ForwardAsync(client, ct);
                lock (_gate) _connections.Add(connection);
            }
        }

        private async Task ForwardAsync(TcpClient inbound, CancellationToken ct)
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                await outbound.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                await using var inboundStream = inbound.GetStream();
                await using var outboundStream = outbound.GetStream();
                var request = inboundStream.CopyToAsync(outboundStream, ct);
                var response = outboundStream.CopyToAsync(inboundStream, ct);
                await Task.WhenAny(request, response);
            }
        }

        private static async Task IgnoreCancellationAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or IOException
                    or SocketException
                    or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync() => await PauseAsync();
    }
}
