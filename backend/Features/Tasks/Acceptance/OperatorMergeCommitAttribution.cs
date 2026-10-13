using System.Text.RegularExpressions;

using AgentStudio.Shared;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-3045 - pure decision for attributing the commit(s) named by an operator
/// card-scoped merge record. An operator merge lands real work on the
/// integration branch outside the pipeline, so the card's <c>commits[]</c>
/// stays empty and the Result header reads "no code change". When the
/// integration record names the merge SHA(s), they become manual commit
/// attributions on the card. A record without <c>commitShas</c> falls back to
/// the SHA suffix of its id (<c>operator-merge-agt-nnnn-&lt;sha&gt;</c>).
/// </summary>
public static partial class OperatorMergeCommitAttribution
{
    public const string RecordIdPrefix = "operator-merge-";

    public static bool IsOperatorMerge(TaskIntegrationRecord record)
        => record.Id.StartsWith(RecordIdPrefix, StringComparison.OrdinalIgnoreCase)
           && record.Classification.StartsWith("integrated-", StringComparison.Ordinal);

    /// <summary>
    /// The merge SHA(s) to attribute. The id suffix names the merge commit
    /// itself; when it is among <c>commitShas</c> (which may also list the
    /// delivery commits the merge brought in) only the merge is attributed.
    /// Without a suffix every recorded SHA is attributed; without recorded
    /// SHAs the suffix alone is.
    /// </summary>
    public static IReadOnlyList<string> MergeShas(TaskIntegrationRecord record)
    {
        var shas = record.CommitShas
            .Where(sha => !string.IsNullOrWhiteSpace(sha) && ShaPattern().IsMatch(sha.Trim()))
            .Select(sha => sha.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var suffix = IdShaSuffix().Match(record.Id);
        if (!suffix.Success) return shas;
        var merge = suffix.Groups[1].Value.ToLowerInvariant();
        var named = shas.Where(sha => SameCommit(sha, merge)).ToList();
        return named.Count > 0 ? named : shas.Count > 0 ? shas : [merge];
    }

    /// <summary>
    /// Commits to append for an operator merge record. SHAs already in the
    /// chain (full or abbreviated either way) are skipped, so a replay is a
    /// no-op. <paramref name="resolve"/> supplies Git metadata when the SHA
    /// exists in the task repository; otherwise the record's evidence is used.
    /// </summary>
    public static IReadOnlyList<TaskCommitInfo> CommitsToAttribute(
        TaskIntegrationRecord record,
        IReadOnlyList<TaskCommitInfo> existing,
        Func<string, ResolvedMergeCommit?> resolve)
    {
        if (!IsOperatorMerge(record)) return [];
        var result = new List<TaskCommitInfo>();
        foreach (var sha in MergeShas(record))
        {
            var resolved = resolve(sha);
            var fullSha = resolved?.Sha ?? sha;
            if (existing.Concat(result).Any(commit => SameCommit(commit.Sha, fullSha))) continue;
            result.Add(new TaskCommitInfo
            {
                Sha = fullSha,
                ShortSha = fullSha[..Math.Min(9, fullSha.Length)],
                Message = string.IsNullOrWhiteSpace(resolved?.Message) ? record.Evidence : resolved.Message.Trim(),
                Branch = record.IntegrationBranch,
                FilesChanged = resolved?.Files.Count ?? 0,
                Files = resolved?.Files.ToList() ?? [],
                At = resolved?.At ?? record.AcceptedAtUtc ?? record.RecordedAtUtc,
                Attribution = CommitAttributionKinds.Manual,
                Confidence = 1.0,
            });
        }
        return result;
    }

    private static bool SameCommit(string left, string right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
               || right.StartsWith(left, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex("-([0-9a-fA-F]{7,40})$")]
    private static partial Regex IdShaSuffix();
}

/// <summary>Git facts for one merge commit, when the repository has it.</summary>
public sealed record ResolvedMergeCommit(string Sha, string Message, IReadOnlyList<string> Files, DateTime At);
