namespace AgentStudio.TaskServer.Contracts;

public static class ReviewCapabilities
{
    public const string CodingExecutor = "coding-executor";
    public const string ReviewExecutor = "review-executor";
    public const string GitMaterialization = "review:git";
    public const string SourceBundleMaterialization = "review:source-bundle";
    public const string SemanticReview = "review:semantic";
    public const string VisionReview = "review:vision";
    public const string BaselineComparison = "review:baseline-comparison";
    public const string DependencyPreparation = "review:dependency-preparation";
}

public static class ReviewCommandKinds
{
    public const string Tool = "tool";
    public const string AgentAspect = "agent-aspect";

    public static bool IsAgent(string? value)
        => string.Equals(value, AgentAspect, StringComparison.OrdinalIgnoreCase);
}

public sealed record ReviewDependencyScopeDto(
    string WorkingSubdir,
    IReadOnlyList<string> Lockfiles);

public sealed record ReviewPreparationCommandDto(
    string StepId,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingSubdir = "",
    int TimeoutSeconds = 1800,
    IReadOnlyList<ReviewDependencyScopeDto>? DependencyScopes = null);

public sealed record ReviewCommandDto(
    string StepId,
    string Aspect,
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Required = true,
    int TimeoutSeconds = 1800,
    bool CompareToBaseline = false,
    string ExecutionKind = ReviewCommandKinds.Tool,
    string? Prompt = null,
    string? CliType = null,
    string? Model = null,
    string? ThinkingLevel = null);

public sealed record ReviewPlanDto(
    IReadOnlyList<ReviewCommandDto> Commands,
    IReadOnlyList<string> RequiredAspects,
    bool RequiresVisualReview = false,
    bool RequireDifferentHostFailureDomain = false,
    string? IntegrationRef = null,
    IReadOnlyList<ReviewPreparationCommandDto>? Preparation = null,
    IReadOnlyList<string>? PreserveGlobs = null,
    string? BuildProfileFingerprint = null);

public sealed record CreateReviewSubjectRequest(
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedResultSha,
    string? ResultRef,
    string? SourceBundleArtifactId,
    string? SourceBundleSha256,
    string? CodingHostId,
    string ReviewPolicyHash,
    ReviewPlanDto Plan,
    string IdempotencyKey);

public sealed record ReviewSubjectDto(
    string SubjectId,
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedResultSha,
    string? ResultRef,
    string? SourceBundleArtifactId,
    string? SourceBundleSha256,
    string? CodingHostId,
    string ReviewPolicyHash,
    ReviewPlanDto Plan,
    DateTime CreatedAt);

public sealed record ReviewAttemptDto(
    string AttemptId,
    string SubjectId,
    string TaskId,
    int AttemptNumber,
    string Status,
    string? ExecutorId,
    string? HostId,
    long Fence,
    DateTime CreatedAt,
    DateTime? ReportedAt,
    DateTime? CleanedAt,
    string? Outcome,
    string? FailureClassification);

public sealed record ReviewLeaseDto(
    string LeaseId,
    string AttemptId,
    string SubjectId,
    string ExecutorId,
    string InstanceId,
    string HostId,
    long Fence,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string Status,
    string ResourceNamespace,
    int PortBase,
    long AuthorityEpoch = 0);

public sealed record ReviewClaimRequest(
    string ExecutorId,
    string InstanceId,
    int RequestedTtlSeconds = 120,
    int AvailableSlots = 1,
    IReadOnlyList<string>? RequiredCapabilities = null);

public sealed record ReviewClaimResponse(
    string Status,
    ReviewAttemptDto? Attempt = null,
    ReviewSubjectDto? Subject = null,
    ReviewLeaseDto? Lease = null,
    string? Message = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? CanaryCapabilities = null);

public sealed record ReviewLeaseRenewRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    int RequestedTtlSeconds = 120,
    long AuthorityEpoch = 0);

/// <summary>
/// Takeover of one specific ReviewAttempt by the executor that still owns its
/// running detached worker. A replacement daemon sends this after a planned
/// restart when it has proven the worker alive but the Task Server refused the
/// handed-off lease. The previous lease identity is the continuity proof: only
/// an attempt whose recorded authority is exactly that handed-off lease, and
/// whose lease is no longer live, may be re-fenced this way. Everything else
/// stays a fresh claim.
/// </summary>
public sealed record ReviewReClaimRequest(
    string ExecutorId,
    string InstanceId,
    string PreviousLeaseId,
    long PreviousFence,
    string IdempotencyKey,
    int RequestedTtlSeconds = 120);

public sealed record ReviewCommandEvidenceDto(
    string StepId,
    string Aspect,
    string FileName,
    IReadOnlyList<string> Arguments,
    string ExpectedResultSha,
    string HeadBefore,
    string TreeBefore,
    DateTime StartedAt,
    DateTime FinishedAt,
    int? ExitCode,
    string? Signal,
    string StdoutSha256,
    string StderrSha256,
    string? BaselineSha = null,
    IReadOnlyList<string>? NewFailures = null,
    IReadOnlyList<string>? PreExistingFailures = null,
    bool BaselineCacheHit = false,
    bool RetryPerformed = false,
    IReadOnlyList<string>? FlakyQuarantinedFailures = null,
    string Phase = "verification",
    string WorkspaceRole = "candidate",
    ReviewCommandBudgetEvidenceDto? Budget = null,
    bool DependencyCacheHit = false,
    IReadOnlyList<ReviewDependencyCacheEvidenceDto>? DependencyCache = null,
    string ExecutionKind = ReviewCommandKinds.Tool,
    string ExecutionLocation = "remote",
    string? ExecutorId = null,
    string? HostId = null,
    string? AttemptId = null,
    string? Model = null,
    string? ThinkingLevel = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0);

public sealed record ReviewCommandBudgetEvidenceDto(
    string Name,
    long LimitMs,
    long ConsumedMs,
    bool Violated);

public sealed record ReviewDependencyCacheEvidenceDto(
    string Scope,
    string State,
    string Reason,
    string LockHash,
    IReadOnlyList<string> Lockfiles,
    bool InstallRan);

public sealed record ReviewWorkspaceProofDto(
    string RepositoryId,
    string ExpectedResultSha,
    string ActualHead,
    string TreeHash,
    bool DirtyBefore,
    bool DirtyAfter,
    string WorkspaceIdentity,
    string ResourceNamespace);

public sealed record ReviewEnvironmentDto(
    string HostId,
    string ExecutorId,
    string InstanceId,
    string OsDescription,
    string Architecture,
    string RuntimeVersion,
    IReadOnlyDictionary<string, string> Toolchain,
    IReadOnlyDictionary<string, string> Isolation);

public sealed record ReviewVerdictDto(
    string Aspect,
    string Status,
    string Classification,
    string Summary,
    string? EvidenceChecked = null,
    string? Missing = null);

/// <summary>
/// Enforces the citation contract for semantic review blocks. A model may
/// refuse a delivery only after naming both the material it checked and the
/// exact missing file, section, or contract. An uncited refusal remains useful
/// reviewer feedback, but it is downgraded to a concern and cannot drive the
/// acceptance rail.
/// </summary>
public static class ReviewVerdictCitationPolicy
{
    public const string BlockWithoutCitation = "block-without-citation";

    public static ReviewReportRequest NormalizeReport(
        ReviewReportRequest request,
        IEnumerable<string> semanticAspects)
    {
        var aspects = semanticAspects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verdicts = request.Verdicts
            .Select(verdict => Normalize(verdict, aspects.Contains(verdict.Aspect)))
            .ToArray();
        var downgraded = verdicts.Any(verdict => string.Equals(
            verdict.Classification,
            BlockWithoutCitation,
            StringComparison.Ordinal));
        var stillBlocked = verdicts.Any(verdict => ReviewGradingPolicy.IsBlockingToken(verdict.Status));
        return request with
        {
            Verdicts = verdicts,
            Outcome = downgraded && !stillBlocked
                && string.Equals(request.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase)
                    ? "Pass"
                    : request.Outcome,
            FailureClassification = downgraded && !stillBlocked
                ? BlockWithoutCitation
                : request.FailureClassification,
        };
    }

    public static ReviewVerdictDto Normalize(ReviewVerdictDto verdict, bool semanticAspect)
    {
        if (!semanticAspect
            || !ReviewGradingPolicy.IsBlockingToken(verdict.Status)
            || HasCitation(verdict))
            return verdict;

        return verdict with
        {
            Status = "concerns",
            Classification = BlockWithoutCitation,
            Summary = string.IsNullOrWhiteSpace(verdict.Summary)
                ? "The reviewer requested a block without citing the checked evidence and exact missing gap."
                : verdict.Summary.Trim() + " (Block downgraded because no review-material citation and exact missing gap were supplied.)",
        };
    }

    public static bool HasCitation(ReviewVerdictDto verdict)
        => Meaningful(verdict.EvidenceChecked)
           && Meaningful(verdict.Missing);

    private static bool Meaningful(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(value.Trim(), "n/a", StringComparison.OrdinalIgnoreCase);
}

public sealed record ReviewArtifactEvidenceDto(
    string Name,
    string MediaType,
    string Sha256,
    long SizeBytes,
    string? ContentBase64 = null);

public static class ReviewToolchainFailurePolicy
{
    public static bool IsUnavailable(
        IReadOnlyList<ReviewCommandEvidenceDto> commands,
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts)
        => commands.Any(command =>
            command.Phase == "verification"
            && (command.ExitCode == 127
                || ArtifactsContainMissingAngularToolchain(artifacts, command)));

    private static bool ArtifactsContainMissingAngularToolchain(
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts,
        ReviewCommandEvidenceDto command)
        => artifacts.Any(artifact =>
        {
            if (artifact.ContentBase64 is null
                || (!string.Equals(artifact.Sha256, command.StdoutSha256, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(artifact.Sha256, command.StderrSha256, StringComparison.OrdinalIgnoreCase)))
                return false;
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(artifact.ContentBase64));
                return text.Replace('\\', '/').Contains(
                    "node_modules/@angular/cli/bin/ng.js",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException)
            {
                return false;
            }
        });
}

public sealed record ReviewReportRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    string Outcome,
    string? FailureClassification,
    string? Summary,
    ReviewWorkspaceProofDto Workspace,
    ReviewEnvironmentDto Environment,
    IReadOnlyList<ReviewCommandEvidenceDto> Commands,
    IReadOnlyList<ReviewArtifactEvidenceDto> Artifacts,
    IReadOnlyList<ReviewVerdictDto> Verdicts,
    long AuthorityEpoch = 0);

public sealed record ReviewReportDto(
    string ReportId,
    string AttemptId,
    string SubjectId,
    string Outcome,
    string? FailureClassification,
    string? Summary,
    string ReportSha256,
    DateTime ReceivedAt,
    bool RetryScheduled,
    string TaskState);

public sealed record ReviewCleanupRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    bool WorkspaceRemoved,
    string? FailureClassification = null,
    long AuthorityEpoch = 0);

public sealed record ReviewCleanupResponse(
    string Status,
    string AttemptId,
    DateTime CleanedAt,
    bool RetryScheduled);
