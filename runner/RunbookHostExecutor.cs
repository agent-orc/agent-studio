using System.Diagnostics;
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
    private const string StepMarkerVariable = "AGENT_STUDIO_RUNBOOK_STEP";
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
        };
        foreach (var (name, value) in variables)
        {
            if (!VariableName(name) || ProjectDeploymentVocabulary.IsHostControlledVariable(name))
                throw new InvalidOperationException("Invalid runbook variable name.");
            values[name] = value;
        }
        foreach (var (name, secret) in secretVariables)
        {
            if (!VariableName(name) || ProjectDeploymentVocabulary.IsHostControlledVariable(name)
                || !SecretIdentifier(secret) || values.ContainsKey(name))
                throw new InvalidOperationException("Invalid runbook secret reference.");
            values[name] = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), cancellationToken))
                .TrimEnd('\r', '\n');
        }
        foreach (var secret in environmentSecretRefs)
        {
            if (!SecretIdentifier(secret)) throw new InvalidOperationException("Invalid environment secret reference.");
            var name = secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant();
            if (ProjectDeploymentVocabulary.IsHostControlledVariable(name))
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
            // Absolute launcher paths keep the inherited PATH from choosing the shell.
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
            // Descendants that call setsid leave the step's process group but keep this marker.
            var marker = Guid.NewGuid().ToString("N");
            start.Environment[StepMarkerVariable] = marker;
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
                await TerminateAsync(process, marker, Task.WhenAll(output, error));
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    /// <summary>
    /// Kills the step's process group and every process that inherited its
    /// marker, including descendants in a new session, then waits a bounded time
    /// for exit and for the output pipes to close. Pipes a survivor still holds
    /// are closed when the process handle is disposed.
    /// </summary>
    private static async Task TerminateAsync(Process process, string marker, Task drain)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        if (OperatingSystem.IsWindows())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        else
        {
            ProcessSignalGuard.TrySignalProcessGroup(process.Id, SigKill, "runbook-step-cancel");
            // Repeat until no marked process is left, so a child forked during the sweep is caught too.
            while (!cleanup.IsCancellationRequested)
            {
                var survivors = FindByMarker(marker);
                if (survivors.Count == 0) break;
                foreach (var pid in survivors) ProcessSignalGuard.TrySignal(pid, SigKill, "runbook-step-cancel");
                await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            }
        }
        try
        {
            await process.WaitForExitAsync(cleanup.Token);
            await drain.WaitAsync(cleanup.Token);
        }
        catch (OperationCanceledException)
        {
            // The drain faults once the handle is disposed; observe it instead of awaiting it.
            _ = drain.ContinueWith(static task => task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    internal static IReadOnlyList<int> FindByMarker(string marker)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc")) return [];
        var needle = Encoding.ASCII.GetBytes(StepMarkerVariable + "=" + marker + "\0");
        var result = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == Environment.ProcessId) continue;
            byte[] environment;
            try
            {
                environment = File.ReadAllBytes(Path.Combine(directory, "environ"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The process exited during the sweep or belongs to another user.
                environment = [];
            }
            if (environment.AsSpan().IndexOf(needle) >= 0) result.Add(pid);
        }
        return result;
    }

    private static string SetsidPath()
        => new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists) ?? "setsid";

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
}
