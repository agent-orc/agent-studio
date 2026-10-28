using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Authority that holds the batch, merge and promotion <see cref="GateSubject"/>
/// runner gate hosts claim (AGT-3055, DP-S4). It is the authority the runners talk to: the monolith's
/// own gate store, or the standalone Task Server when the versioned plane is
/// proxied there. A subject minted anywhere else would never be claimed.
/// </summary>
public interface IBatchGateSubjectAuthority
{
    string Name { get; }
    Task<GateStatus> CreateAsync(CreateGateSubjectRequest request, CancellationToken ct);
    Task<GateStatus?> GetAsync(string subjectId, CancellationToken ct);
    Task CancelAsync(string subjectId, CancellationToken ct);

    /// <summary>
    /// Stores one immutable verification bundle (DP-S4) and returns its
    /// locator. Only an authority with a bundle artifact endpoint supports it.
    /// </summary>
    Task<GateBundleLocator> UploadBundleAsync(string repositoryId, string path, string sha256,
        long sizeBytes, TimeSpan budget, CancellationToken ct);
}

/// <summary>Monolith authority: <c>/api/v1/gates</c> is served from <see cref="MonolithGateStore"/>.</summary>
public sealed class MonolithBatchGateSubjectAuthority(MonolithGateStore gates) : IBatchGateSubjectAuthority
{
    public string Name => "monolith";

    public Task<GateStatus> CreateAsync(CreateGateSubjectRequest request, CancellationToken ct)
        => Task.FromResult(gates.Create(request));

    public Task<GateStatus?> GetAsync(string subjectId, CancellationToken ct)
        => Task.FromResult(gates.GetStatus(subjectId));

    public Task CancelAsync(string subjectId, CancellationToken ct)
    {
        gates.Cancel(subjectId);
        return Task.CompletedTask;
    }

    public Task<GateBundleLocator> UploadBundleAsync(string repositoryId, string path, string sha256,
        long sizeBytes, TimeSpan budget, CancellationToken ct)
        => throw new NotSupportedException(
            "The monolith gate authority has no bundle artifact endpoint; claimed merge and promotion gates require TaskServer:BaseUrl.");
}

/// <summary>
/// Task Server authority: with <c>TaskServer:BaseUrl</c> set, runner gate
/// claims reach the Task Server, so the batch subject is created there with
/// the proxy's service credential (scope <c>orchestration.write</c>).
/// </summary>
public sealed class TaskServerBatchGateSubjectAuthority(Func<HttpClient> client) : IBatchGateSubjectAuthority
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public string Name => "task-server";

    public async Task<GateStatus> CreateAsync(CreateGateSubjectRequest request, CancellationToken ct)
    {
        using var response = await client().PostAsJsonAsync("/api/v1/gates/subjects", request, WireJson, ct)
            .ConfigureAwait(false);
        return await ReadAsync(response, "create", ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Task Server returned no batch gate subject.");
    }

    public async Task<GateStatus?> GetAsync(string subjectId, CancellationToken ct)
    {
        using var response = await client().GetAsync(
            $"/api/v1/gates/subjects/{Uri.EscapeDataString(subjectId)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync(response, "status", ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(string subjectId, CancellationToken ct)
    {
        using var response = await client().PostAsync(
            $"/api/v1/gates/subjects/{Uri.EscapeDataString(subjectId)}/cancel", null, ct).ConfigureAwait(false);
        await ReadAsync(response, "cancel", ct).ConfigureAwait(false);
    }

    public async Task<GateBundleLocator> UploadBundleAsync(string repositoryId, string path, string sha256,
        long sizeBytes, TimeSpan budget, CancellationToken ct)
    {
        var http = client();
        // A fresh factory client: a bundle may need longer than the proxy default.
        http.Timeout = budget;
        await using var content = File.OpenRead(path);
        using var body = new StreamContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = sizeBytes;
        using var response = await http.PostAsync(
            $"/api/v1/gates/bundles/{Uri.EscapeDataString(sha256)}?sizeBytes={sizeBytes}&repositoryId={Uri.EscapeDataString(repositoryId)}",
            body, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Task Server gate bundle upload failed ({(int)response.StatusCode}): {text[..Math.Min(text.Length, 400)]}",
                null, response.StatusCode);
        return JsonSerializer.Deserialize<GateBundleLocator>(text, WireJson)
            ?? throw new InvalidDataException("Task Server returned no gate bundle locator.");
    }

    private static async Task<GateStatus?> ReadAsync(HttpResponseMessage response, string operation,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Task Server batch gate {operation} failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 400)]}",
                null, response.StatusCode);
        return JsonSerializer.Deserialize<GateStatus>(body, WireJson);
    }
}
