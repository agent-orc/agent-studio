using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Scenario;

/// <summary>
/// The typed step catalogue. Every action id in a scenario document resolves
/// here, and every action publishes the facts its steps may assert over. To add
/// a step for a new feature, add one action here, document its facts in
/// docs/operations/testing/deployment-scenario.md, and add the step to the
/// scenario document.
/// </summary>
public static class ScenarioActions
{
    public const string TopologyReady = "topology-ready";
    public const string BootstrapPrincipals = "bootstrap-principals";
    public const string RegisterRunner = "register-runner";
    public const string SeedProject = "seed-project";
    public const string AwaitClaim = "await-claim";
    public const string AwaitRunCompleted = "await-run-completed";
    public const string AutoReviewHandoff = "auto-review-handoff";
    public const string CompleteTask = "complete-task";
    public const string OrchestratorTurn = "orchestrator-turn";
    public const string BackupStore = "backup-store";
    public const string RestoreStore = "restore-store";
    public const string InventoryHash = "inventory-hash";

    public static IReadOnlyList<string> Known { get; } =
    [
        TopologyReady, BootstrapPrincipals, RegisterRunner, SeedProject, AwaitClaim,
        AwaitRunCompleted, AutoReviewHandoff, CompleteTask, OrchestratorTurn,
        BackupStore, RestoreStore, InventoryHash,
    ];

    public static Task<ScenarioActionResult> ExecuteAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
        => step.Action switch
        {
            TopologyReady => TopologyReadyAsync(context, ct),
            BootstrapPrincipals => BootstrapPrincipalsAsync(context, step, ct),
            RegisterRunner => RegisterRunnerAsync(context, step, ct),
            SeedProject => SeedProjectAsync(context, step, ct),
            AwaitClaim => AwaitClaimAsync(context, step, ct),
            AwaitRunCompleted => AwaitRunCompletedAsync(context, step, ct),
            AutoReviewHandoff => AutoReviewHandoffAsync(context, step, ct),
            CompleteTask => CompleteTaskAsync(context, step, ct),
            OrchestratorTurn => OrchestratorTurnAsync(context, step, ct),
            BackupStore => BackupStoreAsync(context, step, ct),
            RestoreStore => RestoreStoreAsync(context, step, ct),
            InventoryHash => InventoryHashAsync(context, step, ct),
            _ => throw new ScenarioStepException(
                $"Unknown scenario action '{step.Action}'. Known actions: {string.Join(", ", Known)}."),
        };

    // ---------------------------------------------------------------- topology

    private static async Task<ScenarioActionResult> TopologyReadyAsync(
        ScenarioRunContext context,
        CancellationToken ct)
    {
        var protocol = await context.Server.GetAsync<ProtocolRangeDto>("/api/v1/protocol", ct);
        var status = await context.Server.GetAsync<TaskServerStatusDto>(
            "/api/v1/management/status", ct);
        context.ServerVersion = status.ServerVersion;

        var studioReachable = false;
        if (context.Studio is not null)
            studioReachable = await context.Studio.ProbeAsync("/healthz", ct) == 200;

        var facts = new ScenarioFactBuilder()
            .Boolean("server.authority-ready", status.AuthorityReady)
            .Text("server.mode", status.Mode.ToString().ToLowerInvariant())
            .Number("server.schema-version", status.SchemaVersion)
            .Number("protocol.current", protocol.Current)
            .Number("protocol.minimum-supported", protocol.MinimumSupported)
            .Boolean("studio.reachable", studioReachable)
            .Evidence(context.WriteJsonEvidence("topology-status.json", new { status, protocol }));
        return facts.Build();
    }

    // -------------------------------------------------------------- principals

    private static async Task<ScenarioActionResult> BootstrapPrincipalsAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var requested = ReadArray(step, "principals");
        var created = 0;
        var shortestCredential = int.MaxValue;
        var scopesByKind = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in requested)
        {
            var principalId = RequiredText(element, "id", step);
            var kind = RequiredText(element, "kind", step);
            var runnerId = OptionalText(element, "runnerId");
            var issued = await context.Server.PostAsync<IssuedPrincipalCredential>(
                "/api/v1/management/principals",
                new CreatePrincipalRequest(principalId, kind, null, runnerId),
                ct);
            created++;
            shortestCredential = Math.Min(shortestCredential, issued.Credential.Length);
            scopesByKind[kind] = string.Join(
                ",", issued.Principal.Scopes.OrderBy(scope => scope, StringComparer.Ordinal));
        }

        var all = await context.Server.GetAsync<List<PrincipalDto>>(
            "/api/v1/management/principals", ct);

        var facts = new ScenarioFactBuilder()
            .Number("principals.created", created)
            .Number("principals.total", all.Count)
            .Number("principals.shortest-credential", created == 0 ? 0 : shortestCredential);
        foreach (var (kind, scopes) in scopesByKind)
            facts.Text($"principals.{kind}.scopes", scopes);
        facts.Evidence(context.WriteJsonEvidence(
            "principals.json",
            all.Select(principal => new { principal.PrincipalId, principal.Kind, principal.Scopes })));
        return facts.Build();
    }

    // ------------------------------------------------------------------ runner

    private static async Task<ScenarioActionResult> RegisterRunnerAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        if (context.Endpoints.RunnerToken is not { } runnerToken)
            throw new ScenarioStepException(
                "The target provided no runner credential, so no runner can be started.");

        context.Runner = await context.Target.StartRunnerAsync(runnerToken, ct);
        var runnerId = context.Endpoints.RunnerId;
        var timeout = Timeout(step, 60);

        // A registered daemon becomes visible as a host projection: the Task
        // Server publishes it only after the runner has reported its capacity
        // and capabilities, which is the readiness signal a deployment needs.
        var projection = await ScenarioWait.UntilAsync(
            async token =>
            {
                var hosts = await context.Server.GetAsync<List<HostProjectionDto>>(
                    "/api/v1/runners", token);
                return hosts.FirstOrDefault(host =>
                    string.Equals(host.RunnerId, runnerId, StringComparison.Ordinal)
                    && host.Capabilities.Count > 0);
            },
            timeout,
            $"runner '{runnerId}' to publish a host projection",
            () => context.Runner?.EnsureRunning(),
            ct);

        return new ScenarioFactBuilder()
            .Boolean("runner.registered", true)
            .Text("runner.id", projection.RunnerId)
            .Boolean("runner.host-id-present", !string.IsNullOrWhiteSpace(projection.HostId))
            .Number("runner.capabilities.count", projection.Capabilities.Count)
            .Number("runner.capacity.configured", projection.Capacity.Configured)
            .Boolean(
                "runner.capability.coding-executor-ready",
                projection.Capabilities.Any(capability =>
                    string.Equals(
                        capability.Kind, ReviewCapabilities.CodingExecutor, StringComparison.Ordinal)
                    && string.Equals(capability.Status, "ready", StringComparison.Ordinal)))
            .Evidence(context.WriteJsonEvidence("runner-projection.json", projection))
            .Build();
    }

    // ----------------------------------------------------------------- seeding

    private static async Task<ScenarioActionResult> SeedProjectAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var client = context.Studio ?? context.Server;
        var workspaceName = RequiredText(step.With, "workspace", step);
        var projectName = RequiredText(step.With, "project", step);
        var prefix = RequiredText(step.With, "prefix", step);
        var taskElement = RequiredObject(step, "task");

        var workspace = await client.PostAsync<WorkspaceDto>(
            "/api/v1/workspaces", new CreateWorkspaceRequest(workspaceName), ct);
        var project = await client.PostAsync<ProjectDto>(
            "/api/v1/projects",
            new CreateProjectRequest(workspace.WorkspaceId, projectName, prefix),
            ct);
        var task = await client.PostAsync<TaskDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest(
                RequiredText(taskElement, "title", step),
                OptionalText(taskElement, "body"),
                OptionalText(taskElement, "state") ?? "2-ready"),
            ct);

        context.WorkspaceId = workspace.WorkspaceId;
        context.ProjectId = project.ProjectId;
        context.TaskId = task.TaskId;
        context.TaskKey = task.TaskKey;
        context.TaskVersion = task.Version;

        return new ScenarioFactBuilder()
            .Text("project.task-key-prefix", project.TaskKeyPrefix)
            .Boolean(
                "task.key-matches-prefix",
                task.TaskKey.StartsWith(prefix, StringComparison.Ordinal))
            .Text("task.state", task.State)
            .Text("task.title", task.Title)
            .Evidence(context.WriteJsonEvidence("seed.json", new { workspace, project, task }))
            .Build();
    }

    // ------------------------------------------------------------------ claims

    private static async Task<ScenarioActionResult> AwaitClaimAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var history = await AwaitHistoryAsync(
            context,
            step,
            "the runner to accept a work permit",
            Timeout(step, 90),
            candidate => candidate.Runs.Count > 0
                         && candidate.Audit.Any(record => record.Action == "work.permit.accepted"),
            ct);

        var run = history.Runs[^1];
        context.RunId = run.RunId;

        return new ScenarioFactBuilder()
            .Text("task.state", history.Task.State)
            .Number(
                "audit.work-permit-accepted.count",
                history.Audit.Count(record => record.Action == "work.permit.accepted"))
            .Number("run.count", history.Runs.Count)
            .Boolean("run.fence-positive", run.Fence is > 0)
            .Text("run.runner-id", run.RunnerId ?? string.Empty)
            .Build();
    }

    private static async Task<ScenarioActionResult> AwaitRunCompletedAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var expectedState = OptionalText(step.With, "state") ?? "4-auto-review";
        var history = await AwaitHistoryAsync(
            context,
            step,
            $"the run to complete and the task to reach '{expectedState}'",
            Timeout(step, 180),
            candidate => string.Equals(candidate.Task.State, expectedState, StringComparison.Ordinal)
                         && candidate.Events.Any(item => item.Kind == LifecycleEventKinds.RunCompleted),
            ct);

        var run = history.Runs[^1];
        context.RunId = run.RunId;

        var facts = new ScenarioFactBuilder()
            .Text("task.state", history.Task.State)
            .Number(
                "audit.run-completed.count",
                history.Audit.Count(record => record.Action == "run.completed"))
            .Number("events.agent-message.count", Count(history, LifecycleEventKinds.AgentMessage))
            .Number("events.tool-trace.count", Count(history, LifecycleEventKinds.ToolTrace))
            // Reported, not asserted. How many CLI stdout lines survive
            // shipping, and whether a frame is classified as a tool trace or as
            // an unknown frame, varies between identical runs, and under host
            // load the whole narrative can be dropped. Asserting even a floor of
            // one here made the gate red without a product change, so the counts
            // are recorded for the report and the drift is tracked as an open
            // finding instead.
            .Number(
                "events.narrative.count",
                Count(history, LifecycleEventKinds.AgentMessage)
                + Count(history, LifecycleEventKinds.ToolTrace)
                + Count(history, LifecycleEventKinds.ProtocolUnknownFrame))
            .Number("events.run-completed.count", Count(history, LifecycleEventKinds.RunCompleted))
            .Number("events.total", history.Events.Count)
            .Number("artifacts.count", history.Artifacts.Count)
            .Boolean(
                "artifacts.status-md",
                history.Artifacts.Any(artifact =>
                    string.Equals(artifact.Name, "status.md", StringComparison.Ordinal)))
            .Text(
                "result-finalization.status",
                (history.ResultFinalization?.Status ?? ResultFinalizationStatus.None)
                .ToString().ToLowerInvariant())
            .Boolean(
                "events.idempotency-keys-unique",
                history.Events.Select(item => item.IdempotencyKey)
                    .Distinct(StringComparer.Ordinal).Count() == history.Events.Count)
            .Number("cli.invocations", ReadInvocationCount(context))
            .Evidence(context.WriteJsonEvidence("run-history.json", history));

        // The durable result lives in the acknowledged handoff envelope, not on
        // the run projection: that envelope is what a review executor later
        // materializes, so it is the deployment-relevant proof.
        var handoff = await context.Server.GetAsync<ResultHandoffDto>(
            $"/api/v1/runs/{run.RunId}/result-handoff", ct);
        context.ResultSha = handoff.Envelope.ResultSha;
        context.ResultRepositoryId = handoff.Envelope.RepositoryId;
        context.ResultRepositoryUrl = handoff.Envelope.RepositoryUrl;
        facts
            .Boolean("result.handoff-acknowledged", true)
            .Number("result.sha.length", handoff.Envelope.ResultSha.Length)
            .Boolean(
                "result.immutable-ref-present",
                !string.IsNullOrWhiteSpace(handoff.Envelope.ImmutableRemoteRef))
            .Number("result.envelope-digest.length", handoff.EnvelopeDigest.Length)
            .Evidence(context.WriteJsonEvidence("result-handoff.json", handoff));

        var status = history.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Name, "status.md", StringComparison.Ordinal));
        if (status is not null && context.RunId is { } runId)
        {
            var content = await context.Server.GetAsync<ArtifactContentDto>(
                $"/api/v1/runs/{runId}/artifacts/{status.ArtifactId}/content", ct);
            var markdown = Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64));
            facts.Evidence(context.WriteEvidence("status.md", markdown));
            // Reported, not asserted: the summary is derived from the shipped
            // narrative, so it currently flips between Success and Partial for
            // an identical run whenever the terminal line is not shipped.
            facts.Text("status.result", StatusResult(markdown));
        }

        return facts.Build();
    }

    // ------------------------------------------------------------ auto review

    /// <summary>
    /// Turns the real coding result into a fenced review subject and proves a
    /// registered review executor is offered exactly that subject. Submitting a
    /// full review report is review-domain coverage and stays in
    /// TaskServer.Tests.RemoteReviewAuthorityTests; this step proves the
    /// deployment hands the result over.
    /// </summary>
    private static async Task<ScenarioActionResult> AutoReviewHandoffAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var runId = context.RunId
            ?? throw new ScenarioStepException("No completed run was recorded.");
        var resultSha = context.ResultSha
            ?? throw new ScenarioStepException(
                "The completed run published no result SHA, so no review subject can be created.");
        var reviewerId = OptionalText(step.With, "reviewerId") ?? "scenario-reviewer";
        var reviewOutput = await RunReviewCliAsync(context, ct);

        var subject = await context.Server.PostAsync<ReviewSubjectDto>(
            "/api/v1/reviews/subjects",
            // Every source identity comes from the acknowledged handoff envelope,
            // because that is what the Task Server compares the subject against.
            new CreateReviewSubjectRequest(
                context.RequireTaskId(),
                runId,
                context.ResultRepositoryId ?? context.RequireProjectId(),
                context.ResultRepositoryUrl ?? context.Fixture.BareRepository,
                resultSha,
                null,
                null,
                null,
                null,
                "scenario-review-policy-v1",
                new ReviewPlanDto(
                    [new ReviewCommandDto(
                        "verify-build-tests",
                        "build-tests",
                        "sh",
                        ["-c", "sh tests/passing.sh && sh tests/failing.sh"])],
                    ["build-tests"]),
                $"scenario-review:{runId}"),
            ct);

        var reviewerInstance = $"{reviewerId}-instance";
        await context.Server.PutAsync<RunnerDto>(
            $"/api/v1/runners/{reviewerId}",
            new RegisterRunnerRequest(
                reviewerId,
                "scenario-review-host",
                reviewerInstance,
                "1.0.0",
                TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                ]),
            ct);

        var claim = await ScenarioWait.UntilAsync(
            async token =>
            {
                var response = await context.Server.PostAsync<ReviewClaimResponse>(
                    $"/api/v1/runners/{reviewerId}/review-claims",
                    new ReviewClaimRequest(reviewerId, reviewerInstance),
                    token);
                return response.Attempt is null ? null : response;
            },
            Timeout(step, 60),
            "the review subject to be offered to a review executor",
            null,
            ct);

        return new ScenarioFactBuilder()
            .Boolean("review.subject-created", true)
            .Text("review.subject.expected-result-sha", subject.ExpectedResultSha)
            .Text("review.claim.status", claim.Status)
            .Boolean(
                "review.claim.matches-subject",
                string.Equals(claim.Subject?.SubjectId, subject.SubjectId, StringComparison.Ordinal))
            .Boolean("review.claim.fenced", claim.Lease is { Fence: > 0 })
            .Number("review.attempt.number", claim.Attempt!.AttemptNumber)
            .Text("review.cli.passing-test", ReviewLine(reviewOutput, "passing.sh"))
            .Text("review.cli.failing-test", ReviewLine(reviewOutput, "failing.sh"))
            .Evidence(context.WriteEvidence("review-cli.txt", reviewOutput))
            .Evidence(context.WriteJsonEvidence("review-claim.json", claim))
            .Build();
    }

    // ---------------------------------------------------------------- lifecycle

    private static async Task<ScenarioActionResult> CompleteTaskAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var client = context.Studio ?? context.Server;
        var targetState = OptionalText(step.With, "state") ?? "6-completed";
        var path = $"/api/v1/projects/{context.RequireProjectId()}/tasks/{context.RequireTaskKey()}";
        var current = await client.GetAsync<TaskDto>(path, ct);
        var updated = await client.PutAsync<TaskDto>(
            path,
            new UpdateTaskRequest(null, null, targetState, current.Version),
            ct);
        context.TaskVersion = updated.Version;

        var history = await client.GetAsync<TaskHistoryDto>(path + "/history", ct);
        return new ScenarioFactBuilder()
            .Text("task.state", updated.State)
            .Boolean("task.version-advanced", updated.Version > current.Version)
            .Number("run.count", history.Runs.Count)
            .Evidence(context.WriteJsonEvidence("completed-task.json", updated))
            .Build();
    }

    private static async Task<ScenarioActionResult> OrchestratorTurnAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var client = context.Studio ?? context.Server;
        var projectId = context.RequireProjectId();
        var body = OptionalText(step.With, "body") ?? "Scenario orchestrator turn.";
        var summary = OptionalText(step.With, "summary") ?? "Scenario project context";
        var turnId = $"scenario-turn-{projectId}";

        await client.PutAsync<OrchestratorContextDto>(
            $"/api/v1/orchestrator-contexts/projects/{projectId}",
            new { summary },
            ct);

        var fixedClock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var turnsPath = $"/api/v1/orchestrator-contexts/projects/{projectId}/turns";
        await client.PostAsync<OrchestratorContextTurnDto>(
            turnsPath,
            new AppendOrchestratorContextTurnRequest(
                new OrchestratorContextTurnDto(turnId, fixedClock, "user", body)),
            ct);

        // A context receipt is what makes an orchestrator answer auditable: it
        // records which sources were included and against which budget. The
        // server binds it to the persisted user turn it answers, so the answer
        // carries the receipt and the user turn must exist first.
        var answerId = $"{turnId}-answer";
        await client.PostAsync<OrchestratorContextTurnDto>(
            turnsPath,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                answerId,
                fixedClock.AddSeconds(1),
                "orchestrator",
                "Scenario answer recorded with its context receipt.",
                Receipt: new OrchestratorContextReceiptDto(
                    $"{turnId}-receipt",
                    turnId,
                    $"project:{projectId}",
                    fixedClock.AddSeconds(1),
                    new OrchestratorContextBudgetReceiptDto(4000, 8000, 12000, 128),
                    [
                        new OrchestratorContextSourceReceiptDto(
                            "scenario-fixture",
                            "repository",
                            "main",
                            null,
                            "fresh",
                            128,
                            32,
                            "included"),
                    ]))),
            ct);

        var transcript = await client.GetAsync<OrchestratorContextTranscriptResponse>(
            turnsPath, ct);
        var question = transcript.Turns.FirstOrDefault(turn =>
            string.Equals(turn.TurnId, turnId, StringComparison.Ordinal))
            ?? throw new ScenarioStepException("The appended user turn was not replayed.");
        var answer = transcript.Turns.FirstOrDefault(turn =>
            string.Equals(turn.TurnId, answerId, StringComparison.Ordinal))
            ?? throw new ScenarioStepException("The appended answer turn was not replayed.");

        return new ScenarioFactBuilder()
            .Number("orchestrator.turns", transcript.Turns.Count)
            .Text("orchestrator.turn.role", question.Role)
            .Text("orchestrator.turn.body", question.Body)
            .Boolean("orchestrator.receipt-present", answer.Receipt is not null)
            .Boolean(
                "orchestrator.receipt.binds-user-turn",
                string.Equals(answer.Receipt?.UserTurnId, turnId, StringComparison.Ordinal))
            .Number("orchestrator.receipt.sources", answer.Receipt?.Sources.Count ?? 0)
            .Number(
                "orchestrator.receipt.hard-cap-tokens",
                answer.Receipt?.Budget.TotalHardCapTokens ?? 0)
            .Evidence(context.WriteJsonEvidence("orchestrator-transcript.json", transcript))
            .Build();
    }

    // ------------------------------------------------------- backup / restore

    private static async Task<ScenarioActionResult> BackupStoreAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var name = OptionalText(step.With, "name") ?? "scenario";
        var backup = await context.Server.PostAsync<BackupResult>(
            "/api/v1/management/backups", new BackupRequest(name), ct);
        context.BackupId = backup.BackupId;

        return new ScenarioFactBuilder()
            .Boolean("backup.created", true)
            .Number("backup.sha256.length", backup.Sha256.Length)
            .Boolean("backup.size-positive", backup.SizeBytes > 0)
            .Evidence(context.WriteJsonEvidence(
                "backup.json",
                new { backup.BackupId, backup.Sha256, backup.SizeBytes }))
            .Build();
    }

    private static async Task<ScenarioActionResult> RestoreStoreAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var backupId = context.BackupId
            ?? throw new ScenarioStepException("No backup was recorded by an earlier step.");
        var backupDirectory = context.Endpoints.BackupDirectory
            ?? throw new ScenarioStepException("The target exposes no backup directory.");

        var peer = await context.Target.StartEmptyPeerAsync(backupDirectory, ct);
        var peerClient = context.Own(new ScenarioHttpClient(
            peer.TaskServerUrl, peer.ManagementToken, "agent-studio-scenario"));

        var empty = await peerClient.GetAsync<List<WorkspaceDto>>("/api/v1/workspaces", ct);
        var verified = await peerClient.PostAsync<RestoreResult>(
            "/api/v1/management/restore", new RestoreRequest(backupId, VerifyOnly: true), ct);

        // Restoring over a live store is refused unless the deployment is in
        // maintenance mode. The scenario performs the same operator sequence a
        // recovery runbook prescribes, including the return to normal.
        await peerClient.PutAsync<TaskServerStatusDto>(
            "/api/v1/management/mode",
            new ChangeModeRequest(TaskServerMode.Maintenance, "scenario restore rehearsal"),
            ct);
        var restored = await peerClient.PostAsync<RestoreResult>(
            "/api/v1/management/restore", new RestoreRequest(backupId), ct);
        var normal = await peerClient.PutAsync<TaskServerStatusDto>(
            "/api/v1/management/mode",
            new ChangeModeRequest(TaskServerMode.Normal, "scenario restore complete"),
            ct);
        var workspaces = await peerClient.GetAsync<List<WorkspaceDto>>("/api/v1/workspaces", ct);

        context.RestoredPeer = peerClient;
        return new ScenarioFactBuilder()
            .Number("restore.target-workspaces-before", empty.Count)
            .Boolean("restore.verify-only-succeeded", verified.Verified)
            .Boolean("restore.restored", restored.Restored)
            .Boolean("restore.sha-matches-backup", string.Equals(
                verified.Sha256, restored.Sha256, StringComparison.OrdinalIgnoreCase))
            .Number("restore.target-workspaces-after", workspaces.Count)
            .Text("restore.mode-after", normal.Mode.ToString().ToLowerInvariant())
            .Evidence(context.WriteJsonEvidence("restore.json", new { verified, restored }))
            .Build();
    }

    /// <summary>
    /// Canonical inventory digest over everything a deployment must still own
    /// after a restore. Equality of this digest before backup and after restore
    /// is the scenario's durability proof.
    /// </summary>
    private static async Task<ScenarioActionResult> InventoryHashAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var scope = OptionalText(step.With, "scope") ?? "primary";
        var client = scope switch
        {
            "primary" => context.Server,
            "restored" => context.RestoredPeer
                ?? throw new ScenarioStepException("No restored deployment was recorded."),
            _ => throw new ScenarioStepException(
                $"Unknown inventory scope '{scope}'. Use 'primary' or 'restored'."),
        };

        var inventory = await ReadInventoryAsync(client, ct);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inventory)));
        var facts = new ScenarioFactBuilder()
            .Text("inventory.scope", scope)
            .Number("inventory.digest.length", digest.Length)
            .Evidence(context.WriteEvidence($"inventory-{scope}.txt", inventory));

        if (scope == "primary")
        {
            context.InventoryHash = digest;
            facts.Boolean("inventory.recorded", true);
        }
        else
        {
            var expected = context.InventoryHash
                ?? throw new ScenarioStepException(
                    "No primary inventory digest was recorded before the restore.");
            facts.Boolean(
                "inventory.matches-primary",
                string.Equals(digest, expected, StringComparison.Ordinal));
        }

        return facts.Build();
    }

    private static async Task<string> ReadInventoryAsync(
        ScenarioHttpClient client,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        var workspaces = await client.GetAsync<List<WorkspaceDto>>("/api/v1/workspaces", ct);
        foreach (var workspace in workspaces.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("workspace\t").Append(workspace.Name).Append('\n');
            var projects = await client.GetAsync<List<ProjectDto>>(
                $"/api/v1/projects?workspaceId={Uri.EscapeDataString(workspace.WorkspaceId)}", ct);
            foreach (var project in projects.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                builder.Append("project\t").Append(project.Name).Append('\t')
                    .Append(project.TaskKeyPrefix).Append('\n');
                var tasks = await client.GetAsync<List<TaskDto>>(
                    $"/api/v1/projects/{project.ProjectId}/tasks", ct);
                foreach (var task in tasks.OrderBy(item => item.TaskKey, StringComparer.Ordinal))
                {
                    builder.Append("task\t").Append(task.TaskKey).Append('\t')
                        .Append(task.State).Append('\t').Append(task.Title).Append('\n');
                    var history = await client.GetAsync<TaskHistoryDto>(
                        $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}/history", ct);
                    builder.Append("runs\t").Append(history.Runs.Count).Append('\n');
                    builder.Append("events\t").Append(history.Events.Count).Append('\n');
                    foreach (var artifact in history.Artifacts
                                 .OrderBy(item => item.Name, StringComparer.Ordinal))
                        builder.Append("artifact\t").Append(artifact.Name).Append('\t')
                            .Append(artifact.Sha256).Append('\n');
                }
            }
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------ shared

    private static async Task<TaskHistoryDto> AwaitHistoryAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        string description,
        TimeSpan timeout,
        Func<TaskHistoryDto, bool> satisfied,
        CancellationToken ct)
    {
        var client = context.Studio ?? context.Server;
        var path = $"/api/v1/projects/{context.RequireProjectId()}"
                   + $"/tasks/{context.RequireTaskKey()}/history";
        return await ScenarioWait.UntilAsync(
            async token =>
            {
                var history = await client.GetAsync<TaskHistoryDto>(path, token);
                return satisfied(history) ? history : null;
            },
            timeout,
            description,
            () => context.Runner?.EnsureRunning(),
            ct);
    }

    private static async Task<string> RunReviewCliAsync(
        ScenarioRunContext context,
        CancellationToken ct)
    {
        var start = new System.Diagnostics.ProcessStartInfo("sh")
        {
            WorkingDirectory = context.Fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(context.Fixture.ReviewCli);
        start.ArgumentList.Add(Path.Combine(context.Fixture.Root, "seed"));
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new ScenarioStepException("Could not start the scenario review CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return stdout;
    }

    private static string StatusResult(string markdown)
    {
        const string marker = "- Result: ";
        var line = markdown
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.StartsWith(marker, StringComparison.Ordinal));
        return line is null ? "unknown" : line[marker.Length..].Trim();
    }

    private static string ReviewLine(string output, string marker)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .FirstOrDefault(line => line.Contains(marker, StringComparison.Ordinal))
           ?? string.Empty;

    private static long ReadInvocationCount(ScenarioRunContext context)
    {
        var path = context.Fixture.InvocationCounter;
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var value)
            ? value
            : 0;
    }

    private static int Count(TaskHistoryDto history, string kind)
        => history.Events.Count(item => string.Equals(item.Kind, kind, StringComparison.Ordinal));

    private static TimeSpan Timeout(ScenarioStep step, int fallbackSeconds)
        => TimeSpan.FromSeconds(
            step.With.TryGetProperty("timeoutSeconds", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var seconds)
            && seconds > 0
                ? seconds
                : fallbackSeconds);

    private static IReadOnlyList<JsonElement> ReadArray(ScenarioStep step, string name)
        => step.With.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : throw new ScenarioStepException(
                $"Step '{step.Id}' needs a 'with.{name}' array.");

    private static JsonElement RequiredObject(ScenarioStep step, string name)
        => step.With.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new ScenarioStepException($"Step '{step.Id}' needs a 'with.{name}' object.");

    private static string RequiredText(JsonElement element, string name, ScenarioStep step)
        => OptionalText(element, name)
           ?? throw new ScenarioStepException($"Step '{step.Id}' needs a non-empty '{name}'.");

    private static string? OptionalText(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
