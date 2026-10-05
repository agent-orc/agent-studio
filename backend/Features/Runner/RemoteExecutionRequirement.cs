namespace AgentStudio.Runner;

/// <summary>Stable infrastructure reason for work that must execute away from the orchestrator.</summary>
public static class RemoteExecutionRequirement
{
    public const string Code = "remote-execution-required";
    public const string Message = "This operation requires a remote executor; local build, test and coding execution is disabled on the orchestrator.";
}

/// <summary>Explicit injection seam for deterministic runner tests. Production defaults to deny.</summary>
public interface ILocalCodingAdmissionPolicy
{
    bool AllowsLocalCoding { get; }
}

/// <summary>Stops a post-step without accepting the card or consuming its code reissue budget.</summary>
internal sealed class RemoteVerificationRequiredException(string failureCode, string message) : Exception(message)
{
    public string FailureCode { get; } = failureCode;
}

/// <summary>Until onboarding validation has a remote protocol, fail closed without spawning a shell.</summary>
public sealed class RemoteRequiredBuildCommandRunner : IBuildCommandRunner
{
    public Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new BuildCommandResult(-1, RemoteExecutionRequirement.Message)
        {
            FailureCode = RemoteExecutionRequirement.Code
        });
    }
}
