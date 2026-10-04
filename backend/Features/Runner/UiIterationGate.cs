using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

public enum UiIterationGateAction
{
    ReadyForHumanReview,
    Incomplete,
    EscalateCapReached,
}

public sealed record UiIterationGateDecision
{
    public UiIterationGateAction Action { get; init; }
    public int Iteration { get; init; }
    public int MaxIterations { get; init; }
    public bool CapReached => Iteration >= MaxIterations;
    public string IterationDirectory { get; init; } = string.Empty;
    public string? ChangeDescriptionPath { get; init; }
    public IReadOnlyList<string> ArtifactPaths { get; init; } = [];
    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>
/// Per-iteration UI evidence contract. Every completed iteration owns an
/// isolated <c>results/ui-iteration-NNN/</c> directory with at least one image
/// and a non-empty <c>changes.md</c>. Evidence from an earlier iteration cannot
/// accidentally satisfy a later gate.
/// </summary>
public static class UiIterationGate
{
    public const int DefaultMaxIterations = 4;
    public const int MinimumIterations = 1;
    public const int MaximumIterations = 10;
    public const string ChangeDescriptionFileName = "changes.md";

    private static readonly Regex IterationDirectoryRegex = new(
        @"^ui-iteration-(?<iteration>\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    public static int NextIteration(string? jobFolder)
    {
        var highest = HighestIteration(jobFolder);
        return highest + 1;
    }

    /// <summary>
    /// Resolves the iteration a run must continue. A Human Review feedback
    /// marker starts the next iteration. Without that explicit boundary, the
    /// highest existing iteration directory remains active, even when it is
    /// empty or incomplete. This prevents an evidence-only retry from falling
    /// back to an earlier iteration when the agent produced no files at all.
    /// </summary>
    public static int ResolveRunIteration(
        string? jobFolder,
        UiIterationReviewContract? reviewedIteration = null)
    {
        if (reviewedIteration is not null)
            return Math.Max(1, reviewedIteration.Iteration + 1);

        return Math.Max(1, HighestIteration(jobFolder));
    }

    /// <summary>
    /// Materializes the iteration-scoped result directory at admission time.
    /// The directory doubles as a durable current-iteration checkpoint across
    /// bounded retries and backend restarts; only a Human Review marker may
    /// advance the number.
    /// </summary>
    public static void PrepareIterationDirectory(string jobFolder, int iteration)
        => Directory.CreateDirectory(IterationDirectory(jobFolder, iteration));

    private static int HighestIteration(string? jobFolder)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return 0;
        var results = TaskPaths.ResultsDir(jobFolder);
        if (!Directory.Exists(results)) return 0;
        try
        {
            return Directory.EnumerateDirectories(results, "ui-iteration-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Select(name => IterationDirectoryRegex.Match(name ?? string.Empty))
                .Where(match => match.Success)
                .Select(match => int.Parse(match.Groups["iteration"].Value, System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
        }
        catch
        {
            return 0;
        }
    }

    public static string IterationDirectory(string jobFolder, int iteration)
        => Path.Combine(TaskPaths.ResultsDir(jobFolder), $"ui-iteration-{iteration:D3}");

    /// <summary>
    /// Part 2 submits feedback as Continue. At or beyond the cap that action is
    /// no longer legal and must take the existing escalation funnel instead.
    /// Recomputing from the counters keeps the boundary safe if an older writer
    /// omitted or incorrectly persisted <c>capReached</c>.
    /// </summary>
    public static bool MustEscalateFeedbackContinuation(UiIterationReviewContract? review)
        => review is not null
           && (review.CapReached || review.Iteration >= review.MaxIterations);

    /// <summary>
    /// Human feedback can enter the runner directly as <see cref="RunIntent.UserContinue"/>
    /// or indirectly as an auto-pickup carrying a persisted pending intent. The
    /// latter is converted to UserContinue later in admission, so the cap guard
    /// must recognise it before that conversion to prevent one extra iteration.
    /// </summary>
    public static bool IsFeedbackContinuation(RunIntent intent, bool hasPendingIntent)
        => intent == RunIntent.UserContinue
           || (intent == RunIntent.AutoPickup && hasPendingIntent);

    public static UiIterationGateDecision Evaluate(string jobFolder, int iteration, int maxIterations)
    {
        maxIterations = Math.Clamp(maxIterations, MinimumIterations, MaximumIterations);
        var directory = IterationDirectory(jobFolder, iteration);
        if (iteration > maxIterations)
        {
            return new UiIterationGateDecision
            {
                Action = UiIterationGateAction.EscalateCapReached,
                Iteration = iteration,
                MaxIterations = maxIterations,
                IterationDirectory = directory,
                Findings = [$"UI iteration cap {maxIterations} was exhausted without a human finish decision."],
            };
        }

        var artifacts = new List<string>();
        var findings = new List<string>();
        try
        {
            if (Directory.Exists(directory))
            {
                artifacts.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => ImageExtensions.Contains(Path.GetExtension(path)) && new FileInfo(path).Length > 0)
                    .Select(path => Path.GetRelativePath(TaskPaths.ResultsDir(jobFolder), path).Replace('\\', '/')));
            }
        }
        catch
        {
            artifacts.Clear();
        }
        if (artifacts.Count == 0)
            findings.Add($"Missing non-empty screenshot or Playwright capture in results/ui-iteration-{iteration:D3}/.");

        var description = Path.Combine(directory, ChangeDescriptionFileName);
        try
        {
            if (!File.Exists(description) || string.IsNullOrWhiteSpace(File.ReadAllText(description)))
                findings.Add($"Missing non-empty results/ui-iteration-{iteration:D3}/{ChangeDescriptionFileName} change description.");
        }
        catch
        {
            findings.Add($"Unreadable results/ui-iteration-{iteration:D3}/{ChangeDescriptionFileName} change description.");
        }

        return new UiIterationGateDecision
        {
            Action = findings.Count == 0
                ? UiIterationGateAction.ReadyForHumanReview
                : UiIterationGateAction.Incomplete,
            Iteration = iteration,
            MaxIterations = maxIterations,
            IterationDirectory = directory,
            ChangeDescriptionPath = findings.Any(f => f.Contains(ChangeDescriptionFileName, StringComparison.Ordinal))
                ? null
                : Path.GetRelativePath(TaskPaths.ResultsDir(jobFolder), description).Replace('\\', '/'),
            ArtifactPaths = artifacts,
            Findings = findings,
        };
    }

    public static string BuildAgentInstructions(string jobFolder, int iteration, int maxIterations)
    {
        var relative = $"results/ui-iteration-{iteration:D3}";
        var sb = new StringBuilder();
        sb.AppendLf();
        sb.AppendLf("UI ITERATION CONTRACT (mandatory for this run):");
        sb.AppendLf($"- This is visual iteration {iteration}/{maxIterations}.");
        sb.AppendLf($"- Save at least one non-empty screenshot or Playwright capture under `{relative}/`.");
        sb.AppendLf($"- Write a short, concrete description of what changed to `{relative}/{ChangeDescriptionFileName}`.");
        sb.AppendLf("- Evidence from another iteration does not count. Do not claim DONE until both files exist.");
        sb.AppendLf($"- The task result directory is `{TaskPaths.ResultsDir(jobFolder)}`.");
        return sb.ToString();
    }

    public static string BuildMissingEvidenceFollowUp(UiIterationGateDecision decision)
        => RunOutcomePolicy.DiffOnlySteeringRule + "\n\n"
           + $"UI iteration {decision.Iteration}/{decision.MaxIterations} is incomplete. "
           + string.Join(" ", decision.Findings)
           + $" Create the required evidence and `{ChangeDescriptionFileName}` in `results/ui-iteration-{decision.Iteration:D3}/`, then finish with [[TASK_DONE]].";
}
