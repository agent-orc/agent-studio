using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

public enum VisualQaVerdictStatus
{
    Acceptable,
    ClearDefect,
    Unavailable,
}

public enum VisualQaAction
{
    ProceedToHumanReview,
    RetryWithSteer,
    EscalateToHumanReview,
}

public sealed record VisualQaDefect(
    string Category,
    string Description,
    string? Screenshot = null);

public sealed record VisualQaVerdict(
    VisualQaVerdictStatus Status,
    string Summary,
    IReadOnlyList<VisualQaDefect> Defects);

public sealed record VisualQaRoute(string Label, string Path);

public sealed record VisualQaDecision(
    VisualQaAction Action,
    string Reason,
    string? SteerPrompt = null);

/// <summary>
/// Pure admission, route-selection, verdict parsing, and retry policy for the
/// first visual QA slice. The model names visible defects; this policy remains
/// the only authority that may turn that verdict into a lane-affecting action.
/// </summary>
public static partial class VisualQaPolicy
{
    public const int MaxAutomaticDefectRetries = 1;
    public const int MaxRoutes = 4;
    public const int MaxDefects = 8;

    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "truncation",
        "misalignment",
        "placeholder-noise",
        "design-token-violation",
        "overlap",
        "overflow",
        "unreadable",
        "broken-layout",
    };

    public static bool IsApplicable(
        string? taskKey,
        IReadOnlyCollection<string>? changedFiles)
    {
        if (string.IsNullOrWhiteSpace(taskKey)
            || !taskKey.Trim().StartsWith("AGT-", StringComparison.OrdinalIgnoreCase))
            return false;

        // A null set means provenance could not be resolved. The caller is
        // already in the UI pipeline, so fail closed and retain visual QA. An
        // empty, authoritative diff does not claim to touch frontend/.
        return changedFiles is null
               || changedFiles.Count > 0 && changedFiles.Any(IsFrontendFile);
    }

    public static IReadOnlyList<VisualQaRoute> ResolveRoutes(
        string taskKey,
        string projectId,
        string? cardPrompt,
        IReadOnlyCollection<string>? changedFiles)
    {
        var routes = new List<VisualQaRoute>();

        foreach (Match match in ExplicitRouteRegex().Matches(cardPrompt ?? string.Empty))
        {
            var path = NormalizeRoute(match.Groups["route"].Value);
            if (path is not null) Add(routes, RouteLabel(path), path);
        }

        foreach (var raw in changedFiles ?? [])
        {
            var file = raw.Replace('\\', '/').ToLowerInvariant();
            if (!IsFrontendFile(file)) continue;

            if (file.Contains("workspace-settings") || file.Contains("/settings/"))
                Add(routes, "workspace-settings", "/#/workspace/settings");
            if (file.Contains("task-detail") || file.Contains("task-timeline"))
                Add(routes, "task-detail", $"/?task={Uri.EscapeDataString(taskKey)}");
            if (file.Contains("project-detail"))
                Add(routes, "project-hub", $"/#/projects/{Uri.EscapeDataString(projectId)}");
            if (file.Contains("workbench") || file.Contains("dossier"))
                Add(routes, "dossiers", "/#/workbenches");
            if (file.Contains("activity") || file.Contains("/feed/"))
                Add(routes, "activity", "/#/feed");
            if (file.Contains("board") || file.Contains("job-column") || file.Contains("task-card"))
                Add(routes, "board", "/#/board");
        }

        // Every card has one stable, directly navigable view. It also gives a
        // useful fallback when a newly introduced component has no route map yet.
        Add(routes, "task-detail", $"/?task={Uri.EscapeDataString(taskKey)}");
        return routes.Take(MaxRoutes).ToArray();
    }

    public static VisualQaVerdict ParseVerdict(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return Unavailable("The visual reviewer returned no verdict.");

        var json = ExtractJson(response);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusNode)
                ? statusNode.GetString()?.Trim().ToLowerInvariant()
                : null;
            var summary = root.TryGetProperty("summary", out var summaryNode)
                ? summaryNode.GetString()?.Trim()
                : null;
            var defects = new List<VisualQaDefect>();
            if (root.TryGetProperty("defects", out var defectNodes)
                && defectNodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in defectNodes.EnumerateArray().Take(MaxDefects))
                {
                    if (node.ValueKind != JsonValueKind.Object) continue;
                    var category = node.TryGetProperty("category", out var categoryNode)
                        ? categoryNode.GetString()?.Trim().ToLowerInvariant()
                        : null;
                    var description = node.TryGetProperty("description", out var descriptionNode)
                        ? descriptionNode.GetString()?.Trim()
                        : null;
                    var screenshot = node.TryGetProperty("screenshot", out var screenshotNode)
                        ? screenshotNode.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(category)
                        || string.IsNullOrWhiteSpace(description)
                        || !AllowedCategories.Contains(category))
                        continue;
                    defects.Add(new VisualQaDefect(category, description, screenshot));
                }
            }

            return status switch
            {
                "acceptable" when defects.Count == 0 => new VisualQaVerdict(
                    VisualQaVerdictStatus.Acceptable,
                    summary ?? "No clear visual defect was found.",
                    []),
                "clear-defect" when defects.Count > 0 => new VisualQaVerdict(
                    VisualQaVerdictStatus.ClearDefect,
                    summary ?? $"The visual reviewer found {defects.Count} clear defect(s).",
                    defects),
                _ => Unavailable(
                    "The visual reviewer response did not match the required acceptable or clear-defect contract."),
            };
        }
        catch (JsonException exception)
        {
            return Unavailable($"The visual reviewer returned invalid JSON: {exception.Message}");
        }
    }

    public static VisualQaDecision Decide(VisualQaVerdict verdict, int priorAutomaticRetries)
    {
        if (verdict.Status == VisualQaVerdictStatus.Acceptable)
        {
            return new VisualQaDecision(
                VisualQaAction.ProceedToHumanReview,
                verdict.Summary);
        }

        if (verdict.Status == VisualQaVerdictStatus.ClearDefect
            && priorAutomaticRetries < MaxAutomaticDefectRetries)
        {
            return new VisualQaDecision(
                VisualQaAction.RetryWithSteer,
                $"Clear visual defects found; automatic visual retry {priorAutomaticRetries + 1} of {MaxAutomaticDefectRetries} is allowed.",
                BuildSteerPrompt(verdict.Defects));
        }

        return new VisualQaDecision(
            VisualQaAction.EscalateToHumanReview,
            verdict.Status == VisualQaVerdictStatus.ClearDefect
                ? $"Clear visual defects remain after the single automatic retry: {verdict.Summary}"
                : $"Visual QA could not produce a trustworthy verdict: {verdict.Summary}");
    }

    public static string BuildSteerPrompt(IReadOnlyList<VisualQaDefect> defects)
    {
        var builder = new StringBuilder();
        builder.AppendLf("Automatic visual QA found clear visible defects. Fix only the named defects, preserve the intended behavior, and rerun the relevant checks:");
        foreach (var defect in defects.Take(MaxDefects))
        {
            builder.Append("- ").Append(defect.Category).Append(": ").Append(defect.Description);
            if (!string.IsNullOrWhiteSpace(defect.Screenshot))
                builder.Append(" (evidence: ").Append(defect.Screenshot).Append(')');
            builder.AppendLf();
        }
        builder.AppendLf("This is the one automatic visual-QA steer round. End with [[TASK_DONE]] and leave the new iteration evidence in the existing results directory.");
        return builder.ToString().TrimEnd();
    }

    private static bool IsFrontendFile(string path)
        => path.Replace('\\', '/').TrimStart('/')
            .StartsWith("frontend/", StringComparison.OrdinalIgnoreCase);

    private static void Add(ICollection<VisualQaRoute> routes, string label, string path)
    {
        if (routes.Count >= MaxRoutes) return;
        if (routes.Any(route => string.Equals(route.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        routes.Add(new VisualQaRoute(label, path));
    }

    private static string? NormalizeRoute(string raw)
    {
        var value = raw.Trim().TrimEnd('.', ',', ')', ']', '`', '\'', '"');
        if ((value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            value = absolute.PathAndQuery + absolute.Fragment;
        if (!value.StartsWith("/", StringComparison.Ordinal)) return null;
        if (value.Contains("..", StringComparison.Ordinal)) return null;
        return value;
    }

    private static string RouteLabel(string route)
    {
        if (route.Contains("task=", StringComparison.OrdinalIgnoreCase)) return "task-detail";
        if (route.Contains("workspace/settings", StringComparison.OrdinalIgnoreCase)) return "workspace-settings";
        if (route.Contains("workbenches", StringComparison.OrdinalIgnoreCase)) return "dossiers";
        if (route.Contains("/feed", StringComparison.OrdinalIgnoreCase)) return "activity";
        if (route.Contains("/board", StringComparison.OrdinalIgnoreCase)) return "board";
        if (route.Contains("/projects/", StringComparison.OrdinalIgnoreCase)) return "project-hub";
        return "affected-view";
    }

    private static string ExtractJson(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && closing > firstLine)
                trimmed = trimmed[(firstLine + 1)..closing].Trim();
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static VisualQaVerdict Unavailable(string summary)
        => new(VisualQaVerdictStatus.Unavailable, summary, []);

    [GeneratedRegex(@"(?<route>https?://[^\s<]+|/(?:\?task=[A-Za-z0-9._-]+|#/[^\s<]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitRouteRegex();
}
