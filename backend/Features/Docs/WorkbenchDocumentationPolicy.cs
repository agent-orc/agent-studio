namespace AgentStudio.Docs;

/// <summary>
/// Pure lifecycle decision for the documented end state. A decided item stays
/// current while any referenced card is unresolved or outside a terminal lane.
/// </summary>
public static class WorkbenchDocumentationPolicy
{
    public static WorkbenchDocumentationProjection Evaluate(
        string status,
        IEnumerable<WorkbenchDocumentationReference> references)
    {
        var normalized = references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Key))
            .DistinctBy(reference => reference.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var terminal = normalized.Count(reference => reference.Exists && reference.Terminal);
        var missing = normalized.Count(reference => !reference.Exists);
        var open = normalized.Count - terminal - missing;
        var eligible = status == "decided"
            && normalized.Count > 0
            && terminal == normalized.Count;

        return new WorkbenchDocumentationProjection(
            eligible,
            normalized.Count,
            terminal,
            open,
            missing,
            normalized)
        {
            BlockedReason = status != "decided" || eligible ? null : BlockedReason(normalized),
        };
    }

    /// <summary>Names every card that still keeps a decided item current.</summary>
    private static string BlockedReason(IReadOnlyList<WorkbenchDocumentationReference> references)
    {
        if (references.Count == 0)
            return "No card is assigned to this item yet, so nothing confirms the implementation.";
        var parts = new List<string>();
        var open = references.Where(reference => reference.Exists && !reference.Terminal)
            .Select(reference => $"{reference.Key} ({reference.Lane ?? "unknown lane"})")
            .ToArray();
        if (open.Length > 0)
            parts.Add($"Still open: {string.Join(", ", open)}.");
        var missing = references.Where(reference => !reference.Exists)
            .Select(reference => reference.Key)
            .ToArray();
        if (missing.Length > 0)
            parts.Add($"Not found: {string.Join(", ", missing)}.");
        return "Mark documented waits until every assigned card is completed or archived. "
            + string.Join(" ", parts);
    }
}

public sealed record WorkbenchDocumentationReference(
    string Key,
    bool Exists,
    bool Terminal,
    string? Lane);

public sealed record WorkbenchDocumentationProjection(
    bool Eligible,
    int TotalCount,
    int TerminalCount,
    int OpenCount,
    int MissingCount,
    IReadOnlyList<WorkbenchDocumentationReference> References)
{
    /// <summary>Why a decided item cannot be marked documented yet; null when it can.</summary>
    public string? BlockedReason { get; init; }
}
