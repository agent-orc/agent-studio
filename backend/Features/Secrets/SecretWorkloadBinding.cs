using AgentStudio.Projects;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Secrets;

/// <summary>
/// A claimed deploy job with a live lease. <see cref="HostRunnerId"/> is the
/// enrolled runner the job's host name resolves to (F6).
/// </summary>
public sealed record DeployJobLease(string JobId, string Project, string EnvironmentId, string HostId,
    IReadOnlyList<string> SecretRefs, IReadOnlyDictionary<string, string>? SecretBindings = null,
    string? HostRunnerId = null)
{
    /// <summary>Keys the project goes by in a reference scope (AGT-3987).</summary>
    public IReadOnlyCollection<string> ProjectKeys { get; init; } = [];
}

/// <summary>
/// A task run with a live lease. <see cref="DeclaredNames"/> lists the secret
/// names declared for the run; without it the run reads no secret.
/// </summary>
public sealed record TaskRunLease(string TaskKey, string Project, string RunnerId, string LeaseId,
    IReadOnlyCollection<string>? DeclaredNames = null);

/// <summary>
/// The managed database credentials of one environment placed on a run host:
/// <see cref="Names"/> are the generated secret names, <see cref="SecretBindings"/>
/// their bound references.
/// </summary>
public sealed record RunTargetCredentialLease(string Project, string EnvironmentId, string HostId,
    IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> SecretBindings)
{
    /// <summary>Keys the project goes by in a reference scope (AGT-3987).</summary>
    public IReadOnlyCollection<string> ProjectKeys { get; init; } = [];
}

/// <summary>Reads the lease facts a workload binding needs.</summary>
public interface ISecretWorkloadLeases
{
    DeployJobLease? FindClaimedDeployJob(string project, string jobId, string leaseToken);
    TaskRunLease? FindLiveTaskRun(string taskKey);
    RunTargetCredentialLease? FindRunTargetCredentials(string project, string environmentId);
}

public sealed class SecretWorkloadLeases(ProjectEnvironmentService environments, RunLeaseService leases,
    TaskScannerService scanner, ProjectSecretBindings bindings, AccessSecurityStore security)
    : ISecretWorkloadLeases
{
    public DeployJobLease? FindClaimedDeployJob(string project, string jobId, string leaseToken)
        => environments.FindClaimedJob(project, jobId, leaseToken) is { } job
            ? Lease(job)
            : null;

    /// <summary>A deploy job with telemetry also reads the deploy ingest token of its environment (M5).</summary>
    private DeployJobLease Lease(DeployJob job)
    {
        IReadOnlyList<string> names = job.Telemetry is { } telemetry
            ? [.. job.SecretRefs.Append(telemetry.SecretName).Distinct(StringComparer.Ordinal)] : job.SecretRefs;
        var bound = bindings.ApprovedReferences(job.Project, job.EnvironmentId, names, job.EnvironmentKind ?? "production");
        return new DeployJobLease(job.Id, job.Project, job.EnvironmentId, job.HostId, names, bound,
            security.DeployHostRunnerId(job.HostId)) { ProjectKeys = bindings.ProjectKeys(job.Project, bound) };
    }

    public RunTargetCredentialLease? FindRunTargetCredentials(string project, string environmentId)
    {
        if (environments.ManagedDatabaseCredentialLease(project, environmentId) is not { } lease) return null;
        var names = lease.Names.Select(ProjectDatabaseVocabulary.CredentialName).ToArray();
        var bound = bindings.ApprovedReferences(lease.Project, lease.EnvironmentId, names, lease.EnvironmentKind);
        return new(lease.Project, lease.EnvironmentId, lease.HostId, names, bound)
            { ProjectKeys = bindings.ProjectKeys(lease.Project, bound) };
    }

    public TaskRunLease? FindLiveTaskRun(string taskKey)
    {
        var lease = leases.Peek(taskKey).Lease;
        var project = scanner.FindJob(taskKey)?.ProjectName;
        return lease is null || string.IsNullOrWhiteSpace(project)
            ? null
            : new TaskRunLease(taskKey, project, lease.RunnerId, lease.LeaseId);
    }
}

/// <summary>
/// Pure binding of an authenticated service credential to a workload. A
/// deploy job needs the deploy scope, the job's host identity and its lease
/// token; a task run needs the lease scope and the live lease of that task;
/// a run target needs the deploy scope and the placement of the environment
/// on its own host, and reads only that environment's database credentials.
/// </summary>
public static class SecretWorkloadBinding
{
    public static (SecretWorkload? Workload, SecretAccessDecision Decision) Bind(
        RunnerPrincipal runner, SecretWorkloadRequest? request, ISecretWorkloadLeases leases)
    {
        switch (request?.Kind)
        {
            case SecretWorkload.DeployJob:
                if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.JobId) || string.IsNullOrWhiteSpace(request.LeaseToken))
                    return Denied("workload-invalid", "A deploy job names project, jobId and leaseToken.");
                return Decide(runner, request, leases.FindClaimedDeployJob(request.Project, request.JobId, request.LeaseToken), null);
            case SecretWorkload.RunTarget:
                if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.EnvironmentId))
                    return Denied("workload-invalid", "A run target names project and environmentId.");
                return DecideRunTarget(runner, leases.FindRunTargetCredentials(request.Project, request.EnvironmentId));
            case SecretWorkload.TaskRun:
                if (string.IsNullOrWhiteSpace(request.TaskKey) || string.IsNullOrWhiteSpace(request.LeaseId))
                    return Denied("workload-invalid", "A task run names taskKey and leaseId.");
                return Decide(runner, request, null, leases.FindLiveTaskRun(request.TaskKey));
            default:
                return Denied("workload-invalid", "Workload kind must be 'deploy-job', 'task-run' or 'run-target'.");
        }
    }

    public static (SecretWorkload? Workload, SecretAccessDecision Decision) DecideRunTarget(
        RunnerPrincipal runner, RunTargetCredentialLease? lease)
    {
        if (!runner.Scopes.Contains(RunnerScopes.Deploy))
            return Denied("runner-scope-denied", $"Runner credential lacks '{RunnerScopes.Deploy}'.");
        if (lease is null)
            return Denied("workload-lease-invalid", "The environment is not placed in the run zone or has no managed database.");
        if (!string.Equals(lease.HostId, runner.RunnerId, StringComparison.Ordinal))
            return Denied("workload-identity-denied", "The environment is placed on another host.");
        return (new SecretWorkload(SecretWorkload.RunTarget, runner.RunnerId, lease.Project, lease.EnvironmentId,
            lease.Names, lease.SecretBindings) { ProjectKeys = lease.ProjectKeys }, SecretAccessDecision.Allow);
    }

    public static (SecretWorkload? Workload, SecretAccessDecision Decision) Decide(
        RunnerPrincipal runner, SecretWorkloadRequest request, DeployJobLease? job, TaskRunLease? run)
    {
        if (request.Kind == SecretWorkload.DeployJob)
        {
            if (!runner.Scopes.Contains(RunnerScopes.Deploy))
                return Denied("runner-scope-denied", $"Runner credential lacks '{RunnerScopes.Deploy}'.");
            if (job is null)
                return Denied("workload-lease-invalid", "No claimed deploy job matches this lease.");
            if (!DeployHostIdentity.Belongs(runner, job.HostId, job.HostRunnerId))
                return Denied(DeployHostIdentity.DeniedCode, DeployHostIdentity.Refusal(job.HostId, runner));
            return (new SecretWorkload(SecretWorkload.DeployJob, runner.RunnerId, job.Project, job.EnvironmentId,
                    job.SecretRefs, job.SecretBindings) { ProjectKeys = job.ProjectKeys },
                SecretAccessDecision.Allow);
        }

        if (!runner.Scopes.Contains(RunnerScopes.Lease))
            return Denied("runner-scope-denied", $"Runner credential lacks '{RunnerScopes.Lease}'.");
        if (run is null || !string.Equals(run.LeaseId, request.LeaseId, StringComparison.Ordinal))
            return Denied("workload-lease-invalid", "No live run lease matches this task and lease.");
        if (!string.Equals(run.RunnerId, runner.RunnerId, StringComparison.Ordinal))
            return Denied("workload-identity-denied", "The run lease is held by another Runner.");
        if (run.DeclaredNames is not { Count: > 0 })
            return Denied("workload-undeclared", "The task run declares no secret names.");
        return (new SecretWorkload(SecretWorkload.TaskRun, runner.RunnerId, run.Project, null, run.DeclaredNames),
            SecretAccessDecision.Allow);
    }

    private static (SecretWorkload?, SecretAccessDecision) Denied(string code, string reason)
        => (null, SecretAccessDecision.Deny(code, reason));
}
