using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>Unsupported local lint execution stays an infrastructure wait, never a code failure.</summary>
public sealed class RemoteRequiredLintScssRunner : ILintScssRunner
{
    public Task<LintScssResult> RunAsync(string repositoryPath, PostStepMode mode, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (mode == PostStepMode.Off)
            return Task.FromResult(new LintScssResult(LintScssVerdict.Skipped, null, 0, "", "mode=off"));
        if (FrontendStylelintCommand.ResolveWorkspace(repositoryPath) is null)
            return Task.FromResult(new LintScssResult(LintScssVerdict.Skipped, null, 0, "", "no frontend/ workspace"));
        return Task.FromResult(new LintScssResult(LintScssVerdict.Fail, null, 0,
            RemoteExecutionRequirement.Message, RemoteExecutionRequirement.Code)
        {
            InfrastructureFailureCode = RemoteExecutionRequirement.Code
        });
    }
}
