namespace AgentStudio.Pipeline;

/// <summary>
/// Build boundary in front of the integration branch. Where
/// <see cref="PreMainTestGate"/> guards the release branch with the mandatory
/// full suite BEFORE a fast-forward, this gate guards <c>develop</c> with the
/// cheap staged verification AFTER the merge commit exists: the interesting subject is
/// the MERGE RESULT (delivery + current integration tip), which no earlier gate
/// has ever verified. Non-frontend deliveries keep the compile-only level.
/// Deliveries whose exact merge diff touches <c>frontend/</c> run the bounded
/// <see cref="TestExecutionLevels.WorkPackage"/> slice: touched-folder Angular
/// specs plus the fixed studio-shell and task-detail barrel collision probes.
/// The full suite remains exclusive to the promotion boundary.
///
/// <para>
/// Convention instead of a settings switch: the gate applies when the project
/// declares build commands in its <see cref="BuildProfile"/>, or when an exact
/// frontend diff provides an Angular work package (<see cref="AppliesTo"/>).
/// A non-frontend project without a declared build command merges as before.
/// The verification itself always runs against the exact merged SHA in an
/// isolated worktree (<c>RequireExactSubject</c>), so the live checkout is never
/// used as a command workspace.
/// </para>
/// </summary>
public sealed class PreDevelopBuildGate
{
    private readonly IBuildTestGateRunner _runner;

    public PreDevelopBuildGate(IBuildTestGateRunner runner) => _runner = runner;

    /// <summary>
    /// True when the project's build profile yields build commands, or when the
    /// exact merge diff touches <c>frontend/</c> and therefore has a convention-
    /// derived Angular work package. A non-frontend project without a declared
    /// build command stays ungated rather than receiving an invented command.
    /// </summary>
    public static bool AppliesTo(
        BuildProfile? profile,
        IReadOnlyList<string>? changedFiles = null)
        => VerifyCommandPlanner.HasProfileBuildCommands(profile)
            || FrontendWorkPackagePlanner.TouchesFrontend(changedFiles);

    /// <summary>
    /// Verifies <paramref name="request"/>'s exact subject. The level is pinned
    /// to work-package for a known frontend diff and build-only otherwise, so
    /// lane configuration cannot turn this into the promotion-only full suite.
    /// <paramref name="reuseRemoteReviewVerdict"/> drops it one further step to
    /// compile-only (AGT-2839): the merge result still has to compile, but the
    /// tests and lint were just run on exactly this content by the Remote
    /// Review. Only <see cref="MergeIntoDevelopRunner"/> sets it, and only after
    /// <see cref="IntegrationGateReusePolicy"/> proved the base is unchanged.
    /// </summary>
    public Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        IReadOnlyList<string> changedFiles,
        BuildProfile? profile,
        TimeSpan timeout,
        CancellationToken ct,
        bool reuseRemoteReviewVerdict = false)
        => _runner.RunAsync(
            request with
            {
                RequireExactSubject = true,
                RequiredTestLevel = LevelFor(changedFiles, reuseRemoteReviewVerdict),
            },
            changedFiles,
            profile,
            PostStepMode.Fail,
            timeout,
            ct);

    internal static string LevelFor(
        IReadOnlyList<string>? changedFiles,
        bool reuseRemoteReviewVerdict)
        => reuseRemoteReviewVerdict
            ? TestExecutionLevels.CompileOnly
            : FrontendWorkPackagePlanner.TouchesFrontend(changedFiles)
                ? TestExecutionLevels.WorkPackage
                : TestExecutionLevels.BuildOnly;

    /// <summary>
    /// The gate is green on <see cref="BuildTestGateVerdict.Ok"/> and on
    /// <see cref="BuildTestGateVerdict.NotApplicable"/> (nothing derivable to
    /// build, so it must not invent a blocker). A skipped run is not green:
    /// where commands exist, an unverified merge never stays on the integration
    /// branch. Infrastructure failures remain fail-closed as well.
    /// </summary>
    public static bool IsGreen(BuildTestGateResult result)
        => result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.NotApplicable;
}
