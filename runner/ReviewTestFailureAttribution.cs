using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Evidence required to charge a backend test failure to a delivery.</summary>
internal static class ReviewTestFailureAttribution
{
    internal static bool IsTestCommand(ReviewCommandDto command)
    {
        var invocation = (command.FileName + " " + string.Join(' ', command.Arguments))
            .Replace('\\', '/');
        return invocation.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("node --test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("npm test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("ng test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("run test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("jest", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("vitest", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBackendTestCommand(ReviewCommandDto command)
        => (command.FileName + " " + string.Join(' ', command.Arguments))
            .Replace('\\', '/')
            .Contains("backend.Tests/", StringComparison.OrdinalIgnoreCase);

    internal static bool BackendChanged(IReadOnlyList<string> changedPaths)
        => changedPaths.Any(path => path.Replace('\\', '/')
            .StartsWith("backend/", StringComparison.Ordinal));

    internal static DeliveryFailureDiagnosisResult? Diagnose(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string>? cleanRepeat,
        IReadOnlyList<string> baseline,
        int baselineExitCode,
        bool backendTest,
        bool? backendChanged)
    {
        if (backendTest && backendChanged is null)
            return new DeliveryFailureDiagnosisResult(
                DeliveryFailureDiagnosis.FirstOccurrence, 0.2,
                ["The backend diff could not be read; a backend test failure cannot be attributed without it."]);
        if (backendTest && backendChanged == false)
            return Environment("The delivery changes no file under backend/; a backend test failure cannot be attributed to this delivery by test output alone.");

        if (cleanRepeat is { Count: > 0 } && !Same(candidate, cleanRepeat))
            return Environment("Candidate and clean repeat failed different tests; the failing set varies on the review host.");

        if (backendTest && baselineExitCode != 0 && !Same(candidate, baseline))
            return Environment("The integration baseline also failed, and its failing set differs from the candidate; the review host failure set varies.");

        return null;
    }

    private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private static DeliveryFailureDiagnosisResult Environment(string reason)
        => new(DeliveryFailureDiagnosis.Environment, 0.9,
            [ReviewUnattributedTestFailurePolicy.EvidencePrefix + reason]);
}
