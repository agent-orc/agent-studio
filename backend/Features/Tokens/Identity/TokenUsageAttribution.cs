namespace AgentStudio.Tokens;

/// <summary>
/// Project attribution of a workspace-scope token row (AGT-3267). Ad-hoc
/// calls (title and summary generation, prompt enhancement, review decision,
/// drift, task spawner, failure intervention) stay in the workspace ledger,
/// so the usage cockpit used to book all of them as "Unattributed" even when
/// the call knew its project or card. The rule, in order:
/// <list type="number">
///   <item>an explicit bus <c>project</c> that names a known project;</item>
///   <item>a <see cref="ProjectTagPrefix"/> tag written by the ad-hoc recorder;</item>
///   <item>the project that owns the row's job id.</item>
/// </list>
/// A row none of these resolve stays unattributed. Pure: the caller supplies
/// the project matcher and the job owner lookup.
/// </summary>
public static class TokenUsageAttribution
{
    /// <summary>Bus tag carrying the project of a workspace-scope row.</summary>
    public const string ProjectTagPrefix = "project:";

    /// <summary>Which rule attributed the row; <see cref="None"/> when no rule did.</summary>
    public const string ByProject = "project";
    public const string ByTag = "tag";
    public const string ByJob = "job";
    public const string None = "none";

    public sealed record Result(string? ProjectId, string Rule);

    /// <param name="project">Bus <c>project</c> field (id or display name).</param>
    /// <param name="tags">Bus tags.</param>
    /// <param name="jobId">Bus job id.</param>
    /// <param name="matchProject">Resolves an id or display name to a known project id, or null.</param>
    /// <param name="jobOwner">Resolves a job id to the id of the project that owns it, or null.</param>
    public static Result Resolve(
        string? project,
        IReadOnlyList<string>? tags,
        string? jobId,
        Func<string, string?> matchProject,
        Func<string, string?> jobOwner)
    {
        if (!string.IsNullOrWhiteSpace(project) && matchProject(project.Trim()) is { } explicitProject)
            return new Result(explicitProject, ByProject);

        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag)
                || !tag.StartsWith(ProjectTagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = tag[ProjectTagPrefix.Length..].Trim();
            if (name.Length > 0 && matchProject(name) is { } tagged)
                return new Result(tagged, ByTag);
        }

        if (!string.IsNullOrWhiteSpace(jobId) && jobOwner(jobId.Trim()) is { } owner)
            return new Result(owner, ByJob);

        return new Result(null, None);
    }
}
