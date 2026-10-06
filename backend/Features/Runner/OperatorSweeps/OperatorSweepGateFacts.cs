namespace AgentStudio.Runner;

/// <summary>
/// Reads the current gate failure the same way the operator failure panel does:
/// the integration projection's failure plus
/// <see cref="TaskFailureContinuationService.CurrentFailureStep"/> over the
/// pipeline log, scoped to the current review subject.
/// </summary>
public sealed class OperatorSweepGateFacts : IOperatorSweepGateFacts
{
    private readonly TaskIntegrationStatusService _integration;
    private readonly PipelineExecutionLog _pipeline;

    public OperatorSweepGateFacts(TaskIntegrationStatusService integration, PipelineExecutionLog pipeline)
    {
        _integration = integration;
        _pipeline = pipeline;
    }

    public OperatorSweepGateFailure? Read(TaskInfo job)
    {
        var status = _integration.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
        if (status is null || status.ReachUnavailable || status.Status == IntegrationStatuses.Integrated)
            return null;
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        var failed = TaskFailureContinuationService.CurrentFailureStep(
            _pipeline.Read(job.FolderPath)?.Steps, status, subject);
        var failure = status.Failure;
        if (failure is null && failed is null) return null;
        // A projected failure without a matching current step belongs to an
        // earlier delivery; the failure panel refuses it, and so does the sweep.
        if (failure is not null && failed is null) return null;

        return new OperatorSweepGateFailure(
            subject?.ResultSha,
            failed?.StepId ?? failure?.Stage ?? "unknown",
            failure?.Code ?? failed?.FailureCode ?? failed?.Verdict ?? "unknown",
            failure?.Reason ?? failed?.Reason ?? string.Empty,
            string.Join("\n", new[] { failed?.VerdictSummary, failure?.EvidenceExcerpt }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            GateEnvironmentRetryPolicy.IsGateEnvironmentFailure(status),
            failure?.FailureClass ?? AgentStudio.TaskServer.Contracts.RunFailureClass.Unknown);
    }
}
