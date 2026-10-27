namespace AgentStudio.Git;

/// <summary>
/// Reads the exact object IDs of the refs a generation is computed from:
/// <c>HEAD</c>, <c>refs/heads/*</c>, <c>refs/remotes/origin/*</c> and the
/// Studio integration lane <c>refs/agent-studio/integration/*</c>. It reads
/// loose ref files and <c>packed-refs</c> and never starts Git. A reftable
/// repository is recorded as such; its refs stay covered by the ref
/// fingerprint until a later slice reads them through the producer.
/// </summary>
internal static class GitRefObjectIds
{
    internal const int MaxRefs = 50_000;
    internal const string FormatKey = "#format";
    internal const string TruncatedKey = "#truncated";

    private static readonly string[] Prefixes =
        ["refs/heads/", "refs/remotes/origin/", "refs/agent-studio/integration/"];

    internal static IReadOnlyDictionary<string, string> Read(string repositoryPath)
    {
        var refs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var gitDirectory = GitRefSignature.ResolveGitDirectory(repositoryPath);
        if (gitDirectory is null || !Directory.Exists(gitDirectory)) return refs;
        var common = GitRefSignature.ResolveCommonGitDirectory(gitDirectory);
        if (Directory.Exists(Path.Combine(common, "reftable")))
        {
            refs[FormatKey] = "reftable";
            return refs;
        }

        try
        {
            ReadPacked(Path.Combine(common, "packed-refs"), refs);
            foreach (var prefix in Prefixes)
                ReadLoose(common, prefix, refs);
            if (refs.Count >= MaxRefs) refs[TruncatedKey] = "true";
            var head = ReadRefFile(Path.Combine(gitDirectory, "HEAD"));
            if (head is not null)
                refs["HEAD"] = head.StartsWith("ref: ", StringComparison.Ordinal)
                    && refs.TryGetValue(head["ref: ".Length..], out var target) ? target : head;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefObjectIds: ref read failed");
            refs[TruncatedKey] = "unreadable";
        }
        return refs;
    }

    private static void ReadPacked(string path, SortedDictionary<string, string> refs)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] is '#' or '^') continue;
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var name = line[(space + 1)..].Trim();
            if (refs.Count < MaxRefs && IsTracked(name)) refs[name] = line[..space];
        }
    }

    private static void ReadLoose(string common, string prefix, SortedDictionary<string, string> refs)
    {
        var root = Path.Combine(common, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root)) return;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (refs.Count >= MaxRefs) return;
            var name = Path.GetRelativePath(common, file).Replace('\\', '/');
            // A loose ref overrides its packed copy; lock files are in-flight writes.
            if (name.EndsWith(".lock", StringComparison.Ordinal)) continue;
            if (ReadRefFile(file) is { } value) refs[name] = value;
        }
    }

    private static bool IsTracked(string name)
        => Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static string? ReadRefFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var value = reader.ReadLine()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
