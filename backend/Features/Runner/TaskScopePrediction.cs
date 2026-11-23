using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// The prompt-path scope of a card (ADR-0052, moved out of <c>ProjectRunner</c>
/// for DS-0 of <c>docs/dispatcher-personas/index.html</c>). The local pickup,
/// the remote claim and the in-flight comparison set read the same prediction,
/// so both claim paths compare like with like. Behaviour is unchanged from the
/// former <c>ProjectRunner.PredictParallelism</c>: the parent directory of every
/// <c>backend/</c>, <c>frontend/</c>, <c>src/</c>, <c>docs/</c> or
/// <c>tools/</c> path named in the title and <c>prompt.md</c>, at most 24. A
/// card that names no such path has unknown scope, which the pick-gate admits
/// optimistically because every coding run is worktree-isolated.
/// </summary>
public static class TaskScopePrediction
{
    /// <summary>Maximum number of predicted scope prefixes per card.</summary>
    public const int MaxScopePrefixes = 24;

    internal static readonly Regex ScopePathRegex =
        new(@"\b((?:backend|frontend|src|docs|tools)/[A-Za-z0-9_./-]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Predict from the card's title and its <c>prompt.md</c>, read best-effort.</summary>
    public static TaskParallelism Predict(TaskInfo info)
    {
        var text = info.Title ?? string.Empty;
        try
        {
            var pf = Path.Combine(info.FolderPath, "prompt.md");
            if (File.Exists(pf)) text += "\n" + File.ReadAllText(pf);
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: best-effort"); /* best-effort */ }
        return FromText(text);
    }

    /// <summary>Pure prediction from already collected title and prompt text.</summary>
    public static TaskParallelism FromText(string? text)
    {
        var scope = ScopePathRegex.Matches(text ?? string.Empty)
            .Select(m => m.Groups[1].Value)
            .Select(p => { var i = p.LastIndexOf('/'); return i > 0 ? p.Substring(0, i) : p; })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxScopePrefixes)
            .ToArray();
        return new TaskParallelism(false, scope);
    }
}
