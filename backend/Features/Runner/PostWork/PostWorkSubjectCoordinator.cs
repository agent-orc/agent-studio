using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Post-work subject authority as seen by the backend (AGT-3171 DP-S8).</summary>
public interface IPostWorkAuthorityClient
{
    Task<PostWorkSubject> MaterializeAsync(MaterializePostWorkSubjectRequest request, CancellationToken ct);
    Task<BackendPassDecision> BeginBackendPassAsync(BeginBackendPassRequest request, CancellationToken ct);
    Task<PostWorkSubject> CompleteBackendPassAsync(string idempotencyKey, CompleteBackendPassRequest request, CancellationToken ct);
    Task<ReconcilePostWorkResponse> ReconcileAsync(ReconcilePostWorkRequest request, CancellationToken ct);
    Task<PostWorkCohortStatus> StartCohortAsync(StartPostWorkCohortRequest request, CancellationToken ct);
    Task<PostWorkCohortStatus> AdvanceCohortAsync(string cohortId, AdvancePostWorkCohortRequest request, CancellationToken ct);
    Task<PostWorkCohortStatus> RollbackCohortAsync(string cohortId, RollbackPostWorkCohortRequest request, CancellationToken ct);
    Task<PostWorkShadowSummary> ShadowAsync(CancellationToken ct);
}

/// <summary>The standalone Task Server owns post-work subjects when the versioned plane is proxied there.</summary>
public sealed class TaskServerPostWorkAuthorityClient(Func<HttpClient> client) : IPostWorkAuthorityClient
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public Task<PostWorkSubject> MaterializeAsync(MaterializePostWorkSubjectRequest request, CancellationToken ct)
        => PostAsync<PostWorkSubject>("/api/v1/post-work/subjects", request, ct);

    public Task<BackendPassDecision> BeginBackendPassAsync(BeginBackendPassRequest request, CancellationToken ct)
        => PostAsync<BackendPassDecision>("/api/v1/post-work/backend-passes", request, ct);

    public Task<PostWorkSubject> CompleteBackendPassAsync(
        string idempotencyKey, CompleteBackendPassRequest request, CancellationToken ct)
        => PostAsync<PostWorkSubject>(
            $"/api/v1/post-work/subjects/{Uri.EscapeDataString(idempotencyKey)}/backend-pass/complete", request, ct);

    public Task<ReconcilePostWorkResponse> ReconcileAsync(ReconcilePostWorkRequest request, CancellationToken ct)
        => PostAsync<ReconcilePostWorkResponse>("/api/v1/post-work/reconcile", request, ct);

    public Task<PostWorkCohortStatus> StartCohortAsync(StartPostWorkCohortRequest request, CancellationToken ct)
        => PostAsync<PostWorkCohortStatus>("/api/v1/post-work/cohorts", request, ct);

    public Task<PostWorkCohortStatus> AdvanceCohortAsync(
        string cohortId, AdvancePostWorkCohortRequest request, CancellationToken ct)
        => PostAsync<PostWorkCohortStatus>(
            $"/api/v1/post-work/cohorts/{Uri.EscapeDataString(cohortId)}/advance", request, ct);

    public Task<PostWorkCohortStatus> RollbackCohortAsync(
        string cohortId, RollbackPostWorkCohortRequest request, CancellationToken ct)
        => PostAsync<PostWorkCohortStatus>(
            $"/api/v1/post-work/cohorts/{Uri.EscapeDataString(cohortId)}/rollback", request, ct);

    public async Task<PostWorkShadowSummary> ShadowAsync(CancellationToken ct)
    {
        using var response = await client().GetAsync("/api/v1/post-work/shadow", ct).ConfigureAwait(false);
        return await ReadAsync<PostWorkShadowSummary>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await client().PostAsJsonAsync(path, body, WireJson, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PostWorkAuthorityException(response.StatusCode, body[..Math.Min(body.Length, 400)]);
        return JsonSerializer.Deserialize<T>(body, WireJson)
               ?? throw new InvalidDataException("Task Server returned an empty post-work response.");
    }
}

public sealed class PostWorkAuthorityException(HttpStatusCode status, string body)
    : HttpRequestException($"Task Server post-work call failed ({(int)status}): {body}", null, status);

/// <summary>Whether this backend may run a post-processing pass for a card.</summary>
public sealed record PostWorkPassAdmission(
    bool Admitted,
    string Reason,
    string? IdempotencyKey = null,
    PostWorkLease? Lease = null)
{
    public static readonly PostWorkPassAdmission Unmanaged = new(true, "post-work-off");

    /// <summary>The card belongs to another authority holder; the queue leaves its lifecycle alone.</summary>
    public bool OwnedElsewhere => !Admitted && Reason is "authority-held" or "already-applied" or "retired";

    /// <summary>The authority could not answer; the pass waits rather than risk a concurrent holder.</summary>
    public bool Retryable => !Admitted && Reason == PostWorkSubjectCoordinator.AuthorityUnavailable;
}

/// <summary>
/// Backend side of DP-S8. In <c>shadow</c> it materializes one subject per
/// auto-review lane entry and records backend passes without ever refusing
/// one. In <c>canary</c> every backend pass first holds the subject's
/// authority, so a host claim and a backend pass cannot process one card at
/// the same time. Lifecycle effects stay in the existing worker.
/// </summary>
public sealed class PostWorkSubjectCoordinator
{
    public const string AuthorityUnavailable = "post-work-authority-unavailable";
    private readonly IConfiguration _configuration;
    private readonly IPostWorkAuthorityClient? _authority;
    private readonly ILogger<PostWorkSubjectCoordinator> _logger;

    public PostWorkSubjectCoordinator(
        IConfiguration configuration,
        ILogger<PostWorkSubjectCoordinator> logger,
        IPostWorkAuthorityClient? authority = null)
    {
        _configuration = configuration;
        _logger = logger;
        _authority = authority;
    }

    /// <summary>One id per backend process; a restart supersedes passes of the earlier process.</summary>
    public string BackendInstanceId { get; } = $"backend_{Guid.NewGuid():N}";

    public string Mode => _authority is null
        ? PostWorkModes.Off
        : PostWorkModes.Normalize(_configuration["PostWork:Mode"]);

    public bool Enabled => Mode != PostWorkModes.Off;

    /// <summary>Builds the immutable subject for the card's current lane entry.</summary>
    public MaterializePostWorkSubjectRequest BuildRequest(TaskInfo card, string source)
    {
        var artifacts = new List<PostWorkArtifactReference>();
        if (card.Commit?.Sha is { Length: > 0 } sha)
            artifacts.Add(new PostWorkArtifactReference("result-sha", sha));
        if (!string.IsNullOrWhiteSpace(card.IntegrationBranch))
            artifacts.Add(new PostWorkArtifactReference("integration-branch", card.IntegrationBranch!));
        var steps = _configuration.GetSection("PostWork:Steps").GetChildren()
            .Select(step => new PostWorkStep(
                step["StepId"] ?? "",
                step["FileName"] ?? "",
                step.GetSection("Arguments").GetChildren().Select(argument => argument.Value ?? "").ToArray(),
                step.GetValue("TimeoutSeconds", 600)))
            .Where(step => step.StepId.Length > 0 && step.FileName.Length > 0)
            .ToArray();
        var capabilities = _configuration.GetSection("PostWork:RequiredCapabilities").GetChildren()
            .Select(item => item.Value ?? "").Where(item => item.Length > 0).ToArray();
        return new MaterializePostWorkSubjectRequest(
            ProjectIdFor(card.ProjectName), CardId(card), LaneEntryId(card),
            card.SessionChain.LastOrDefault(), capabilities, artifacts, steps, source);
    }

    public string ProjectIdFor(string projectName)
        => _configuration[$"PostWork:ProjectIds:{projectName}"] is { Length: > 0 } mapped ? mapped : projectName;

    public static string CardId(TaskInfo card) => string.IsNullOrWhiteSpace(card.Key) ? card.Id : card.Key!;

    public static string LaneEntryId(TaskInfo card) => card.EnteredLaneAt.ToUniversalTime().ToString("O");

    /// <summary>Records the subject off the transition path; shadow never delays a lane move.</summary>
    public void MaterializeInBackground(TaskInfo card, string source)
    {
        if (!Enabled || card.Fixture || !string.Equals(card.State, TaskStates.AutoReview, StringComparison.Ordinal))
            return;
        var request = BuildRequest(card, source);
        _ = Task.Run(async () =>
        {
            try { await _authority!.MaterializeAsync(request, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "post-work-materialize-failed project={Project} task={Task} source={Source}",
                    request.ProjectId, request.TaskId, source);
            }
        });
    }

    public async Task<PostWorkPassAdmission> AdmitAsync(TaskInfo card, string passId, CancellationToken ct)
    {
        if (!Enabled) return PostWorkPassAdmission.Unmanaged;
        var canary = Mode == PostWorkModes.Canary;
        try
        {
            var subject = await _authority!.MaterializeAsync(BuildRequest(card, "backend-pass"), ct).ConfigureAwait(false);
            var decision = await _authority.BeginBackendPassAsync(new BeginBackendPassRequest(
                subject.IdempotencyKey, BackendInstanceId, passId,
                _configuration.GetValue("PostWork:BackendPassLeaseSeconds", 3600)), ct).ConfigureAwait(false);
            if (!decision.Admitted)
                _logger.LogInformation("post-work-backend-pass-refused project={Project} task={Task} reason={Reason}",
                    subject.ProjectId, subject.TaskId, decision.Reason);
            return new PostWorkPassAdmission(decision.Admitted || !canary, decision.Reason,
                subject.IdempotencyKey, decision.Lease);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "post-work-admission-failed project={Project} job={Job} mode={Mode}",
                card.ProjectName, card.Id, Mode);
            // Shadow never changes the queue. Canary must not run without the
            // authority, because a host may hold the card.
            return new PostWorkPassAdmission(!canary, AuthorityUnavailable);
        }
    }

    public async Task CompleteAsync(
        PostWorkPassAdmission admission, PostProcessingCardResult outcome, DateTime? retryAt, CancellationToken ct)
    {
        if (admission.Lease is null || admission.IdempotencyKey is null || _authority is null) return;
        var status = outcome.Status switch
        {
            PostProcessingCardStatus.Decided => PostWorkOutcomes.Decided,
            PostProcessingCardStatus.Deferred => PostWorkOutcomes.Deferred,
            _ => PostWorkOutcomes.Blocked,
        };
        try
        {
            await _authority.CompleteBackendPassAsync(admission.IdempotencyKey, new CompleteBackendPassRequest(
                BackendInstanceId, admission.Lease.LeaseId, admission.Lease.Fence, status, outcome.Reason,
                status == PostWorkOutcomes.Deferred ? retryAt : null), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The lane outcome record remains authoritative; restart
            // reconciliation marks the subject applied from it.
            _logger.LogWarning(ex, "post-work-backend-pass-complete-failed key={Key} status={Status}",
                admission.IdempotencyKey, status);
        }
    }

    /// <summary>Durable lane facts for every card in auto-review: entry and recorded decision.</summary>
    public IReadOnlyList<PostWorkLaneFact> LaneFacts(IEnumerable<TaskInfo> cards, ILogger logger)
        => cards
            .Where(card => !card.Fixture && string.Equals(card.State, TaskStates.AutoReview, StringComparison.Ordinal))
            .Select(card => new PostWorkLaneFact(ProjectIdFor(card.ProjectName), CardId(card), LaneEntryId(card), true,
                !AutoReviewPostProcessingRecoveryService.NeedsPostProcessingRecovery(card,
                    AutoReviewPostProcessingRecoveryService.ReadOutcomes(card.FolderPath, logger))))
            .ToArray();

    public Task<ReconcilePostWorkResponse> ReconcileAsync(
        IReadOnlyList<string> projectNames, IReadOnlyList<PostWorkLaneFact> facts, CancellationToken ct)
        => _authority!.ReconcileAsync(new ReconcilePostWorkRequest(BackendInstanceId,
            projectNames.Select(ProjectIdFor).Distinct(StringComparer.Ordinal).ToArray(), facts), ct);

    public IPostWorkAuthorityClient Authority
        => _authority ?? throw new InvalidOperationException("Post-work subjects require TaskServer:BaseUrl.");
}
