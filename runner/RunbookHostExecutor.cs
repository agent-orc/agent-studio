using System.Diagnostics;

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
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

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
            if (!VariableName(name) || name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid runbook variable name.");
            values[name] = value;
        }
        foreach (var (name, secret) in secretVariables)
        {
            if (!VariableName(name) || name.StartsWith("AGENT_STUDIO_", StringComparison.Ordinal)
                || !SecretIdentifier(secret) || values.ContainsKey(name))
                throw new InvalidOperationException("Invalid runbook secret reference.");
            values[name] = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), cancellationToken))
                .TrimEnd('\r', '\n');
        }
        foreach (var secret in environmentSecretRefs)
        {
            if (!SecretIdentifier(secret)) throw new InvalidOperationException("Invalid environment secret reference.");
            var name = secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant();
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
            var start = new ProcessStartInfo("sh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(script);
            foreach (var (name, value) in values) start.Environment[name] = value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Runbook step could not start.");
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, error);
                results.Add(new(index, process.ExitCode == 0));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                results.Add(new(index, false));
            }
            if (!results[^1].Success) break;
        }
        return new(results.Count == steps.Count && results.All(result => result.Success), results);
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
}
