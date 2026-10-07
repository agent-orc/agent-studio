using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Setup;

/// <summary>
/// Authenticated calls from setup to a Task Server management API. Every
/// /api/v1 request carries the protocol header the Task Server requires.
/// </summary>
internal static class AuthorityApi
{
    public const string ClientId = "agent-studio-setup";

    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpMethod method, Uri uri, string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        request.Headers.Add(TaskServerProtocol.ClientVersionHeaderName, ReleaseArtifacts.CurrentVersion());
        request.Headers.Add("X-Client-Id", ClientId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"The Task Server at {uri.GetLeftPart(UriPartial.Authority)} rejected the management token " +
                $"({(int)response.StatusCode}). Use an unrevoked management-scope token file and retry.");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public static async Task<string> ReadTokenAsync(string tokenFile, string label)
    {
        SetupSecrets.RequireProtected(tokenFile, label);
        var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
        if (token.Length == 0) throw new InvalidDataException($"{label} is empty.");
        return token;
    }
}
