namespace AgentStudio.Shared;

/// <summary>
/// Wire contract for the Runner → Server log-ingestion API (Step 6). A remote
/// Runner ships the output lines it produced for a task to the server, which
/// appends them to that task's durable <c>logs/cli-output.log</c> — the
/// "Schreiben über die API" half of the distributed model. While a run is in
/// progress the live view is still read locally (direct, fast); ingestion is
/// what lets the server aggregate a remote Runner's output for history and
/// cross-machine viewing.
/// </summary>
public sealed record LogIngestRequest(
    string TaskKey,
    List<CliOutputLine> Lines,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? AttemptId = null,
    long? Fence = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null);

public sealed record LogIngestResponse(string TaskKey, int Appended, string? Message = null);

/// <summary>
/// Wire contract for Runner -> Server artifact ingestion. Remote runners upload
/// durable review evidence such as screenshots and Playwright output, and the
/// server writes it under the task's <c>results/</c> folder.
/// </summary>
public sealed record ArtifactIngestRequest(
    string TaskKey,
    List<RunnerArtifactUpload> Artifacts,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? AttemptId = null,
    long? Fence = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null,
    bool FinalizeResult = false);

public sealed record RunnerArtifactUpload(string Path, string ContentBase64);

public sealed record ArtifactIngestResponse(
    string TaskKey,
    int Uploaded,
    List<string> Files,
    string? Message = null,
    string? CommitSha = null,
    string? CommitStatus = null,
    bool ResultDocumentGenerated = false,
    string? ResultDocumentStatus = null);

public sealed record ArtifactTransferLimitsResponse(
    long MaxRequestBodyBytes,
    long MaxFileBytes,
    long MaxTotalBytes);

public sealed record ArtifactTransferIssue(
    string Path,
    long SizeBytes,
    string Reason,
    string Outcome = "ArtifactTooLarge");

public sealed record ArtifactTransferReportRequest(
    string TaskKey,
    string Status,
    IReadOnlyList<ArtifactTransferIssue> Issues,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? AttemptId = null);
