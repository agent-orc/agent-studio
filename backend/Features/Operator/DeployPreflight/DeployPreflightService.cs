using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;

namespace AgentStudio.Operator;

/// <summary>Collects existing batch and pipeline facts without changing their authority.</summary>
public sealed class DeployPreflightService(
    BatchTriageReader triage, TaskScannerService scanner, PipelineExecutionLog pipeline,
    TimeProvider time, StableReleaseIdentity release, HostReleaseDriftWatchdog hosts,
    TaskServerBoardClient taskServer)
{
    public DeployPreflightSnapshot Check(string operation)
    {
        var now = time.GetUtcNow();
        var batch = triage.Read(scanner.GetWatchPaths().Select(project => project.Name));
        var facts = new DeployPreflightFacts(
            batch.Projects.SelectMany(project => project.PendingPushes.Select(push =>
                new DeployPushFact(project.Project, push.JobId, push.SinceUtc))).ToArray(),
            batch.Projects.SelectMany(project => project.Flights.Select(flight =>
                new DeployFlightFact(project.Project, flight.BatchId, flight.Phase))).ToArray(),
            scanner.ScanAllAutomationJobs().SelectMany(card =>
            {
                var record = pipeline.Read(card.FolderPath);
                return record?.Steps.Where(step => step.Status == PipelineStepStatus.Running
                        && (step.StepId.Contains("commit", StringComparison.OrdinalIgnoreCase)
                            || step.StepId == PipelineCatalogue.MergeIntoDevelopStepId))
                    .Select(step => new DeployCommitFact(card.ProjectName, card.TaskKey, step.StepId))
                    ?? Enumerable.Empty<DeployCommitFact>();
            }).ToArray());
        var blockers = DeployPreflightPolicy.Check(facts, now);
        return new DeployPreflightSnapshot(operation, blockers.Count == 0, now, blockers);
    }

    public async Task<DeployPlan> PlanAsync(string targetVersion, int windowMinutes, CancellationToken ct)
    {
        var server = taskServer.IsConfigured ? await taskServer.GetProtocolAsync(ct) : null;
        var hostVersions = hosts.Current.Hosts
            .GroupBy(host => host.HostId, StringComparer.Ordinal)
            .Select(group => new DeployHostVersion(group.Key,
                group.Select(item => item.Release?.Version).FirstOrDefault(version => !string.IsNullOrWhiteSpace(version))
                    ?? "unknown"))
            .OrderBy(host => host.HostId, StringComparer.Ordinal).ToArray();
        return DeployPlanPolicy.Create(time.GetUtcNow(), TimeSpan.FromMinutes(windowMinutes),
            server?.ServerVersion ?? release.Version, targetVersion,
            server?.Current ?? AgentStudio.TaskServer.Contracts.TaskServerProtocol.Current,
            hostVersions);
    }
}

public sealed record DeployPreflightSnapshot(string Operation, bool Allowed,
    DateTimeOffset CheckedAtUtc, IReadOnlyList<DeployPreflightBlocker> Blockers);
