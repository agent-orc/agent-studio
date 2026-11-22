using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3968: the review after a round whose new commits are only merges of
/// the integration branch. Such a round changes the card's own work only
/// where it resolved conflicts. The review covers that resolution: the files
/// whose merge result differs from Git's automatic merge, and what the round
/// changed in them. The build and the affected tests run as usual; the
/// aspects that passed on the unchanged work are carried over through the
/// scoped review policy (<c>scopedReviewAfterFinding</c>).
/// </summary>
public sealed partial class RemoteReviewPlanBuilder
{
    /// <summary>The aspect that reviews a merge resolution when the plan has it.</summary>
    internal const string MergeResolutionAspect = "code-quality";

    internal sealed record MergeOnlyRound(
        IReadOnlyList<string> Merges,
        IReadOnlyList<string> ResolutionFiles);

    private static RemoteReviewSettlementEntry? LatestPassedSettlement(TaskInfo task, string? briefSha256)
    {
        var logs = TaskPaths.LogsDir(task.FolderPath);
        if (string.IsNullOrWhiteSpace(briefSha256) || !Directory.Exists(logs)) return null;
        return Directory.EnumerateFiles(logs, "remote-review-settlement-*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path)["remote-review-settlement-".Length..])
            .Select(id => RemoteReviewSettlementJournal.Read(task.FolderPath, id).Entry)
            .Where(entry => entry is not null
                && string.Equals(entry.Report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Report.BriefSha256, briefSha256, StringComparison.OrdinalIgnoreCase)
                && ReviewSubjectStore.IsValidResultSha(entry.Report.Workspace.ExpectedResultSha)
                && ReviewSubjectStore.IsValidResultSha(entry.Report.Workspace.MergeBaseSha))
            .OrderByDescending(entry => entry!.ReceivedAtUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The merges of <paramref name="reviewedSha"/>..<paramref name="resultSha"/>
    /// and the files they resolved, or null when the range holds a commit that
    /// is not a merge of the integration branch.
    /// </summary>
    internal static MergeOnlyRound? ReadMergeOnlyRound(
        string repositoryPath, string reviewedSha, string resultSha, string integrationRef)
    {
        if (Git(repositoryPath, "merge-base", "--is-ancestor", reviewedSha, resultSha) is null) return null;
        var own = Git(repositoryPath, "rev-list", "--no-merges", $"{reviewedSha}..{resultSha}",
            "--not", integrationRef);
        if (own is null || own.Length > 0) return null;
        var merges = Git(repositoryPath, "rev-list", "--merges", $"{reviewedSha}..{resultSha}",
            "--not", integrationRef);
        if (string.IsNullOrWhiteSpace(merges)) return null;
        var mergeShas = merges.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var merge in mergeShas)
        {
            var parents = Git(repositoryPath, "rev-list", "--parents", "-n", "1", merge)
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents is not { Length: >= 3 }) return null;
            if (parents.Skip(2).Any(parent =>
                    Git(repositoryPath, "merge-base", "--is-ancestor", parent, integrationRef) is null))
                return null;
            // --remerge-diff shows how the recorded merge differs from Git's
            // own merge of its parents: the conflict resolution and anything
            // else the round changed inside the merge.
            var resolved = Git(repositoryPath, "show", "--remerge-diff", "--format=", "--name-only", merge);
            if (resolved is null) return null;
            foreach (var path in resolved.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                files.Add(path);
        }
        return new MergeOnlyRound(mergeShas, files.ToArray());
    }

    private static Contract.ReviewPlanDto ApplyMergeOnlyRoundReview(
        Contract.ReviewPlanDto plan,
        TaskInfo task,
        string? repositoryPath,
        ProjectSettings? settings,
        string? integrationRef,
        string? resultSha,
        bool noCarryOver)
    {
        if (noCarryOver
            || !(settings?.ScopedReviewAfterFinding ?? true)
            || plan.CarriedVerdicts is { Count: > 0 }
            || string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(integrationRef)
            || !ReviewSubjectStore.IsValidResultSha(resultSha))
            return plan;
        var previous = LatestPassedSettlement(task, plan.BriefSha256);
        var reviewedSha = previous?.Report.Workspace.ExpectedResultSha;
        if (previous is null || string.Equals(reviewedSha, resultSha, StringComparison.OrdinalIgnoreCase))
            return plan;
        var round = ReadMergeOnlyRound(repositoryPath, reviewedSha!, resultSha!, integrationRef);
        if (round is null) return plan;

        var verdicts = previous.Report.Verdicts
            .Where(verdict => string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
            .GroupBy(verdict => verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var agentAspects = plan.Commands
            .Where(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .Select(command => command.Aspect)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolutionAspect = round.ResolutionFiles.Count == 0
            ? null
            : agentAspects.FirstOrDefault(aspect =>
                  string.Equals(aspect, MergeResolutionAspect, StringComparison.OrdinalIgnoreCase))
              ?? agentAspects.FirstOrDefault(aspect =>
                  !string.Equals(aspect, "visual-review", StringComparison.OrdinalIgnoreCase));
        var facts = new Contract.ScopedReviewFacts(
            Enabled: true,
            ReviewedTreeIdentityMatches: true,
            DeltaFileCount: round.ResolutionFiles.Count,
            MaximumDeltaFiles: Math.Max(0, settings?.ScopedReviewMaximumDeltaFiles ?? 20),
            ChangedFiles: round.ResolutionFiles,
            RemovedFiles: [],
            PreviousAspects: agentAspects
                .Where(aspect => verdicts.ContainsKey(aspect))
                .Select(aspect => new Contract.ScopedReviewAspect(
                    aspect,
                    verdicts[aspect].Status,
                    EvidencePaths(verdicts[aspect].EvidenceChecked),
                    RaisedFinding: string.Equals(aspect, resolutionAspect, StringComparison.OrdinalIgnoreCase)))
                .ToArray());
        var decisions = Contract.ScopedReviewPolicy.Plan(facts)
            .ToDictionary(item => item.Aspect, StringComparer.OrdinalIgnoreCase);

        var carried = new List<Contract.ReviewVerdictDto>();
        var commands = new List<Contract.ReviewCommandDto>();
        foreach (var command in plan.Commands)
        {
            var isAgent = Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind);
            if (isAgent
                && decisions.TryGetValue(command.Aspect, out var decision) && !decision.Run
                && !string.Equals(command.Aspect, "visual-review", StringComparison.OrdinalIgnoreCase)
                && verdicts.TryGetValue(command.Aspect, out var prior))
            {
                carried.Add(prior with
                {
                    Classification = "carried-over",
                    CarriedOverFrom = previous.AttemptId,
                });
                continue;
            }
            commands.Add(isAgent && string.Equals(command.Aspect, resolutionAspect, StringComparison.OrdinalIgnoreCase)
                ? command with { Prompt = (command.Prompt ?? string.Empty) + ResolutionBrief(previous.AttemptId, reviewedSha!, round) }
                : command);
        }
        if (carried.Count == 0) return plan;
        return plan with
        {
            Commands = commands,
            CarriedOverFrom = previous.AttemptId,
            CarriedVerdicts = carried,
        };
    }

    private static string ResolutionBrief(string reviewAttemptId, string reviewedSha, MergeOnlyRound round)
        => "\n\n## Merge resolution to review\n\n"
           + $"The work passed review {reviewAttemptId} at {reviewedSha}. Since then this delivery only merged the "
           + "integration branch. Review the conflict resolution, not the unchanged work: the files below, and what "
           + "the merges changed in them compared with Git's own merge. Block a resolution that drops either side's "
           + "change, duplicates code, or breaks the card's behaviour.\n\n"
           + "- Resolved files: " + string.Join(", ", round.ResolutionFiles) + "\n"
           + "- Show the resolution: " + string.Join("; ", round.Merges.Select(merge =>
               $"`git show --remerge-diff {merge}`")) + "\n";
}
