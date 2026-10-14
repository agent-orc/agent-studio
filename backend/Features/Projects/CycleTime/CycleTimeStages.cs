namespace AgentStudio.Projects;

/// <summary>
/// Stage identifiers of the per-project cycle-time view. The additive
/// stages partition a task's lead time (created to completed): every instant
/// between creation and the last real entry into <c>6-completed</c> belongs to
/// exactly one of them. The rollups overlap the additive stages and are
/// reported separately so the operator can read both the composition and the
/// familiar totals. See <c>docs/concepts/cycle-time-stage-model.md</c>.
/// </summary>
public static class CycleTimeStages
{
    // ---- additive lane stages (sum == lead time) ----

    /// <summary>Time in <c>0-backlog</c>, <c>1-preparation</c>, and <c>1a-orchestrator-prep</c>.</summary>
    public const string Preparation = "preparation";
    /// <summary>Time in <c>2-ready</c>: waiting for a runner slot (queue wait).</summary>
    public const string QueueWait = "queueWait";
    /// <summary>Time in <c>3-progress</c> (and the 3a/3b sub-lanes): coding runs incl. retries.</summary>
    public const string Coding = "coding";
    /// <summary>Time in <c>4-auto-review</c> before the review attempt started working (post-processing queue wait).</summary>
    public const string ReviewWait = "reviewWait";
    /// <summary>Build/test gate executions inside the review run (<c>post-build-test-gate</c> steps).</summary>
    public const string TestGate = "testGate";
    /// <summary>Remainder of the review run: aspect reviews, grade, decision, and step overhead.</summary>
    public const string ReviewOther = "reviewOther";
    /// <summary>Delivery integration spans (merge into the integration branch), wherever they occur.</summary>
    public const string Integration = "integration";
    /// <summary>Time in <c>5-human-review</c> and <c>5e-escalated</c>, minus integration spans.</summary>
    public const string HumanReview = "humanReview";
    /// <summary>A completed-lane stay before a genuine coding rework completion.</summary>
    public const string CompletedBeforeReopen = "completedBeforeReopen";
    /// <summary>Lead-time remainder that no lane interval explains (unknown lanes, clock skew, missing ledger).</summary>
    public const string Unattributed = "unattributed";

    // ---- rollups (overlap the additive stages) ----

    /// <summary>Review attempt span: first post step started to leaving <c>4-auto-review</c> (testGate + reviewOther + integration inside auto review).</summary>
    public const string ReviewRun = "reviewRun";
    /// <summary>Created to the last real completion.</summary>
    public const string LeadTime = "leadTime";
    /// <summary>First claim (<c>3-progress</c> entry) to the last real completion.</summary>
    public const string CycleTime = "cycleTime";

    // ---- counts ----

    public const string CodingRuns = "codingRuns";
    public const string ReviewRounds = "reviewRounds";
    public const string BounceRounds = "bounceRounds";
    public const string IntegrationAttempts = "integrationAttempts";

    /// <summary>Additive stages in lane order. The sum of these equals <see cref="LeadTime"/> for every task.</summary>
    public static readonly IReadOnlyList<string> Additive =
    [
        Preparation, QueueWait, Coding, ReviewWait, TestGate, ReviewOther, Integration, HumanReview, CompletedBeforeReopen, Unattributed,
    ];

    /// <summary>Stages the operator named as the known bottlenecks; the view emphasises them.</summary>
    public static readonly IReadOnlySet<string> Highlighted = new HashSet<string>(StringComparer.Ordinal)
    {
        TestGate, Integration,
    };

    public static string Label(string stage) => stage switch
    {
        Preparation => "Preparation",
        QueueWait => "Queue wait",
        Coding => "Coding run",
        ReviewWait => "Post-processing wait",
        TestGate => "Build/test gate",
        ReviewOther => "Review aspects and decision",
        Integration => "Integration",
        HumanReview => "Human review",
        CompletedBeforeReopen => "Completed before reopen",
        Unattributed => "Unattributed",
        ReviewRun => "Review run",
        LeadTime => "Lead time",
        CycleTime => "Claim to completion",
        CodingRuns => "Coding runs",
        ReviewRounds => "Review rounds",
        BounceRounds => "Bounce rounds",
        IntegrationAttempts => "Integration attempts",
        _ => stage,
    };
}
