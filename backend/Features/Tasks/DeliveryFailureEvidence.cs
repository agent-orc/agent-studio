using System.Text;
using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>Unabridged receipt facts shared by manual and automatic fix rounds.</summary>
internal static class DeliveryFailureEvidence
{
    internal const string RelativePath = "attachments/gate-failure.md";
    internal const string Heading = "## Delivery failure evidence";

    internal static string Gate(BuildTestGateResult gate)
    {
        var text = new StringBuilder($"{Heading}\n\nTested SHA: {gate.TestedSha ?? "not recorded"}\n\n{gate.Reason}\n");
        foreach (var process in gate.Processes.Where(process => process.ExitCode != 0))
        {
            text.AppendLine($"\nCommand: {process.Command}");
            var output = process.StandardOutput + "\n" + process.StandardError;
            foreach (var test in (process.TestReport?.FailedTests ?? []).Concat(process.FailureMessages.Keys)
                         .Concat(GateFlakyRerunPolicy.ParseFailedTests(output)).Distinct(StringComparer.Ordinal))
            {
                var error = process.TestReport?.FailureMessages.GetValueOrDefault(test)
                    ?? process.FailureMessages.GetValueOrDefault(test) ?? FirstError(output, test);
                text.AppendLine($"- {test}: {error}");
            }
            if (process.TestReport?.FailedTests.Count is not > 0)
                text.AppendLine($"First error: {process.FirstError ?? FirstError(output, null)}");
        }
        var reported = gate.Processes.SelectMany(process => (process.TestReport?.FailedTests ?? [])
            .Concat(process.FailureMessages.Keys).Concat(GateFlakyRerunPolicy.ParseFailedTests(process.StandardOutput + "\n" + process.StandardError))).ToHashSet(StringComparer.Ordinal);
        foreach (var test in BatchBaseRedPolicy.FailedTests(gate).Where(test => !reported.Contains(test)))
            text.AppendLine($"- {test}: {FirstError(gate.Output, test)}");
        return text.ToString();
    }

    internal static string FirstError(string output, string? test)
    {
        return Contract.TestFailureMessages.FirstError(output, test);
    }

    internal static void RecordGate(string folder, BuildTestGateResult gate)
    {
        if (gate.Verdict == BuildTestGateVerdict.Fail) Write(folder, Gate(gate));
        else if (gate.Verdict == BuildTestGateVerdict.Ok) Clear(folder);
    }

    internal static void RecordReview(string folder, Contract.ReviewReportRequest request)
    {
        var failed = request.Commands.Where(command => command.ExitCode != 0
            && command.WorkspaceRole == "candidate" && !Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)).ToArray();
        if (failed.Length == 0) return;
        var text = new StringBuilder(Heading + "\n");
        foreach (var command in failed)
        {
            text.AppendLine($"\nTested SHA: {command.HeadBefore}\nCommand: {command.FileName} {string.Join(" ", command.Arguments)}");
            var output = command.OutputTail?.Stdout + "\n" + command.OutputTail?.Stderr;
            var tests = (command.NewFailures ?? []).Concat(command.PreExistingFailures ?? [])
                .Concat(command.InitialFailures ?? []).Concat(command.FailureMessages?.Keys ?? [])
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var test in tests)
                text.AppendLine($"- {test}: {command.FailureMessages?.GetValueOrDefault(test) ?? FirstError(output, test)}");
            if (tests.Length == 0) text.AppendLine($"First error: {FirstError(output, null)}");
        }
        Write(folder, text.ToString());
    }

    internal static void Write(string folder, string text)
    {
        var path = Path.Combine(folder, RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    internal static string? Read(string folder)
    {
        var path = Path.Combine(folder, RelativePath);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "DeliveryFailureEvidence: input unavailable");
            return null;
        }
    }

    internal static string Append(string folder, string prompt)
    {
        var path = Path.Combine(folder, RelativePath);
        if (!File.Exists(path)) RecoverParkedReceipt(folder);
        if (!File.Exists(path)) return prompt;
        var evidence = File.ReadAllText(path);
        if (prompt.Contains(evidence, StringComparison.Ordinal)) return prompt;
        return prompt.TrimEnd() + $"\n\nReceipt facts are also available in {RelativePath}.\n\n" + evidence;
    }

    private static void RecoverParkedReceipt(string folder)
    {
        var reason = ParkedBlockerMarker.TryRead(folder)?.Reason;
        var marker = reason?.LastIndexOf("Evidence: ", StringComparison.Ordinal) ?? -1;
        if (marker < 0) return;
        var receipt = reason![(marker + "Evidence: ".Length)..].Trim();
        try
        {
            if (!File.Exists(receipt)) return;
            var line = File.ReadLines(receipt).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var gate = line is null ? null : System.Text.Json.JsonSerializer.Deserialize<BuildTestGateResult>(line,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (gate?.Verdict == BuildTestGateVerdict.Fail) RecordGate(folder, gate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SilentCatch.Note(ex, "DeliveryFailureEvidence: parked receipt unavailable");
        }
    }

    private static void Clear(string folder)
    {
        var path = Path.Combine(folder, RelativePath);
        if (File.Exists(path)) File.Delete(path);
    }
}
