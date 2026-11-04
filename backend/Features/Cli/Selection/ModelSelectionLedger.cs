using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>
/// Write seam for the selection ledger. The selection service records every
/// result that is not a plain primary launch here; the daily JSONL store with
/// retention and the read API arrive with MS-2 behind the same seam.
/// </summary>
public interface IModelSelectionLedger
{
    void Record(SelectionLedgerEntry entry);
}

/// <summary>
/// Interim ledger: one structured <c>model_selection_event</c> log line per
/// event. Claim and pickup loops ask again every tick, so an event identical
/// to the previous one for the same card and consumer is written once.
/// </summary>
public sealed class LoggingModelSelectionLedger(ILogger<LoggingModelSelectionLedger> logger) : IModelSelectionLedger
{
    private const int MaxTrackedScopes = 2048;
    private readonly Dictionary<string, string> _lastByScope = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Record(SelectionLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var scope = $"{entry.Consumer}|{entry.Project}|{entry.TaskKey}|{entry.StepId}";
        var signature = $"{entry.Kind}|{entry.Rule}|{entry.From}|{entry.To}|{entry.Reason}";
        lock (_lock)
        {
            if (_lastByScope.TryGetValue(scope, out var last) && last == signature) return;
            if (_lastByScope.Count >= MaxTrackedScopes) _lastByScope.Clear();
            _lastByScope[scope] = signature;
        }
        logger.LogInformation(
            "model_selection_event id={Id} kind={Kind} consumer={Consumer} path={Path} project={Project} task={TaskKey} host={Host} from={FromCli}/{FromModel}/{FromThinking} to={ToCli}/{ToModel}/{ToThinking} rule={Rule} reason={Reason}",
            entry.Id, entry.Kind, entry.Consumer, entry.ExecutionPath, entry.Project ?? "-", entry.TaskKey ?? "-",
            entry.Host ?? "-", entry.From.CliType, entry.From.Model ?? "<default>", entry.From.ThinkingLevel ?? "-",
            entry.To.CliType, entry.To.Model ?? "<default>", entry.To.ThinkingLevel ?? "-", entry.Rule, entry.Reason);
    }
}
