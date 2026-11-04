using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

public interface IModelSelectionLedger
{
    void Record(SelectionLedgerEntry entry);
    SelectionLedgerEntry? GetActive(SelectionRequest request) => null;
    IReadOnlyList<SelectionLedgerEntry> Active() => [];
}

/// <summary>Adapts planner receipts to the workspace selection ledger and its active projection.</summary>
public sealed class LoggingModelSelectionLedger : IModelSelectionLedger
{
    private readonly ILogger<LoggingModelSelectionLedger> _logger;
    private readonly ModelSelectionLedger _ledger;

    public LoggingModelSelectionLedger(ILogger<LoggingModelSelectionLedger> logger,
        IConfiguration config, TimeProvider? timeProvider = null, ModelSelectionLedger? ledger = null)
    {
        _logger = logger;
        _ledger = ledger ?? new ModelSelectionLedger(config, timeProvider);
    }

    public SelectionLedgerEntry? GetActive(SelectionRequest request) => Active().FirstOrDefault(entry =>
        entry.Consumer == request.Consumer && entry.Project == request.Project
        && entry.TaskKey == request.TaskKey && entry.StepId == request.StepId
        && entry.Host == request.Host?.HostId);

    public IReadOnlyList<SelectionLedgerEntry> Active() => _ledger.Active()
        .Where(entry => entry.Kind is SelectionLedgerKinds.Switched or "operator-switch"
            && entry.From is not null && entry.To is not null)
        .Select(FromEvent).ToArray();

    public void Record(SelectionLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var quota = entry.Quota;
        var projection = quota?.Projection;
        var receipt = new ModelSelectionEvent
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
            To = new ModelSelectionRoute(entry.To.CliType, entry.To.Model, entry.To.ThinkingLevel),
            Rule = entry.Rule,
            Reason = entry.Reason,
            Quota = quota is null ? null : new ModelSelectionQuota(entry.From.CliType,
                quota.Window, quota.UsedPct, projection?.CapPct, projection?.ProjectedUsedPct,
                projection?.ResetAt is { } reset ? new DateTimeOffset(DateTime.SpecifyKind(reset, DateTimeKind.Utc)) : null, null),
            Equivalence = entry.Equivalence is null ? null : new ModelSelectionEquivalence(
                entry.Equivalence.CapabilityClass, entry.Equivalence.CatalogueVersion, entry.Equivalence.Source),
            ReturnToPrimaryAt = entry.ReturnToPrimaryAt is { } returnAt
                ? new DateTimeOffset(DateTime.SpecifyKind(returnAt, DateTimeKind.Utc)) : null,
            ClosesId = entry.ClosesId,
            PricePerMTok = entry.FromPricePerMTok is null && entry.ToPricePerMTok is null
                ? null : new ModelSelectionPrice((double?)entry.FromPricePerMTok, (double?)entry.ToPricePerMTok),
        };
        if (_ledger.Append(receipt))
            _logger.LogInformation("model_selection_event id={Id} kind={Kind} consumer={Consumer} rule={Rule}",
                receipt.Id, receipt.Kind, receipt.Consumer, receipt.Rule);
    }

    private static SelectionLedgerEntry FromEvent(ModelSelectionEvent entry) => new(
        entry.Id, entry.At.UtcDateTime, entry.Kind, entry.Consumer, entry.Host, entry.Project,
        entry.PipelineType, entry.StepId, entry.TaskKey, entry.AttemptId, entry.Round, "ledger",
        new SelectionRoute(entry.From!.CliType ?? "", entry.From.Model, entry.From.ThinkingLevel),
        new SelectionRoute(entry.To!.CliType ?? "", entry.To.Model, entry.To.ThinkingLevel),
        entry.Rule, entry.Reason, null,
        entry.Equivalence is null ? null : new SelectionEquivalence(entry.Equivalence.CapabilityClass,
            entry.Equivalence.CatalogueVersion, null, entry.Equivalence.Source),
        null, entry.ReturnToPrimaryAt?.UtcDateTime);
}
