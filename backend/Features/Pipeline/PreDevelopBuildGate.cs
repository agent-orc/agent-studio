namespace AgentStudio.Pipeline;

/// <summary>
/// Build boundary in front of the integration branch. Where
/// <see cref="PreMainTestGate"/> guards the release branch with the mandatory
/// full suite BEFORE a fast-forward, this gate guards <c>develop</c> with the
/// cheap staged verification AFTER the merge commit exists: the interesting subject is
/// the MERGE RESULT (delivery + current integration tip), which no earlier gate
/// has ever verified. Deliveries whose exact merge diff touches code run the
/// bounded <see cref="TestExecutionLevels.WorkPackage"/> slice: touched-folder
/// Angular specs plus the fixed studio-shell and task-detail barrel collision
/// probes for a frontend diff, the impacted .NET test projects narrowed to the
/// touched test classes for a managed diff. A diff that touches neither keeps
/// the build-only level. The full suite remains exclusive to the promotion
/// boundary; the cheaper <see cref="TestExecutionLevels.CompileOnly"/> level is
/// reachable only through a reused Remote Review verdict (AGT-2839).
///
/// <para>
/// AGT-2854: before this, a backend-only diff was pinned to
/// <see cref="TestExecutionLevels.BuildOnly"/> and ran no test here at all,
/// while the auto-review gate that was meant to cover it runs on Linux. A
/// Windows-only red test therefore reached <c>develop</c> without ever having
/// been executed on the host that fails it.
/// </para>
///
/// <para>
/// Convention instead of a settings switch: the gate applies when the project
/// declares build commands in its <see cref="BuildProfile"/>, or when the exact
/// merge diff provides a work package (<see cref="AppliesTo"/>).
/// A project whose merge touches neither stack merges as before.
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
    /// exact merge diff touches code that has a convention-derived work package
    /// (Angular specs for <c>frontend/</c>, impacted test projects for managed
    /// sources). A project whose merge touches neither stays ungated rather than
    /// receiving an invented command.
    /// </summary>
    public static bool AppliesTo(
        BuildProfile? profile,
        IReadOnlyList<string>? changedFiles = null)
        => VerifyCommandPlanner.HasProfileBuildCommands(profile)
            || FrontendWorkPackagePlanner.TouchesFrontend(changedFiles)
            || DotNetWorkPackagePlanner.TouchesDotNet(changedFiles);

    /// <summary>
    /// The level matrix for the exact merge diff. Pure, so the four cases are
    /// tested directly:
    /// <list type="bullet">
    ///   <item>managed sources only: <c>work-package</c>, the impacted .NET test
    ///     projects narrowed to the touched test classes.</item>
    ///   <item><c>frontend/</c> only: <c>work-package</c>, the Angular include
    ///     slice and the frontend lints, no .NET test project.</item>
    ///   <item>both: <c>work-package</c> covering both stacks.</item>
    ///   <item>neither (docs, scripts, workflow files): <c>build-only</c>.</item>
    /// </list>
    /// An unavailable diff resolves to work-package, where the planner's own
    /// conservative fallback takes over; it must never silently become
    /// build-only.
    /// </summary>
    public static string ResolveTestLevel(IReadOnlyList<string>? changedFiles)
        => changedFiles is null
            || FrontendWorkPackagePlanner.TouchesFrontend(changedFiles)
            || DotNetWorkPackagePlanner.TouchesDotNet(changedFiles)
                ? TestExecutionLevels.WorkPackage
                : TestExecutionLevels.BuildOnly;

    /// <summary>
    /// Verifies <paramref name="request"/>'s exact subject. The level is pinned
    /// by <see cref="ResolveTestLevel"/>, so lane configuration can neither turn
    /// this into the promotion-only full suite nor drop a code diff back to a
    /// cheaper stage.
    /// <paramref name="reuseRemoteReviewVerdict"/> is the one documented step
    /// below that matrix (AGT-2839): it drops the level to compile-only, where
    /// the merge result still has to compile, but the tests and lint were just
    /// run on exactly this content by the Remote Review. Only
    /// <see cref="MergeIntoDevelopRunner"/> sets it, and only after
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

    /// <summary>
    /// The level the gate actually requests: <see cref="ResolveTestLevel"/>'s
    /// matrix, or compile-only when the reuse policy granted a reused Remote
    /// Review verdict for this exact merge result.
    /// </summary>
    internal static string LevelFor(
        IReadOnlyList<string>? changedFiles,
        bool reuseRemoteReviewVerdict)
        => reuseRemoteReviewVerdict
            ? TestExecutionLevels.CompileOnly
            : ResolveTestLevel(changedFiles);

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
