using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// The Git reads of the fact producer (Git facts S3). Every read is batched
/// over many SHAs or covers one ref move, runs through
/// <see cref="GitProcessBoundary"/>, and is called only from the producer:
/// request handlers read published facts and never reach this class.
/// </summary>
internal static class GitFactCommands
{
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Most patch bytes one batched patch read keeps; a larger batch is split.</summary>
    internal const long MaxPatchBytes = 64L * 1024 * 1024;

    /// <summary>SHAs per batched read, so stdin and output stay bounded.</summary>
    internal const int BatchSize = 500;

    private const string CommitMarker = "\u001egit-facts-patch:";

    /// <summary>
    /// Reads the facts of <paramref name="shas"/> (full or abbreviated) in one
    /// <c>git log --no-walk</c> per batch. A SHA the repository does not have is
    /// absent from the result and listed in <paramref name="missing"/>.
    /// </summary>
    internal static IReadOnlyList<GitCommitFact> ReadCommits(string root, IReadOnlyCollection<string> shas,
        out IReadOnlyList<string> missing)
    {
        var facts = new List<GitCommitFact>();
        var absent = new List<string>();
        foreach (var batch in shas.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
        {
            var result = LogCommits(root, batch);
            if (!result.Success)
            {
                // One unknown object fails the whole --stdin batch; keep the
                // ones the repository has and read them again.
                var present = Existing(root, batch, out var gone);
                absent.AddRange(gone);
                if (present.Count == 0) continue;
                result = LogCommits(root, present);
                if (!result.Success) throw new GitFactReadException("log", result.StandardError);
            }
            var parsed = GitCommitFactParser.Parse(result.StandardOutput);
            facts.AddRange(parsed);
            absent.AddRange(batch.Where(sha => !parsed.Any(fact =>
                fact.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase))));
        }
        missing = absent.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return facts;
    }

    private static GitProcessResult LogCommits(string root, IReadOnlyCollection<string> shas)
        => Run(root, ["-c", "core.quotepath=off", "log", "--no-walk=unsorted", "--stdin", "--root", "-M",
            "--diff-merges=first-parent", "--raw", "--numstat", "-z", "--no-color", "--no-ext-diff",
            "--format=" + GitCommitFactParser.Format], string.Join('\n', shas) + "\n");

    /// <summary>The SHAs of <paramref name="shas"/> that resolve to commits, via <c>cat-file --batch-check</c>.</summary>
    internal static IReadOnlyList<string> Existing(string root, IReadOnlyCollection<string> shas, out IReadOnlyList<string> missing)
    {
        var result = Run(root, ["cat-file", "--batch-check=%(objectname) %(objecttype)"], string.Join('\n', shas) + "\n");
        if (!result.Success) throw new GitFactReadException("cat-file", result.StandardError);
        var present = new List<string>();
        var gone = new List<string>();
        var lines = result.StandardOutput.Split('\n');
        var index = 0;
        foreach (var sha in shas)
        {
            var line = index < lines.Length ? lines[index++].Trim() : "";
            if (line.EndsWith(" commit", StringComparison.Ordinal)) present.Add(line[..line.IndexOf(' ')]);
            else gone.Add(sha);
        }
        missing = gone;
        return present;
    }

    /// <summary>
    /// The commits reachable from <paramref name="tip"/> but not from
    /// <paramref name="oldTip"/>, and whether the move was a fast-forward. With
    /// <c>--boundary</c>, the old tip is a boundary commit exactly when it is an
    /// ancestor of the new tip, so one process answers both. Null when the old
    /// tip is no longer an object (the caller recomputes in full).
    /// </summary>
    internal static (IReadOnlyList<string> Added, bool FastForward)? RevListSince(string root, string tip, string oldTip)
    {
        var result = Run(root, ["rev-list", "--boundary", tip, "^" + oldTip]);
        if (!result.Success) return null;
        var added = new List<string>();
        var fastForward = false;
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '-')
            {
                if (string.Equals(line[1..], oldTip, StringComparison.OrdinalIgnoreCase)) fastForward = true;
                continue;
            }
            added.Add(line);
        }
        return (added, fastForward);
    }

    /// <summary>Every commit reachable from <paramref name="tip"/> (one <c>rev-list</c>).</summary>
    internal static IReadOnlyList<string> RevListAll(string root, string tip)
    {
        var result = Run(root, ["rev-list", tip]);
        if (!result.Success) throw new GitFactReadException("rev-list", result.StandardError);
        return Lines(result.StandardOutput);
    }

    /// <summary>Non-merge commits of <c>base..head</c>, newest first, as <c>log --no-merges</c> lists them.</summary>
    internal static IReadOnlyList<string>? RangeCommits(string root, string baseSha, string headSha)
    {
        var result = Run(root, ["rev-list", "--no-merges", baseSha + ".." + headSha]);
        return result.Success ? Lines(result.StandardOutput) : null;
    }

    /// <summary>
    /// Non-merge commits on HEAD inside a wall-clock window, padded like
    /// <see cref="GitService.GetCommitsBetween"/>: the run views of runs that
    /// captured no SHA range.
    /// </summary>
    internal static IReadOnlyList<string>? WindowCommits(string root, DateTime fromUtc, DateTime toUtc, int paddingSeconds = 5)
    {
        if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        var from = fromUtc.AddSeconds(-paddingSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var to = toUtc.AddSeconds(paddingSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var result = Run(root, ["rev-list", "--no-merges", "--since=" + from, "--until=" + to, "HEAD"]);
        return result.Success ? Lines(result.StandardOutput) : null;
    }

    /// <summary>
    /// The incremental run trailer scan: commits reachable from
    /// <paramref name="tips"/> but from none of <paramref name="covered"/>
    /// whose message carries the worktree run trailer, with the job ids it
    /// names. Null when a covered tip is no longer an object.
    /// </summary>
    internal static IReadOnlyList<(string Sha, string JobId)>? ScanRunTrailers(string root,
        IReadOnlyCollection<string> tips, IReadOnlyCollection<string> covered)
    {
        if (tips.Count == 0) return [];
        var stdin = new StringBuilder();
        foreach (var tip in tips) stdin.Append(tip).Append('\n');
        foreach (var tip in covered) stdin.Append('^').Append(tip).Append('\n');
        var result = Run(root, ["log", "--stdin", "--no-merges", "--fixed-strings",
            "--grep=" + RunTrailerPrefix, "--format=%x1e%H%x1f%B", "--no-color"], stdin.ToString());
        if (!result.Success) return null;
        var found = new List<(string, string)>();
        foreach (var record in result.StandardOutput.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\u001f');
            if (separator <= 0) continue;
            var sha = record[..separator].Trim();
            foreach (var jobId in RunTrailerJobIds(record[(separator + 1)..])) found.Add((sha, jobId));
        }
        return found;
    }

    /// <summary>The fixed prefix of <see cref="GitService.WorktreeRunCommitTrailer"/>.</summary>
    internal static readonly string RunTrailerPrefix = GitService.WorktreeRunCommitTrailer("")[..^1];

    /// <summary>Every job id a commit message names through the run trailer.</summary>
    internal static IEnumerable<string> RunTrailerJobIds(string message)
    {
        var start = 0;
        while ((start = message.IndexOf(RunTrailerPrefix, start, StringComparison.Ordinal)) >= 0)
        {
            start += RunTrailerPrefix.Length;
            var end = message.IndexOf(']', start);
            if (end < 0) yield break;
            var jobId = message[start..end];
            // The trailer is matched verbatim, so the reconstruction stays exact.
            if (jobId.Length > 0 && message.Contains(GitService.WorktreeRunCommitTrailer(jobId), StringComparison.Ordinal))
                yield return jobId;
            start = end;
        }
    }

    /// <summary>
    /// Curated <c>merge(KEY)</c> commits reachable from <paramref name="tip"/>,
    /// excluding those reachable from <paramref name="since"/> when given, in
    /// <c>log</c> order (newest first), parsed like
    /// <see cref="GitService.GetIntegrationMergeCommits"/>.
    /// </summary>
    internal static IReadOnlyList<GitIntegrationMergeCommit> IntegrationMerges(string root, string tip, string? since)
    {
        var args = new List<string> { "log", "--no-color", "-E", "--grep=^merge(-recut)?\\(",
            "--format=%H%x1f%h%x1f%cI%x1f%cN%x1f%s", tip };
        if (since is not null) args.Add("^" + since);
        var result = Run(root, args);
        if (!result.Success) throw new GitFactReadException("log", result.StandardError);
        var merges = new List<GitIntegrationMergeCommit>();
        foreach (var line in result.StandardOutput.Replace("\r\n", "\n").Split('\n'))
        {
            var parts = line.Split('\u001f');
            if (parts.Length < 5) continue;
            var key = GitService.ParseIntegrationMergeKey(parts[4]);
            if (key is null) continue;
            if (!DateTime.TryParse(parts[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) continue;
            merges.Add(new GitIntegrationMergeCommit(key, parts[0], parts[1],
                DateTime.SpecifyKind(at, DateTimeKind.Utc), parts[3], parts[4]));
        }
        return merges;
    }

    /// <summary>
    /// First-parent patches of <paramref name="shas"/> in one batched
    /// <c>git log -p</c>, split per commit. A commit whose patch did not fit in
    /// <see cref="MaxPatchBytes"/> is absent from the result.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadPatches(string root, IReadOnlyCollection<string> shas)
    {
        var patches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (shas.Count == 0) return patches;
        var result = RunBinary(root, ["-c", "core.quotepath=off", "log", "--no-walk=unsorted", "--stdin", "--root",
            "-M", "--diff-merges=first-parent", "-p", "--no-color", "--no-ext-diff", "--format=%x1egit-facts-patch:%H"],
            string.Join('\n', shas) + "\n", MaxPatchBytes);
        if (!result.Success && !result.OutputTruncated) throw new GitFactReadException("log", result.StandardError);
        var text = Encoding.UTF8.GetString(result.StandardOutput);
        var records = text.Split(CommitMarker, StringSplitOptions.RemoveEmptyEntries);
        // A truncated read cut the last record; it is read again on its own.
        var complete = result.OutputTruncated ? records.Length - 1 : records.Length;
        for (var index = 0; index < complete; index++)
        {
            var record = records[index];
            var newline = record.IndexOf('\n');
            var sha = (newline < 0 ? record : record[..newline]).Trim();
            if (sha.Length == 0) continue;
            patches[sha] = newline < 0 ? "" : record[(newline + 1)..].TrimStart('\n');
        }
        return patches;
    }

    /// <summary>The name-status and numstat of <c>base..head</c> as one tree diff.</summary>
    internal static IReadOnlyList<GitCommitFileFact> RangeFiles(string root, string baseSha, string headSha)
    {
        var result = Run(root, ["-c", "core.quotepath=off", "diff", "-M", "--raw", "--numstat", "-z", "--no-color",
            "--no-ext-diff", baseSha, headSha]);
        if (!result.Success) throw new GitFactReadException("diff", result.StandardError);
        return GitCommitFactParser.ParseDiff(result.StandardOutput);
    }

    /// <summary>The unified patch of <c>base..head</c> as one tree diff, bounded like <see cref="ReadPatches"/>.</summary>
    internal static string? RangePatch(string root, string baseSha, string headSha)
    {
        var result = RunBinary(root, ["-c", "core.quotepath=off", "diff", "-M", "-p", "--no-color", "--no-ext-diff",
            baseSha, headSha], null, MaxPatchBytes);
        if (result.OutputTruncated) return null;
        if (!result.Success) throw new GitFactReadException("diff", result.StandardError);
        return Encoding.UTF8.GetString(result.StandardOutput);
    }

    internal static string? MergeBase(string root, string a, string b)
    {
        var result = Run(root, ["merge-base", a, b]);
        var sha = result.Success ? result.StandardOutput.Trim() : "";
        return sha.Length == 0 ? null : sha;
    }

    internal static GitProcessResult SetLocalConfig(string root, string key, string value)
        => Run(root, ["config", "--local", key, value]);

    private static IReadOnlyList<string> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static GitProcessResult Run(string root, IReadOnlyList<string> args, string? stdin = null)
        => GitProcessBoundary.Run(Start(root, args, stdin is not null), stdin, ReadTimeout);

    private static GitBinaryProcessResult RunBinary(string root, IReadOnlyList<string> args, string? stdin, long maxBytes)
        => GitProcessBoundary.RunBinary(Start(root, args, stdin is not null), stdin, maxBytes, ReadTimeout);

    private static ProcessStartInfo Start(string root, IReadOnlyList<string> args, bool redirectInput)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // A pure read: never take the optional index lock of a busy checkout.
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return start;
    }
}

/// <summary>A producer Git read that failed for the whole repository, not for one card.</summary>
internal sealed class GitFactReadException(string command, string error)
    : Exception($"git {command} failed: {error.Trim()}")
{
    public string Command { get; } = command;
}
