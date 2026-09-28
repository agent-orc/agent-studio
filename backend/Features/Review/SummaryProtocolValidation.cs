using System.Text.RegularExpressions;

namespace AgentStudio.Review;

/// <summary>Validates model output before it can replace an application-owned Result.</summary>
public static class SummaryProtocolValidation
{
    public const string ErrorPrefix = "Invalid summary protocol: ";

    public static string? Validate(string? markdown)
    {
        var text = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Split('\n')[0] != "# Status")
            return ErrorPrefix + "the response must start with '# Status'.";

        if (Regex.Matches(text, @"(?m)^# ").Count != 1
            || text.Contains("```", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"\{\{[^\r\n]*\}\}"))
            return ErrorPrefix + "unexpected heading, code fence, or prompt placeholder.";

        var headings = Regex.Matches(text, @"(?m)^## (?<name>[^\n]+)$");
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var name = heading.Groups["name"].Value.Trim();
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            var body = text[(heading.Index + heading.Length)..end].Trim();
            if (!sections.TryAdd(name, body))
                return ErrorPrefix + $"duplicate section '## {name}'.";
            var visibleBody = Regex.Replace(body, @"<!--[\s\S]*?-->", string.Empty);
            if (!visibleBody.Split('\n').Any(line => !line.TrimStart().StartsWith('#') && line.Any(char.IsLetterOrDigit)))
                return ErrorPrefix + $"section '## {name}' is empty.";
        }

        string[] required = ["Overview", "What Was Done", "Open Items"];
        var previousIndex = -1;
        foreach (var name in required)
        {
            if (!sections.ContainsKey(name))
                return ErrorPrefix + $"missing section '## {name}'.";
            var index = text.IndexOf("## " + name + "\n", StringComparison.Ordinal);
            if (index <= previousIndex)
                return ErrorPrefix + "required sections are out of order.";
            previousIndex = index;
        }

        var head = text[..headings[0].Index];
        var result = Field(head, "Result");
        if (result is not ("Success" or "Failed" or "NoOp" or "Blocked" or "NeedsInput" or "Partial"))
            return ErrorPrefix + "missing or invalid Result field.";
        var taskCase = Field(head, "Case");
        if (taskCase is not ("bugfix" or "feature" or "refactor" or "docs" or "forensics" or "ui-cleanup" or "blocked" or "generic"))
            return ErrorPrefix + "missing or invalid Case field.";
        if (!HasContent(Field(head, "Duration")))
            return ErrorPrefix + "missing or empty Duration field.";
        foreach (var name in new[] { "Problem", "Solution" })
        {
            if (!HasContent(Field(sections["Overview"], name)))
                return ErrorPrefix + $"missing or empty {name} field in Overview.";
        }
        if (!Regex.IsMatch(sections["What Was Done"], @"(?m)^-\s+.*[\p{L}\p{N}]"))
            return ErrorPrefix + "What Was Done must contain a substantive bullet.";

        return null;
    }

    private static string? Field(string section, string name)
    {
        var fields = Regex.Matches(section, @"(?m)^- " + name + @":[ \t]*(?<value>[^\n]*)$");
        return fields.Count == 1 ? fields[0].Groups["value"].Value.Trim() : null;
    }

    private static bool HasContent(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Any(char.IsLetterOrDigit)
           && !(value.StartsWith('<') && value.EndsWith('>'));
}
