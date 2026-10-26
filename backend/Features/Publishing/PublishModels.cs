namespace AgentStudio.Publishing;

/// <summary>
/// PUB-1 - publish-target derivation from repository facts. The kind of thing a
/// project can publish, derived from workflows + manifests (never from a stored
/// setting): a distributable <see cref="Package"/> (npm / NuGet) or a
/// <see cref="Website"/> (GitHub Pages / deploy-website workflow).
/// </summary>
public enum PublishTargetKind
{
    Package,
    Website,
}

/// <summary>
/// String constants for the package ecosystem, kept as literals so the JSON wire
/// value is stable across refactors. <see cref="Npm"/> and <see cref="NuGet"/>
/// are the two the derivation recognises today (from a release workflow's publish
/// step or a located manifest); the label the badge renders lives in
/// <see cref="PublishTarget.Label"/>.
/// </summary>
public static class PublishEcosystems
{
    public const string Npm = "npm";
    public const string NuGet = "nuget";
}

/// <summary>
/// How a target's pending-delta baseline ("since when") was established. This is
/// surfaced so the UI and tests never present a fabricated count as fact:
/// <list type="bullet">
///   <item><c>tag</c> - the last <c>v*</c> release tag (packages).</item>
///   <item><c>release-tag</c> - a website anchored to the last release tag,
///   because no dedicated deploy record exists in git.</item>
///   <item><c>pages-branch</c> - the tip date of a <c>gh-pages</c> deploy branch
///   (the only in-git website deploy record).</item>
///   <item><c>none</c> - no baseline could be derived from pure git facts
///   (e.g. a first publish, or a modern <c>actions/deploy-pages</c> flow that
///   leaves no git marker). The count is then not asserted.</item>
/// </list>
/// </summary>
public static class PublishReferenceKinds
{
    public const string Tag = "tag";
    public const string ReleaseTag = "release-tag";
    public const string PagesBranch = "pages-branch";
    public const string None = "none";
}

public static class PublishAutomationModes
{
    public const string Manual = "manual";
    public const string Suggest = "suggest";
    public const string Auto = "auto";
    public static readonly string[] All = [Manual, Suggest, Auto];

    public static string Normalize(string targetId, string? mode)
    {
        var requested = All.FirstOrDefault(x => string.Equals(x, mode, StringComparison.OrdinalIgnoreCase)) ?? Manual;
        return targetId.StartsWith("package:", StringComparison.OrdinalIgnoreCase) && requested == Auto
            ? Suggest
            : requested;
    }
}

public record PublishPendingTask(string TaskId, string TaskKey, string Title, string TaskType);

public record PublishActionPanel
{
    public string Project { get; init; } = "";
    public PublishTarget? Target { get; init; }
    public string AutomationMode { get; init; } = PublishAutomationModes.Manual;
    public List<PublishPendingTask> PendingTasks { get; init; } = [];
    public string? SuggestedVersion { get; init; }
    public string? Notice { get; init; }
    public PublishWorkflowRun? LastRun { get; init; }
}

public record PublishWorkflowRun
{
    public string Project { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string Workflow { get; init; } = "";
    public long? RunId { get; init; }
    public string Status { get; init; } = "queued";
    public string? Conclusion { get; init; }
    public string? Version { get; init; }
    public string? Url { get; init; }
    public DateTime TriggeredAt { get; init; }
    public string? Error { get; init; }
}

public record PublishPackageRequest(string TargetId, string Version);
public record DeployWebsiteRequest(string TargetId = "website");
public record SetPublishAutomationRequest(string TargetId, string Mode);

/// <summary>
/// One derived publish target for a project, as rendered by the Project Hub
/// badge. Read-only and repo-fact-derived: nothing here is an operator setting.
/// The wire shape deliberately omits the internal per-commit SHA set used to
/// answer per-task publishability (see <see cref="PublishTargetComputation"/>).
/// </summary>
public record PublishTarget
{
    /// <summary>Stable id: <c>package:npm</c>, <c>package:nuget</c>, or <c>website</c>. Used as the badge key and the task-chip token.</summary>
    public string Id { get; init; } = "";

    public PublishTargetKind Kind { get; init; }

    /// <summary>Package ecosystem (<see cref="PublishEcosystems"/>); null for a website target.</summary>
    public string? Ecosystem { get; init; }

    /// <summary>Short human label the badge renders: <c>npm</c>, <c>NuGet</c>, <c>Website</c>.</summary>
    public string Label { get; init; } = "";

    /// <summary>Package id/name from the located manifest (e.g. <c>coding-agent-chat</c>); null for websites or when no manifest was found.</summary>
    public string? PackageName { get; init; }

    /// <summary>Current published version = the last <c>v*</c> tag with the <c>v</c> stripped (e.g. <c>0.3.1</c>); null when never released.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>
    /// A package for which the derivation found a release workflow / manifest but
    /// no <c>v*</c> tag at all: it has never been published. The badge shows
    /// "first publish pending (manual, operator)" instead of a delta count.
    /// </summary>
    public bool FirstPublishPending { get; init; }

    /// <summary>
    /// Count of merged mainline (first-parent) commits since the reference point
    /// that touch this target's path scope - the "N tasks pending" number. Zero
    /// means quiet (no badge). Null means "no baseline available" (see
    /// <see cref="ReferenceKind"/> = <c>none</c>): the UI stays quiet rather than
    /// inventing a number.
    /// </summary>
    public int? PendingCount { get; init; }

    /// <summary>How the pending baseline was established. See <see cref="PublishReferenceKinds"/>.</summary>
    public string ReferenceKind { get; init; } = PublishReferenceKinds.None;

    /// <summary>Human reference the baseline resolves to: a tag name (<c>v0.3.1</c>) or a short SHA; null when <see cref="ReferenceKind"/> is <c>none</c>.</summary>
    public string? Reference { get; init; }
}

// TaskPublishSignal (the per-task chip projection folded onto TaskInfo) lives in
// AgentStudio.Shared alongside the other read-time projections (TaskMergeSignal,
// WaitsOnStatus) so TaskInfo's dependencies stay within Shared.

/// <summary>
/// A derived target plus the internal artefacts the wire shape hides: the set of
/// pending mainline commit SHAs (used to answer "is task X publishable to this
/// target?" by set-membership against the task's merge-commit anchor - no
/// per-task git spawn) and the resolved path scope. The service returns these;
/// the endpoint projects out just <see cref="Target"/>.
/// </summary>
public sealed record PublishTargetComputation(
    PublishTarget Target,
    IReadOnlyCollection<string> PendingShas);

/// <summary>
/// The project-level publish status returned by the derivation service and folded
/// into the project snapshot. <see cref="IsRepo"/> false + <see cref="Error"/>
/// set is the clean empty state for a non-repo / unknown project; the frontend
/// branches on it. An empty <see cref="Targets"/> list on a real repo means
/// "nothing publishable derived" - also a quiet state, no badges.
/// </summary>
public record ProjectPublishStatus
{
    public string Project { get; init; } = "";
    public bool IsRepo { get; init; }
    public List<PublishTarget> Targets { get; init; } = [];
    public string? Error { get; init; }
}
