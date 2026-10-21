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
    private const int MaxKillRounds = 100;
    private const string StepMarkerVariable = "AGENT_STUDIO_RUNBOOK_STEP";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

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
            if (!VariableName(name) || name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal)
                || ProjectDeploymentVocabulary.IsProcessControlVariable(name))
                throw new InvalidOperationException("Invalid runbook variable name.");
            values[name] = value;
        }
        foreach (var (name, secret) in secretVariables)
        {
            if (!VariableName(name) || name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal)
                || ProjectDeploymentVocabulary.IsProcessControlVariable(name)
                || !SecretIdentifier(secret) || values.ContainsKey(name))
                throw new InvalidOperationException("Invalid runbook secret reference.");
            values[name] = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), cancellationToken))
                .TrimEnd('\r', '\n');
        }
        foreach (var secret in environmentSecretRefs)
        {
            if (!SecretIdentifier(secret)) throw new InvalidOperationException("Invalid environment secret reference.");
            var name = secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant();
            if (ProjectDeploymentVocabulary.IsProcessControlVariable(name))
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
            // A separate session lets cancellation signal descendants even if the shell exits first.
            // Absolute launcher paths keep the inherited PATH away from the shell choice.
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "sh" : SetsidPath())
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!OperatingSystem.IsWindows()) start.ArgumentList.Add("/bin/sh");
            start.ArgumentList.Add(script);
            foreach (var (name, value) in values) start.Environment[name] = value;
            // Every descendant inherits the marker, including one that starts its own session.
            var marker = Guid.NewGuid().ToString("N");
            start.Environment[StepMarkerVariable] = marker;
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
                Terminate(process, marker);
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                try
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    await Task.WhenAll(output, error).WaitAsync(cleanup.Token);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    // A holder that survived the kill rounds must not block the host; abandon its pipes.
                    drainStop.Cancel();
                }
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    private static void Terminate(Process process, string marker)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return;
        }
        if (SendSignal(-process.Id, SigKill) != 0 && !process.HasExited) process.Kill(entireProcessTree: true);
        if (!OperatingSystem.IsLinux()) return;
        // A descendant that called setsid left the process group; find it by the inherited marker.
        // Repeat until a scan finds none, so a child forked during a round is caught by the next.
        var entry = Encoding.ASCII.GetBytes($"{StepMarkerVariable}={marker}");
        for (var round = 0; round < MaxKillRounds; round++)
        {
            var found = MarkedProcesses(entry);
            if (found.Count == 0) return;
            foreach (var id in found) SendSignal(id, SigKill);
        }
    }

    private static List<int> MarkedProcesses(byte[] entry)
    {
        var found = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var id) || id == Environment.ProcessId) continue;
            var environment = ReadEnvironment(directory);
            if (environment is not null && HasEntry(environment, entry)) found.Add(id);
        }
        return found;
    }

    /// <summary>Null when the process exited or belongs to another user; a zombie reads empty.</summary>
    private static byte[]? ReadEnvironment(string directory)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(directory, "environ"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasEntry(ReadOnlySpan<byte> environment, ReadOnlySpan<byte> entry)
    {
        foreach (var range in environment.Split((byte)0))
            if (environment[range].SequenceEqual(entry)) return true;
        return false;
    }

    private static string SetsidPath()
        => File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";

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
