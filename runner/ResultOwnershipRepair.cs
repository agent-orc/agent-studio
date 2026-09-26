namespace AgentRunner;

/// <summary>Bounded repair for container-owned result files in the coding workdir.</summary>
internal static class ResultOwnershipRepair
{
    internal static async Task EnsureOwnedAsync(
        string taskKey, string resultsDirectory, Action<string> log, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(resultsDirectory)) return;
        var foreign = await ProcessRunner.RunAsync("find",
            [resultsDirectory, "!", "-user", Environment.UserName, "-print", "-quit"], ct: ct);
        if (!foreign.Success || !string.IsNullOrWhiteSpace(foreign.StdOut))
            await RepairOrThrowAsync(taskKey, resultsDirectory, log, ct);
    }

    internal static async Task RepairOrThrowAsync(
        string taskKey, string resultsDirectory, Action<string> log, CancellationToken ct,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? run = null)
    {
        run ??= (command, args, token) => ProcessRunner.RunAsync(command, args, ct: token);
        string? sample = null;
        try
        {
            if (Directory.Exists(resultsDirectory))
                sample = Directory.EnumerateFileSystemEntries(resultsDirectory, "*", SearchOption.AllDirectories)
                    .FirstOrDefault();
        }
        catch (UnauthorizedAccessException) { /* the helper owns the repair */ }
        if (OperatingSystem.IsLinux() && Directory.Exists(resultsDirectory))
        {
            var foreign = await run("find",
                [resultsDirectory, "!", "-user", Environment.UserName, "-print", "-quit"], ct);
            if (foreign.Success && !string.IsNullOrWhiteSpace(foreign.StdOut))
                sample = foreign.StdOut.Trim();
        }
        sample ??= resultsDirectory;
        var owner = "unknown";
        var stat = await run("stat", ["-c", "%u:%g", "--", sample], ct);
        if (stat.Success) owner = stat.StdOut.Trim();
        var repair = await run("sudo",
            ["-n", "/usr/local/sbin/agent-runner-deploy", "chown-results", GitWorkspace.SafeSegment(taskKey)],
            ct);
        if (!repair.Success)
            throw new UnauthorizedAccessException(
                $"Result ownership repair failed: path={sample} owner={owner}; " +
                $"helper={repair.StdErr.Trim()}. Recovery: chown results to the runner user on this host.");
        log($"result-ownership-repaired task={taskKey} path={resultsDirectory} previousOwner={owner}");
    }
}
