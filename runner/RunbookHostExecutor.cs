using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record RunbookHostStepResult(int Index, bool Success);
public sealed record RunbookHostResult(bool Success, IReadOnlyList<RunbookHostStepResult> Steps);

/// <summary>
/// Runs operator-installed scripts by identifier on the assigned deploy host.
/// No repository command or shell text arrives from project.yml. Script output
/// is drained locally; only bounded step results return to the control plane.
/// </summary>
public static class RunbookHostExecutor
{
    private const int SigKill = 9;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(25);
    private const int SweepRounds = 20;
    /// <summary>Inherited by every descendant, also one that starts its own session.</summary>
    private const string RunMarker = "AGENT_STUDIO_RUNBOOK_RUN";

    public static async Task<RunbookHostResult> ExecuteAsync(string project, string environment,
        string destination, IReadOnlyList<string> steps, IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> secretVariables, IReadOnlyList<string> environmentSecretRefs,
        string scriptRoot, string secretRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(project) || project is "." or ".."
            || project.IndexOfAny(['/', '\\', '\0']) >= 0 || !Identifier(environment) || steps.Count == 0
            || steps.Any(step => !Identifier(step)))
            throw new InvalidOperationException("Invalid runbook identifiers.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENT_STUDIO_PROJECT"] = project,
            ["AGENT_STUDIO_ENVIRONMENT"] = environment,
            ["AGENT_STUDIO_DESTINATION"] = destination,
        };
        foreach (var (name, value) in variables)
        {
            if (!AllowedName(name)) throw new InvalidOperationException("Invalid runbook variable name.");
            values[name] = value;
        }
        foreach (var (name, secret) in secretVariables)
        {
            if (!AllowedName(name) || !SecretIdentifier(secret) || values.ContainsKey(name))
                throw new InvalidOperationException("Invalid runbook secret reference.");
            values[name] = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), cancellationToken))
                .TrimEnd('\r', '\n');
        }
        foreach (var secret in environmentSecretRefs)
        {
            if (!SecretIdentifier(secret)) throw new InvalidOperationException("Invalid environment secret reference.");
            var name = secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant();
            if (values.ContainsKey(name)) continue;
            if (!AllowedName(name)) throw new InvalidOperationException("Invalid environment secret reference.");
            values[name] = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), cancellationToken))
                .TrimEnd('\r', '\n');
        }
        var results = new List<RunbookHostStepResult>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RunTimeout);
        for (var index = 0; index < steps.Count; index++)
        {
            if (deadline.IsCancellationRequested)
            {
                results.Add(new(index, false));
                break;
            }
            var script = Path.Combine(scriptRoot, project, steps[index] + ".sh");
            if (!File.Exists(script))
            {
                results.Add(new(index, false));
                break;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            timeout.CancelAfter(StepTimeout);
            // A separate session lets cancellation signal descendants even if the shell exits first.
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "sh" : "setsid")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!OperatingSystem.IsWindows()) start.ArgumentList.Add("sh");
            start.ArgumentList.Add(script);
            foreach (var (name, value) in values) start.Environment[name] = value;
            var marker = RunMarker + "=" + Guid.NewGuid().ToString("N");
            start.Environment[RunMarker] = marker[(RunMarker.Length + 1)..];
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Runbook step could not start.");
            using var drainStop = new CancellationTokenSource();
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, drainStop.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, drainStop.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                // A child can inherit a redirected pipe after the script exits.
                await Task.WhenAll(output, error).WaitAsync(timeout.Token);
                results.Add(new(index, process.ExitCode == 0));
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process, marker, output, error, drainStop);
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    /// <summary>
    /// Kills the script, its process group and every descendant that carries the
    /// step marker, then waits a bounded time for exit and for the output drains.
    /// </summary>
    private static async Task StopAsync(Process process, string marker, Task output, Task error,
        CancellationTokenSource drainStop)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        else
        {
            SendSignal(-process.Id, SigKill);
            if (Directory.Exists("/proc")) await KillMarkedProcessesAsync(marker);
            else if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), output, error).WaitAsync(CleanupTimeout);
            return;
        }
        catch (TimeoutException)
        {
            // A descendant that dropped the marker can still hold a pipe; stop reading it.
            drainStop.Cancel();
        }
        try
        {
            await Task.WhenAll(output, error).WaitAsync(CleanupTimeout);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Disposing the process closes the pipe ends this host still holds.
            process.Dispose();
        }
    }

    /// <summary>
    /// A descendant can call setsid and leave the script's process group, so the
    /// sweep finds processes by the inherited marker and by parent, and repeats
    /// until no live one remains or the bounded rounds end.
    /// </summary>
    private static async Task KillMarkedProcessesAsync(string marker)
    {
        for (var round = 0; round < SweepRounds; round++)
        {
            var targets = new HashSet<int>();
            var parents = new List<(int Id, int Parent)>();
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id == Environment.ProcessId || ReadProc(directory, "stat") is not { } stat)
                    continue;
                var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || fields[0] == "Z") continue;
                if (int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
                    parents.Add((id, parent));
                if (ReadProc(directory, "environ") is { } environ
                    && environ.Split('\0').Contains(marker, StringComparer.Ordinal))
                    targets.Add(id);
            }
            bool added;
            do
            {
                added = false;
                foreach (var (id, parent) in parents)
                    added |= targets.Contains(parent) && targets.Add(id);
            }
            while (added);
            if (targets.Count == 0) return;
            foreach (var id in targets) SendSignal(id, SigKill);
            await Task.Delay(SweepInterval);
        }
    }

    private static string? ReadProc(string directory, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(directory, name));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The process exited or belongs to another user.
            return null;
        }
    }

    private static bool AllowedName(string name)
        => VariableName(name) && !name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal)
           && !ProjectDeploymentVocabulary.IsReservedVariableName(name);

    private static bool Identifier(string value)
        => value.Length is > 0 and <= 64 && value[0] is >= 'a' and <= 'z'
           && value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character)
               || character is '.' or '_' or '-');

    private static bool VariableName(string value)
        => value.Length is > 0 and <= 128 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool SecretIdentifier(string value)
        => value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0])
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int SendSignal(int processId, int signal);
}
