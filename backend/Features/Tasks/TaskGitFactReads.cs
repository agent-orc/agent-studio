using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// The task Git reads of Git facts S3 (AGT-3323): provenance, commit lists,
/// commit files, the default diff and the run views, answered by joining the
/// current task with the facts the Git fact producer published. A read starts
/// no Git process. A fact that is not published yet is queued for the
/// producer with priority and the read answers <c>warming</c>.
/// </summary>
internal static class TaskGitFactReads
{
    /// <summary>
    /// The published facts of a task's repository, an empty fact set for a
    /// project without a Git repository (every join then answers from the task
    /// alone), or null while the repository's first generation is warming.
    /// </summary>
    internal static GitPublishedFacts? Facts(GitCardFactReader reader, TaskInfo info, ProjectSettingsService settings)
    {
        var lookup = reader.Read(info.WatchPath);
        if (lookup.Known) return lookup.Facts;
        return GitPublishedFacts.Unavailable(info.WatchPath, settings.Get(info.ProjectName).IntegrationBranch);
    }

    /// <summary>The job-level commit aggregate from the run timeline and published facts.</summary>
    internal static TaskCommitsAggregate Aggregate(TaskInfo info, GitPublishedFacts facts, ICollection<GitFactWork> misses)
        => GitCardFactJoins.Commits(info, Runs(info), facts, misses);

    /// <summary>The runs of the task's timeline, as <c>JobCommitsAggregation.Build</c> reads them.</summary>
    internal static IReadOnlyList<RunRecord> Runs(TaskInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.FolderPath)) return [];
        var events = TaskSessionLog.ReadSessionEventsInFolder(info.FolderPath);
        var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
        return RunTimelineBuilder.Build(events, lines, DateTime.UtcNow).Runs;
    }

    /// <summary>
    /// Whether <paramref name="sha"/> belongs to the job: its recorded commit,
    /// or a commit of its aggregate. Null when the aggregate is still warming.
    /// </summary>
    internal static bool? IsKnownCommit(TaskInfo info, GitPublishedFacts facts, string sha, ICollection<GitFactWork> misses)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        if (info.Commit != null && string.Equals(info.Commit.Sha, sha, StringComparison.OrdinalIgnoreCase)) return true;
        if (info.Commits.Any(commit => string.Equals(commit.Sha, sha, StringComparison.OrdinalIgnoreCase))) return true;
        var aggregate = Aggregate(info, facts, misses);
        if (aggregate.Commits.Any(commit => string.Equals(commit.Sha, sha, StringComparison.OrdinalIgnoreCase))) return true;
        return misses.Count > 0 ? null : false;
    }

    /// <summary>
    /// The unified diff of one commit object, all files or one path. Null and a
    /// queued object while it is not written yet.
    /// </summary>
    internal static string? CommitDiff(GitCardFactReader reader, GitPublishedFacts facts, GitCommitFact commit,
        string? path, ICollection<GitFactWork> misses, out bool tooLarge)
    {
        tooLarge = false;
        var key = GitDiffObjectKey.Commit(facts.RepoId, commit);
        var manifest = reader.DiffObject(facts, key.ObjectId);
        if (manifest is null)
        {
            misses.Add(new GitCommitDiffWork(commit.Sha));
            return null;
        }
        tooLarge = manifest.State == GitDiffObjectManifest.TooLarge;
        return Join(reader.DiffPatches(facts, key.ObjectId, NormalizePath(path)));
    }

    /// <summary>
    /// The aggregate diff of an ordered commit set from its aggregate object:
    /// each member commit's patch, concatenated commit by commit like
    /// <see cref="GitService.GetAggregateCommitDiffResult"/> did.
    /// </summary>
    internal static string? AggregateDiff(GitCardFactReader reader, GitPublishedFacts facts,
        IReadOnlyList<GitCommitFact> members, string? path, ICollection<GitFactWork> misses, out bool tooLarge)
    {
        tooLarge = false;
        var key = GitDiffObjectKey.Aggregate(facts.RepoId, members.Select(member => member.Sha).ToList());
        var manifest = reader.DiffObject(facts, key.ObjectId);
        if (manifest is null)
        {
            misses.Add(new GitAggregateDiffWork(members.Select(member => member.Sha).ToList()));
            return null;
        }
        var parts = new StringBuilder();
        foreach (var member in manifest.Members)
        {
            if (reader.DiffObject(facts, member) is { State: GitDiffObjectManifest.TooLarge }) tooLarge = true;
            var diff = Join(reader.DiffPatches(facts, member, NormalizePath(path)));
            if (string.IsNullOrWhiteSpace(diff)) continue;
            if (parts.Length > 0) parts.AppendLine();
            parts.Append(diff.TrimEnd());
            parts.AppendLine();
        }
        return parts.ToString();
    }

    /// <summary>The tree diff object of <c>base..head</c>, or null and a queued object.</summary>
    internal static GitDiffObjectManifest? RangeObject(GitCardFactReader reader, GitPublishedFacts facts,
        string baseSha, string headSha, ICollection<GitFactWork> misses)
    {
        var manifest = reader.DiffObject(facts, GitDiffObjectKey.Range(facts.RepoId, baseSha, headSha).ObjectId);
        if (manifest is null) misses.Add(new GitRangeDiffWork(baseSha, headSha));
        return manifest;
    }

    internal static string RangeDiff(GitCardFactReader reader, GitPublishedFacts facts, GitDiffObjectManifest manifest,
        string? path)
        => Join(reader.DiffPatches(facts, manifest.ObjectId, NormalizePath(path)));

    /// <summary>Queues every miss for the producer; true when something was missing.</summary>
    internal static bool Queue(GitCardFactReader reader, TaskInfo info, ICollection<GitFactWork> misses)
    {
        if (misses.Count == 0) return false;
        reader.Request(info.WatchPath, misses.DistinctBy(item => item.Key).ToList());
        return true;
    }

    internal static object DiffJson(string diff, string emptyReason)
    {
        var hasDiff = !string.IsNullOrWhiteSpace(diff);
        return new { diff, hasDiff, emptyReason = hasDiff ? null : emptyReason };
    }

    internal const string TooLargeReason = "The diff of this commit exceeds the stored diff limit.";

    private static string Join(IReadOnlyList<GitPatchFile> patches)
        => patches.Count == 1 ? patches[0].Patch : string.Concat(patches.Select(file => file.Patch));

    private static string? NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').TrimStart('/');
}
