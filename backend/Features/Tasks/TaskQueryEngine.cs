using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace AgentStudio.Tasks;

internal sealed record TaskQueryRequest
{
    private static readonly HashSet<string> AnalysisKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "state", "kind", "cliType", "model", "epicId", "mode", "phase", "tag", "area",
        "verdict", "issueKind", "hasIssue", "minCommits", "maxCommits",
        "activitySince", "activityBefore", "createdSince", "createdBefore",
        "durationMin", "durationMax", "sortBy", "order", "limit", "offset", "fields",
        "q", "regex", "caseSensitive", "in", "aggregate"
    };

    public bool IsActive { get; init; }
    public string[] Project { get; init; } = [];
    public string[] State { get; init; } = [];
    public string[] Kind { get; init; } = [];
    public string[] CliType { get; init; } = [];
    public string[] Model { get; init; } = [];
    public string[] EpicId { get; init; } = [];
    public string[] Mode { get; init; } = [];
    public string[] Phase { get; init; } = [];
    public string[] Tag { get; init; } = [];
    /// <summary>
    /// AGT-2803: area tag ids. Alternatives among themselves, and a
    /// conjunction with <see cref="Tag"/>, so area plus facet narrows.
    /// </summary>
    public string[] Area { get; init; } = [];
    public string[] Verdict { get; init; } = [];
    public string[] IssueKind { get; init; } = [];
    public bool? HasIssue { get; init; }
    public int? MinCommits { get; init; }
    public int? MaxCommits { get; init; }
    public DateTime? ActivitySince { get; init; }
    public DateTime? ActivityBefore { get; init; }
    public DateTime? CreatedSince { get; init; }
    public DateTime? CreatedBefore { get; init; }
    public double? DurationMin { get; init; }
    public double? DurationMax { get; init; }
    public string SortBy { get; init; } = "lastActivity";
    public string Order { get; init; } = "desc";
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
    public string[] Fields { get; init; } = [];
    public string? Q { get; init; }
    public bool Regex { get; init; }
    public bool CaseSensitive { get; init; }
    public string[] In { get; init; } = [];
    public string[] Aggregate { get; init; } = [];

    public static TaskQueryRequest FromQuery(IQueryCollection query)
    {
        var isActive = query.Keys.Any(k => AnalysisKeys.Contains(k));
        return new TaskQueryRequest
        {
            IsActive = isActive,
            Project = Csv(query, "project"),
            State = Csv(query, "state"),
            Kind = Csv(query, "kind"),
            CliType = Csv(query, "cliType"),
            Model = Csv(query, "model"),
            EpicId = Csv(query, "epicId"),
            Mode = Csv(query, "mode"),
            Phase = Csv(query, "phase"),
            Tag = Csv(query, "tag"),
            Area = Csv(query, "area"),
            Verdict = Csv(query, "verdict"),
            IssueKind = Csv(query, "issueKind"),
            HasIssue = Bool(query, "hasIssue"),
            MinCommits = Int(query, "minCommits"),
            MaxCommits = Int(query, "maxCommits"),
            ActivitySince = Date(query, "activitySince"),
            ActivityBefore = Date(query, "activityBefore"),
            CreatedSince = Date(query, "createdSince"),
            CreatedBefore = Date(query, "createdBefore"),
            DurationMin = Double(query, "durationMin"),
            DurationMax = Double(query, "durationMax"),
            SortBy = First(query, "sortBy") ?? "lastActivity",
            Order = First(query, "order") ?? "desc",
            Limit = Math.Clamp(Int(query, "limit") ?? 100, 1, 1000),
            Offset = Math.Max(0, Int(query, "offset") ?? 0),
            Fields = Csv(query, "fields"),
            Q = First(query, "q"),
            Regex = Bool(query, "regex") ?? false,
            CaseSensitive = Bool(query, "caseSensitive") ?? false,
            In = Csv(query, "in"),
            Aggregate = Csv(query, "aggregate"),
        };
    }

    private static string? First(IQueryCollection query, string key)
        => query.TryGetValue(key, out var value) ? value.FirstOrDefault() : null;

    private static string[] Csv(IQueryCollection query, string key)
        => query.TryGetValue(key, out var values)
            ? values.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : [];

    private static bool? Bool(IQueryCollection query, string key)
        => bool.TryParse(First(query, key), out var value) ? value : null;

    private static int? Int(IQueryCollection query, string key)
        => int.TryParse(First(query, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? Double(IQueryCollection query, string key)
        => double.TryParse(First(query, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTime? Date(IQueryCollection query, string key)
        => DateTime.TryParse(First(query, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : null;
}

internal sealed record TaskQueryResponse
{
    public int Total { get; init; }
    public int Limit { get; init; }
    public int Offset { get; init; }
    public IReadOnlyList<object> Items { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Aggregates { get; init; }
    public string? Error { get; init; }
}

internal sealed record TaskSearchMatch(string File, int Line, string Snippet);

internal sealed record TaskSearchResult(
    string Key,
    string Id,
    string Title,
    string State,
    string Project,
    TaskSearchMatch Match);

internal static partial class TaskQueryEngine
{
    private const int MaxSearchChars = 200_000;

    public static TaskQueryResponse Execute(IReadOnlyList<TaskInfo> source, TaskQueryRequest query)
    {
        var needsDuration = NeedsDuration(query);
        Regex? regex = null;
        if (!string.IsNullOrWhiteSpace(query.Q) && query.Regex)
        {
            try
            {
                regex = new Regex(query.Q, query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                return new TaskQueryResponse { Error = $"Invalid regex: {ex.Message}" };
            }
        }

        var rows = source
            .Select(job => new TaskQueryRow(job, null))
            .Where(row => MatchesNonDurationFilters(row, query))
            .ToList();
        if (needsDuration)
        {
            rows = rows
                .Select(row => row with { DurationSeconds = LatestDurationSeconds(row.Job) })
                .Where(row => MatchesDurationFilters(row, query))
                .ToList();
        }

        List<(TaskQueryRow Row, TaskSearchMatch? Match)> matched;
        if (string.IsNullOrWhiteSpace(query.Q))
        {
            matched = rows.Select(row => (row, (TaskSearchMatch?)null)).ToList();
        }
        else
        {
            matched = [];
            foreach (var row in rows)
            {
                var match = FindMatch(row.Job, query, regex);
                if (match != null) matched.Add((row, match));
            }
        }

        var aggregates = query.Aggregate.Length == 0
            ? null
            : BuildAggregates(matched.Select(m => m.Row.Job), query.Aggregate);

        var sorted = Sort(matched, query).ToList();
        var total = sorted.Count;
        var page = sorted
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(item => ShapeItem(item.Row, item.Match, query))
            .ToList();

        return new TaskQueryResponse
        {
            Total = total,
            Limit = query.Limit,
            Offset = query.Offset,
            Items = page,
            Aggregates = aggregates
        };
    }

    private static bool MatchesNonDurationFilters(TaskQueryRow row, TaskQueryRequest q)
    {
        var j = row.Job;
        return In(q.Project, j.ProjectName)
               && In(q.State, j.State)
               && In(q.Kind, j.Kind)
               && In(q.CliType, j.CliType)
               && In(q.Model, j.Model)
               && In(q.EpicId, j.EpicId)
               && In(q.Mode, j.Mode)
               && In(q.Phase, j.Phase)
               && (q.Tag.Length == 0 || q.Tag.Any(t => j.Tags.Any(x => Eq(x, t))))
               && (q.Area.Length == 0 || q.Area.Any(a => j.Tags.Any(x => Eq(x, a))))
               && In(q.Verdict, j.OrchestratorVerdict)
               && In(q.IssueKind, j.OutcomeIssue?.Kind)
               && (q.HasIssue is null || (j.OutcomeIssue != null) == q.HasIssue.Value)
               && (q.MinCommits is null || j.CommitCount >= q.MinCommits.Value)
               && (q.MaxCommits is null || j.CommitCount <= q.MaxCommits.Value)
               && (q.ActivitySince is null || j.LastActivity >= q.ActivitySince.Value)
               && (q.ActivityBefore is null || j.LastActivity <= q.ActivityBefore.Value)
               && (q.CreatedSince is null || j.CreatedAt >= q.CreatedSince.Value)
               && (q.CreatedBefore is null || j.CreatedAt <= q.CreatedBefore.Value);
    }

    private static bool MatchesDurationFilters(TaskQueryRow row, TaskQueryRequest q)
        => (q.DurationMin is null || row.DurationSeconds >= q.DurationMin.Value)
           && (q.DurationMax is null || row.DurationSeconds <= q.DurationMax.Value);

    private static IEnumerable<(TaskQueryRow Row, TaskSearchMatch? Match)> Sort(
        IEnumerable<(TaskQueryRow Row, TaskSearchMatch? Match)> rows,
        TaskQueryRequest q)
    {
        var desc = !string.Equals(q.Order, "asc", StringComparison.OrdinalIgnoreCase);
        return q.SortBy.ToLowerInvariant() switch
        {
            "createdat" => desc ? rows.OrderByDescending(x => x.Row.Job.CreatedAt) : rows.OrderBy(x => x.Row.Job.CreatedAt),
            "commits" => desc ? rows.OrderByDescending(x => x.Row.Job.CommitCount) : rows.OrderBy(x => x.Row.Job.CommitCount),
            "duration" => desc ? rows.OrderByDescending(x => x.Row.DurationSeconds ?? -1) : rows.OrderBy(x => x.Row.DurationSeconds ?? double.MaxValue),
            "key" => desc ? rows.OrderByDescending(x => x.Row.Job.Key ?? x.Row.Job.Id, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(x => x.Row.Job.Key ?? x.Row.Job.Id, StringComparer.OrdinalIgnoreCase),
            _ => desc ? rows.OrderByDescending(x => x.Row.Job.LastActivity) : rows.OrderBy(x => x.Row.Job.LastActivity),
        };
    }

    private static object ShapeItem(TaskQueryRow row, TaskSearchMatch? match, TaskQueryRequest q)
    {
        var job = row.Job;
        if (match != null && q.Fields.Length == 0)
            return new TaskSearchResult(job.Key ?? job.Id, job.Id, job.Title, job.State, job.ProjectName, match);

        if (q.Fields.Length == 0) return job;

        var shaped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in q.Fields)
            shaped[field] = FieldValue(row, field, match);
        if (match != null && !shaped.ContainsKey("match")) shaped["match"] = match;
        return shaped;
    }

    private static object? FieldValue(TaskQueryRow row, string field, TaskSearchMatch? match) => field.ToLowerInvariant() switch
    {
        "id" => row.Job.Id,
        "key" => row.Job.Key ?? row.Job.Id,
        "title" => row.Job.Title,
        "state" => row.Job.State,
        "project" or "projectname" => row.Job.ProjectName,
        "kind" => row.Job.Kind,
        "clitype" => row.Job.CliType,
        "model" => row.Job.Model,
        "epicid" => row.Job.EpicId,
        "mode" => row.Job.Mode,
        "phase" => row.Job.Phase,
        "tags" => row.Job.Tags,
        "verdict" or "orchestratorverdict" => row.Job.OrchestratorVerdict,
        "issuekind" => row.Job.OutcomeIssue?.Kind,
        "hasissue" => row.Job.OutcomeIssue != null,
        "commits" or "commitcount" => row.Job.CommitCount,
        "lastactivity" => row.Job.LastActivity,
        "createdat" => row.Job.CreatedAt,
        "duration" => row.DurationSeconds,
        "match" => match,
        _ => null
    };

    private static Dictionary<string, IReadOnlyDictionary<string, int>> BuildAggregates(IEnumerable<TaskInfo> jobs, string[] groups)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            IEnumerable<string?> values = group.ToLowerInvariant() switch
            {
                "state" => jobs.Select(j => j.State),
                "verdict" => jobs.Select(j => j.OrchestratorVerdict),
                "issuekind" => jobs.Select(j => j.OutcomeIssue?.Kind),
                "model" => jobs.Select(j => j.Model),
                "clitype" => jobs.Select(j => j.CliType),
                _ => []
            };
            result[group] = values
                .Select(v => string.IsNullOrWhiteSpace(v) ? "(none)" : v!)
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    private static TaskSearchMatch? FindMatch(TaskInfo job, TaskQueryRequest query, Regex? regex)
    {
        foreach (var candidate in SearchCandidates(job, query.In))
        {
            var match = MatchText(candidate.Text, query.Q!, query.CaseSensitive, regex);
            if (match != null) return new TaskSearchMatch(candidate.Name, match.Value.Line, match.Value.Snippet);
        }
        return null;
    }

    private static (int Line, string Snippet)? MatchText(string text, string needle, bool caseSensitive, Regex? regex)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var hit = regex != null
                ? regex.IsMatch(line)
                : line.IndexOf(needle, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;
            if (hit) return (i + 1, Snippet(line));
        }
        return null;
    }

    private static IEnumerable<(string Name, string Text)> SearchCandidates(TaskInfo job, string[] scopes)
    {
        var selected = scopes.Length == 0 ? ["title", "prompt", "status", "review", "log", "output"] : scopes;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in selected.Select(s => s.ToLowerInvariant()))
        {
            if (scope == "title")
            {
                yield return ("title", job.Title);
                continue;
            }

            foreach (var path in PathsForScope(job.FolderPath, scope))
            {
                if (!emitted.Add(path)) continue;
                var text = ReadTail(path, MaxSearchChars);
                if (text != null) yield return (RelativeSearchName(job.FolderPath, path), text);
            }
        }
    }

    private static IEnumerable<string> PathsForScope(string folder, string scope)
    {
        switch (scope)
        {
            case "prompt":
                yield return Path.Combine(folder, "prompt.md");
                break;
            case "status":
                yield return Path.Combine(folder, "status.md");
                break;
            case "review":
                if (Directory.Exists(folder))
                {
                    foreach (var path in Directory.EnumerateFiles(folder, "code-review-*.md", SearchOption.TopDirectoryOnly))
                        yield return path;
                }
                break;
            case "output":
                yield return Path.Combine(folder, "logs", "cli-output.log");
                break;
            case "log":
                var logs = Path.Combine(folder, "logs");
                if (Directory.Exists(logs))
                {
                    foreach (var path in Directory.EnumerateFiles(logs, "*.log", SearchOption.TopDirectoryOnly))
                        yield return path;
                }
                break;
        }
    }

    private static double? LatestDurationSeconds(TaskInfo job)
    {
        var log = ReadTail(Path.Combine(job.FolderPath, "logs", "cli-output.log"), 80_000);
        if (log == null) return null;
        double? latest = null;
        foreach (Match match in DurationRegex().Matches(log))
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                latest = value;
        }
        return latest;
    }

    private static string? ReadTail(string path, int maxChars)
    {
        if (!File.Exists(path)) return null;
        var text = TaskScannerService.ReadTailUtf8(path, maxChars * 4);
        if (text.Length == 0) return "";
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    private static bool NeedsDuration(TaskQueryRequest q)
        => q.DurationMin is not null
           || q.DurationMax is not null
           || string.Equals(q.SortBy, "duration", StringComparison.OrdinalIgnoreCase)
           || q.Fields.Any(field => string.Equals(field, "duration", StringComparison.OrdinalIgnoreCase));

    private static bool In(string[] accepted, string? value)
        => accepted.Length == 0 || accepted.Any(x => Eq(x, value));

    private static bool Eq(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Snippet(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 220 ? trimmed : trimmed[..217].TrimEnd() + "...";
    }

    private static string RelativeSearchName(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch { return Path.GetFileName(path); }
    }

    [GeneratedRegex(@"duration=([0-9]+(?:\.[0-9]+)?)s", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    private sealed record TaskQueryRow(TaskInfo Job, double? DurationSeconds);
}
