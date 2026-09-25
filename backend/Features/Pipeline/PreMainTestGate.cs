namespace AgentStudio.Pipeline;

/// <summary>
/// Hard release boundary for every connected merge-to-main workflow. This
/// wrapper overwrites both lane configuration and caller input with
/// <c>full</c>; no diff, Test Hub signal, or LLM answer can reduce the suite.
/// The configured integration merge invokes this boundary whenever its target
/// resolves to <c>main</c>; future release workflows must use the same entry
/// point.
/// </summary>
public sealed class PreMainTestGate
{
    private readonly IBuildTestGateRunner _runner;

    public PreMainTestGate(IBuildTestGateRunner runner) => _runner = runner;

    public async Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        BuildProfile? profile,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            request with
            {
                RequireExactSubject = true,
                RequiredTestLevel = TestExecutionLevels.Full,
            },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            timeout,
            ct).ConfigureAwait(false);

        if (result.Verdict != BuildTestGateVerdict.Ok)
            return result.FailureKind == BuildTestGateFailureKind.Code
                   && result.Diagnosis?.ChargesCard != true
                ? result with { FailureKind = BuildTestGateFailureKind.Environment }
                : result;
        if (result.TestSelection is
            {
                Level: TestExecutionLevels.Full,
                FullSuiteRequired: true,
                FullSuiteRan: true,
            })
        {
            return result;
        }

        const string reason =
            "pre-main gate rejected an incomplete result: mandatory full-suite evidence is missing";
        return result with
        {
            Verdict = BuildTestGateVerdict.Fail,
            Reason = reason,
            FailureKind = BuildTestGateFailureKind.Environment,
            FailureFingerprint = BuildTestGateRunner.Fingerprint(
                BuildTestGateFailureKind.Environment, reason),
        };
    }
}
