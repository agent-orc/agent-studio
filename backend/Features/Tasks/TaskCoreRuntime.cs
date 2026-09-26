using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

public interface ITaskCoreRuntime
{
    TaskCoreRuntimeSnapshot Read(TaskCoreRecord core);
}

public sealed record TaskCoreRuntimeSnapshot
{
    public TaskRunActivity? Activity { get; init; }
    public string? ExecutionStatus { get; init; }
    public DateTime? ExecutionStartedAt { get; init; }
    public int? ProcessId { get; init; }
    public string Location { get; init; } = "none";
    public string? RunnerId { get; init; }
    public string? RunnerName { get; init; }
    public string? Hostname { get; init; }
    public string? BackendName { get; init; }
    public string? AttemptId { get; init; }
    public string? LeaseId { get; init; }
    public long? LeaseGeneration { get; init; }
    public string LeaseState { get; init; } = "none";
    public DateTime? HeartbeatAt { get; init; }
    public string? SummaryState { get; init; }

    [JsonIgnore]
    public string Version => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this))).ToLowerInvariant()[..16];
}

/// <summary>Reads only resident execution, lease and summary state.</summary>
public sealed class TaskCoreRuntime(
    TaskRunnerService runners, CliRouter router, RunLeaseService leases) : ITaskCoreRuntime
{
    public TaskCoreRuntimeSnapshot Read(TaskCoreRecord core)
    {
        var inProgress = core.State == TaskStates.Progress;
        var execution = inProgress ? router.Get(core.CliType).GetExecution(core.TaskKey) : null;
        var issue = core.OutcomeIssue is { } currentIssue ? new TaskOutcomeIssue
        {
            Kind = currentIssue.Kind, Severity = currentIssue.Severity,
            Label = currentIssue.Label, Summary = currentIssue.Summary,
        } : null;
        var activity = inProgress ? TaskRunActivityClassifier.Classify(
            runners.GetRunActivityForJob(core.Id, core.ProjectName), execution, issue) : null;
        var runner = inProgress ? runners.ResolveRunnerBadge(core.TaskKey) : null;
        var leaseReply = inProgress ? leases.Peek(core.TaskKey) : null;
        var lease = leaseReply?.Lease;
        var summaryState = runners.SummaryService.GetState(core.TaskKey);
        return new TaskCoreRuntimeSnapshot
        {
            Activity = activity, ExecutionStatus = execution?.Status,
            ExecutionStartedAt = execution?.StartedAt, ProcessId = execution?.ProcessId,
            Location = !inProgress ? "none" : runner is { IsRemote: true } ? "remote"
                : execution is not null ? "local" : "none",
            RunnerId = TaskCoreRecord.Limit(runner?.RunnerId, 128),
            RunnerName = TaskCoreRecord.Limit(runner?.RunnerName, 128),
            Hostname = TaskCoreRecord.Limit(runner?.Hostname, 128),
            BackendName = TaskCoreRecord.Limit(runner?.BackendName, 128),
            AttemptId = TaskCoreRecord.Limit(lease?.AttemptId, 128),
            LeaseId = TaskCoreRecord.Limit(lease?.LeaseId, 128),
            LeaseGeneration = lease?.FencingToken,
            LeaseState = leaseReply?.Outcome ?? "none",
            HeartbeatAt = lease?.LastHeartbeatAt,
            SummaryState = summaryState?.Status.ToString().ToLowerInvariant(),
        };
    }
}
