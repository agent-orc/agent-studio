using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Git;

/// <summary>Outcome of <see cref="GitService.TryResolveAddAddConflicts"/>.</summary>
public sealed record AddAddConflictResolution(
    bool Resolved,
    IReadOnlyList<UnionResolvedHunk> Hunks,
    string? Refusal);

/// <summary>
/// AGT-3968: integration-conflict helpers. A conflict whose hunks all only add
/// lines on both sides is resolved by keeping both; the cards whose
/// integrated commits touched the conflicting files are named when a card is
/// handed to an agent or parked.
/// </summary>
public partial class GitService
{
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,15}-\d+\b(?!-[A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictCardKeyPattern();

    /// <summary>
    /// Resolves the unmerged <paramref name="conflictedFiles"/> of a merge in
    /// progress at <paramref name="worktreePath"/> by union, but only when
    /// every hunk of every file is an add/add hunk. Nothing is written unless
    /// all files resolve; the resolved files are staged, the merge is left
    /// for the caller to commit.
    /// </summary>
    public AddAddConflictResolution TryResolveAddAddConflicts(
        string worktreePath, IReadOnlyList<string> conflictedFiles)
    {
        if (conflictedFiles.Count == 0)
            return new AddAddConflictResolution(false, [], "no conflicted file");
        var resolved = new List<(string Path, byte[] Content)>();
        var hunks = new List<UnionResolvedHunk>();
        foreach (var path in conflictedFiles)
        {
            var (stages, stageError, stageCode) = RunGitArgs(worktreePath, "ls-files", "-u", "-z", "--", path);
            if (stageCode != 0)
                return Refused($"{path}: unmerged stages could not be read: {stageError.Trim()}");
            var entries = stages.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('\t', 2)[0].Split(' '))
                .Where(columns => columns.Length == 3)
                .ToArray();
            if (entries.Length != 3
                || !entries.Select(columns => columns[2]).Order(StringComparer.Ordinal).SequenceEqual(["1", "2", "3"])
                || entries.Any(columns => columns[0] is not ("100644" or "100755")))
                return Refused($"{path}: not a text conflict that both sides changed from one merge base");

            var (temporary, tempError, tempCode) = RunGitArgs(worktreePath,
                "checkout-index", "--stage=all", "--temp", "--", path);
            var names = tempCode == 0 ? temporary.Split('\t', 2)[0].Split(' ') : [];
            if (names.Length != 3 || names.Any(name => name == "."))
                return Refused($"{path}: conflict stages could not be checked out: {tempError.Trim()}");
            var full = names.Select(name => Path.Combine(worktreePath, name)).ToArray();
            try
            {
                var (_, mergeError, mergeCode) = RunGitArgs(worktreePath, "merge-file", "--diff3",
                    $"--marker-size={AddAddUnionResolution.MarkerSize}",
                    "-L", "integration", "-L", "merge-base", "-L", "delivery",
                    names[1], names[0], names[2]);
                if (mergeCode < 0 || mergeCode > 127)
                    return Refused($"{path}: three-way merge failed: {mergeError.Trim()}");
                var bytes = File.ReadAllBytes(full[1]);
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                    return Refused($"{path}: binary content");
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text;
                try
                {
                    text = new UTF8Encoding(false, throwOnInvalidBytes: true)
                        .GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                }
                catch (DecoderFallbackException)
                {
                    return Refused($"{path}: not UTF-8 text");
                }
                var union = AddAddUnionResolution.Resolve(path, text);
                if (!union.Resolved) return Refused(union.Refusal!);
                var content = Encoding.UTF8.GetBytes(union.Content!);
                resolved.Add((path, bom ? [0xEF, 0xBB, 0xBF, .. content] : content));
                hunks.AddRange(union.Hunks);
            }
            finally
            {
                foreach (var file in full)
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (IOException ex)
                    {
                        AgentStudio.Diagnostics.SilentCatch.Note(ex, "Union-resolution stage file could not be removed.");
                    }
            }
        }

        foreach (var (path, content) in resolved)
        {
            File.WriteAllBytes(Path.Combine(worktreePath, path), content);
            var (_, addError, addCode) = RunGitArgs(worktreePath, "add", "--", path);
            if (addCode != 0) return Refused($"{path}: resolved file could not be staged: {addError.Trim()}");
        }
        return new AddAddConflictResolution(true, hunks, null);

        static AddAddConflictResolution Refused(string reason) => new(false, [], reason);
    }

    /// <summary>
    /// Card keys named by the commits on <paramref name="tipSha"/> since its
    /// merge base with <paramref name="resultSha"/> that touched one of
    /// <paramref name="paths"/>, in first-seen order. <paramref name="ownKey"/>
    /// is left out. Empty when git cannot answer.
    /// </summary>
    public IReadOnlyList<string> CardsTouchingPaths(
        string repoRoot, string resultSha, string tipSha,
        IReadOnlyCollection<string> paths, string? ownKey)
    {
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return [];
        var (mergeBase, _, baseCode) = RunGitArgs(repoRoot, "merge-base", resultSha, tipSha);
        if (baseCode != 0 || string.IsNullOrWhiteSpace(mergeBase)) return [];
        string[] args = ["log", "--format=%s%n%b", $"{mergeBase.Trim()}..{tipSha}", "--", .. paths];
        var (log, _, logCode) = RunGitArgs(repoRoot, args);
        if (logCode != 0) return [];
        return ConflictCardKeyPattern().Matches(log)
            .Select(match => match.Value)
            .Where(key => !string.Equals(key, ownKey?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
