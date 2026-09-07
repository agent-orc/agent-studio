using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>
/// One commit considered for attribution to a task. Carries everything the
/// rule engine needs to decide attribute / exclude without re-querying git,
/// so the engine stays pure and unit-testable. The production wiring builds
/// these from the deterministic SHA-range lookup
/// (<see cref="GitService.GetCommitsInShaRange"/>); tests build them by hand.
/// </summary>
public sealed record AttributionCandidate
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Author { get; init; } = "";
    /// <summary>First line of the commit message.</summary>
    public string Subject { get; init; } = "";
    /// <summary>Full message including trailers (e.g. <c>Co-Authored-By:</c>). May equal <see cref="Subject"/> when the body was not captured.</summary>
    public string Message { get; init; } = "";
    public DateTime AuthorDateUtc { get; init; }
    public int FilesChanged { get; init; }
    /// <summary>Changed paths, repo-root relative. Empty when not captured.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>
    /// True when the commit has more than one parent. Set from git
    /// (<c>%P</c>) when available; more robust than matching the subject
    /// line, which an ordinary commit could coincidentally start with.
    /// </summary>
    public bool IsMerge { get; init; }
}

/// <summary>
/// Input bundle for one attribution pass. See the field docs for what each
/// signal feeds (ADR "Commit-Attribution-Regel"). Everything optional
/// degrades gracefully: a missing branch / working-dir / window simply skips
/// that check rather than excluding everything.
/// </summary>
public sealed record AttributionInput
{
    /// <summary>The task whose commits we are attributing (its folder/job id).</summary>
    public string TaskId { get; init; } = "";
    /// <summary>Registered repository id or remote URL for every candidate.</summary>
    public string? Repository { get; init; }
    /// <summary>Branch the task ran on (today single-branch <c>main</c>). Null skips the branch check.</summary>
    public string? TaskBranch { get; init; }
    /// <summary>Current HEAD branch. Null skips the branch check.</summary>
    public string? HeadBranch { get; init; }
    /// <summary>Substring that marks the agent in author/co-author lines. Default <c>Claude</c>.</summary>
    public string AgentMarker { get; init; } = "Claude";
    /// <summary>Repo-root-relative path prefix for the dev checkout (e.g. <c>agent-taskboard-dev/</c>). Null skips the working-dir check.</summary>
    public string? WorkingDirPrefix { get; init; }
    /// <summary>Inclusive lower bound of the session window. Commits authored before this are excluded as out-of-window. Null skips the window check.</summary>
    public DateTime? WindowStartUtc { get; init; }
    /// <summary>
    /// SHAs the platform itself stamped onto this task (the auto-commit on
    /// <c>3-progress -&gt; 4-auto-review</c>). These are attributed with full
    /// confidence and bypass the exclusion rules: the platform committed the
    /// task's accepted work deliberately, so a pattern match in its message
    /// must not withhold it.
    /// </summary>
    public IReadOnlyCollection<string> PlatformStampShas { get; init; } = [];
    public IReadOnlyList<AttributionCandidate> Candidates { get; init; } = [];
}

public sealed record AttributionResult
{
    public List<TaskCommitInfo> Attributed { get; init; } = [];
    public List<TaskExcludedCommitInfo> Excluded { get; init; } = [];
}

/// <summary>
/// Deterministic, side-effect-free commit-to-task attribution (ADR
/// "Commit-Attribution-Regel"). Given the commits captured in a task's
/// session SHA-window, decides which belong to the task and which are
/// excluded (crash-recovery for another task, update-stable / submodule
/// bumps, merge commits, out-of-window). No git, no filesystem, no LLM, no
/// clock - the same input always yields the same output, which is what makes
/// the post-execution step idempotent.
///
/// <para>
/// The class is intentionally pure so unit tests can pin every rule without
/// a real repository. The thin <c>TaskTransitionService</c> wiring builds the
/// candidates from <see cref="GitService"/> and persists the result through
/// <see cref="TaskMutationService"/> (never a direct file write).
/// </para>
/// </summary>
public static class CommitAttributionService
{
    // chore(crash-recovery): rescue orphan changes for <target>
    // <target> is either a job/folder id or "project <name>" (see CrashRecoveryService).
    private static readonly Regex CrashRecoveryRe = new(
        @"crash-recovery\)\s*:\s*rescue orphan changes for\s+(?<target>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // chore(submodules): bump dev ...  /  any "update-stable" stream commit.
    private static readonly Regex UpdateStableRe = new(
        @"(submodules\)\s*:\s*bump\b|update[- ]stable)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Merge commits produced by the update-stable git-pull workflow.
    private static readonly Regex MergeRe = new(
        @"^Merge\s+(branch|remote-tracking|pull request|commit)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static AttributionResult Attribute(AttributionInput input)
    {
        var attributed = new List<TaskCommitInfo>();
        var excluded = new List<TaskExcludedCommitInfo>();
        var marker = string.IsNullOrWhiteSpace(input.AgentMarker) ? "Claude" : input.AgentMarker.Trim();
        var stamps = new HashSet<string>(input.PlatformStampShas, StringComparer.OrdinalIgnoreCase);

        foreach (var c in input.Candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Sha)) continue;

            var subject = c.Subject ?? "";
            var message = string.IsNullOrEmpty(c.Message) ? subject : c.Message;

            // 0) Platform-stamped task commit: always attributed, full confidence.
            if (stamps.Contains(c.Sha))
            {
                attributed.Add(new TaskCommitInfo
                {
                    Sha = c.Sha,
                    ShortSha = string.IsNullOrEmpty(c.ShortSha) ? Short(c.Sha) : c.ShortSha,
                    Message = message,
                    Repository = input.Repository,
                    Branch = input.TaskBranch,
                    FilesChanged = c.FilesChanged,
                    Files = c.Files.ToList(),
                    At = c.AuthorDateUtc,
                    Attribution = CommitAttributionKinds.Automatic,
                    Confidence = 1.0,
                });
                continue;
            }

            // 1) Out of window: authored before the task's first start.
            if (input.WindowStartUtc is { } start && c.AuthorDateUtc < start)
            {
                excluded.Add(Exclude(c, CommitExclusionReasons.OutsideTaskWindow));
                continue;
            }

            // 2) Merge commit from the release/update-stable stream.
            if (c.IsMerge || MergeRe.IsMatch(subject))
            {
                excluded.Add(Exclude(c, CommitExclusionReasons.MergeCommit));
                continue;
            }

            // 3) Submodule / update-stable bump: belongs to the release stream, no task.
            if (UpdateStableRe.IsMatch(subject))
            {
                excluded.Add(Exclude(c, CommitExclusionReasons.UpdateStableBump));
                continue;
            }

            // 4) Crash-recovery: attribute only when it rescues THIS task.
            var cr = CrashRecoveryRe.Match(message);
            if (cr.Success)
            {
                var target = cr.Groups["target"].Value.Trim();
                if (!CrashRecoveryTargetsTask(target, input.TaskId))
                {
                    excluded.Add(Exclude(c, CommitExclusionReasons.CrashRecoveryOfOtherTask));
                    continue;
                }
                // Falls through: a crash-recovery of our own task is real work.
            }

            // 5) Working-dir check: drop commits that touch nothing in the dev checkout.
            if (!string.IsNullOrWhiteSpace(input.WorkingDirPrefix)
                && c.Files.Count > 0
                && !c.Files.Any(f => PathIsUnder(f, input.WorkingDirPrefix!)))
            {
                excluded.Add(Exclude(c, CommitExclusionReasons.Other));
                continue;
            }

            attributed.Add(new TaskCommitInfo
            {
                Sha = c.Sha,
                ShortSha = string.IsNullOrEmpty(c.ShortSha) ? Short(c.Sha) : c.ShortSha,
                Message = message,
                Repository = input.Repository,
                Branch = input.TaskBranch,
                FilesChanged = c.FilesChanged,
                Files = c.Files.ToList(),
                At = c.AuthorDateUtc,
                Attribution = CommitAttributionKinds.Automatic,
                Confidence = Confidence(c, input, marker),
            });
        }

        return new AttributionResult { Attributed = attributed, Excluded = excluded };
    }

    /// <summary>
    /// Confidence in an automatic attribution. The discriminating signal in
    /// today's single-branch / single-checkout world is authorship: a commit
    /// carrying the agent marker (author line or a <c>Co-Authored-By</c>
    /// trailer) is almost certainly the task's work; an operator commit that
    /// merely landed inside the window is plausible but weaker. Branch and
    /// working-dir agreement nudge the score up.
    /// </summary>
    private static double Confidence(AttributionCandidate c, AttributionInput input, string marker)
    {
        var hasAgent =
            c.Author.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || c.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || c.Message.Contains("Co-Authored-By", StringComparison.OrdinalIgnoreCase);

        var score = hasAgent ? 0.9 : 0.6;

        // Branch agreement (only counts when both sides are known).
        if (!string.IsNullOrWhiteSpace(input.TaskBranch) && !string.IsNullOrWhiteSpace(input.HeadBranch))
        {
            if (string.Equals(input.TaskBranch, input.HeadBranch, StringComparison.OrdinalIgnoreCase))
                score += 0.05;
            else
                score -= 0.2;
        }

        // Working-dir agreement: the commit touches the dev checkout.
        if (!string.IsNullOrWhiteSpace(input.WorkingDirPrefix) && c.Files.Count > 0
            && c.Files.Any(f => PathIsUnder(f, input.WorkingDirPrefix!)))
        {
            score += 0.05;
        }

        return Math.Round(Math.Clamp(score, 0.0, 1.0), 2);
    }

    private static TaskExcludedCommitInfo Exclude(AttributionCandidate c, string reason) => new()
    {
        Sha = c.Sha,
        ShortSha = string.IsNullOrEmpty(c.ShortSha) ? Short(c.Sha) : c.ShortSha,
        Reason = reason,
        Subject = c.Subject,
        At = c.AuthorDateUtc,
    };

    /// <summary>
    /// True when a crash-recovery message's target refers to the task we are
    /// attributing. CrashRecoveryService emits either the job/folder id or
    /// "project &lt;name&gt;"; the latter never names a single task, so only an
    /// id match counts. Compared case-insensitively and tolerant of the
    /// task id being a suffix of the folder slug.
    /// </summary>
    private static bool CrashRecoveryTargetsTask(string target, string taskId)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(taskId)) return false;
        if (target.StartsWith("project ", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(target, taskId, StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(taskId, StringComparison.OrdinalIgnoreCase)
            || taskId.EndsWith(target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathIsUnder(string path, string prefix)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        var pre = prefix.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        return pre.Length == 0
            || p.Equals(pre, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(pre + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
