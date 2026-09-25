using System.Text;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;

namespace AgentStudio.Operations.Server.Features.Dispatch;

public static class OperationPolicy
{
    public static string? Admission(OperationsPrincipal principal, OperationCommand command, DateTimeOffset now)
    {
        var definition = OperationCatalogue.Find(command.OperationId, command.Version);
        if (definition is null) return "unknown-operation";
        var access = OperationsAccessPolicy.Denial(principal, "service", definition.Scope, command.AgentId);
        if (access is not null) return access;
        if (!ValidText(command.IdempotencyKey) || !ValidText(command.Actor) || !ValidText(command.CorrelationId)
            || !ValidText(command.AgentId)) return "invalid-command";
        if (command.Deadline <= now || command.Deadline > now.AddMinutes(5)) return "invalid-deadline";
        if (command.Input.ValueKind != JsonValueKind.Object || command.Input.EnumerateObject().Any()) return "invalid-input";
        if (command.InputDigest != OperationsProtocol.Digest(command.Input)) return "input-digest-mismatch";
        if (command.Subject is not null && (!ValidText(command.Subject.TaskId) || !ValidText(command.Subject.RunOrReviewId)
            || command.Subject.Fence < 1 || !ValidText(command.Permit, 8192))) return "permit-required";
        if (command.Subject is null && (command.Permit is not null
            || !principal.Scopes.Contains("maintenance.execute", StringComparer.Ordinal))) return "maintenance-scope-required";
        return null;
    }

    public static bool Eligible(AgentRegistration agent, OperationCommand command, DateTimeOffset now) =>
        agent.LastSeen > now.AddSeconds(-45)
        && agent.Capability.ProtocolMin <= OperationsProtocol.Version
        && agent.Capability.ProtocolMax >= OperationsProtocol.Version
        && agent.Capability.Operations.TryGetValue(command.OperationId, out var version) && version == command.Version;

    public static string? LeaseDenial(OperationAttempt attempt, string agentId, string bootId, long fence, DateTimeOffset now)
    {
        if (attempt.AgentId != agentId || attempt.BootId != bootId || attempt.Fence != fence) return "stale-attempt";
        if (attempt.State is not ("running" or "cancelling")) return "attempt-not-running";
        if (attempt.LeaseUntil <= now || attempt.Command.Deadline <= now) return "lease-expired";
        return null;
    }

    public static string? ResultDenial(OperationAttempt attempt, OperationResult result, DateTimeOffset now)
    {
        if (result.Outcome is not ("succeeded" or "failed" or "cancelled")) return "invalid-outcome";
        if (attempt.State == "cancelling" && result.Outcome != "cancelled") return "cancellation-required";
        if (result.SubjectDigest != attempt.Command.InputDigest || !result.CleanupConfirmed) return "invalid-result-proof";
        if (result.StartedAt > result.FinishedAt || result.FinishedAt > now
            || result.StartedAt < attempt.Events[0].At) return "invalid-result-time";
        if (!ValidText(result.ExitClassification) || result.Artifacts is null || result.Artifacts.Length != 0) return "invalid-result";
        if (result.Output.ValueKind != JsonValueKind.Object) return "invalid-output";
        if (result.Outcome == "succeeded")
        {
            if (result.Output.EnumerateObject().Count() != 3
                || !result.Output.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.String || !ValidText(os.GetString())
                || !result.Output.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.String || !ValidText(architecture.GetString(), 64)
                || !result.Output.TryGetProperty("processors", out var processors) || processors.ValueKind != JsonValueKind.Number || !processors.TryGetInt32(out var count) || count < 1 || count > 16384)
                return "invalid-output";
        }
        else if (result.Output.EnumerateObject().Any()) return "invalid-output";
        // host.inspect has no artifact channel. Never accept arbitrary paths or URLs.
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, OperationsProtocol.Json)) > OperationCatalogue.HostInspect.MaxOutputBytes)
            return "output-limit";
        return null;
    }

    public static bool ValidText(string? value, int max = 256) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
}
