using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Scope predicted from paths cited in the title and brief. Files retain their
/// full path; folders retain the deepest cited prefix. Incidental documentation
/// folders, root folders and generated artifacts cannot serialize a card.
/// </summary>
public static class TaskScopePrediction
{
    private sealed record Prediction(string Title, long Length, long Modified, TaskParallelism Scope);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TaskInfo, Prediction> Cache = new();
    private static readonly object CacheGate = new();
    /// <summary>Maximum number of predicted scope prefixes per card.</summary>
    public const int MaxScopePrefixes = 24;

    internal static readonly Regex ScopePathRegex =
        new(@"\b((?:backend(?:\.Tests)?|frontend|src|docs|tools|runner(?:\.Tests)?|task-server(?:\.Tests)?|orchestrator-engine(?:\.Tests)?|orchestration-decisions|contracts|scripts|results)/[A-Za-z0-9_./-]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Predict from the card's title and its <c>prompt.md</c>, read best-effort.</summary>
    public static TaskParallelism Predict(TaskInfo info)
    {
        if (TaskModes.IsReportOnly(info.Mode)) return TaskParallelism.ReadOnlyTask;
        var text = info.Title ?? string.Empty;
        var prompt = new FileInfo(Path.Combine(info.FolderPath, "prompt.md"));
        var length = prompt.Exists ? prompt.Length : -1;
        var modified = prompt.Exists ? prompt.LastWriteTimeUtc.Ticks : 0;
        lock (CacheGate)
        {
            if (Cache.TryGetValue(info, out var cached) && cached.Title == text
                && cached.Length == length && cached.Modified == modified) return cached.Scope;
        }
        try
        {
            var pf = Path.Combine(info.FolderPath, "prompt.md");
            if (File.Exists(pf)) text += "\n" + File.ReadAllText(pf);
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "TaskScopePrediction: prompt.md read is best-effort"); }
        var result = FromText(text);
        lock (CacheGate)
        {
            Cache.Remove(info);
            Cache.Add(info, new(info.Title ?? "", length, modified, result));
        }
        return result;
    }

    /// <summary>Pure prediction from already collected title and prompt text.</summary>
    public static TaskParallelism FromText(string? text)
    {
        var scope = ScopePathRegex.Matches(text ?? string.Empty)
            .Select(m => m.Groups[1].Value)
            .Select(p => p.TrimEnd('.', ',', ';', ':', '/'))
            .Where(IsCollisionScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxScopePrefixes)
            .ToArray();
        return new TaskParallelism(false, scope);
    }
    internal static bool IsCollisionScope(string path)
    {
        var parts = path.Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length < 2 || parts.Any(part => part is "." or "..")) return false;
        if (parts.Any(part => new[] { "results", "generated", "bin", "obj", "node_modules", "dist", "coverage", "test-results" }
            .Contains(part, StringComparer.OrdinalIgnoreCase))) return false;
        var file = parts[^1];
        if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || file.Contains(".generated.", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".generated.ts", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || file.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)) return false;
        var isFile = Path.HasExtension(file);
        if (parts[0].Equals("docs", StringComparison.OrdinalIgnoreCase)) return isFile;
        return isFile || parts.Length >= 3;
    }

}
