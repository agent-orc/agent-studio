using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(50);
    /// <summary>Every script process inherits this; a descendant in its own session still carries it.</summary>
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
            [RunMarker] = Guid.NewGuid().ToString("N"),
        };
        foreach (var (name, value) in variables)
        {
            if (!VariableName(name) || ProjectDeploymentVocabulary.IsReservedVariableName(name))
                throw new InvalidOperationException("Invalid runbook variable name.");
            values[name] = value;
        }
        foreach (var (name, secret) in secretVariables)
        {
            if (!VariableName(name) || ProjectDeploymentVocabulary.IsReservedVariableName(name)
                || !SecretIdentifier(secret) || values.ContainsKey(name))
                throw new InvalidOperationException("Invalid runbook secret reference.");
            values[name] = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), cancellationToken))
                .TrimEnd('\r', '\n');
        }
        foreach (var secret in environmentSecretRefs)
        {
            if (!SecretIdentifier(secret)) throw new InvalidOperationException("Invalid environment secret reference.");
            var name = secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant();
            if (ProjectDeploymentVocabulary.IsReservedVariableName(name))
                throw new InvalidOperationException("Invalid environment secret reference.");
            if (values.ContainsKey(name)) continue;
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
            // A separate session lets cancellation signal the group even if the shell exits first.
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "sh" : "setsid")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!OperatingSystem.IsWindows()) start.ArgumentList.Add("/bin/sh");
            start.ArgumentList.Add(script);
            foreach (var (name, value) in values) start.Environment[name] = value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Runbook step could not start.");
            using var drain = new CancellationTokenSource();
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, drain.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, drain.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                // A child can inherit a redirected pipe after the script exits.
                await Task.WhenAll(output, error).WaitAsync(timeout.Token);
                results.Add(new(index, process.ExitCode == 0));
            }
            catch (OperationCanceledException)
            {
                await TerminateAsync(process, values[RunMarker], drain, output, error);
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    /// <summary>
    /// Kills the script, its process group, and every process that carries this
    /// run's marker or descends from one, until none remains or the cleanup bound
    /// passes. Then waits for the script and its output pipes within that bound.
    /// </summary>
    private static async Task TerminateAsync(Process process, string marker, CancellationTokenSource drain,
        Task output, Task error)
    {
        var started = Stopwatch.GetTimestamp();
        if (OperatingSystem.IsWindows())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        else
        {
            if (!process.HasExited && SendSignal(-process.Id, SigKill) != 0 && !process.HasExited)
                process.Kill(entireProcessTree: true);
            while (KillMarkedProcesses(marker) > 0 && Stopwatch.GetElapsedTime(started) < CleanupTimeout)
                await Task.Delay(SweepInterval, CancellationToken.None);
        }
        var remaining = CleanupTimeout - Stopwatch.GetElapsedTime(started);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(Max(remaining, SweepInterval));
            remaining = CleanupTimeout - Stopwatch.GetElapsedTime(started);
            await Task.WhenAll(output, error).WaitAsync(Max(remaining, SweepInterval));
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Runbook step cleanup passed its bound; its output pipes are abandoned.");
        }
        drain.Cancel();
        try
        {
            await Task.WhenAll(output, error).WaitAsync(SweepInterval);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            Console.Error.WriteLine($"Runbook step output drain stopped: {ex.GetType().Name}");
        }
    }

    private static TimeSpan Max(TimeSpan value, TimeSpan floor) => value > floor ? value : floor;

    /// <summary>Signals live processes of this run; returns how many were still alive.</summary>
    private static int KillMarkedProcesses(string marker)
    {
        var entry = Encoding.ASCII.GetBytes($"{RunMarker}={marker}\0");
        var processes = new Dictionary<int, int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var id) || id == Environment.ProcessId) continue;
            if (ReadStat(directory) is { } stat && stat.State != 'Z') processes[id] = stat.ParentId;
        }
        var marked = processes.Keys.Where(id => CarriesMarker(id, entry)).ToHashSet();
        // A descendant that cleared its environment still has a marked ancestor while that ancestor lives.
        for (var added = true; added;)
        {
            added = false;
            foreach (var (id, parent) in processes)
                if (!marked.Contains(id) && marked.Contains(parent)) added = marked.Add(id) || added;
        }
        foreach (var id in marked) SendSignal(id, SigKill);
        return marked.Count;
    }

    private static (char State, int ParentId)? ReadStat(string directory)
    {
        try
        {
            // pid (comm) state ppid ...; comm may itself contain spaces and parentheses.
            var stat = File.ReadAllText(Path.Combine(directory, "stat"));
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return (fields[0][0], int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The process exited between enumeration and read, or belongs to another user.
            return null;
        }
    }

    private static bool CarriesMarker(int id, byte[] entry)
    {
        try
        {
            var environment = File.ReadAllBytes($"/proc/{id}/environ");
            return environment.AsSpan().IndexOf(entry) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
