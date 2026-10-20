using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestratorEngine;

public sealed class EngineTaskServerClient : IDecisionReceiptPlane, IEngineArtifactSource, IDisposable
{
    // Match the Task Server HTTP JSON contract: orchestration enums are numbers.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly HttpClient _decisions;

    public EngineTaskServerClient(EngineOptions options)
        : this(CreateHttpClient(options), string.IsNullOrWhiteSpace(options.DecisionPlaneUrl)
            ? null : CreateHttpClient(options, decisionPlane: true))
    {
    }

    internal EngineTaskServerClient(HttpClient http, HttpClient? decisions = null)
    {
        _http = http;
        _decisions = decisions ?? http;
        if (!_http.DefaultRequestHeaders.Contains(TaskServerProtocol.HeaderName))
            _http.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        if (!_http.DefaultRequestHeaders.Contains(TaskServerProtocol.ClientVersionHeaderName))
            _http.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, EngineVersion.ProductVersion);
        if (!ReferenceEquals(_decisions, _http))
        {
            _decisions.DefaultRequestHeaders.TryAddWithoutValidation(TaskServerProtocol.HeaderName,
                TaskServerProtocol.Current.ToString());
            _decisions.DefaultRequestHeaders.TryAddWithoutValidation(TaskServerProtocol.ClientVersionHeaderName,
                EngineVersion.ProductVersion);
        }
    }

    public async Task EnsureCompatibleAsync(CancellationToken ct)
    {
        var response = await PostAsync<ProtocolCompatibilityRequest, ProtocolCompatibilityResponse>(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest(
                TaskServerProtocol.EngineClientKind,
                EngineVersion.ProductVersion,
                TaskServerProtocol.Current),
            ct,
            allowUpgradeRequired: true);
        if (!response.Supported)
            throw new EngineProtocolException(response.Reason ?? "Task Server protocol is not compatible.");
    }

    public Task<OrchestrationClaimResponse> ClaimAsync(
        OrchestrationClaimRequest request,
        CancellationToken ct)
        => PostAsync<OrchestrationClaimRequest, OrchestrationClaimResponse>(
            "/api/v1/orchestration/claims",
            request,
            ct);

    public Task<SteeringActionReceipt> ApplySteeringActionAsync(
        string projectId, string taskId, SteeringActionRequest request, CancellationToken ct)
        => PostAsync<SteeringActionRequest, SteeringActionReceipt>(
            $"/api/v1/steering/projects/{Uri.EscapeDataString(projectId)}/tasks/{Uri.EscapeDataString(taskId)}/actions",
            request, ct);

    public async Task<SteeringActionReceipt> GetSteeringActionAsync(
        string projectId, string taskId, string commandId, CancellationToken ct)
    {
        var path = $"/api/v1/steering/projects/{Uri.EscapeDataString(projectId)}/tasks/{Uri.EscapeDataString(taskId)}/actions/{Uri.EscapeDataString(commandId)}";
        using var response = await _http.GetAsync(path, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new EngineTaskServerException((int)response.StatusCode, content);
        return JsonSerializer.Deserialize<SteeringActionReceipt>(content, Json)
            ?? throw new EngineTaskServerException((int)response.StatusCode, "Task Server returned an empty steering receipt.");
    }

    public Task<OrchestrationRunDto> CompleteStageAsync(
        string runId,
        CompleteOrchestrationStageRequest request,
        CancellationToken ct)
        => PostAsync<CompleteOrchestrationStageRequest, OrchestrationRunDto>(
            $"/api/v1/orchestration/runs/{Uri.EscapeDataString(runId)}/stages/complete",
            request,
            ct);

    public Task<OrchestrationRunDto> ReleaseAsync(
        string runId,
        ReleaseOrchestrationLeaseRequest request,
        CancellationToken ct)
        => PostAsync<ReleaseOrchestrationLeaseRequest, OrchestrationRunDto>(
            $"/api/v1/orchestration/runs/{Uri.EscapeDataString(runId)}/lease/release",
            request,
            ct);

    public Task<OrchestrationLeaseDto> RenewAsync(
        string runId, OrchestrationLeaseRenewRequest request, CancellationToken ct)
        => PostAsync<OrchestrationLeaseRenewRequest, OrchestrationLeaseDto>(
            $"/api/v1/orchestration/runs/{Uri.EscapeDataString(runId)}/lease/renew", request, ct);

    public Task<GateStatus> CreateGateSubjectAsync(CreateGateSubjectRequest request, CancellationToken ct)
        => PostAsync<CreateGateSubjectRequest, GateStatus>("/api/v1/gates/subjects", request, ct);

    public async Task<GateStatus> GetGateStatusAsync(string subjectId, CancellationToken ct)
        => await GetAsync<GateStatus>($"/api/v1/gates/subjects/{Uri.EscapeDataString(subjectId)}", ct);

    public async Task<ReviewSubjectDto> GetReviewSubjectAsync(string subjectId, CancellationToken ct)
        => await GetAsync<ReviewSubjectDto>($"/api/v1/gates/review-sources/{Uri.EscapeDataString(subjectId)}", ct);

    /// <summary>
    /// Empty when the selected receipt host has no decision plane. A Task Server
    /// proxy deployment selects Studio through DECISION_PLANE_URL.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPendingDecisionGroundsAsync(int limit, CancellationToken ct)
    {
        try
        {
            return await GetAsync<IReadOnlyList<string>>(
                $"/api/v1/orchestration/decision-grounds/pending?limit={limit}", ct, _decisions);
        }
        catch (EngineTaskServerException exception) when (exception.StatusCode == 404) { return []; }
    }

    /// <summary>Null when the occurrence has no frozen grounds, so no shadow model step applies.</summary>
    public async Task<DecisionShadowInputV1?> GetGroundsInputAsync(string runId, CancellationToken ct)
    {
        try
        {
            return await GetAsync<DecisionShadowInputV1>(
                $"/api/v1/orchestration/runs/{Uri.EscapeDataString(runId)}/grounds-input", ct, _decisions);
        }
        catch (EngineTaskServerException exception) when (exception.StatusCode == 404) { return null; }
    }

    public Task<OrchestratorDecisionJobDto> CreateDecisionJobAsync(
        OrchestratorDecisionJobRequest request, CancellationToken ct)
        => PostAsync<OrchestratorDecisionJobRequest, OrchestratorDecisionJobDto>(
            "/api/v1/orchestration/decision-jobs", request, ct, client: _decisions);

    public Task<OrchestratorDecisionJobDto> GetDecisionJobAsync(string jobId, CancellationToken ct)
        => GetAsync<OrchestratorDecisionJobDto>(
            $"/api/v1/orchestration/decision-jobs/{Uri.EscapeDataString(jobId)}", ct, _decisions);

    /// <summary>Read an immutable run artefact by IDs through the authorized Task Server route.</summary>
    public Task<ArtifactContentDto> GetArtifactContentAsync(string runId, string artifactId, CancellationToken ct)
        => GetAsync<ArtifactContentDto>(
            $"/api/v1/runs/{Uri.EscapeDataString(runId)}/artifacts/{Uri.EscapeDataString(artifactId)}/content", ct);

    public async Task<OrchestratorDecisionReceiptV1> PutDecisionReceiptAsync(
        OrchestratorDecisionReceiptV1 receipt, CancellationToken ct)
    {
        using var response = await _decisions.PutAsJsonAsync(
            $"/api/v1/orchestration/runs/{Uri.EscapeDataString(receipt.OccurrenceId)}/decision-receipt",
            receipt, Json, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new EngineTaskServerException((int)response.StatusCode, content);
        return JsonSerializer.Deserialize<OrchestratorDecisionReceiptV1>(content, Json)
            ?? throw new EngineTaskServerException(500, "Task Server returned an empty response.");
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct, HttpClient? client = null)
    {
        using var response = await (client ?? _http).GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new EngineTaskServerException((int)response.StatusCode, body);
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new EngineTaskServerException(500, "Task Server returned an empty response.");
    }

    internal static HttpClient CreateHttpClient(EngineOptions options, bool decisionPlane = false)
    {
        // A decision plane client carries only its own credential, never the Task Server one.
        var credentialFile = decisionPlane ? options.DecisionPlaneClientCredentialFile : options.ClientCredentialFile;
        var credential = decisionPlane ? options.DecisionPlaneClientCredential : options.ClientCredential;
        HttpMessageHandler handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(credentialFile))
            handler = new EngineCredentialReloadHandler(credentialFile, handler);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(decisionPlane ? options.DecisionPlaneUrl! : options.ServerUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.Add("X-Client-Id", options.ClientId);
        client.DefaultRequestHeaders.Add("X-Principal-Consumer-Id", options.ClientId);
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, EngineVersion.ProductVersion);
        if (!string.IsNullOrWhiteSpace(credential) && string.IsNullOrWhiteSpace(credentialFile))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken ct,
        bool allowUpgradeRequired = false,
        HttpClient? client = null)
    {
        using var response = await (client ?? _http).PostAsJsonAsync(path, request, Json, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode && !(allowUpgradeRequired && (int)response.StatusCode == 426))
            throw new EngineTaskServerException((int)response.StatusCode, content);
        try
        {
            return JsonSerializer.Deserialize<TResponse>(content, Json)
                   ?? throw new EngineTaskServerException((int)response.StatusCode, "Task Server returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new EngineTaskServerException(
                (int)response.StatusCode,
                $"Task Server returned invalid JSON: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_decisions, _http)) _decisions.Dispose();
        _http.Dispose();
    }
}

public sealed class EngineTaskServerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class EngineProtocolException(string message) : Exception(message);
