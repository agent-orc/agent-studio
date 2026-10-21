namespace AgentStudio.Shared;

/// <summary>
/// Task execution mode - orthogonal to <see cref="TaskKinds"/>. It
/// describes how a leaf task's run behaves:
/// <list type="bullet">
/// <item><c>coding</c> (default) - mutates source, runs the full git pipeline.</item>
/// <item><c>planning</c> - read-only; analyses the codebase and proposes the next
///   concrete piece of work (promotable to a coding task).</item>
/// <item><c>research</c> - read-only; broader fact-finding, web access on by
///   default; the deliverable is a report.</item>
/// <item><c>concept</c> - product-source-read-only; authors one reviewable
///   dossier under <c>docs/&lt;slug&gt;/</c> and waits for a
///   human sight review before implementation cards are promoted.</item>
/// </list>
/// Report-only modes skip worktree and git steps. Concept uses an isolated
/// worktree but publishes only its bounded document and never merges the task
/// branch. Persisted as the <c>"mode"</c> field in <c>job.json</c>; keep values
/// stable. See
/// docs/concepts/planning-research-task-kinds-2026-05.md.
/// </summary>
public static class TaskModes
{
    public const string Coding = "coding";
    public const string Planning = "planning";
    public const string Research = "research";
    public const string Concept = "concept";

    public static readonly string[] All = [Coding, Planning, Research, Concept];

    /// <summary>Coerce a free-form value to a known mode; unknown / empty -> Coding.</summary>
    public static string Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            Planning => Planning,
            Research => Research,
            Concept => Concept,
            _ => Coding,
        };
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && System.Array.IndexOf(All, value!.Trim().ToLowerInvariant()) >= 0;

    /// <summary>
    /// Product-source-read-only modes never modify implementation files.
    /// Planning/research produce reports; concept may write only its bounded
    /// <c>docs/&lt;slug&gt;/</c> dossier.
    /// </summary>
    public static bool IsReadOnly(string? value) => Normalize(value) is Planning or Research or Concept;

    /// <summary>Strict report-only modes that must leave no repository diff.</summary>
    public static bool IsReportOnly(string? value) => Normalize(value) is Planning or Research;

    public static bool IsConcept(string? value) => Normalize(value) == Concept;
}
