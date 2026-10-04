using System.Text;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

/// <summary>
/// Renders the card-owned requirement boundary for semantic review. Structured
/// metadata is authoritative. The text inference is deliberately narrow and
/// exists only for cards authored before <c>acceptanceScope</c>: the prompt must
/// explicitly declare partial delivery or one slice per delivery.
/// </summary>
public static class RequirementAcceptanceScope
{
    private static readonly string[] BoundedDeclarations =
    [
        "partial delivery is success",
        "partial delivery is a success",
        "partial delivery counts as success",
        "one slice per delivery",
        "single slice per delivery",
        "one slice is delivered per card",
    ];

    public static string Describe(TaskAcceptanceScope? structured, string? taskBody)
    {
        var normalized = TaskAcceptanceScopes.Normalize(structured);
        if (normalized?.DeliveryMode == TaskAcceptanceDeliveryModes.BoundedSlice)
        {
            var sb = new StringBuilder();
            sb.AppendLf("Acceptance mode: bounded slice (structured card field, authoritative).");
            sb.AppendLf($"Slice: {normalized.Slice}");
            sb.AppendLf("Required criteria for this delivery:");
            foreach (var criterion in normalized.Criteria)
                sb.AppendLf($"- {criterion}");
            sb.AppendLf("Requirements outside this slice belong to the parent, Dossier, or later slice. Do not block this delivery because that broader wishlist remains open.");
            return sb.ToString().TrimEnd();
        }

        if (DeclaresBoundedLegacyScope(taskBody))
        {
            return "Acceptance mode: bounded slice (inferred from the card's explicit one-slice/partial-delivery declaration).\n" +
                   "Judge only the single slice claimed by the status summary and evidenced by the diff/results. " +
                   "Unchecked recommendations outside that claimed slice are future deliveries and must not block this one.";
        }

        return "Acceptance mode: full task. No bounded delivery scope is declared, so every load-bearing requirement in the task body is in scope.";
    }

    public static bool DeclaresBoundedLegacyScope(string? taskBody)
        => !string.IsNullOrWhiteSpace(taskBody)
           && BoundedDeclarations.Any(declaration =>
               taskBody.Contains(declaration, StringComparison.OrdinalIgnoreCase));
}
