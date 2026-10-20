using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

public sealed record RunbookRun(
    string Id, string Name, string EnvironmentId, string State, string RequestedBy,
    string? ApprovedBy, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc,
    IReadOnlyList<DeploymentStep> Steps, IReadOnlyDictionary<string, string> Variables,
    IReadOnlyDictionary<string, string> SecretVariables, string? JobId, string DefinitionCommit);

public sealed record RunbookRunResponse(
    string Id, string Name, string EnvironmentId, string State, string RequestedBy,
    string? ApprovedBy, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc,
    IReadOnlyList<DeploymentStep> Steps, string DefinitionCommit);

/// <summary>Repository-defined operations use the deploy host queue, log store and approval authority.</summary>
public sealed class RunbookService
{
    private readonly object _gate = new();
    private readonly ProjectEnvironmentService _environments;
    private readonly DeployStore _store;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _clock;

    public RunbookService(ProjectEnvironmentService environments, DeployStore store, GitService git,
        ProjectSettingsService settings, TimeProvider clock)
    {
        _environments = environments;
        _store = store;
        _git = git;
        _settings = settings;
        _clock = clock;
    }

    public IReadOnlyList<ProjectRunbookDefinition>? Definitions(string project)
        => _environments.Declarations(project) is null ? null : ReadDefinition(project)?.Definition.Runbooks ?? [];

    public IReadOnlyList<RunbookRunResponse>? Runs(string project, string? environmentId)
    {
        lock (_gate)
        {
            if (_environments.Declarations(project) is null) return null;
            return Sync(project).Where(run => environmentId is null || run.EnvironmentId == environmentId)
                .OrderByDescending(run => run.CreatedAtUtc).Select(ToResponse).ToArray();
        }
    }

    public RunbookRunResponse? Run(string project, string id)
        => Runs(project, null)?.FirstOrDefault(run => run.Id == id);

    public DeploymentLogResponse? Log(string project, string id, string? step)
    {
        if (Run(project, id) is null) return null;
        return new(id, _store.ReadLog(project, id)
            .Where(line => step is null || line.Step == step).ToArray());
    }

    public DeployOutcome<RunbookRunResponse> Request(string project, string environmentId, string name, DeployActor actor)
    {
        lock (_gate)
        {
            var declarations = _environments.Declarations(project);
            if (declarations is null) return DeployOutcome<RunbookRunResponse>.NotFound($"Unknown project '{project}'.");
            var environment = DeployService.MapEnvironments(project, declarations)
                .FirstOrDefault(item => item.Id == environmentId);
            if (environment is null) return DeployOutcome<RunbookRunResponse>.NotFound($"Unknown environment '{environmentId}'.");
            var source = ReadDefinition(project);
            if (source is null) return DeployOutcome<RunbookRunResponse>.Conflict("A valid schema 2 project.yml is required.");
            var definition = source.Value.Definition.Runbooks.FirstOrDefault(item => item.Name == name);
            if (definition is null || definition.Environments.Count > 0 && !definition.Environments.Contains(environment.Kind))
                return DeployOutcome<RunbookRunResponse>.NotFound($"Runbook '{name}' is unavailable in {environment.Name}.");
            var view = _environments.Read(project);
            if (view is null) return DeployOutcome<RunbookRunResponse>.Conflict("Environment state is unavailable.");
            if (view.Jobs.Any(job => job.EnvironmentId == environmentId && job.Status is "queued" or "claimed"))
                return DeployOutcome<RunbookRunResponse>.Conflict("An operation is already active in this environment.");
            var variables = source.Value.Definition.Variables
                .Where(item => item.Environments.Count == 0 || item.Environments.Contains(environment.Kind)).ToArray();
            var plain = variables.Where(item => item.Value is not null)
                .ToDictionary(item => item.Name, item => item.Value!, StringComparer.Ordinal);
            var secrets = variables.Where(item => item.Secret is not null)
                .ToDictionary(item => item.Name, item => item.Secret!, StringComparer.Ordinal);
            var now = _clock.GetUtcNow().UtcDateTime;
            var existing = Sync(project);
            var protectedEnvironment = StandardLifecyclePolicy.RequiresApproval(environment);
            var id = Guid.NewGuid().ToString("N");
            var operations = definition.Steps.Select(step => new DeploymentStep(step, DeploymentStepStates.Pending,
                null, null, LogRef(project, id, step))).ToArray();
            DeploymentStep[] steps = protectedEnvironment
                ? [new(DeployStepNames.Approval, DeploymentStates.AwaitingApproval, now, null,
                    LogRef(project, id, DeployStepNames.Approval)), .. operations]
                : operations;
            var run = new RunbookRun(id, name, environmentId,
                protectedEnvironment ? DeploymentStates.AwaitingApproval : DeploymentStates.Queued,
                actor.Id, null, now, null, null, steps, plain, secrets, null, source.Value.Commit);
            if (!protectedEnvironment)
            {
                var job = _environments.QueueRunbook(project, declarations.First(item => item.Id == environmentId),
                    id, source.Value.Commit, name, definition.Steps, plain, secrets, actor.Id);
                if (job is null) return DeployOutcome<RunbookRunResponse>.Conflict("The runbook job could not be queued.");
                run = run with { JobId = job.Id };
            }
            var log = protectedEnvironment
                ? $"Requested by {actor.Id}. Waiting for approval by {StandardLifecyclePolicy.ApprovalRoles}."
                : $"Queued for {environment.Name} by {actor.Id}.";
            Persist(project, [.. existing, run],
                [new(id, protectedEnvironment ? DeployStepNames.Approval : name, now, log)],
                [new(now, actor.Id, protectedEnvironment ? "runbook-approval-requested" : "runbook-queued",
                    null, id, environmentId, $"Runbook {name}: {log}")]);
            return DeployOutcome<RunbookRunResponse>.Ok(ToResponse(run));
        }
    }

    public DeployOutcome<RunbookRunResponse> Approve(string project, string id, DeployActor actor)
    {
        lock (_gate)
        {
            var run = Sync(project).FirstOrDefault(item => item.Id == id);
            if (run is null) return DeployOutcome<RunbookRunResponse>.NotFound($"Unknown runbook run '{id}'.");
            var rejection = StandardLifecyclePolicy.ApprovalRejection(run.State, actor);
            if (rejection is not null) return actor.IsApprover
                ? DeployOutcome<RunbookRunResponse>.Conflict(rejection)
                : DeployOutcome<RunbookRunResponse>.Forbidden(rejection);
            var declaration = _environments.Declarations(project)?.FirstOrDefault(item => item.Id == run.EnvironmentId);
            if (declaration is null) return DeployOutcome<RunbookRunResponse>.Conflict("The environment is no longer declared.");
            var job = _environments.QueueRunbook(project, declaration, run.Id, run.DefinitionCommit, run.Name,
                run.Steps.Where(item => item.Name != DeployStepNames.Approval).Select(item => item.Name).ToArray(),
                run.Variables, run.SecretVariables, actor.Id);
            if (job is null) return DeployOutcome<RunbookRunResponse>.Conflict("The environment is busy or unavailable.");
            var now = _clock.GetUtcNow().UtcDateTime;
            var approved = run with { State = DeploymentStates.Queued, ApprovedBy = actor.Id, JobId = job.Id,
                Steps = run.Steps.Select(step => step.Name == DeployStepNames.Approval
                    ? step with { State = DeploymentStates.Succeeded, FinishedAtUtc = now } : step).ToArray() };
            Persist(project, _store.ReadRunbookRuns(project).Select(item => item.Id == id ? approved : item).ToArray(),
                [new(id, DeployStepNames.Approval, now, $"Approved by {actor.Id}."),
                    new(id, run.Name, now, $"Queued for {run.EnvironmentId}.")],
                [new(now, actor.Id, "runbook-approved", null, id, run.EnvironmentId,
                    $"Runbook {run.Name} approved and queued.")]);
            return DeployOutcome<RunbookRunResponse>.Ok(ToResponse(approved));
        }
    }

    public DeployOutcome<RunbookRunResponse> Reject(string project, string id, DeployActor actor)
    {
        lock (_gate)
        {
            var run = Sync(project).FirstOrDefault(item => item.Id == id);
            if (run is null) return DeployOutcome<RunbookRunResponse>.NotFound($"Unknown runbook run '{id}'.");
            var rejection = StandardLifecyclePolicy.ApprovalRejection(run.State, actor);
            if (rejection is not null) return actor.IsApprover
                ? DeployOutcome<RunbookRunResponse>.Conflict(rejection)
                : DeployOutcome<RunbookRunResponse>.Forbidden(rejection);
            var now = _clock.GetUtcNow().UtcDateTime;
            var rejected = run with { State = DeploymentStates.Cancelled, FinishedAtUtc = now,
                Steps = run.Steps.Select(step => step.Name == DeployStepNames.Approval
                    ? step with { State = DeploymentStates.Cancelled, FinishedAtUtc = now }
                    : step with { State = DeploymentStepStates.Skipped }).ToArray() };
            Persist(project, _store.ReadRunbookRuns(project).Select(item => item.Id == id ? rejected : item).ToArray(),
                [new(id, DeployStepNames.Approval, now, $"Rejected by {actor.Id}.")],
                [new(now, actor.Id, "runbook-rejected", null, id, run.EnvironmentId,
                    $"Runbook {run.Name} rejected.")]);
            return DeployOutcome<RunbookRunResponse>.Ok(ToResponse(rejected));
        }
    }

    public void SyncAfterHostCall(string project)
    {
        lock (_gate) Sync(project);
    }

    private List<RunbookRun> Sync(string project)
    {
        var runs = _store.ReadRunbookRuns(project).ToList();
        var jobs = _environments.Read(project)?.Jobs ?? [];
        var log = new List<DeploymentLogEntry>();
        var audit = new List<DeployAuditEntry>();
        for (var i = 0; i < runs.Count; i++)
        {
            var waiting = runs[i];
            if (waiting.JobId is not null || waiting.State != DeploymentStates.AwaitingApproval) continue;
            var queued = jobs.FirstOrDefault(job => job.RunbookRunId == waiting.Id && job.RunbookName == waiting.Name);
            if (queued is null) continue;
            var actor = queued.RequestedBy ?? "deploy-host";
            runs[i] = waiting with { JobId = queued.Id, State = DeploymentStates.Queued,
                ApprovedBy = actor, Steps = waiting.Steps.Select(step => step.Name == DeployStepNames.Approval
                    ? step with { State = DeploymentStates.Succeeded, FinishedAtUtc = queued.CreatedAt } : step).ToArray() };
            log.Add(new(waiting.Id, DeployStepNames.Approval, queued.CreatedAt, $"Approval by {actor} recovered from the host queue."));
            audit.Add(new(queued.CreatedAt, actor, "runbook-approved", null, waiting.Id, waiting.EnvironmentId,
                $"Runbook {waiting.Name} approval recovered from the host queue."));
        }
        foreach (var job in jobs.Where(item => item.RunbookName is not null
            && !runs.Any(run => run.Id == item.RunbookRunId)))
        {
            var id = job.RunbookRunId ?? job.Id;
            var protectedEnvironment = _environments.Declarations(project)?.FirstOrDefault(item => item.Id == job.EnvironmentId)
                ?.Kind == "production";
            var steps = job.RunbookSteps.Select(step => new DeploymentStep(step, DeploymentStepStates.Pending,
                null, null, LogRef(project, id, step))).ToArray();
            var approvedBy = protectedEnvironment ? job.RequestedBy : null;
            DeploymentStep[] plan = protectedEnvironment
                ? [new(DeployStepNames.Approval, DeploymentStates.Succeeded, job.CreatedAt, job.CreatedAt,
                    LogRef(project, id, DeployStepNames.Approval)), .. steps]
                : steps;
            runs.Add(new(id, job.RunbookName!, job.EnvironmentId, DeploymentStates.Queued,
                job.RequestedBy ?? "deploy-host", approvedBy, job.CreatedAt, null, null,
                plan, job.RunbookVariables, job.RunbookSecretVariables, job.Id,
                job.RunbookDefinitionCommit ?? "unknown"));
            log.Add(new(id, job.RunbookName!, job.CreatedAt, "Recovered runbook job from the host queue."));
            audit.Add(new(job.CreatedAt, job.RequestedBy ?? "deploy-host", "runbook-recovered", null,
                id, job.EnvironmentId, $"Runbook {job.RunbookName} recovered from the host queue."));
        }
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var job = jobs.FirstOrDefault(item => item.Id == run.JobId);
            if (job is null) continue;
            var state = DeploymentReconciler.StateForJob(job.Status);
            if (state == run.State) continue;
            var started = job.ClaimedAt ?? run.StartedAtUtc;
            var finished = job.FinishedAt;
            var steps = run.Steps.Select((step, index) =>
            {
                if (step.Name == DeployStepNames.Approval) return step;
                var operationIndex = run.ApprovedBy is null ? index : index - 1;
                var result = job.RunbookResults.FirstOrDefault(item => item.Index == operationIndex);
                var hasResult = job.RunbookResults.Any(item => item.Index == operationIndex);
                var stepState = hasResult ? result!.Success ? DeploymentStates.Succeeded : DeploymentStates.Failed
                    : state == DeploymentStates.Running && operationIndex == job.RunbookResults.Count
                        ? DeploymentStates.Running
                        : DeploymentStates.IsFinished(state) ? DeploymentStepStates.Skipped : DeploymentStepStates.Pending;
                return step with { State = stepState,
                    StartedAtUtc = stepState == DeploymentStepStates.Pending ? null : started,
                    FinishedAtUtc = hasResult ? finished : null };
            }).ToArray();
            if (state == DeploymentStates.Running)
                log.Add(new(run.Id, run.Name, started ?? run.CreatedAtUtc, $"Host {job.HostId} started the runbook."));
            if (DeploymentStates.IsFinished(state))
            {
                foreach (var result in job.RunbookResults)
                    log.Add(new(run.Id, job.RunbookSteps[result.Index], finished ?? run.CreatedAtUtc,
                        result.Success ? "Operation succeeded." : "Operation failed."));
                var action = state == DeploymentStates.Succeeded ? "runbook-succeeded" : "runbook-failed";
                audit.Add(new(finished ?? run.CreatedAtUtc, "deploy-host", action, null, run.Id, run.EnvironmentId,
                    $"Runbook {run.Name} {state} on {job.HostId} ({job.Evidence})."));
            }
            runs[i] = run with { State = state, StartedAtUtc = started, FinishedAtUtc = finished, Steps = steps };
        }
        if (log.Count > 0 || audit.Count > 0) Persist(project, runs, log, audit);
        return runs;
    }

    private (ProjectDeploymentDefinition Definition, string Commit)? ReadDefinition(string project)
    {
        var repo = _git.ResolveRepoRootForProject(project);
        if (repo is null) return null;
        var branch = _git.ResolveIntegrationBranch(repo, _settings.Get(project).IntegrationBranch);
        var commit = _git.GetBranchTip(repo, branch);
        if (commit is null) return null;
        var yaml = _git.GetFileAtCommit(repo, commit, ProjectPreparationPaths.Definition);
        if (yaml is null) return null;
        var parsed = ProjectDefinitionReader.Parse(yaml);
        return parsed.IsValid && parsed.Definition?.Deployment is { } definition ? (definition, commit) : null;
    }

    private void Persist(string project, IReadOnlyList<RunbookRun> runs,
        IReadOnlyList<DeploymentLogEntry> log, IReadOnlyList<DeployAuditEntry> audit)
    {
        _store.AppendLog(project, log);
        _store.AppendAudit(project, audit);
        _store.SaveRunbookRuns(project, runs);
    }

    private static string LogRef(string project, string id, string step)
        => $"/api/projects/{Uri.EscapeDataString(project)}/deploy/runbooks/runs/{id}/log?step={Uri.EscapeDataString(step)}";

    private static RunbookRunResponse ToResponse(RunbookRun run)
        => new(run.Id, run.Name, run.EnvironmentId, run.State, run.RequestedBy, run.ApprovedBy,
            run.CreatedAtUtc, run.StartedAtUtc, run.FinishedAtUtc, run.Steps, run.DefinitionCommit);
}
