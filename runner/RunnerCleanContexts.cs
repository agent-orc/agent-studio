using AgentStudio.CliHosting;

namespace AgentRunner;

/// <summary>Retains task session homes on a coding host between attempts.</summary>
internal sealed class RunnerCleanContexts(
    string root,
    Func<IReadOnlyList<PersistedRunnerSlot>> slots,
    Func<string[], CancellationToken, Task<string[]>> terminalHashes,
    Action<string> log)
{
    internal const int IdleRetentionDays = 2;
    private static readonly string ProbeHash = TaskCleanContextStore.TaskIdentityHash(ProviderAuthProbe.CleanContextIdentity);
    private CleanContextUsage? _usage;

    public CleanContextUsage? Usage => Volatile.Read(ref _usage);

    internal async Task SweepAsync(DateTimeOffset now, CancellationToken stop)
    {
        try
        {
            var homes = TaskCleanContextStore.Inventory(root);
            if (OperatingSystem.IsLinux() && homes.Count > 0)
            {
                var hashes = homes.Select(home => home.TaskIdentityHash)
                    .OfType<string>().Where(hash => hash != ProbeHash).Distinct();
                var terminal = new HashSet<string>(StringComparer.Ordinal);
                foreach (var batch in hashes.Chunk(512))
                {
                    try { terminal.UnionWith(await terminalHashes(batch, stop)); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskServerException or System.Text.Json.JsonException
                        || ex is OperationCanceledException && !stop.IsCancellationRequested)
                    {
                        log($"clean-context-lifecycle-unavailable error={ex.Message}");
                        break;
                    }
                }
                stop.ThrowIfCancellationRequested();
                // Slots reserve the task before the daemon reads continuation state
                // or launches a worker. Read them after the server observations.
                var reserved = slots().Select(slot => TaskCleanContextStore.TaskIdentityHash(slot.TaskKey))
                    .ToHashSet(StringComparer.Ordinal);
                var cutoff = now.AddDays(-IdleRetentionDays);
                reserved.Add(ProbeHash);
                var candidates = homes.Where(home =>
                    (home.TaskIdentityHash is not { } hash || !reserved.Contains(hash))
                    && (home.LastUsedAtUtc < cutoff || terminal.Contains(home.TaskIdentityHash ?? string.Empty)))
                    .ToArray();
                var sweep = TaskCleanContextStore.SweepHomes(root, candidates);
                foreach (var failure in sweep.FailedPaths) log($"clean-context-remove-failed path={failure}");
                if (sweep.Deleted > 0)
                    log($"clean-context-swept removed={sweep.Deleted} candidates={candidates.Length}");
                if (candidates.Length > 0) homes = TaskCleanContextStore.Inventory(root);
            }
            Volatile.Write(ref _usage, new CleanContextUsage(root, homes.Sum(home =>
                RunnerPreparationCopies.Measure(new DirectoryInfo(home.Path))), homes.Count));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log($"clean-context-sweep-failed error={ex.Message}");
        }
    }
}

public sealed record CleanContextUsage(string Path, long Bytes, int Count);
