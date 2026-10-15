using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Authority that holds the batch <see cref="GateSubject"/> runner gate hosts
/// claim (AGT-3055). It is the authority the runners talk to: the monolith's
/// own gate store, or the standalone Task Server when the versioned plane is
/// proxied there. A subject minted anywhere else would never be claimed.
/// </summary>
public interface IBatchGateSubjectAuthority
{
    string Name { get; }
    Task<GateStatus> CreateAsync(CreateGateSubjectRequest request, CancellationToken ct);
    Task<GateStatus?> GetAsync(string subjectId, CancellationToken ct);
    Task CancelAsync(string subjectId, CancellationToken ct);
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
