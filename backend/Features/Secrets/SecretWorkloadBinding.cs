using AgentStudio.Projects;
using AgentStudio.Security;

namespace AgentStudio.Secrets;

/// <summary>A claimed deploy job with a live lease.</summary>
public sealed record DeployJobLease(string JobId, string Project, string EnvironmentId, string HostId, IReadOnlyList<string> SecretRefs);

/// <summary>A task run with a live lease.</summary>
public sealed record TaskRunLease(string TaskKey, string Project, string RunnerId, string LeaseId);

/// <summary>Reads the lease facts a workload binding needs.</summary>
public interface ISecretWorkloadLeases
{
    DeployJobLease? FindClaimedDeployJob(string project, string jobId, string leaseToken);
    TaskRunLease? FindLiveTaskRun(string taskKey);
}

public sealed class SecretWorkloadLeases(ProjectEnvironmentService environments, RunLeaseService leases, TaskScannerService scanner)
    : ISecretWorkloadLeases
{
    public DeployJobLease? FindClaimedDeployJob(string project, string jobId, string leaseToken)
        => environments.FindClaimedJob(project, jobId, leaseToken) is { } job
            ? new DeployJobLease(job.Id, job.Project, job.EnvironmentId, job.HostId, job.SecretRefs)
            : null;

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
/// token; a task run needs the lease scope and the live lease of that task.
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
            case SecretWorkload.TaskRun:
                if (string.IsNullOrWhiteSpace(request.TaskKey) || string.IsNullOrWhiteSpace(request.LeaseId))
                    return Denied("workload-invalid", "A task run names taskKey and leaseId.");
                return Decide(runner, request, null, leases.FindLiveTaskRun(request.TaskKey));
            default:
                return Denied("workload-invalid", "Workload kind must be 'deploy-job' or 'task-run'.");
        }
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
            if (!string.Equals(job.HostId, runner.RunnerId, StringComparison.Ordinal))
                return Denied("workload-identity-denied", "The deploy job is claimed by another host.");
            return (new SecretWorkload(SecretWorkload.DeployJob, runner.RunnerId, job.Project, job.EnvironmentId, job.SecretRefs),
                SecretAccessDecision.Allow);
        }

        if (!runner.Scopes.Contains(RunnerScopes.Lease))
            return Denied("runner-scope-denied", $"Runner credential lacks '{RunnerScopes.Lease}'.");
        if (run is null || !string.Equals(run.LeaseId, request.LeaseId, StringComparison.Ordinal))
            return Denied("workload-lease-invalid", "No live run lease matches this task and lease.");
        if (!string.Equals(run.RunnerId, runner.RunnerId, StringComparison.Ordinal))
            return Denied("workload-identity-denied", "The run lease is held by another Runner.");
        return (new SecretWorkload(SecretWorkload.TaskRun, runner.RunnerId, run.Project, null), SecretAccessDecision.Allow);
    }

    private static (SecretWorkload?, SecretAccessDecision) Denied(string code, string reason)
        => (null, SecretAccessDecision.Deny(code, reason));
}
