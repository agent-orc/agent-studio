using System.Text;
using AgentStudio.Prompts;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// Freezes the plan of a shadow package (AGT-3400 S4, PK4 R-B). Each
/// package-tier aspect runs once, framed by <c>review-package-aspect.md</c>
/// around the aspect's own rules, with the CLI, model and thinking level the
/// members' per-card plans froze for that aspect, so the shadow comparison
/// holds the model constant. The grade is computed from the verdicts.
/// </summary>
public sealed class ReviewPackagePlanBuilder(RuntimePromptService prompts)
{
    public const string FramePrompt = "review-package-aspect.md";
    public const string CrossCardPrompt = "review-aspect-cross-card-consistency.md";
    public const string CrossCardStepId = "aspect-cross-card-consistency";

    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ReviewPackageAspects.CodeQuality] = "code quality",
        [ReviewPackageAspects.DocumentationImpact] = "documentation impact",
        [ReviewPackageAspects.TestsAndEvidence] = "tests and evidence",
        [ReviewPackageAspects.CrossCardConsistency] = "cross-card consistency",
    };

    /// <summary>
    /// The frozen plan, or null when no member plan names a model for any
    /// package-tier aspect. <paramref name="memberPlans"/> are the members'
    /// per-card plans, latest member last.
    /// </summary>
    public ReviewPackagePlanDto? Build(
        string project,
        string dossierPath,
        IReadOnlyList<ReviewPackageMemberDto> members,
        IReadOnlyList<ReviewPlanDto> memberPlans,
        string? repositoryId,
        string? repositoryUrl,
        string? integrationRef)
    {
        var commands = new List<ReviewCommandDto>();
        var membersSection = MembersSection(members);
        var keys = string.Join(", ", members.OrderBy(member => member.Order).Select(member => $"`{member.TaskKey}`"));
        foreach (var aspect in ReviewPackageAspects.Agent)
        {
            var source = ModelSource(memberPlans, aspect);
            if (source is null) continue;
            var template = aspect == ReviewPackageAspects.CrossCardConsistency
                ? CrossCardPrompt : $"review-aspect-{aspect}.md";
            var content = prompts.TryReadOverride(template) ?? prompts.TryReadDefault(template);
            if (content is null) continue;
            var stepId = aspect == ReviewPackageAspects.CrossCardConsistency ? CrossCardStepId : "package-aspect-" + aspect;
            var prompt = prompts.Render(FramePrompt, new Dictionary<string, string?>
            {
                ["aspect_title"] = Titles[aspect],
                ["aspect_id"] = aspect,
                ["aspect_rules"] = Rules(content),
                ["project"] = project,
                ["dossier_path"] = dossierPath,
                ["member_count"] = members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["members"] = membersSection,
                ["member_keys"] = keys,
            }, new PromptCallContext(project, stepId, source.Model));
            commands.Add(new ReviewCommandDto(stepId, aspect, source.CliType ?? source.FileName, [],
                TimeoutSeconds: source.TimeoutSeconds, ExecutionKind: ReviewCommandKinds.AgentAspect,
                Prompt: prompt, CliType: source.CliType, Model: source.Model, ThinkingLevel: source.ThinkingLevel));
        }
        return commands.Count == 0 || string.IsNullOrWhiteSpace(integrationRef)
            ? null
            : new ReviewPackagePlanDto(repositoryId, repositoryUrl, integrationRef, commands,
                PrivateRepository: memberPlans.Any(plan => plan.PrivateRepository));
    }

    /// <summary>
    /// The aspect's rules without its per-card context and output contract:
    /// the text before the first per-card section, plus its "do not" list.
    /// </summary>
    internal static string Rules(string content)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var cut = new[] { "\n## Project / Job", "\n## What you must emit", "\n## Things you should *not* do" }
            .Select(marker => text.IndexOf(marker, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(text.Length)
            .Min();
        var rules = new StringBuilder(text[..cut].TrimEnd());
        var avoid = text.IndexOf("\n## Things you should *not* do", StringComparison.Ordinal);
        if (avoid >= 0)
            rules.AppendLf().AppendLf().Append(text[(avoid + 1)..].TrimEnd());
        return rules.ToString();
    }

    /// <summary>One block per member in stack order: key, title, result, dependencies, criteria.</summary>
    internal static string MembersSection(IReadOnlyList<ReviewPackageMemberDto> members)
    {
        var keys = members.Select(member => member.TaskKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        foreach (var member in members.OrderBy(member => member.Order))
        {
            var dependsOn = (member.DependsOn ?? []).Where(keys.Contains).ToArray();
            text.AppendLf($"{member.Order + 1}. **{member.TaskKey}** {member.Title}".TrimEnd());
            text.AppendLf($"   - Result SHA: `{member.ResultSha}`");
            text.AppendLf($"   - Depends on in this package: {(dependsOn.Length == 0 ? "none" : string.Join(", ", dependsOn))}");
            var criteria = (member.AcceptanceCriteria ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (criteria.Length == 0)
            {
                text.AppendLf("   - Acceptance criteria: none recorded");
                continue;
            }
            text.AppendLf("   - Acceptance criteria:");
            foreach (var criterion in criteria) text.AppendLf($"     - {criterion}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The per-card command whose model the package aspect reuses: the same
    /// aspect from the latest member that ran it; the cross-card aspect uses
    /// the code-quality model.
    /// </summary>
    private static ReviewCommandDto? ModelSource(IReadOnlyList<ReviewPlanDto> plans, string aspect)
    {
        var wanted = aspect == ReviewPackageAspects.CrossCardConsistency ? ReviewPackageAspects.CodeQuality : aspect;
        return plans.Reverse()
            .SelectMany(plan => plan.Commands)
            .FirstOrDefault(command => ReviewCommandKinds.IsAgent(command.ExecutionKind)
                && string.Equals(command.Aspect, wanted, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(command.Model));
    }
}
