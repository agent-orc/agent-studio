using System.Net;
using System.Net.Http.Json;
using AgentStudio.Shared;

namespace AgentStudio.Dispatch;

/// <summary>Recuts use the existing task HTTP API, including its DAG and move guards.</summary>
public interface IDispatchRecutTaskApi
{
    Task<TaskDetail?> ReadAsync(string key, CancellationToken ct);
    Task<TaskInfo> CreateAsync(CreateTaskRequest request, CancellationToken ct);
    Task ReferencesAsync(string key, TaskReferences references, CancellationToken ct, TaskReferences? expected = null);
    Task MoveAsync(string key, string from, string to, CancellationToken ct);
}

public sealed class DispatchRecutTaskApi(HttpClient client, string project, string? recutId = null) : IDispatchRecutTaskApi
{
    private string Card(string key) => $"api/tasks/{Uri.EscapeDataString(key)}";
    private string Scope => "?project=" + Uri.EscapeDataString(project);
    public async Task<TaskDetail?> ReadAsync(string key, CancellationToken ct)
    {
        using var response = await client.GetAsync(Card(key) + Scope, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TaskDetail>(ct);
    }
    public async Task<TaskInfo> CreateAsync(CreateTaskRequest request, CancellationToken ct)
    {
        // The deterministic slug belongs to this receipt. Recovery never creates another card.
        if (await ReadAsync(request.Id, ct) is { } existing)
        {
            if (existing.Info.CreationSource != request.CreationSource)
                throw new InvalidOperationException("Recut slug is already owned by another source.");
            if (existing.Info.Title != request.Title || existing.PromptMarkdown != request.PromptMarkdown)
                throw new InvalidOperationException("The recovered recut card has a later title or prompt edit.");
            return existing.Info;
        }
        using var response = await client.PostAsJsonAsync("api/tasks/", request, ct);
        response.EnsureSuccessStatusCode();
        return (await ReadAsync(request.Id, ct))?.Info ?? throw new InvalidOperationException("Created recut card is unavailable.");
    }
    public async Task ReferencesAsync(string key, TaskReferences references, CancellationToken ct, TaskReferences? expected = null)
    {
        var request = new SetTaskReferencesRequest { DependsOn = references.DependsOn, RelatedTo = references.RelatedTo,
            BlockedBy = references.BlockedBy, Supersedes = references.Supersedes, FollowUpOf = references.FollowUpOf,
            RaisedFollowUps = references.RaisedFollowUps, Workbenches = references.Workbenches, ExpectedReferences = expected };
        using var response = await client.PutAsJsonAsync(Card(key) + "/references" + Scope, request, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task MoveAsync(string key, string from, string to, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(Card(key) + "/move" + Scope,
            new MoveJobRequest { TargetState = to, ExpectedSourceState = from, RequireUnstarted = true,
                DispatchRecutId = to == TaskStates.Archive ? recutId : null,
                Reason = "Packer recut with a recorded 24-hour undo" }, ct);
        response.EnsureSuccessStatusCode();
    }
}
