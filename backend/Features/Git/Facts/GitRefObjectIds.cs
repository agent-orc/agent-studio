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
        => Read(repositoryPath, Prefixes);

    /// <summary>
    /// Every ref under <c>refs/</c> (branches, remotes, tags, result and salvage
    /// refs), read from files. The run trailer scan uses these as the tips it
    /// covered, so its next scan walks only commits new since then.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadAll(string repositoryPath)
        => Read(repositoryPath, ["refs/"]);

    private static IReadOnlyDictionary<string, string> Read(string repositoryPath, string[] prefixes)
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
            ReadPacked(Path.Combine(common, "packed-refs"), refs, prefixes);
            foreach (var prefix in prefixes)
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

    private static void ReadPacked(string path, SortedDictionary<string, string> refs, string[] prefixes)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] is '#' or '^') continue;
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var name = line[(space + 1)..].Trim();
            if (refs.Count < MaxRefs && IsTracked(name, prefixes)) refs[name] = line[..space];
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

    private static bool IsTracked(string name, string[] prefixes)
        => prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// The full ref a short name resolves to among <paramref name="refs"/>, in
    /// Git's order for the refs read here: an exact <c>refs/</c> name, then
    /// <c>refs/heads/</c>, then <c>refs/remotes/</c>. Null when absent.
    /// </summary>
    internal static string? Resolve(IReadOnlyDictionary<string, string> refs, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.StartsWith("refs/", StringComparison.Ordinal)) return refs.ContainsKey(name) ? name : null;
        foreach (var candidate in new[] { "refs/heads/" + name, "refs/remotes/" + name })
            if (refs.ContainsKey(candidate)) return candidate;
        return null;
    }

    /// <summary>True when <paramref name="name"/> is inside the namespaces <see cref="Read(string)"/> covers.</summary>
    internal static bool Covers(string name)
        => name.StartsWith("refs/", StringComparison.Ordinal)
            ? IsTracked(name, Prefixes)
            : !name.Contains("..", StringComparison.Ordinal) && !name.Contains('^') && !name.Contains('~')
              && !name.Contains(':') && (!name.Contains('/') || name.StartsWith("origin/", StringComparison.Ordinal));

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
