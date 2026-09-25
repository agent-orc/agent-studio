using System.Text.Json;
using AngleSharp.Html.Parser;

namespace AgentStudio.Pipeline;

/// <summary>Reads Dossier option labels and recorded operator responses for promoted cards.</summary>
public static class ConceptDecisionAssumptions
{
    public static List<ConceptDecisionAssumption> Read(string htmlPath, string descriptorPath)
    {
        if (!File.Exists(htmlPath)) return [];
        var selected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (File.Exists(descriptorPath))
        {
            using var descriptor = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            if (descriptor.RootElement.TryGetProperty("decision", out var decision)
                && decision.TryGetProperty("responses", out var responses)
                && responses.ValueKind == JsonValueKind.Array)
            {
                foreach (var response in responses.EnumerateArray())
                {
                    if (!response.TryGetProperty("decisionId", out var id)
                        || !response.TryGetProperty("selectedOptionIds", out var ids)
                        || ids.ValueKind != JsonValueKind.Array) continue;
                    var key = id.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                        selected[key] = ids.EnumerateArray()
                            .Where(value => value.ValueKind == JsonValueKind.String)
                            .Select(value => value.GetString()!)
                            .ToHashSet(StringComparer.Ordinal);
                }
            }
        }

        var document = new HtmlParser().ParseDocument(File.ReadAllText(htmlPath));
        var result = new List<ConceptDecisionAssumption>();
        foreach (var block in document.QuerySelectorAll("[data-decision-id][data-decision-kind]"))
        {
            var id = block.GetAttribute("data-decision-id")?.Trim();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var options = block.QuerySelectorAll("[data-option-id]");
            var operatorChoice = selected.TryGetValue(id, out var selectedIds)
                && selectedIds!.Count > 0;
            foreach (var option in options)
            {
                var optionId = option.GetAttribute("data-option-id")?.Trim();
                if (string.IsNullOrWhiteSpace(optionId)) continue;
                var optionLabel = option.GetAttribute("data-option-label")?.Trim();
                if (string.IsNullOrWhiteSpace(optionLabel)) optionLabel = option.TextContent.Trim();
                var recommended = optionLabel.Contains("recommended", StringComparison.OrdinalIgnoreCase)
                    || option.QuerySelector("input[checked]") is not null;
                result.Add(new ConceptDecisionAssumption
                {
                    Id = id,
                    Label = block.GetAttribute("data-decision-label")?.Trim() ?? id,
                    OptionId = optionId,
                    OptionLabel = optionLabel,
                    OperatorSelected = operatorChoice && selectedIds!.Contains(optionId),
                    IsWorkingAssumption = operatorChoice ? selectedIds!.Contains(optionId) : recommended,
                });
            }
        }
        return result;
    }
}
