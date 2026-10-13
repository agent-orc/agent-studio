using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Identity of one stored candidate verify result: the repository, the exact
/// Result-SHA the command ran on, the verify command line (with the
/// preparation and library step it depends on), and the toolchain that
/// executed it.
/// </summary>
internal sealed record ReviewVerifyCacheKey(
    string RepositoryId,
    string ResultSha,
    string CommandHash,
    string ToolchainFingerprint);

/// <summary>
/// One passed candidate verify result on disk: the parsed result (exit code
/// and the verdict the source attempt derived from it) plus the complete
/// process streams, so a reused result is as citable in the grade as a
/// freshly executed one.
/// </summary>
internal sealed record VerifyCacheEntry(
    string RepositoryId,
    string ResultSha,
    string CommandHash,
    string ToolchainFingerprint,
    string AttemptId,
    string StepId,
    string HeadSha,
    string TreeSha,
    int ExitCode,
    string VerdictStatus,
    string StdOut,
    string StdErr,
    DateTime CreatedAt);

/// <summary>
/// Per-host store of passed candidate verify results (AGT-2841). A ReviewInfra
/// outcome (an aspect call that timed out, a tool that went missing, ...) says
/// nothing about the build, test and lint commands that already passed in that
/// attempt, yet its successor used to run all of them again. Every passed
/// deterministic command is stored here once per repository, Result-SHA,
/// command and toolchain; a successor whose plan names the failed attempt in
/// <c>VerifyReuseFromAttemptIds</c> re-attaches the stored result instead of
/// executing the command, and re-stores it under its own attempt id so only
/// its own successor can carry it further. Follows the AGT-2843 baseline cache
/// (<see cref="ReviewBaselineResultCache"/>).
///
/// A failed result is never stored and never reused, and a command that
/// executes drops the stored pass of its key first: whatever failed or did not
/// run in the source attempt executes again.
/// </summary>
internal static class ReviewVerifyResultCache
{
    /// <summary>
    /// Bounded lifetime of a stored result, the same as the baseline cache. The
    /// retry chain spends minutes, a cause-breaker probe can come hours later;
    /// past this the command simply executes again.
    /// </summary>
    internal static readonly TimeSpan MaximumAge = ReviewBaselineResultCache.MaximumAge;

    internal static string RepositoryDirectory(string root, string repositoryId)
        => Path.Combine(root, RemoteReviewWorkspace.HashText(repositoryId));

    internal static string ResultDirectory(string root, string repositoryId, string resultSha)
        => Path.Combine(RepositoryDirectory(root, repositoryId), RemoteReviewWorkspace.SafeSegment(resultSha));

    internal static string EntryPath(string root, ReviewVerifyCacheKey key)
        => Path.Combine(
            ResultDirectory(root, key.RepositoryId, key.ResultSha),
            $"{key.CommandHash}.{Short(key.ToolchainFingerprint)}.json");

    /// <summary>
    /// Pure reuse decision. Every key input is re-checked against the stored
    /// entry, the entry must come from one of the attempts the plan named, and
    /// only a passed result qualifies.
    /// </summary>
    internal static bool IsReusable(
        VerifyCacheEntry entry,
        ReviewVerifyCacheKey key,
        IReadOnlyCollection<string> sourceAttemptIds,
        DateTime utcNow)
        => entry.ExitCode == 0
           && string.Equals(entry.VerdictStatus, "pass", StringComparison.OrdinalIgnoreCase)
           && sourceAttemptIds.Contains(entry.AttemptId, StringComparer.Ordinal)
           && string.Equals(entry.RepositoryId, key.RepositoryId, StringComparison.Ordinal)
           && string.Equals(entry.ResultSha, key.ResultSha, StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.CommandHash, key.CommandHash, StringComparison.Ordinal)
           && string.Equals(entry.ToolchainFingerprint, key.ToolchainFingerprint, StringComparison.Ordinal)
           && Age(entry, utcNow) <= MaximumAge;

    /// <summary>
    /// Pure dependency rule over the reusable step ids. A reused command leaves
    /// no output in this attempt's fresh workspace. The clean repeat already
    /// runs every verify command alone in a fresh clone, with one exception: a
    /// test that runs with <c>--no-build</c> needs the build before it
    /// (<see cref="ReviewTargetedRerunPolicy.PrecedingBuild"/>). So whenever
    /// such a test executes, the build it depends on executes too.
    /// </summary>
    internal static IReadOnlyList<string> WithoutBuildsOfExecutedTests(
        IReadOnlyList<ReviewCommandDto> plannedCommands,
        ReviewPlanDto plan,
        IReadOnlyCollection<string> reusableStepIds)
    {
        var reusable = reusableStepIds.ToHashSet(StringComparer.Ordinal);
        foreach (var command in plannedCommands)
        {
            if (reusable.Contains(command.StepId)
                || ReviewCommandKinds.IsAgent(command.ExecutionKind)
                || !command.Arguments.Any(argument => argument.Contains("--no-build", StringComparison.Ordinal)))
                continue;
            if (ReviewTargetedRerunPolicy.PrecedingBuild(plan, command) is { } build)
                reusable.Remove(build.StepId);
        }
        return plannedCommands.Select(command => command.StepId).Where(reusable.Contains).ToArray();
    }

    internal static TimeSpan Age(VerifyCacheEntry entry, DateTime utcNow)
    {
        var createdAt = entry.CreatedAt.Kind == DateTimeKind.Local
            ? entry.CreatedAt.ToUniversalTime()
            : DateTime.SpecifyKind(entry.CreatedAt, DateTimeKind.Utc);
        var age = utcNow - createdAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    internal static async Task<VerifyCacheEntry?> ReadAsync(
        string path,
        ReviewVerifyCacheKey key,
        IReadOnlyCollection<string> sourceAttemptIds,
        DateTime utcNow,
        CancellationToken ct)
    {
        var entry = await ReadEntryAsync(path, ct);
        return entry is not null && IsReusable(entry, key, sourceAttemptIds, utcNow) ? entry : null;
    }

    internal static async Task WriteAsync(string path, VerifyCacheEntry entry, CancellationToken ct)
    {
        if (entry.ExitCode != 0)
            throw new ArgumentException("Only a passed verify result may be stored.", nameof(entry));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: ct);
            await stream.FlushAsync(ct);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Drops every result stored for one Result-SHA. Called when an attempt
    /// settled without a ReviewInfra outcome: no successor will name any attempt
    /// of that SHA, so its results would only occupy disk until they expire.
    /// </summary>
    internal static bool DeleteResult(string root, string repositoryId, string resultSha, Action<string> log)
    {
        var directory = ResultDirectory(root, repositoryId, resultSha);
        if (!Directory.Exists(directory)) return false;
        try
        {
            ResilientDirectory.Delete(directory);
            return !Directory.Exists(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"review verify cache delete failed path={directory}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Drops the stored result of one key before the command executes again,
    /// so a run that does not pass never leaves an older pass behind.
    /// </summary>
    internal static bool Delete(string root, ReviewVerifyCacheKey key, Action<string> log)
    {
        var path = EntryPath(root, key);
        return File.Exists(path) && TryDeleteFile(path, log);
    }

    /// <summary>
    /// Drops every entry past <see cref="MaximumAge"/> for one repository. A
    /// concurrent attempt on this host may delete a Result-SHA directory while
    /// the prune walks it; that directory simply has nothing left to prune.
    /// </summary>
    internal static async Task<int> PruneAsync(
        string root,
        string repositoryId,
        DateTime utcNow,
        Action<string> log,
        CancellationToken ct)
    {
        var repository = RepositoryDirectory(root, repositoryId);
        if (!Directory.Exists(repository)) return 0;
        var removed = 0;
        foreach (var directory in Snapshot(() => Directory.GetDirectories(repository), repository, log))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Snapshot(() => Directory.GetFiles(directory, "*.json"), directory, log))
            {
                var entry = await ReadEntryAsync(file, ct);
                if (entry is not null && Age(entry, utcNow) <= MaximumAge) continue;
                if (TryDeleteFile(file, log)) removed++;
            }
            TryDeleteEmptyDirectory(directory, log);
        }
        if (removed > 0)
            log($"review verify cache prune repository={repositoryId} removed={removed} " +
                $"maxAgeHours={MaximumAge.TotalHours:F0}");
        return removed;
    }

    private static async Task<VerifyCacheEntry?> ReadEntryAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<VerifyCacheEntry>(stream, cancellationToken: ct);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryDeleteFile(string path, Action<string> log)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"review verify cache delete failed path={path}: {exception.Message}");
            return false;
        }
    }

    /// <summary>Lists a cache directory eagerly; one that is already gone lists nothing.</summary>
    internal static string[] Snapshot(Func<string[]> list, string path, Action<string> log)
    {
        try
        {
            return list();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"review verify cache prune skipped path={path}: {exception.Message}");
            return [];
        }
    }

    /// <summary>An empty directory left behind is harmless; the next prune retries.</summary>
    private static void TryDeleteEmptyDirectory(string path, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                Directory.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"review verify cache directory delete failed path={path}: {exception.Message}");
        }
    }

    private static string Short(string fingerprint)
        => fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
