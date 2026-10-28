using System.Diagnostics;

namespace AgentRunner.WorkContainers;

public sealed record WorkProcessResult(int ExitCode, string Output, double WallMs, string Bound);

/// <summary>
/// The worktree process executor kept beside the container executor. On
/// Windows every attempt runs in a Job Object built from the same grant; on
/// Linux it is the process and cgroup rollback, bounded by the worker cgroup
/// the calling worker already runs in. Neither path needs a container runtime.
/// </summary>
public static class WorkProcessExecutor
{
    public static async Task<WorkProcessResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        WorkContainerLimits limits,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (OperatingSystem.IsWindows())
        {
            using var bound = WindowsProcessTreeBound.Create(limits);
            var start = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in environment ?? new Dictionary<string, string?>()) start.Environment[key] = value;
            WorkerEdgeCredentialBoundary.RemoveFrom(start.Environment);
            WorkerBuildServerHygiene.ApplyTo(start.Environment);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the work process.");
            bound.Assign(process);
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            // On cancellation, disposing the bound closes the job and kills the whole tree.
            await process.WaitForExitAsync(ct);
            return new WorkProcessResult(process.ExitCode, await output + await error,
                watch.Elapsed.TotalMilliseconds, "windows-job-object");
        }

        var result = await ProcessRunner.RunAsync(fileName, arguments, workingDirectory,
            environment: environment, isolateProcessGroup: OperatingSystem.IsLinux(), ct: ct);
        return new WorkProcessResult(result.ExitCode, result.StdOut + result.StdErr,
            watch.Elapsed.TotalMilliseconds, LinuxBound());
    }

    private static string LinuxBound()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/cgroup")) return "process-group";
        var relative = File.ReadLines("/proc/self/cgroup").FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))?[3..];
        if (relative is null) return "process-group";
        var cpuMax = Path.Combine("/sys/fs/cgroup", relative.TrimStart('/'), "cpu.max");
        return File.Exists(cpuMax) ? $"cgroup {relative} cpu.max={File.ReadAllText(cpuMax).Trim()}" : $"cgroup {relative}";
    }
}
