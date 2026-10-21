using System.Diagnostics;
using System.Globalization;
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
    private const int MaxTerminationSweeps = 20;
    /// <summary>Inherited by every descendant, including one that starts a new session.</summary>
    private const string RunMarkerVariable = "AGENT_STUDIO_RUNBOOK_RUN";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

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
            [RunMarkerVariable] = Guid.NewGuid().ToString("N"),
        };
        foreach (var (name, value) in variables)
        {
            if (!VariableName(name) || name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal)
                || ProjectDeploymentVocabulary.IsReservedVariableName(name))
                throw new InvalidOperationException("Invalid runbook variable name.");
            values[name] = value;
        }
        foreach (var (name, secret) in secretVariables)
        {
            if (!VariableName(name) || name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal)
                || ProjectDeploymentVocabulary.IsReservedVariableName(name)
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
            // An absolute shell path keeps the inherited PATH from choosing the interpreter.
            if (!OperatingSystem.IsWindows()) start.ArgumentList.Add("/bin/sh");
            start.ArgumentList.Add(script);
            foreach (var (name, value) in values) start.Environment[name] = value;
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
                Terminate(process, values[RunMarkerVariable]);
                // A survivor that holds a pipe must not hang execution; the bounds end cleanup either way.
                await SettleAsync(process.WaitForExitAsync(CancellationToken.None));
                await SettleAsync(Task.WhenAll(output, error));
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    private static void Terminate(Process process, string runMarker)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return;
        }
        if (SendSignal(-process.Id, SigKill) != 0 && !process.HasExited) process.Kill(entireProcessTree: true);
        // A descendant that called setsid left the process group but still carries the run marker.
        // Repeat until a sweep finds nothing, so a child forked during a sweep is caught by the next.
        for (var sweep = 0; sweep < MaxTerminationSweeps; sweep++)
            if (KillMarkedProcesses(runMarker) == 0) return;
    }

    private static int KillMarkedProcesses(string runMarker)
    {
        if (!Directory.Exists("/proc")) return 0;
        var entry = Encoding.ASCII.GetBytes(RunMarkerVariable + "=" + runMarker + "\0");
        var killed = 0;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id == Environment.ProcessId)
                continue;
            byte[] environment;
            try
            {
                environment = File.ReadAllBytes(Path.Combine(directory, "environ"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The process exited during the sweep or belongs to another user; neither is a runbook descendant.
                continue;
            }
            if (HasEntry(environment, entry) && SendSignal(id, SigKill) == 0) killed++;
        }
        return killed;
    }

    private static bool HasEntry(ReadOnlySpan<byte> environment, ReadOnlySpan<byte> entry)
    {
        for (var start = 0; start < environment.Length;)
        {
            if (environment[start..].StartsWith(entry)) return true;
            var next = environment[start..].IndexOf((byte)0);
            if (next < 0) return false;
            start += next + 1;
        }
        return false;
    }

    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task.WaitAsync(CleanupTimeout);
        }
        catch (TimeoutException)
        {
            // The step already failed; an unreaped survivor must not hold the host.
            return;
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
