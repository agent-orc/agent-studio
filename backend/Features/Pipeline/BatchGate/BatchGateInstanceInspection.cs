using System.Diagnostics;
using AgentStudio.Shared;

namespace AgentStudio.Pipeline;

/// <summary>Explicit, bounded restore-migration aid; never called by host startup.</summary>
public static class BatchGateInstanceInspection
{
    public const string Command = "--inspect-batch-state-instances";
    public const int EntryLimit = 10_000;
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(2);

    public static int RunCommand(string[] args, TextWriter output)
    {
        if (args.Length is < 1 or > 2)
        {
            output.WriteLine($"Usage: {Command} <workspace> [<local-state-root>]");
            return 2;
        }
        var current = Path.GetDirectoryName(BatchGateStorageRoot.ForInstance(
            AppContext.BaseDirectory, args[0], "batch-gates", args.ElementAtOrDefault(1)))!;
        Run(current, output.WriteLine);
        return 0;
    }

    public static void Run(string currentRoot, Action<string> log, int maxEntries = EntryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        var instances = Path.GetDirectoryName(currentRoot)!;
        if (!Directory.Exists(instances)) return;
        var timer = Stopwatch.StartNew();
        var entries = 0;
        var limited = false;
        foreach (var other in Directory.EnumerateDirectories(instances))
        {
            if (!Budget()) break;
            if (FileSystemPathComparer.Instance.Equals(other, currentRoot)) continue;
            try
            {
                if ((File.GetAttributes(other) & FileAttributes.ReparsePoint) != 0) continue;
                var batches = Path.Combine(other, "batch-gates");
                if (!Directory.Exists(batches)) continue;
                var pending = new Stack<string>();
                pending.Push(batches);
                var found = false;
                while (!found && !limited && pending.TryPop(out var directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (!Budget()) break;
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else if (Path.GetFileName(entry) == "manifest.json")
                        {
                            log($"batch-state-unmatched-instance path={other} current={currentRoot}; batch manifests may require restore migration");
                            found = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"batch-state-inspection-kept path={other} reason={error.Message}");
            }
            if (limited) break;
        }
        log($"batch-state-inspection limitReached={limited} entries={entries} entryLimit={maxEntries} timeLimitSeconds={TimeLimit.TotalSeconds}");

        bool Budget()
        {
            if (entries >= maxEntries || timer.Elapsed >= TimeLimit)
            {
                limited = true;
                return false;
            }
            entries++;
            return true;
        }
    }
}
