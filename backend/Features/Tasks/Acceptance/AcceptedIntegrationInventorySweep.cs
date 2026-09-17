namespace AgentStudio.Tasks;

/// <summary>
/// Read-only startup inventory for accepted coding cards that have no proof of
/// integration or whose latest attempt ended in Error or NoTaskBranch.
/// </summary>
public sealed class AcceptedIntegrationInventorySweep
{
    internal const string PreInvariantNotEvaluated = "PreInvariantNotEvaluated";

    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TimelineLog _timeline;
    private readonly ILogger<AcceptedIntegrationInventorySweep> _logger;

    public AcceptedIntegrationInventorySweep(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TimelineLog timeline,
        ILogger<AcceptedIntegrationInventorySweep> logger)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _timeline = timeline;
        _logger = logger;
    }

    public IReadOnlyList<AcceptedIntegrationInventoryItem> Run()
    {
        var accepted = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => task.State is TaskStates.Completed or TaskStates.Archive)
            .Where(AcceptanceIntegrationPolicy.IsIntegrationRequired)
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var statusByKey = _integrationStatus.BuildLookup(accepted);
        var findings = new List<AcceptedIntegrationInventoryItem>();

        foreach (var task in accepted)
        {
            statusByKey.TryGetValue(task.TaskKey, out var status);
            var step = _integrationStatus.ReadLatestMergeStep(task);
            if (string.Equals(step?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase))
                continue;

            var timeline = _timeline.ReadAll(task.FolderPath);
            var verification = TaskIntegrationRecordDetector.LatestVerification(task);
            var hasIntegrationRecord = verification is not null
                || TaskIntegrationRecordDetector.HasNativeRecord(step, timeline);
            var outcome = ClassifyFinding(
                step,
                status,
                hasIntegrationRecord,
                verification?.Classification);
            if (outcome is null) continue;
            var detail = verification?.Evidence
                ?? (string.Equals(outcome, PreInvariantNotEvaluated, StringComparison.Ordinal)
                    ? "Acceptance predates integration recording; the invariant has not been evaluated yet."
                    : step?.Reason ?? step?.VerdictSummary);

            var item = new AcceptedIntegrationInventoryItem(
                task.ProjectName,
                task.Id,
                task.State,
                outcome,
                status?.Status,
                detail,
                task.FolderPath);
            findings.Add(item);
            if (string.Equals(outcome, PreInvariantNotEvaluated, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "accepted-integration-inventory project={Project} job={JobId} lane={Lane} outcome={Outcome} status={Status} detail={Detail}",
                    item.Project,
                    item.JobId,
                    item.Lane,
                    item.Outcome,
                    item.IntegrationStatus ?? "null",
                    item.Detail);
            }
            else
            {
                _logger.LogWarning(
                    "accepted-integration-inventory project={Project} job={JobId} lane={Lane} outcome={Outcome} status={Status} detail={Detail}",
                    item.Project,
                    item.JobId,
                    item.Lane,
                    item.Outcome,
                    item.IntegrationStatus ?? "null",
                    item.Detail ?? "none");
            }
        }

        var unevaluated = findings.Count(item => string.Equals(
            item.Outcome,
            PreInvariantNotEvaluated,
            StringComparison.Ordinal));
        _logger.LogInformation(
            "accepted-integration-inventory completed scanned={Scanned} findings={Findings} preInvariantNotEvaluated={PreInvariantNotEvaluated}",
            accepted.Count,
            findings.Count - unevaluated,
            unevaluated);
        return findings;
    }

    internal static string? ClassifyFinding(
        PipelineStepExecution? step,
        TaskIntegrationStatus? status,
        bool hasIntegrationRecord,
        string? verificationClass = null)
    {
        if (!string.IsNullOrWhiteSpace(verificationClass))
        {
            return IntegrationRecordClasses.IsOperatorVisible(verificationClass)
                ? verificationClass
                : null;
        }

        var finding = step?.Verdict?.ToLowerInvariant() switch
        {
            "error" => "Error",
            "no-branch" => "NoTaskBranch",
            _ when step is null && !IntegrationStatuses.IsMerged(status?.Status) => "Null",
            _ => null,
        };
        if (finding is null) return null;
        return hasIntegrationRecord ? finding : PreInvariantNotEvaluated;
    }
}

public sealed record AcceptedIntegrationInventoryItem(
    string Project,
    string JobId,
    string Lane,
    string Outcome,
    string? IntegrationStatus,
    string? Detail,
    string FolderPath);
