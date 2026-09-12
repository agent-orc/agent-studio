namespace AgentStudio.Docs;

/// <summary>
/// In-process boundary between durable Workbench mutations and live transports.
/// The descriptor write has already succeeded when an event is published, so a
/// failing subscriber is logged and never rolls the mutation back.
/// </summary>
public sealed class WorkbenchChangeNotifier
{
    private readonly ILogger<WorkbenchChangeNotifier> _logger;

    public WorkbenchChangeNotifier(ILogger<WorkbenchChangeNotifier> logger)
    {
        _logger = logger;
    }

    public event Action<WorkbenchDecisionRecordedEvent>? DecisionRecorded;
    public event Action<WorkbenchReviewRecordedEvent>? ReviewRecorded;

    public void PublishReviewRecorded(string projectName, string workbenchId)
    {
        var handler = ReviewRecorded;
        if (handler == null) return;
        try
        {
            handler(new WorkbenchReviewRecordedEvent(projectName, workbenchId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dossier ReviewRecorded subscriber threw for {Project} {WorkbenchId}",
                projectName, workbenchId);
        }
    }

    public void PublishDecisionRecorded(
        string projectName,
        string workbenchId,
        string previousStatus,
        string currentStatus)
    {
        var handler = DecisionRecorded;
        if (handler == null) return;
        try
        {
            handler(new WorkbenchDecisionRecordedEvent(
                projectName, workbenchId, previousStatus, currentStatus));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dossier DecisionRecorded subscriber threw for {Project} {WorkbenchId}",
                projectName, workbenchId);
        }
    }
}

public readonly record struct WorkbenchDecisionRecordedEvent(
    string ProjectName,
    string WorkbenchId,
    string PreviousStatus,
    string CurrentStatus);

public readonly record struct WorkbenchReviewRecordedEvent(
    string ProjectName,
    string WorkbenchId);
