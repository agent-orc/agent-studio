using System.Text.RegularExpressions;
using AgentStudio.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

public sealed record RemoteCommitAttributionResult(
    bool Accepted,
    IReadOnlyList<TaskCommitInfo> Commits,
    string? Warning);

/// <summary>
/// Converts an exact remote runner branch range into task commit attribution.
/// The whole range is rejected when either the branch belongs to another task
/// or a commit subject names another card's work (AGT-2242 carried foreign
/// commits on 28.07.2026). The runner's own teardown salvage commit for this
/// task is exempt from the subject scan: its subject carries the card title,
/// which may name related cards (AGT-3717).
/// <para>AGT-3854: a subject is foreign only when it names no own key and at
/// least one key that the card does not reference itself (its relations,
/// title and brief). A finding or draft id such as <c>AGT-3794-F13</c> is not
/// a task key.</para>
/// </summary>
public static partial class RemoteCommitAttributionGuard
{
    internal static bool IsOwnedFallbackCommit(GitCommitInfo commit, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(commit.Body)) return false;
        var lines = commit.Body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).ToArray();
        return lines.Contains($"Agent-Studio-Task: {taskKey.Trim()}", StringComparer.OrdinalIgnoreCase);
    }
    // A key directly followed by "-" and letters or digits is a finding or
    // draft id of that card (AGT-3794-F13, AGT-3796-D9), not a task key.
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,15}-\d+\b(?!-[A-Z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TaskKeyPattern();

    // In the card's own text a finding id still names its card: a brief that
    // fixes AGT-3794-F13 references AGT-3794.
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,15}-\d+\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReferencedKeyPattern();

    // Immutable ResultEnvelope refs are task-neutral by design:
    // <project>/results/run_<id>/fence-<n>/<result-sha>. Their identity is not the ref
    // name but the fenced result SHA the caller already verified against the
    // branch tip (InspectRemoteDeliveryCommitRange), so the task-key suffix
    // rule must not apply to them - it rejected EVERY envelope delivery and
    // left canonical remote cards without attributed commits ("No branch"
    // on reviewed cards, AGT-2434/AGT-2445).
    [GeneratedRegex(@"(^|/)results/run_[0-9a-f]{32}/(?:fence-[1-9][0-9]*/)?[0-9a-f]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ImmutableResultRefPattern();

    // A divergent salvage publishes the run's own result to
    // <card-branch>-collision-<local-sha>-<remote-sha> because the canonical
    // branch kept a foreign tip. That branch still belongs to this card, so its
    // range must be attributable - otherwise the reviewed delivery arrives with
    // an empty commits[], which is half of what AGT-2220 showed on 28.07.
    [GeneratedRegex(@"-collision-[0-9a-f]{7,64}-[0-9a-f]{7,64}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DivergentCollisionSuffixPattern();

    public static RemoteCommitAttributionResult Attribute(
        string taskKey,
        string deliveryBranch,
        IReadOnlyList<GitCommitInfo> commits,
        string? repository = null,
        IReadOnlyCollection<string>? referencedKeys = null,
        IReadOnlyCollection<TaskCommitInfo>? previousCommits = null,
        Func<string, bool>? mergeParentOnIntegration = null)
    {
        var expected = taskKey.Trim();
        var normalizedBranch = TaskIntegrationBranch.Name(deliveryBranch);
        var lastSegment = normalizedBranch
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        var branchTaskKey = lastSegment is null
            ? null
            : DivergentCollisionSuffixPattern().Replace(lastSegment, string.Empty);
        if (!string.Equals(branchTaskKey, expected, StringComparison.OrdinalIgnoreCase)
            && !ImmutableResultRefPattern().IsMatch(normalizedBranch))
        {
            return Rejected(
                $"Remote commit attribution rejected: branch '{deliveryBranch}' does not belong to task '{expected}'.");
        }

        // A continuation may add only integration merges. Its earlier task
        // commits remain in the fenced range, but must still be task-owned.
        // Check the actual second parents, never a merge's editable subject.
        var previousShas = previousCommits?.Select(commit => commit.Sha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var newCommits = commits.Where(commit => !previousShas.Contains(commit.Sha)).ToList();
        var mergeOnlyContinuation = previousShas.Count > 0 && newCommits.Count > 0
            && newCommits.All(commit => commit.IsMerge);
        if (mergeOnlyContinuation)
        {
            var missing = commits.Where(commit => !commit.IsMerge
                && !IsOwnedFallbackCommit(commit, expected)
                && !TaskKeyPattern().Matches(commit.Subject).Cast<Match>().Any(match =>
                    string.Equals(match.Value, expected, StringComparison.OrdinalIgnoreCase)))
                .Select(commit => $"{commit.ShortSha} ({commit.Subject})").ToList();
            missing.AddRange(newCommits.Where(commit => commit.ParentShas.Count < 2
                    || mergeParentOnIntegration is null
                    || commit.ParentShas.Skip(1).Any(parent => !mergeParentOnIntegration(parent)))
                .Select(commit => $"{commit.ShortSha} ({commit.Subject}; merge parent is not on the integration branch)"));
            if (missing.Count > 0)
                return Rejected($"Remote commit attribution rejected for '{expected}': commits lacking task attribution: {string.Join(", ", missing)}. The next round must publish task-owned commits without merging develop itself.");
        }

        foreach (var commit in commits)
        {
            if (RunnerSalvageCommit.IsTeardownFor(commit.Body, expected)) continue;
            if (mergeOnlyContinuation && commit.IsMerge) continue;
            if (ForeignKeyInSubject(commit.Subject, expected, referencedKeys) is { } foreign)
            {
                return Rejected(
                    $"Remote commit attribution rejected for '{expected}': commit {commit.ShortSha} names foreign task '{foreign}'.");
            }
        }

        var attributed = commits.Select(commit => new TaskCommitInfo
        {
            Sha = commit.Sha,
            ShortSha = commit.ShortSha,
            Message = commit.Body ?? commit.Subject,
            Repository = repository,
            Branch = TaskIntegrationBranch.Name(deliveryBranch),
            FilesChanged = commit.FilesChanged,
            At = commit.AuthorDateUtc,
            Attribution = CommitAttributionKinds.Automatic,
            Confidence = 1.0,
        }).ToList();
        return new RemoteCommitAttributionResult(true, attributed, null);
    }

    /// <summary>
    /// The first key in <paramref name="subject"/> that makes the commit
    /// foreign work, or null. A subject naming <paramref name="taskKey"/> is
    /// the card's own; otherwise a key the card references is allowed.
    /// </summary>
    internal static string? ForeignKeyInSubject(
        string subject,
        string taskKey,
        IReadOnlyCollection<string>? referencedKeys = null)
    {
        var keys = TaskKeyPattern().Matches(subject).Select(match => match.Value).ToList();
        if (keys.Any(key => string.Equals(key, taskKey.Trim(), StringComparison.OrdinalIgnoreCase)))
            return null;
        return keys.FirstOrDefault(key => referencedKeys?.Contains(key, StringComparer.OrdinalIgnoreCase) != true);
    }

    /// <summary>The first task key in <paramref name="subject"/> other than <paramref name="taskKey"/>.</summary>
    internal static string? OtherKeyInSubject(string subject, string taskKey)
        => TaskKeyPattern().Matches(subject)
            .Select(match => match.Value)
            .FirstOrDefault(key => !string.Equals(key, taskKey.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Task keys the card names itself: its relations, its title, and its
    /// brief (<c>prompt.md</c> and its extensions). A commit subject may name
    /// these without being foreign work.
    /// </summary>
    public static IReadOnlyCollection<string> CardReferencedKeys(TaskInfo? task, string? folderPath = null)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (task is null) return keys;
        void Collect(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (Match match in ReferencedKeyPattern().Matches(text)) keys.Add(match.Value);
        }
        foreach (var (_, target) in task.References.Enumerate()) Collect(target);
        Collect(task.Title);
        var folder = folderPath ?? task.FolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, "prompt*.md"))
                    Collect(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable brief only narrows the allowed keys.
                SilentCatch.Note(ex, "RemoteCommitAttributionGuard: card brief unreadable");
            }
        }
        keys.Remove((task.Key ?? task.Id).Trim());
        return keys;
    }

    private static RemoteCommitAttributionResult Rejected(string warning)
        => new(false, [], warning);
}
