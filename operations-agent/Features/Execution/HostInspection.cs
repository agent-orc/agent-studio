using System.Runtime.InteropServices;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Agent.Features.Execution;

public static class HostInspection
{
    public static OperationResult Execute(OperationAttempt attempt, string agentId, string bootId, DateTimeOffset now)
    {
        if (attempt.AgentId != agentId || attempt.BootId != bootId || attempt.Fence < 1
            || attempt.State is not ("running" or "cancelling") || attempt.LeaseUntil is null || attempt.LeaseUntil <= now
            || attempt.Command.Deadline <= now || attempt.Command.OperationId != "host.inspect" || attempt.Command.Version != 1
            || attempt.Command.Input.ValueKind != JsonValueKind.Object || attempt.Command.Input.EnumerateObject().Any()
            || OperationsProtocol.Digest(attempt.Command.Input) != attempt.Command.InputDigest)
            throw new InvalidOperationException("The assignment is outside the agent capability or lease.");
        var cancelled = attempt.State == "cancelling";
        var output = cancelled ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new
        {
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            processors = Environment.ProcessorCount,
        });
        return new OperationResult(cancelled ? "cancelled" : "succeeded", attempt.Command.InputDigest,
            cancelled ? "cancelled-before-execution" : "completed", output, [], true, now, now);
    }
}
