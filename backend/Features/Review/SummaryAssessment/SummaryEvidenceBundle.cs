using System.Text.Json.Serialization;

namespace AgentStudio.Review;

/// <summary>
/// The card signals the closing Result assessment reconciles (AGT-3046). It is
/// a projection, not a store: every field is read from the canonical record
/// that already owns it (task.json, pipeline-execution.json, timeline.jsonl,
/// integration-verification.json, the generated status.md header). The same
/// value is serialized into the summary prompt and fed to
/// <see cref="SummaryAssessmentPolicy"/>, so the model and the deterministic
/// check read one bundle.
/// </summary>
public sealed record SummaryEvidenceBundle
{
    public string TaskKey { get; init; } = "";
    public string? TaskType { get; init; }
    public string? Mode { get; init; }
    public string Lane { get; init; } = "";
    public string? RunOutcome { get; init; }
    public List<SummaryLaneTransition> LaneHistory { get; init; } = [];
    public SummaryPipelineEvidence? Pipeline { get; init; }
    public List<SummaryCommitEvidence> Commits { get; init; } = [];
    public bool CodeActivityDetected { get; init; }
    public List<SummaryIntegrationEvidence> IntegrationRecords { get; init; } = [];
    public SummaryVerificationEvidence? IntegrationVerification { get; init; }
    public List<SummarySalvageEvidence> SalvageRefs { get; init; } = [];
    public SummaryCompletionClaimEvidence? CompletionClaim { get; init; }
    public string? OrchestratorVerdict { get; init; }
    public List<SummaryStepVerdict> ReviewVerdicts { get; init; } = [];
    public string? SightReviewVerdict { get; init; }

    /// <summary>
    /// Header metrics of the status.md this assessment accompanies. Null in the
    /// prompt (the model is writing that header); filled from the generated
    /// markdown before the policy runs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SummaryStatusHeader? StatusHeader { get; init; }
}

public sealed record SummaryLaneTransition(DateTime At, string Change);

public sealed record SummaryPipelineEvidence(
    int Attempt,
    bool Completed,
    List<SummaryStepEvidence> Steps);

public sealed record SummaryStepEvidence(
    string StepId,
    string Status,
    string? Reason,
    string? FailureCode,
    string? Verdict);

public sealed record SummaryCommitEvidence(
    string Sha,
    string Subject,
    int FilesChanged,
    bool Superseded);

public sealed record SummaryIntegrationEvidence(
    string Classification,
    string? IntegrationBranch,
    string? IntegrationSha,
    string? SourceSha,
    List<string> CommitShas,
    string Evidence);

public sealed record SummaryVerificationEvidence(string State, string? Sha, string Evidence);

public sealed record SummarySalvageEvidence(
    string? Branch,
    string? CommitSha,
    string? Resolution,
    string? RunStatus);

public sealed record SummaryCompletionClaimEvidence(
    string Basis,
    string Evidence,
    string? Reason,
    string? CommitSha,
    string? DeliverablePath);

public sealed record SummaryStepVerdict(string StepId, string Verdict);

/// <summary>The optional quality-head lines of the generated status.md.</summary>
public sealed record SummaryStatusHeader(string? Result, int? Files, string? Tests);
