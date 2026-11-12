using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Conservative cleanup of clean, unreferenced terminal-card worktrees.</summary>
internal sealed class RunnerWorktreeRetention(RunnerOptions options, TaskServerClient client,
    Func<IReadOnlyCollection<string>> activeCards, SemaphoreSlim admissionGate, Action<string> log)
{
    public static bool Eligible(string path, SalvageCardFacts? card, DateTime nowUtc,
        IReadOnlyCollection<string> active, int retentionDays)
        => card is { Lifecycle: SalvageCardLifecycle.Terminal, TerminalSince: { } terminal }
            && terminal <= nowUtc.AddDays(-Math.Max(1, retentionDays))
            && Directory.Exists(path)
            && !active.Contains(card.CardKey, StringComparer.OrdinalIgnoreCase)
            && !File.Exists(path + ".retained.json")
            && !File.Exists(path + ".push-protection.json");

    public async Task SweepOnceAsync(CancellationToken ct)
    {
        if (!Directory.Exists(options.WorkDir)) return;
        var roots = Directory.EnumerateDirectories(options.WorkDir)
            .Append(options.WorkDir)
            .Select(parent => (Project: parent == options.WorkDir ? (string?)null : Path.GetFileName(parent),
                Root: Path.Combine(parent, "worktrees")))
            .Where(item => Directory.Exists(item.Root)).ToArray();
        var entries = roots.SelectMany(root => Directory.EnumerateDirectories(root.Root)
                .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)
                    && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                .Select(path => (root.Project, root.Root, Path: path,
                    Card: Path.GetFileName(path))))
            .ToArray();
        var directory = new TaskServerSalvageCardDirectory(client);
        var facts = await directory.ResolveAsync(entries.Select(entry =>
            new SalvageCardReference(entry.Project, entry.Card)).ToArray(), ct);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await admissionGate.WaitAsync(ct);
            try
            {
                var card = facts.GetValueOrDefault(SalvageRetentionPolicy.NormalizeCardKey(entry.Card));
                if (!Eligible(entry.Path, card, DateTime.UtcNow, activeCards(), options.SalvageRetentionDays))
                    continue;
                var lease = Path.Combine(entry.Root, ".leases", entry.Card + ".json");
                if (File.Exists(lease) || File.Exists(lease + ".retained.json")
                    || File.Exists(lease + ".push-protection.json")) continue;
                var status = await ProcessRunner.RunAsync("git", ["status", "--porcelain"], entry.Path, ct: ct);
                if (!status.Success || !string.IsNullOrWhiteSpace(status.StdOut)) continue;
                var remoteCopies = await ProcessRunner.RunAsync("git",
                    ["branch", "-r", "--contains", "HEAD"], entry.Path, ct: ct);
                if (!remoteCopies.Success || string.IsNullOrWhiteSpace(remoteCopies.StdOut)) continue;
                var repo = Path.Combine(Path.GetDirectoryName(entry.Root)!, "repo");
                if (!Directory.Exists(repo)) continue;
                var removed = await ProcessRunner.RunAsync("git",
                    ["worktree", "remove", "--force", entry.Path], repo, ct: ct);
                if (removed.Success)
                    log($"runner-worktree-retention deleted path={entry.Path} card={entry.Card}");
                else
                    log($"runner-worktree-retention kept path={entry.Path} error={removed.StdErr.Trim()}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log($"runner-worktree-retention kept path={entry.Path} error={error.Message}");
            }
            finally { admissionGate.Release(); }
        }
        var resultsRoot = Path.Combine(options.WorkDir, "tasks");
        if (!Directory.Exists(resultsRoot)) return;
        var archives = Directory.EnumerateDirectories(resultsRoot)
            .Select(task => (Card: Path.GetFileName(task), Path: Path.Combine(task, "results")))
            .Where(item => Directory.Exists(item.Path)).ToArray();
        var archiveFacts = await directory.ResolveAsync(archives.Select(item =>
            new SalvageCardReference(null, item.Card)).ToArray(), ct);
        foreach (var archive in archives)
        {
            await admissionGate.WaitAsync(ct);
            try
            {
                var card = archiveFacts.GetValueOrDefault(SalvageRetentionPolicy.NormalizeCardKey(archive.Card));
                if (!Eligible(archive.Path, card, DateTime.UtcNow, activeCards(), options.SalvageRetentionDays))
                    continue;
                if (ResilientDirectory.TryDelete(archive.Path))
                    log($"runner-result-retention deleted card={archive.Card} path={archive.Path}");
            }
            finally { admissionGate.Release(); }
        }
    }
}
