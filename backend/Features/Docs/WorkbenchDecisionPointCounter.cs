using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AgentStudio.Docs;

/// <summary>
/// Counts the same valid inline decision-point convention consumed by the
/// browser host without executing repository-authored HTML.
/// </summary>
public static partial class WorkbenchDecisionPointCounter
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "single", "multi", "confirm" };

    public static int Count(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        var document = new HtmlParser().ParseDocument(html);
        var decisionIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var element in document.QuerySelectorAll("[data-decision-id][data-decision-kind]"))
        {
            if (element.GetAttribute("data-decision-status") == "decided") continue;
            var id = element.GetAttribute("data-decision-id")?.Trim() ?? "";
            var kind = element.GetAttribute("data-decision-kind")?.Trim() ?? "";
            if (!SafeId().IsMatch(id) || !Kinds.Contains(kind) || decisionIds.Contains(id))
                continue;

            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in element.QuerySelectorAll("[data-option-id]"))
            {
                var optionId = option.GetAttribute("data-option-id")?.Trim() ?? "";
                if (SafeId().IsMatch(optionId)) optionIds.Add(optionId);
            }
            if (optionIds.Count == 0) continue;

            decisionIds.Add(id);
            count++;
        }
        return count;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
