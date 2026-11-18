using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <param name="Output">The masked, bounded tail of the step's stdout and stderr, one line per entry.</param>
public sealed record RunbookHostStepResult(int Index, bool Success, string? Output = null);
public sealed record RunbookHostResult(bool Success, IReadOnlyList<RunbookHostStepResult> Steps);

/// <summary>
/// Runs operator-installed scripts by identifier on the assigned deploy host.
/// No repository command or shell text arrives from project.yml. Script output
/// returns only as a bounded tail per step with every secret value masked.
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
        var mask = SecretLiterals(values, secretVariables.Keys.Concat(environmentSecretRefs
            .Select(secret => secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant())));
        var results = new List<RunbookHostStepResult>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RunTimeout);
        for (var index = 0; index < steps.Count; index++)
        {
            if (deadline.IsCancellationRequested)
            {
                results.Add(new(index, false, "The runbook time limit ended before this step started."));
                break;
            }
            var script = Path.Combine(scriptRoot, project, steps[index] + ".sh");
            if (!File.Exists(script))
            {
                results.Add(new(index, false, $"Script {script} is not installed on this host."));
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
            var tail = new StepOutput(mask);
            var output = tail.DrainAsync(process.StandardOutput);
            var error = tail.DrainAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                // A child can inherit a redirected pipe after the script exits.
                await Task.WhenAll(output, error).WaitAsync(timeout.Token);
                if (process.ExitCode != 0) tail.Note($"Exit code {process.ExitCode}.");
                results.Add(new(index, process.ExitCode == 0, tail.Text()));
            }
            catch (OperationCanceledException)
            {
                Terminate(process, values[RunMarkerVariable]);
                // A survivor that holds a pipe must not hang execution; the bounds end cleanup either way.
                await SettleAsync(process.WaitForExitAsync(CancellationToken.None));
                await SettleAsync(Task.WhenAll(output, error));
                cancellationToken.ThrowIfCancellationRequested();
                tail.Note("The step was stopped at its time limit.");
                results.Add(new(index, false, tail.Text()));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
    }

    /// <summary>Every secret value the scripts receive, and each line of a multi-line one, longest first.</summary>
    private static string[] SecretLiterals(IReadOnlyDictionary<string, string> values, IEnumerable<string> names)
        => names.Distinct(StringComparer.Ordinal).Where(values.ContainsKey).Select(name => values[name])
            .SelectMany(value => value.Split('\n').Select(line => line.TrimEnd('\r')).Append(value))
            .Where(value => value.Length >= StepOutput.MinimumSecretLength).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length).ToArray();

    /// <summary>
    /// The last <see cref="MaxLines"/> lines of stdout and stderr, interleaved as they arrive.
    /// Each line is masked before it is cut, so a cut never exposes the start of a secret.
    /// </summary>
    internal sealed class StepOutput(IReadOnlyList<string> secrets)
    {
        public const int MaxLines = 200;
        public const int MaxLineLength = 400;
        public const int MinimumSecretLength = 4;
        /// <summary>Raw characters kept per line before masking; longer lines are cut here.</summary>
        private const int MaxRawLineLength = 8192;
        private const string Masked = "[masked]";
        private readonly object _gate = new();
        private readonly Queue<string> _lines = new();
        private int _omitted;

        public async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        Add(line.ToString());
                        line.Clear();
                    }
                    else if (line.Length < MaxRawLineLength) line.Append(buffer[i]);
                }
            }
            if (line.Length > 0) Add(line.ToString());
        }

        public void Note(string message) => Add(message);

        public string? Text()
        {
            lock (_gate)
            {
                if (_lines.Count == 0) return null;
                var lines = _omitted > 0 ? _lines.Prepend($"({_omitted} earlier lines omitted)") : _lines;
                return string.Join('\n', lines);
            }
        }

        private void Add(string raw)
        {
            var text = raw.TrimEnd('\r');
            foreach (var secret in secrets) text = text.Replace(secret, Masked, StringComparison.Ordinal);
            text = new string(text.Select(ch => ch == '\t' ? ' ' : ch).Where(ch => !char.IsControl(ch)).ToArray());
            if (text.Length > MaxLineLength) text = text[..MaxLineLength] + "…";
            lock (_gate)
            {
                _lines.Enqueue(text);
                if (_lines.Count <= MaxLines) return;
                _lines.Dequeue();
                _omitted++;
            }
        }
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
