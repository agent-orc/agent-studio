using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// Where package subjects live: the monolith's own review store when the v1
/// review plane is served locally, else the standalone Task Server that the
/// review runners claim from. Both serve the same package contract.
/// </summary>
public interface IReviewPackageAuthority
{
    string Name { get; }
    Task<string?> PackageOfAsync(string taskKey, string runAttemptId, CancellationToken ct);
    Task<ReviewPackageSubjectDto> CreateAsync(CreateReviewPackageSubjectRequest request, CancellationToken ct);
    Task<IReadOnlyList<ReviewPackageRecordDto>> ListAsync(string project, CancellationToken ct);
}

public sealed class MonolithReviewPackageAuthority(ReviewPackageStore store) : IReviewPackageAuthority
{
    public string Name => "monolith";

    public Task<string?> PackageOfAsync(string taskKey, string runAttemptId, CancellationToken ct)
        => Task.FromResult(store.PackageOf(taskKey, runAttemptId));

    public Task<ReviewPackageSubjectDto> CreateAsync(CreateReviewPackageSubjectRequest request, CancellationToken ct)
        => Task.FromResult(store.Create(request));

    public Task<IReadOnlyList<ReviewPackageRecordDto>> ListAsync(string project, CancellationToken ct)
        => Task.FromResult(store.List(project));
}

public sealed class TaskServerReviewPackageAuthority(Func<HttpClient> client) : IReviewPackageAuthority
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public string Name => "task-server";

    public async Task<string?> PackageOfAsync(string taskKey, string runAttemptId, CancellationToken ct)
    {
        using var response = await client().GetAsync(
            $"/api/v1/review-packages/members/{Uri.EscapeDataString(taskKey)}/{Uri.EscapeDataString(runAttemptId)}",
            ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return (await ReadAsync<ReviewPackageMembershipDto>(response, "membership", ct).ConfigureAwait(false))?.PackageId;
    }

    public async Task<ReviewPackageSubjectDto> CreateAsync(CreateReviewPackageSubjectRequest request, CancellationToken ct)
    {
        using var response = await client().PostAsJsonAsync("/api/v1/review-packages", request, WireJson, ct)
            .ConfigureAwait(false);
        return await ReadAsync<ReviewPackageSubjectDto>(response, "create", ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Task Server returned no review package subject.");
    }

    public async Task<IReadOnlyList<ReviewPackageRecordDto>> ListAsync(string project, CancellationToken ct)
    {
        using var response = await client().GetAsync(
            $"/api/v1/review-packages?project={Uri.EscapeDataString(project)}", ct).ConfigureAwait(false);
        return await ReadAsync<ReviewPackageRecordDto[]>(response, "list", ct).ConfigureAwait(false) ?? [];
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new ReviewPackageConflictException("task-server-conflict", text[..Math.Min(text.Length, 400)]);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Task Server review package {operation} failed ({(int)response.StatusCode}): {text[..Math.Min(text.Length, 400)]}");
        return JsonSerializer.Deserialize<T>(text, WireJson);
    }
}
