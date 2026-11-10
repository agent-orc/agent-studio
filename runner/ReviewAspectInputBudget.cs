using System.Text;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Bounds the final prompt, including material added after the plan was frozen.</summary>
internal static class ReviewAspectInputBudget
{
    internal const int MaximumCharacters = 800_000;
    private const int MaximumContextCharacters = 120_000;
    private const int MaximumDiffCharacters = 580_000;
    private const int MaximumFileCharacters = 40_000;

    internal sealed record Result(string Prompt, string Report);

    internal static Result Compose(string authored, string material, string screenshotSection)
    {
        var report = new StringBuilder();
        foreach (var line in screenshotSection.Split('\n'))
            if (line.Contains("omitted", StringComparison.OrdinalIgnoreCase))
                report.AppendLf("Screenshot evidence: " + line.Trim());
        if (material.Contains("[[REVIEW_DIFF_TRUNCATED:", StringComparison.Ordinal))
            report.AppendLf("Source diff was already limited by the review material file and line budget; see its truncation marker.");
        var context = authored;
        if (context.Length > MaximumContextCharacters)
        {
            report.AppendLf($"Authored context: {context.Length - MaximumContextCharacters} characters omitted.");
            context = context[..80_000] + "\n[Authored context omitted to meet review input budget.]\n"
                + context[^39_000..];
        }

        var fences = Regex.Matches(material, @"^```diff\r?\n(?<body>[\s\S]*?)^```\r?$", RegexOptions.Multiline);
        if (fences.Count == 0)
        {
            var promptWithoutDiff = Join(context, material, screenshotSection, report);
            return Finish(promptWithoutDiff, report);
        }
        var byFence = new List<List<(int Id, string Text, string File)>>();
        var all = new List<(int Id, string Text, string File)>();
        foreach (Match fence in fences)
        {
            var sections = new List<(int Id, string Text, string File)>();
            foreach (var section in Regex.Split(fence.Groups["body"].Value.TrimStart('\r', '\n'),
                         @"(?=^diff --git )", RegexOptions.Multiline).Where(section => section.Length > 0))
            {
                var item = (Id: all.Count, Text: section, File: FileName(section));
                sections.Add(item);
                all.Add(item);
            }
            byFence.Add(sections);
        }
        var ordered = all.OrderByDescending(section => IsTemplateOrStyle(section.File))
            .ThenBy(section => section.File, StringComparer.Ordinal).ToArray();
        var selected = new Dictionary<int, string>();
        var omitted = new List<string>();
        var used = 0;
        var fileUsage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var section in ordered)
        {
            var available = Math.Min(MaximumFileCharacters - fileUsage.GetValueOrDefault(section.File) - 1,
                MaximumDiffCharacters - used - 1);
            if (available < 300)
            {
                omitted.Add(section.File);
                continue;
            }
            if (section.Text.Length > available)
            {
                var cut = section.Text.LastIndexOf('\n', available - 1);
                if (cut < available / 2) cut = available;
                selected[section.Id] = section.Text[..cut] + "\n";
                used += selected[section.Id].Length;
                fileUsage[section.File] = fileUsage.GetValueOrDefault(section.File) + selected[section.Id].Length;
                report.AppendLf($"{section.File}: {section.Text.Length - cut} diff characters omitted (per-file or total limit).");
            }
            else
            {
                selected[section.Id] = section.Text;
                used += section.Text.Length;
                fileUsage[section.File] = fileUsage.GetValueOrDefault(section.File) + section.Text.Length;
            }
        }
        if (omitted.Count > 0)
            report.AppendLf($"Diff sections omitted ({omitted.Count}): {string.Join(", ", omitted)}");
        var bounded = new StringBuilder();
        var cursor = 0;
        for (var index = 0; index < fences.Count; index++)
        {
            var fence = fences[index];
            bounded.Append(material, cursor, fence.Index - cursor).AppendLf("```diff");
            foreach (var section in byFence[index])
                if (selected.TryGetValue(section.Id, out var excerpt)) bounded.Append(excerpt);
            bounded.Append("```");
            cursor = fence.Index + fence.Length;
        }
        bounded.Append(material, cursor, material.Length - cursor);
        var boundedMaterial = bounded.ToString();
        return Finish(Join(context, boundedMaterial, screenshotSection, report), report);
    }

    private static Result Finish(string prompt, StringBuilder report)
    {
        if (prompt.Length > MaximumCharacters)
        {
            var excess = prompt.Length - MaximumCharacters + 512;
            report.AppendLf($"Final input: {excess} additional characters omitted to meet the CLI limit.");
            prompt = prompt[..(MaximumCharacters - 512)];
        }
        var summary = report.Length == 0
            ? "No review input omitted."
            : report.ToString().TrimEnd();
        if (summary.Length > 20_000)
            summary = summary[..20_000] + "\nAdditional omission names omitted from this report.";
        var note = "\n\n## Review input omissions\n" + summary;
        if (prompt.Length + note.Length > MaximumCharacters)
            prompt = prompt[..(MaximumCharacters - note.Length)];
        return new Result(prompt + note, summary);
    }

    private static string Join(string context, string material, string screenshots, StringBuilder report)
    {
        if (screenshots.Length > 24_000)
        {
            report.AppendLf($"Screenshot section: {screenshots.Length - 24_000} characters omitted.");
            screenshots = screenshots[..24_000];
        }
        var marker = RemoteReviewWorkspace.SharedMaterialMarker;
        var at = context.IndexOf(marker, StringComparison.Ordinal);
        return at < 0
            ? context.TrimEnd() + "\n\n" + material + screenshots
            : context[..at] + material + "\n\n" + context[(at + marker.Length)..].TrimStart() + screenshots;
    }

    private static string FileName(string section)
    {
        var line = section.Split('\n', 2)[0];
        var at = line.LastIndexOf(" b/", StringComparison.Ordinal);
        return at < 0 ? "unknown diff section" : line[(at + 3)..].Trim();
    }

    private static bool IsTemplateOrStyle(string path)
        => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".scss", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".sass", StringComparison.OrdinalIgnoreCase);
}
