namespace AgentStudio.Shared;

public static class TaskStates
{
    /// <summary>
    /// Triage staging area for new tasks. Sits before <see cref="Preparation"/>
    /// and is the default landing lane for <see cref="CreateTaskRequest"/> when
    /// no explicit <c>targetState</c> is supplied. Auto-pickup never reaches
    /// into this lane: a backlog job must be promoted explicitly. The numeric
    /// prefix sorts it before <c>1-preparation</c> on disk and in the kanban.
    /// </summary>
    public const string Backlog = "0-backlog";

    public const string Preparation = "1-preparation";

    // ADR-0026: the orchestrator-prep lane is *additive* (no rename of the
    // existing 1-preparation -> 2-ready -> ... chain). The 1a- sort key slots
    // between 1- and 2- both on disk and in the kanban: ASCII '-' (45) is less
    // than 'a' (97), and '1' is less than '2'.
    //
    // The former 1b-needs-human-review bounce lane has been retired: the
    // "Human decision needed" concept was obsoleted. Prep now admits actionable
    // cards straight to 2-ready, and genuine "a human must decide" cases are
    // escalated to 5e-escalated by the orchestrator / the human-review funnel.
    // Boot migration in TaskStateMachine moves any stray 1b folder to 2-ready.
    public const string OrchestratorPrep = "1a-orchestrator-prep";

    public const string Ready = "2-ready";
    public const string Progress = "3-progress";

    // 3a-failed-pickup is the visible orphan lane for boot-sweep verdicts
    // that previously vanished into 7-archive. The pickup-loud-not-archive
    // contract: a folder that crossed the resume window without a completion
    // sentinel lands here, never silently in 7-archive. Hide-when-empty in
    // the UI (same rule as 5-human-review). The
    // additive 3a- sort key keeps existing folders, code references, and
    // tests valid: ASCII '-' (45) < 'a' (97) so 3-progress sorts before
    // 3a-...; '3' < '4' so 3a- sorts before 4-auto-review. Populated by
    // StaleProgressArchiver when it sees a stale orphan or empty 3-progress
    // folder. See ADR-0028.
    public const string FailedPickup = "3a-failed-pickup";

    // 3b-code-not-complete is the park lane for a task that exhausted its
    // auto-pickup retry budget without ever reaching review (no commit, agent
    // never signalled done, classifier-unknown). Instead of stopping the whole
    // project at the first broken task, the runner parks it here and keeps
    // pulling the next Ready task; the project only flips to manual once the
    // systemic "3x3" pattern trips (see ProjectRunner.AutoFailureDistinctTaskHaltThreshold).
    // Additive lane (no boot migration): the 3b- sort key slots between
    // 3a-failed-pickup and 4-auto-review on disk and in the kanban (ASCII '-'
    // (45) < 'a' (97), and '3' < '4'). Hide-when-empty in the UI (same rule as
    // 5-human-review). Auto-pickup never reaches into
    // this lane: the picker only enumerates 3-progress.
    public const string CodeNotComplete = "3b-code-not-complete";

    // 4-auto-review is the orchestrator's lane: ReviewDecisionOrchestrator
    // can reissue, accept-as-done, or escalate. Escalated is the intervention
    // basin: the machine cannot continue and records a category plus a concrete
    // reason for the operator. HumanReview is later in the flow and is reserved
    // for acceptance of a finished delivery backed by review evidence.
    // The legacy single 4-review lane is migrated on backend boot via
    // TaskStateMachine.EnsureStateFoldersAndMigrate. See ADR-0025.
    public const string AutoReview = "4-auto-review";
    public const string Escalated = "5e-escalated";
    public const string HumanReview = "5-human-review";
    public const string Completed = "6-completed";
    public const string Archive = "7-archive";

    public static readonly string[] All =
        [Backlog, Preparation, OrchestratorPrep, Ready, Progress, FailedPickup, CodeNotComplete, AutoReview, Escalated, HumanReview, Completed, Archive];

    /// <summary>Maps old unnumbered folder names to new numbered ones.</summary>
    public static readonly Dictionary<string, string> LegacyFolderMap = new()
    {
        ["preparation"] = Preparation,
        ["ready"] = Ready,
        ["progress"] = Progress,
        // The pre-ADR-0025 lane shape mapped one "review" lane to the
        // orchestrator's pass; preserve that meaning by funnelling unnumbered
        // legacy folders into 4-auto-review.
        ["review"] = AutoReview,
        ["completed"] = Completed,
    };

    /// <summary>
    /// Numbered legacy lane names that pre-date ADR-0025 (three-stage review
    /// pipeline). The boot-time migration in
    /// <see cref="AgentStudio.Tasks.TaskStateMachine.EnsureStateFoldersAndMigrate"/>
    /// uses this to rename folders and rewrite job.json state fields.
    /// </summary>
    public static readonly Dictionary<string, string> NumberedLegacyMap = new()
    {
        ["4-review"] = AutoReview,
        ["5-completed"] = Completed,
        ["6-archive"] = Archive,
    };

    public static string MapLegacyState(string state) => state switch
    {
        "draft" => Preparation,
        "running" => Progress,
        "review-needed" => AutoReview,
        "accepted" => Completed,
        "rejected" => Completed,
        "archived" => Completed,
        "4-review" => AutoReview,
        "5-completed" => Completed,
        "6-archive" => Archive,
        // Retired lane: any job still tagged with the removed 1b state lands
        // in 2-ready (the destination the lane was manually emptied into).
        "1b-needs-human-review" => Ready,
        _ => Preparation
    };

    /// <summary>Returns the display name without the number prefix.</summary>
    public static string DisplayName(string state) =>
        state.Contains('-') ? state[(state.IndexOf('-') + 1)..] : state;
}
