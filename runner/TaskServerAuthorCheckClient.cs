using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed partial class TaskServerClient
{
    internal async Task<AuthorCheckProfile> AuthorCheckProfileAsync(AuthorCheckParent parent, CancellationToken ct)
        => await PostJsonAsync<AuthorCheckParent, AuthorCheckProfile>("/api/v1/author-checks/profile", parent, ct)
           ?? throw new InvalidOperationException("Author-check profile is missing.");

    internal async Task<AuthorCheckReceipt> SubmitAuthorCheckAsync(
        AuthorCheckSubmission submission, string bundle, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/author-checks/submit");
        message.Headers.Add("X-Author-Check", Convert.ToBase64String(
            JsonSerializer.SerializeToUtf8Bytes(submission, TaskServerContractJson)));
        message.Content = new StreamContent(File.OpenRead(bundle));
        message.Content.Headers.ContentLength = submission.Snapshot.Bytes;
        using var response = await _http.SendAsync(message, ct);
        await RequireAuthorCheckResponseAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AuthorCheckReceipt>(TaskServerContractJson, ct))!;
    }

    internal async Task<AuthorCheckReceipt> AuthorCheckReceiptAsync(
        AuthorCheckParent parent, string id, CancellationToken ct)
        => await PostJsonAsync<AuthorCheckParent, AuthorCheckReceipt>($"/api/v1/author-checks/{id}/receipt", parent, ct)
           ?? throw new InvalidOperationException("Author-check receipt is missing.");

    internal async Task DownloadAuthorCheckSourceAsync(
        string attemptId, AuthorCheckSourceGrant grant, int bytes, string path, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/author-checks/attempts/{attemptId}/source");
        message.Content = JsonContent.Create(grant, options: TaskServerContractJson);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireAuthorCheckResponseAsync(response, ct);
        if (response.Content.Headers.ContentLength != bytes || bytes is < 1 or > AuthorCheckProtocol.BundleLimit)
            throw new InvalidDataException("Author-check source length differs from its claim.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        await source.CopyToAsync(file, ct);
    }

    internal async Task<AuthorCheckReceipt> SettleAuthorCheckAsync(
        string attemptId, AuthorCheckSettlement report, CancellationToken ct)
        => await PostJsonAsync<AuthorCheckSettlement, AuthorCheckReceipt>($"/api/v1/author-checks/attempts/{attemptId}/settle", report, ct)
           ?? throw new InvalidOperationException("Author-check settlement receipt is missing.");

    internal Task ReleaseContainedAuthorCheckAsync(string attemptId, PostWorkAuthority authority, CancellationToken ct)
        => PostJsonWithoutResponseAsync($"/api/v1/author-checks/attempts/{attemptId}/contained", authority, ct);

    private static async Task RequireAuthorCheckResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new TaskServerException((int)response.StatusCode, $"Author-check request failed: {Trim(body)}",
            TryReadApiErrorCode(body));
    }
}
