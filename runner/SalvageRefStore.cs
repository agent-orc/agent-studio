using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Reads and deletes this runner's salvage refs on each project origin.</summary>
internal interface ISalvageRefStore
{
    Task<IReadOnlyList<SalvageRefRepository>> CollectAsync(CancellationToken ct);

    /// <summary>Delete refs from origin; the result maps each ref to null (deleted) or an error.</summary>
    Task<IReadOnlyDictionary<string, string?>> DeleteAsync(
        SalvageRefRepository repository,
        IReadOnlyList<SalvageEntry> refs,
        CancellationToken ct);
}

/// <summary>
/// One project clone. <see cref="Error"/> is set when its facts could not be
/// read; such a repository contributes no refs and therefore no deletion.
/// </summary>
internal sealed record SalvageRefRepository(
    string RepoPath,
    string? ProjectId,
    string? IntegrationBranch,
    IReadOnlyList<SalvageEntry> Refs,
    string? Error = null);

/// <summary>
/// Git implementation over the runner's shared project clones
/// (<c>&lt;workdir&gt;/&lt;project&gt;/repo</c> and the legacy <c>&lt;workdir&gt;/repo</c>).
/// Only <c>agent-studio/salvage/&lt;this runner&gt;/*</c> is listed, so another
/// runner's refs, whose active runs this host cannot see, are never touched.
/// The integration branch is the branch the stable checkout was last prepared
/// on. Deletion is a lease-guarded push, so a ref that moved since it was read
/// is rejected by the remote instead of being deleted.
/// </summary>
internal sealed partial class GitSalvageRefStore(RunnerOptions options) : ISalvageRefStore
{
    private const int DeleteBatchSize = 50;
    private const string RemotePrefix = "refs/remotes/origin/";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CardKeyShape();

    public string Namespace => $"agent-studio/salvage/{GitWorkspace.SafeSegment(options.RunnerId)}/";

    public async Task<IReadOnlyList<SalvageRefRepository>> CollectAsync(CancellationToken ct)
    {
        var repositories = new List<SalvageRefRepository>();
        foreach (var (repoPath, projectId) in ProjectClones())
        {
            await GitWorkspace.GitMetadataGate.WaitAsync(ct);
            try
            {
                repositories.Add(await CollectOneAsync(repoPath, projectId, ct));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                repositories.Add(new SalvageRefRepository(repoPath, projectId, null, [], exception.Message));
            }
            finally
            {
                GitWorkspace.GitMetadataGate.Release();
            }
        }
        return repositories;
    }

    public async Task<IReadOnlyDictionary<string, string?>> DeleteAsync(
        SalvageRefRepository repository,
        IReadOnlyList<SalvageEntry> refs,
        CancellationToken ct)
    {
        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var batch in refs.Where(IsOwnedRef).Chunk(DeleteBatchSize))
        {
            var args = new List<string> { "push", "--porcelain", "origin" };
            args.AddRange(batch.Select(entry => $"--force-with-lease=refs/heads/{entry.Id}:{entry.Sha}"));
            args.AddRange(batch.Select(entry => $":refs/heads/{entry.Id}"));
            ProcessResult push;
            await GitWorkspace.GitMetadataGate.WaitAsync(ct);
            try
            {
                push = await ProcessRunner.RunAsync("git", args, repository.RepoPath, ct: ct);
            }
            finally
            {
                GitWorkspace.GitMetadataGate.Release();
            }

            var statuses = ParsePushStatuses(push.StdOut);
            foreach (var entry in batch)
            {
                results[entry.Id] = statuses.TryGetValue($"refs/heads/{entry.Id}", out var status)
                    ? status
                    : $"no push status (exit {push.ExitCode}): {OneLine(push.StdErr)}";
            }
        }
        foreach (var entry in refs.Where(entry => !IsOwnedRef(entry)))
            results[entry.Id] = "ref is outside this runner's salvage namespace";
        return results;
    }

    /// <summary>
    /// Map <c>git push --porcelain</c> lines (<c>flag\tfrom:to\tsummary</c>) to
    /// null for a completed deletion and the summary for anything else.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> ParsePushStatuses(string porcelain)
    {
        var statuses = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3 || parts[0].Length != 1) continue;
            var colon = parts[1].IndexOf(':');
            if (colon < 0) continue;
            var target = parts[1][(colon + 1)..];
            statuses[target] = parts[0] == "-" ? null : parts[2].Trim();
        }
        return statuses;
    }

    private bool IsOwnedRef(SalvageEntry entry)
        => entry.Kind == SalvageEntryKind.GitRef
           && entry.Id.StartsWith(Namespace, StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(entry.Sha);

    private async Task<SalvageRefRepository> CollectOneAsync(string repoPath, string? projectId, CancellationToken ct)
    {
        var fetch = await ProcessRunner.RunAsync("git", ["fetch", "origin", "--prune"], repoPath, ct: ct);
        if (!fetch.Success)
            return new SalvageRefRepository(repoPath, projectId, null, [], $"git fetch failed: {OneLine(fetch.StdErr)}");

        var head = await ProcessRunner.RunAsync("git", ["symbolic-ref", "--quiet", "--short", "HEAD"], repoPath, ct: ct);
        string? integration = head.Success ? head.StdOut.Trim() : null;
        if (integration is { Length: > 0 })
        {
            var tracked = await ProcessRunner.RunAsync(
                "git", ["rev-parse", "--verify", "--quiet", RemotePrefix + integration], repoPath, ct: ct);
            if (!tracked.Success) integration = null;
        }
        else
        {
            integration = null;
        }

        var listed = await ProcessRunner.RunAsync(
            "git",
            ["for-each-ref", "--format=%(refname)%09%(objectname)%09%(committerdate:unix)", RemotePrefix + Namespace],
            repoPath,
            ct: ct);
        if (!listed.Success)
            return new SalvageRefRepository(repoPath, projectId, integration, [], $"git for-each-ref failed: {OneLine(listed.StdErr)}");

        var refs = new List<SalvageEntry>();
        foreach (var line in listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length != 3 || !parts[0].StartsWith(RemotePrefix, StringComparison.Ordinal)) continue;
            var branch = parts[0][RemotePrefix.Length..];
            var sha = parts[1];
            var created = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : DateTime.UtcNow;
            bool? contained = null;
            if (integration is not null)
            {
                var ancestor = await ProcessRunner.RunAsync(
                    "git", ["merge-base", "--is-ancestor", sha, RemotePrefix + integration], repoPath, ct: ct);
                contained = ancestor.ExitCode switch { 0 => true, 1 => false, _ => null };
            }
            refs.Add(new SalvageEntry(
                SalvageEntryKind.GitRef, branch, projectId, CardKeyOf(branch), created, 0, contained, sha));
        }
        return new SalvageRefRepository(repoPath, projectId, integration, refs);
    }

    /// <summary>Card key segment of <c>agent-studio/salvage/&lt;runner&gt;/&lt;card&gt;/...</c>, or null.</summary>
    internal static string? CardKeyOf(string branch)
    {
        var segments = branch.Split('/');
        return segments.Length >= 5 && CardKeyShape().IsMatch(segments[3])
            ? SalvageRetentionPolicy.NormalizeCardKey(segments[3])
            : null;
    }

    private IEnumerable<(string RepoPath, string? ProjectId)> ProjectClones()
    {
        var root = Path.GetFullPath(options.WorkDir);
        if (!Directory.Exists(root)) yield break;
        var legacy = Path.Combine(root, "repo");
        if (Directory.Exists(Path.Combine(legacy, ".git")))
            yield return (legacy, null);
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
        {
            var repo = Path.Combine(directory, "repo");
            if (Directory.Exists(Path.Combine(repo, ".git")))
                yield return (repo, Path.GetFileName(directory));
        }
    }

    private static string OneLine(string value)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
