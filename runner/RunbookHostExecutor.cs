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
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private const string RunMarker = "AGENT_STUDIO_RUNBOOK_STEP";

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
            // Descendants inherit the marker even after they leave the session.
            var marker = Guid.NewGuid().ToString("N");
            start.Environment[RunMarker] = marker;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Runbook step could not start.");
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                // A child can inherit a redirected pipe after the script exits.
                await Task.WhenAll(output, error).WaitAsync(timeout.Token);
                results.Add(new(index, process.ExitCode == 0));
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process, marker, output, error);
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    /// <summary>
    /// Kills the script, its session, and every descendant that carries the step
    /// marker, including ones that started a new session. Waiting for the exit
    /// and for the output drain is bounded; an unfinished drain is abandoned.
    /// </summary>
    private static async Task StopAsync(Process process, string marker, Task output, Task error)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        if (OperatingSystem.IsWindows())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        else
        {
            if (SendSignal(-process.Id, SigKill) != 0 && !process.HasExited) process.Kill(entireProcessTree: true);
            // Repeat until no marked process is left, so a fork in flight is caught too.
            while (!cleanup.IsCancellationRequested)
            {
                var survivors = MarkedProcesses(marker, process.Id);
                if (survivors.Count == 0) break;
                foreach (var id in survivors) SendSignal(id, SigKill);
                await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
            }
        }
        try
        {
            await process.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(output, error).WaitAsync(cleanup.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Runbook step process {process.Id} did not release its output after cleanup.");
        }
    }

    /// <summary>Live processes in the script's session or with the step marker in their environment.</summary>
    private static IReadOnlyList<int> MarkedProcesses(string marker, int session)
    {
        var found = new List<int>();
        if (!Directory.Exists("/proc")) return found;
        var needle = $"{RunMarker}={marker}";
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id == Environment.ProcessId)
                continue;
            try
            {
                // Fields after the command name: state, ppid, pgrp, session.
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields[0] == "Z") continue;
                if (fields[3] == session.ToString(CultureInfo.InvariantCulture)
                    || File.ReadAllText(Path.Combine(directory, "environ")).Split('\0').Contains(needle))
                    found.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process exited or belongs to another user; neither came from this step.
                continue;
            }
        }
        return found;
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
