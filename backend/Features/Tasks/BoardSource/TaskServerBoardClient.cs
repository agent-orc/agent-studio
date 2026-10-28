using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

public enum TaskServerBoardReadStatus { Ok, NotModified, ProjectMissing }

public sealed record TaskServerBoardRead(TaskServerBoardReadStatus Status, BoardReadModelResponse? Model);

/// <summary>
/// Client for the Task Server board read model and workspace import, and for
/// the task-file mirror and integrated-card query of AGT-3320.
/// <c>Board:TaskServer:BaseUrl</c> names the Task Server; it falls back to
/// <c>TaskServer:BaseUrl</c>. The board client stays separate from the
/// <c>/api/v1</c> plane proxy, so shadowing never turns that proxy on.
/// </summary>
public sealed class TaskServerBoardClient(IHttpClientFactory factory, IConfiguration configuration)
{
    public const string ClientName = "board-task-server";

    public bool IsConfigured => BaseUri(configuration) is not null;

    public async Task<TaskServerBoardRead> GetBoardAsync(string project, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BoardPath(project));
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        using var response = await Client().SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new TaskServerBoardRead(TaskServerBoardReadStatus.NotModified, null);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new TaskServerBoardRead(TaskServerBoardReadStatus.ProjectMissing, null);
        await EnsureSuccessAsync(response, ct);
        var model = await response.Content.ReadFromJsonAsync<BoardReadModelResponse>(ct)
            ?? throw new HttpRequestException("The Task Server answered an empty board.");
        return new TaskServerBoardRead(TaskServerBoardReadStatus.Ok, model);
    }

    /// <summary>Null when the Task Server does not know the project.</summary>
    public async Task<BoardWorkspaceImportResponse?> ImportAsync(
        string project, BoardWorkspaceImportRequest batch, CancellationToken ct)
    {
        using var response = await Client().PutAsJsonAsync(BoardPath(project) + "/workspace-import", batch, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<BoardWorkspaceImportResponse>(ct)
            ?? throw new HttpRequestException("The Task Server answered an empty import result.");
    }

    /// <summary>Null when the Task Server does not know the project.</summary>
    public async Task<TaskFileMirrorResponse?> MirrorTaskFilesAsync(
        string project, TaskFileMirrorRequest batch, CancellationToken ct)
    {
        using var response = await Client().PutAsJsonAsync(ProjectPath(project) + "/task-files/workspace-import", batch, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TaskFileMirrorResponse>(ct)
            ?? throw new HttpRequestException("The Task Server answered an empty task file import result.");
    }

    /// <summary>Cards with a verified integration record; null when the Task Server does not know the project.</summary>
    public async Task<IntegratedCardsResponse?> GetIntegratedCardsAsync(string project, CancellationToken ct)
    {
        using var response = await Client().GetAsync(ProjectPath(project) + "/integrated-cards", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<IntegratedCardsResponse>(ct)
            ?? throw new HttpRequestException("The Task Server answered an empty integrated card list.");
    }

    public static void AddTaskServerBoardClient(IServiceCollection services, IConfiguration configuration)
        => services.AddHttpClient(ClientName, client =>
            {
                if (BaseUri(configuration) is { } baseUri) client.BaseAddress = baseUri;
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            })
            .AddHttpMessageHandler(() => new BearerHandler(configuration));

    internal static Uri? BaseUri(IConfiguration configuration)
    {
        var configured = configuration["Board:TaskServer:BaseUrl"]?.Trim();
        if (string.IsNullOrEmpty(configured)) configured = configuration["TaskServer:BaseUrl"]?.Trim();
        if (string.IsNullOrEmpty(configured)) return null;
        return Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
            ? uri
            : null;
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient(ClientName);
        if (client.BaseAddress is null)
            throw new HttpRequestException("Board:TaskServer:BaseUrl is not configured.");
        return client;
    }

    private static string ProjectPath(string project) => $"api/v1/projects/{Uri.EscapeDataString(project)}";

    private static string BoardPath(string project) => ProjectPath(project) + "/board";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"The Task Server answered {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}",
            null,
            response.StatusCode);
    }

    /// <summary>Reads the bearer per request, so a rotated token file applies without a restart.</summary>
    private sealed class BearerHandler(IConfiguration configuration) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = configuration["Board:TaskServer:AuthToken"]?.Trim();
            var file = configuration["Board:TaskServer:AuthTokenFile"]?.Trim();
            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(file))
                token = File.ReadAllText(Path.GetFullPath(file)).Trim();
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
