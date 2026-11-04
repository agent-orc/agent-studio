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

/// <summary>Maps planner decisions into the workspace's durable selection log.</summary>
public sealed class DurableModelSelectionLedger(ModelSelectionLedger ledger) : IModelSelectionLedger
{
    public void Record(SelectionLedgerEntry entry)
    {
        var projection = entry.Quota?.Projection;
        ledger.Append(new ModelSelectionEvent
        {
            Id = entry.Id,
            At = new DateTimeOffset(DateTime.SpecifyKind(entry.At, DateTimeKind.Utc)),
            Kind = entry.Kind,
            Consumer = entry.Consumer,
            Host = entry.Host,
            Project = entry.Project,
            PipelineType = entry.PipelineType,
            StepId = entry.StepId,
            TaskKey = entry.TaskKey,
            AttemptId = entry.AttemptId,
            Round = entry.Round,
            From = new ModelSelectionRoute(entry.From.CliType, entry.From.Model, entry.From.ThinkingLevel),
            To = entry.Kind == SelectionLedgerKinds.Waiting ? null
                : new ModelSelectionRoute(entry.To.CliType, entry.To.Model, entry.To.ThinkingLevel),
            Rule = entry.Rule,
            Reason = entry.Reason,
            Quota = entry.Quota is null ? null : new ModelSelectionQuota(
                entry.From.CliType, entry.Quota.Window,
                entry.Quota.UsedPct, projection?.CapPct, projection?.ProjectedUsedPct,
                projection?.ResetAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(projection.ResetAt.Value, DateTimeKind.Utc)),
                null),
            Equivalence = entry.Equivalence is null ? null : new ModelSelectionEquivalence(
                entry.Equivalence.CapabilityClass, entry.Equivalence.CatalogueVersion, entry.Equivalence.Source),
            ReturnToPrimaryAt = entry.ReturnToPrimaryAt is null ? null
                : new DateTimeOffset(DateTime.SpecifyKind(entry.ReturnToPrimaryAt.Value, DateTimeKind.Utc)),
        });
    }
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
