using AgentStudio.Operator;
using System.Text.Json;

namespace AgentStudio.Security;

/// <summary>Projects open audit alarms and persisted recovery evidence into the owner's attention list.</summary>
public sealed class ControlPlaneAuditAlertFactSource(ControlPlaneAuditMonitor monitor, ControlPlaneAuditLog log)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.AuditAlert];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        var alerts = monitor.Raised.Where(alert => monitor.IsOpen(alert.Code))
            .GroupBy(alert => alert.Code).Select(group => group.Last()).ToList();
        // Recovery does not make the historical damage disappear, including after a restart.
        var anchor = log.LatestRecoveryAnchor;
        ControlPlaneAuditAlert? recovered = null;
        if (anchor?.Details is { ValueKind: JsonValueKind.Object } details
            && details.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            && details.TryGetProperty("firstAffected", out var first) && first.ValueKind == JsonValueKind.Number
            && first.TryGetInt64(out var sequence))
        {
            var occurrence = details.TryGetProperty("occurrence", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : $"anchor:{anchor.Hash}";
            var occurredAt = details.TryGetProperty("firstObservedAt", out var at) && at.ValueKind == JsonValueKind.String
                && at.TryGetDateTime(out var firstObservedAt) ? firstObservedAt : anchor.At;
            recovered = new(ControlPlaneAuditAlertCodes.ChainBroken, message.GetString()!, anchor.At, sequence)
            {
                Occurrence = occurrence, OccurredAt = occurredAt,
            };
        }
        if (recovered is not null && !alerts.Any(alert => alert.Code == ControlPlaneAuditAlertCodes.ChainBroken))
            alerts.Add(recovered);
        return alerts.Select(alert =>
        {
            var fromAnchor = recovered is not null && alert.Code == recovered.Code
                && alert.Occurrence == recovered.Occurrence;
            var current = fromAnchor ? recovered! : alert;
            var occurrence = current.Occurrence ?? $"{alert.Sequence}|{alert.RaisedAt:O}";
            return new OperatorDecisionFact(
                OperatorDecisionTypes.AuditAlert,
                $"{OperatorDecisionTypes.AuditAlert}|{alert.Code}|{occurrence}",
                null, null, current.Message, current.OccurredAt ?? current.RaisedAt,
                [new("Audit trail", "/workspace/settings/access")],
                Title: $"Audit trail: {alert.Code}");
        }).ToArray();
    }
}
