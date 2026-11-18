using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Projects;
using AgentStudio.Security;

namespace AgentStudio.Deploy;

public sealed record RunbookRun(
    string Id, string Name, string EnvironmentId, string State, string RequestedBy,
    string? ApprovedBy, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc,
    IReadOnlyList<DeploymentStep> Steps, IReadOnlyDictionary<string, string> Variables,
    IReadOnlyDictionary<string, string> SecretVariables, string? JobId, string DefinitionCommit,
    string? IncidentId = null, string? IncidentCardId = null, string? ReleaseId = null)
{
    /// <summary>The definition's <c>verify</c> value; the run succeeds only when it passes.</summary>
    public string? Verify { get; init; }
    /// <summary>The state the host job reached. While it is succeeded and <see cref="Verify"/> is unchecked, the run is running.</summary>
    public string? HostState { get; init; }
    public RunbookVerificationResult? Verification { get; init; }
}

/// <param name="Target">What was checked, for example "health check ready".</param>
public sealed record RunbookVerificationResult(string Target, bool Passed, string Detail, DateTime CheckedAtUtc);

public sealed record RunbookRunResponse(
    string Id, string Name, string EnvironmentId, string State, string RequestedBy,
    string? ApprovedBy, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc,
    IReadOnlyList<DeploymentStep> Steps, string DefinitionCommit)
{
    public string RequestedByName { get; init; } = RequestedBy;
    public string? ApprovedByName { get; init; } = ApprovedBy;
    /// <summary>What the run verifies after its steps, for example "health check ready"; null without verify.</summary>
    public string? Verifies { get; init; }
    public RunbookVerificationResult? Verification { get; init; }
}

/// <summary>
/// Repository-defined operations use the deploy host queue, log store and approval authority.
/// A runbook with <c>verify</c> runs its check from the control plane after the host reports
/// success, and only a passing check makes the run succeed.
/// </summary>
public sealed class RunbookService
{
    private readonly object _gate = new();
    private readonly ProjectEnvironmentService _environments;
    private readonly DeployStore _store;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunbookService> _logger;
    private readonly IncidentActions? _incidentActions;
    private readonly RunbookVerifier? _verifier;
    private readonly IUserDisplayNames? _names;
    private readonly ConcurrentDictionary<string, Task> _verifying = new(StringComparer.Ordinal);

    public RunbookService(ProjectEnvironmentService environments, DeployStore store, GitService git,
        ProjectSettingsService settings, TimeProvider clock, ILogger<RunbookService> logger,
        IncidentActions? incidentActions = null, RunbookVerifier? verifier = null, IUserDisplayNames? names = null)
    {
        _verifier = verifier;
        _names = names;
        _environments = environments;
        _store = store;
        _git = git;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _incidentActions = incidentActions;
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
            var verification = ProjectRunbookVerification.Parse(definition.Verify);
            if (definition.Verify is not null && (verification is null || _verifier is null))
                return DeployOutcome<RunbookRunResponse>.Conflict($"Runbook '{name}' names a verification this installation cannot run.");
            if (verification is not null && _verifier!.Problem(project, environmentId, verification) is { } problem)
                return DeployOutcome<RunbookRunResponse>.Conflict($"Runbook '{name}' cannot be verified: {problem}");
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
                null, null, LogRef(project, id, step)))
                .Concat(verification is null ? [] : [new DeploymentStep(ProjectRunbookVerification.StepName,
                    DeploymentStepStates.Pending, null, null, LogRef(project, id, ProjectRunbookVerification.StepName))])
                .ToArray();
            DeploymentStep[] steps = protectedEnvironment
                ? [new(DeployStepNames.Approval, DeploymentStates.AwaitingApproval, now, null,
                    LogRef(project, id, DeployStepNames.Approval)), .. operations]
                : operations;
            var run = new RunbookRun(id, name, environmentId,
                protectedEnvironment ? DeploymentStates.AwaitingApproval : DeploymentStates.Queued,
                actor.Id, null, now, null, null, steps, plain, secrets, null, source.Value.Commit)
                { Verify = verification is null ? null : definition.Verify };
            var incident = _incidentActions?.Open(project, environmentId);
            var currentRelease = _store.ReadDeployments(project)
                .Where(item => item.EnvironmentId == environmentId && item.State == DeploymentStates.Succeeded)
                .OrderByDescending(item => item.FinishedAtUtc).FirstOrDefault()?.ReleaseId;
            run = run with { IncidentId = incident?.IncidentId, IncidentCardId = incident?.CardId,
                ReleaseId = currentRelease };
            if (!protectedEnvironment)
            {
                var job = _environments.QueueRunbook(project, declarations.First(item => item.Id == environmentId),
                    id, source.Value.Commit, name, definition.Steps, plain, secrets, actor.Id);
                if (job is null) return DeployOutcome<RunbookRunResponse>.Conflict("The runbook job could not be queued.");
                run = run with { JobId = job.Id };
            }
            var log = protectedEnvironment
                ? $"Requested by {Name(actor.Id)}. Waiting for approval by {StandardLifecyclePolicy.ApprovalRoles}."
                : $"Queued for {environment.Name} by {Name(actor.Id)}.";
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
                OperationSteps(run).Select(item => item.Name).ToArray(),
                run.Variables, run.SecretVariables, actor.Id);
            if (job is null) return DeployOutcome<RunbookRunResponse>.Conflict("The environment is busy or unavailable.");
            var now = _clock.GetUtcNow().UtcDateTime;
            var approved = run with { State = DeploymentStates.Queued, ApprovedBy = actor.Id, JobId = job.Id,
                Steps = run.Steps.Select(step => step.Name == DeployStepNames.Approval
                    ? step with { State = DeploymentStates.Succeeded, FinishedAtUtc = now } : step).ToArray() };
            Persist(project, _store.ReadRunbookRuns(project).Select(item => item.Id == id ? approved : item).ToArray(),
                [new(id, DeployStepNames.Approval, now, $"Approved by {Name(actor.Id)}."),
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
                [new(id, DeployStepNames.Approval, now, $"Rejected by {Name(actor.Id)}.")],
                [new(now, actor.Id, "runbook-rejected", null, id, run.EnvironmentId,
                    $"Runbook {run.Name} rejected.")]);
            return DeployOutcome<RunbookRunResponse>.Ok(ToResponse(rejected));
        }
    }

    public void SyncAfterHostCall(string project)
    {
        try
        {
            lock (_gate) Sync(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Runbook runs for {Project} were not reconciled after a host call", project);
        }
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
            log.Add(new(waiting.Id, DeployStepNames.Approval, queued.CreatedAt, $"Approval by {Name(actor)} recovered from the host queue."));
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
            var hostState = DeploymentReconciler.StateForJob(job.Status);
            if (hostState == (run.HostState ?? run.State)) continue;
            var verification = ProjectRunbookVerification.Parse(run.Verify);
            var verifying = verification is not null && hostState == DeploymentStates.Succeeded;
            var state = verifying ? DeploymentStates.Running : hostState;
            var started = job.ClaimedAt ?? run.StartedAtUtc;
            var finished = job.FinishedAt;
            var offset = run.Steps.Count > 0 && run.Steps[0].Name == DeployStepNames.Approval ? 1 : 0;
            var operationCount = OperationSteps(run).Count;
            var steps = run.Steps.Select((step, index) =>
            {
                var operationIndex = index - offset;
                if (operationIndex < 0 || operationIndex >= operationCount)
                {
                    if (step.Name == DeployStepNames.Approval || verification is null) return step;
                    var verifyState = verifying ? DeploymentStates.Running
                        : DeploymentStates.IsFinished(hostState) ? DeploymentStepStates.Skipped : DeploymentStepStates.Pending;
                    return step with { State = verifyState, StartedAtUtc = verifying ? finished : null };
                }
                var result = job.RunbookResults.FirstOrDefault(item => item.Index == operationIndex);
                var hasResult = result is not null;
                var stepState = hasResult ? result!.Success ? DeploymentStates.Succeeded : DeploymentStates.Failed
                    : hostState == DeploymentStates.Running && operationIndex == job.RunbookResults.Count
                        ? DeploymentStates.Running
                        : DeploymentStates.IsFinished(hostState) ? DeploymentStepStates.Skipped : DeploymentStepStates.Pending;
                return step with { State = stepState,
                    StartedAtUtc = stepState == DeploymentStepStates.Pending ? null : started,
                    FinishedAtUtc = hasResult ? finished : null };
            }).ToArray();
            if (hostState == DeploymentStates.Running)
                log.Add(new(run.Id, run.Name, started ?? run.CreatedAtUtc, $"Host {job.HostId} started the runbook."));
            if (DeploymentStates.IsFinished(hostState))
            {
                var at = finished ?? run.CreatedAtUtc;
                foreach (var result in job.RunbookResults)
                {
                    var step = job.RunbookSteps[result.Index];
                    log.AddRange(RunbookStepOutput.Lines(result.Output).Select(line => new DeploymentLogEntry(run.Id, step, at, line)));
                    log.Add(new(run.Id, step, at, result.Success ? "Operation succeeded." : "Operation failed."));
                }
                if (verifying)
                    log.Add(new(run.Id, ProjectRunbookVerification.StepName, at,
                        $"The host steps succeeded. Checking {verification!.Describe()}."));
                else
                {
                    var action = state == DeploymentStates.Succeeded ? "runbook-succeeded" : "runbook-failed";
                    audit.Add(new(at, "deploy-host", action, null, run.Id, run.EnvironmentId,
                        $"Runbook {run.Name} {state} on {job.HostId} ({job.Evidence})."));
                }
            }
            runs[i] = run with { State = state, HostState = hostState, StartedAtUtc = started,
                FinishedAtUtc = verifying ? null : finished, Steps = steps };
        }
        if (log.Count > 0 || audit.Count > 0) Persist(project, runs, log, audit);
        ScheduleVerifications(project, runs);
        return runs;
    }

    /// <summary>Waits until every verification started so far has finished. Hosts and tests use it to drain.</summary>
    public async Task WaitForVerificationsAsync()
    {
        while (_verifying.Values.ToArray() is { Length: > 0 } pending) await Task.WhenAll(pending);
    }

    private static bool AwaitsVerification(RunbookRun run)
        => run.Verify is not null && run.HostState == DeploymentStates.Succeeded && run.State == DeploymentStates.Running;

    /// <summary>Called under the gate, so a run is never scheduled twice; a run left over from a restart resumes here.</summary>
    private void ScheduleVerifications(string project, IReadOnlyList<RunbookRun> runs)
    {
        if (_verifier is null) return;
        foreach (var run in runs.Where(AwaitsVerification))
        {
            var key = project + "/" + run.Id;
            if (_verifying.ContainsKey(key)) continue;
            _verifying[key] = Task.Run(() => VerifyAsync(project, run, key));
        }
    }

    private async Task VerifyAsync(string project, RunbookRun run, string key)
    {
        try
        {
            var verification = ProjectRunbookVerification.Parse(run.Verify)!;
            RunbookVerificationOutcome outcome;
            try
            {
                outcome = await _verifier!.VerifyAsync(project, run.EnvironmentId, verification, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Runbook {Run} of {Project} could not be verified", run.Id, project);
                outcome = new(false, $"The {verification.Describe()} could not be checked ({ex.GetType().Name}).");
            }
            lock (_gate) CompleteVerification(project, run.Id, verification, outcome);
        }
        catch (Exception ex)
        {
            // A later Sync schedules the run again; a background failure must not go unobserved.
            _logger.LogWarning(ex, "Runbook {Run} of {Project} verification result was not saved", run.Id, project);
        }
        finally
        {
            _verifying.TryRemove(key, out _);
        }
    }

    private void CompleteVerification(string project, string id, ProjectRunbookVerification verification,
        RunbookVerificationOutcome outcome)
    {
        var runs = _store.ReadRunbookRuns(project).ToList();
        var index = runs.FindIndex(item => item.Id == id);
        if (index < 0 || !AwaitsVerification(runs[index])) return;
        var run = runs[index];
        var now = _clock.GetUtcNow().UtcDateTime;
        var state = outcome.Passed ? DeploymentStates.Succeeded : DeploymentStates.Failed;
        runs[index] = run with
        {
            State = state, FinishedAtUtc = now,
            Verification = new(verification.Describe(), outcome.Passed, outcome.Detail, now),
            Steps = run.Steps.Select(step => step.Name == ProjectRunbookVerification.StepName && step.State == DeploymentStates.Running
                ? step with { State = state, FinishedAtUtc = now } : step).ToArray(),
        };
        Persist(project, runs, [new(id, ProjectRunbookVerification.StepName, now, outcome.Detail)],
            [new(now, EnvironmentHealthService.ProbeOrigin, outcome.Passed ? "runbook-succeeded" : "runbook-failed", null, id,
                run.EnvironmentId, $"Runbook {run.Name} {state}: {outcome.Detail}")]);
    }

    /// <summary>The steps the host runs: every step but the approval and verification bookends.</summary>
    private static List<DeploymentStep> OperationSteps(RunbookRun run)
    {
        var steps = run.Steps.ToList();
        if (steps.Count > 0 && steps[0].Name == DeployStepNames.Approval) steps.RemoveAt(0);
        if (run.Verify is not null && steps.Count > 0 && steps[^1].Name == ProjectRunbookVerification.StepName)
            steps.RemoveAt(steps.Count - 1);
        return steps;
    }

    private string Name(string actor) => _names?.DisplayNameOf(actor) ?? actor;

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
        var before = _store.ReadRunbookRuns(project).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var linkedRuns = runs.Select(run =>
        {
            if (run.IncidentId is not null) return run;
            if (before.TryGetValue(run.Id, out var stored))
                return run with { IncidentId = stored.IncidentId, IncidentCardId = stored.IncidentCardId,
                    ReleaseId = run.ReleaseId ?? stored.ReleaseId };
            var incident = _incidentActions?.At(project, run.EnvironmentId, run.CreatedAtUtc);
            return run with { IncidentId = incident?.IncidentId, IncidentCardId = incident?.CardId };
        }).ToArray();
        _store.AppendLog(project, log);
        _store.AppendAudit(project, audit);
        _store.SaveRunbookRuns(project, linkedRuns);
        var changedLinkedRuns = linkedRuns.Where(item => item.IncidentId is not null
            && (!before.TryGetValue(item.Id, out var previous) || previous.State != item.State)).ToArray();
        var releases = changedLinkedRuns.Length > 0 ? _store.ReadReleases(project).ToDictionary(item => item.Id, StringComparer.Ordinal)
            : new Dictionary<string, Release>(StringComparer.Ordinal);
        foreach (var run in changedLinkedRuns)
        {
            _incidentActions?.Report(project, run.IncidentCardId, run.IncidentId, run.Id,
                $"Runbook {run.Name}", run.RequestedBy,
                run.ReleaseId is null ? null : releases.GetValueOrDefault(run.ReleaseId)?.Version ?? run.ReleaseId,
                run.State);
        }
    }

    private static string LogRef(string project, string id, string step)
        => $"/api/projects/{Uri.EscapeDataString(project)}/deploy/runbooks/runs/{id}/log?step={Uri.EscapeDataString(step)}";

    private RunbookRunResponse ToResponse(RunbookRun run)
        => new(run.Id, run.Name, run.EnvironmentId, run.State, run.RequestedBy, run.ApprovedBy,
            run.CreatedAtUtc, run.StartedAtUtc, run.FinishedAtUtc, run.Steps, run.DefinitionCommit)
        {
            RequestedByName = Name(run.RequestedBy),
            ApprovedByName = run.ApprovedBy is null ? null : Name(run.ApprovedBy),
            Verifies = ProjectRunbookVerification.Parse(run.Verify)?.Describe(),
            Verification = run.Verification,
        };
}
