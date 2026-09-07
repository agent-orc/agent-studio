using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestratorEngine;

public sealed class EngineTaskServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public EngineTaskServerClient(EngineOptions options)
        : this(CreateHttpClient(options))
    {
    }

    internal EngineTaskServerClient(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.Contains(TaskServerProtocol.HeaderName))
            _http.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        if (!_http.DefaultRequestHeaders.Contains(TaskServerProtocol.ClientVersionHeaderName))
            _http.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, EngineVersion.ProductVersion);
    }

    /// <summary>How long the liveness probe waits before it calls the server unreachable.</summary>
    private const int HealthProbeTimeoutSeconds = 10;

    /// <summary>
    /// Liveness probe against the Task Server's open <c>/healthz</c> route. Returns
    /// <c>null</c> when the server answers 200, otherwise a short human-readable
    /// reason. It never throws for an unreachable server, so a caller can report
    /// "control plane down" cleanly instead of letting a transport exception
    /// cascade. Mirrors <c>TaskServerClient.ProbeHealthAsync</c> on the Agent Host.
    /// </summary>
    public async Task<string?> ProbeHealthAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HealthProbeTimeoutSeconds));
        try
        {
            using var response = await _http.GetAsync("/healthz", timeout.Token);
            return response.IsSuccessStatusCode
                ? null
                : $"server answered /healthz with HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a real shutdown, not a health failure - let the caller unwind
        }
        catch (OperationCanceledException)
        {
            return $"no response within {HealthProbeTimeoutSeconds}s";
        }
        catch (Exception exception)
        {
            return exception.Message;
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

    private static HttpClient CreateHttpClient(EngineOptions options)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(options.ServerUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.Add("X-Client-Id", options.ClientId);
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, EngineVersion.ProductVersion);
        if (!string.IsNullOrWhiteSpace(options.ClientCredential))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ClientCredential);
        return client;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken ct,
        bool allowUpgradeRequired = false)
    {
        using var response = await _http.PostAsJsonAsync(path, request, Json, ct);
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

    public void Dispose() => _http.Dispose();
}

public sealed class EngineTaskServerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class EngineProtocolException(string message) : Exception(message);
