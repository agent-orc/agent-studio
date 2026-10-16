using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

public static partial class TaskDossierProvenance
{
    public static TaskDossier FromImplementation(
        string path, ConceptImplementationTask item, int index) => new()
    {
        Path = path,
        Section = item.Section ?? SectionFromPrompt(item.PromptMarkdown) ?? item.Id,
        Decision = item.DecisionId ?? item.Decision?.Question,
        ImplementationIndex = index,
        Environments = item.Environments,
    };

    private static string? SectionFromPrompt(string prompt)
    {
        var match = SectionPattern().Match(prompt);
        return match.Success ? $"§{match.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"\b(?:implement\s+)?section\s+(\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SectionPattern();
}
