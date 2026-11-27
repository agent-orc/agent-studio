using AgentStudio.Security;
using System.Text.Json;

namespace AgentStudio.Projects;

/// <summary>Undo takes its previous value from the immutable audit, never from caller-supplied parameters.</summary>
public static class TestingStrategyElevation
{
    public static readonly SemaphoreSlim MutationGate = new(1, 1);
    public static string? UndoStrategy(ControlPlaneAuditLog audit, string project, long sequence)
    {
        var entry = audit.ReadNewest(item => item.Sequence == sequence, 1).SingleOrDefault();
        if (entry is null || entry.Operation != OperatorOperationIds.ProjectTestingSet || entry.Target != project
            || entry.Details is not { } details || !details.TryGetProperty("elevationClass", out var actionClass)
            || actionClass.ValueKind != JsonValueKind.String || actionClass.GetString() != "project.testing"
            || !details.TryGetProperty("oldStrategy", out var previous) || previous.ValueKind != JsonValueKind.String)
            return null;
        if (audit.ReadNewest(item => item.Operation == OperatorOperationIds.ProjectTestingSet && item.Target == project
                && item.Details is { } d && d.TryGetProperty("undoSequence", out var undo)
                && undo.ValueKind == JsonValueKind.Number && undo.TryGetInt64(out var id) && id == sequence, 1).Count > 0)
            return null;
        var strategy = previous.GetString();
        return TestingStrategies.IsKnown(strategy) ? strategy : null;
    }
}
