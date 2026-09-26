using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Server.Features.Dispatch;

public interface IOperationPermitAuthority
{
    Task<DateTimeOffset?> ValidateAsync(string principalId, OperationCommand command, CancellationToken cancellationToken);
}

public sealed class OperationPermitAuthority(HttpClient client, IConfiguration configuration) : IOperationPermitAuthority
{
    public async Task<DateTimeOffset?> ValidateAsync(string principalId, OperationCommand command, CancellationToken cancellationToken)
    {
        if (command.Subject is null) return command.Deadline;
        var url = configuration["Operations:PermitIntrospectionUrl"];
        var tokenFile = configuration["Operations:PermitCredentialFile"];
        // Until a Task Server authority endpoint is configured, task-linked work
        // stays closed. A local assertion or agent-supplied fence is never authority.
        if (url is null || tokenFile is null) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile, cancellationToken)).Trim());
            request.Headers.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            request.Content = JsonContent.Create(new PermitIntrospectionRequest(command.Permit!, principalId,
                command.Actor, command.Subject, command.OperationId, command.Version, command.InputDigest,
                command.AgentId, OperationsProtocol.Audience, command.CorrelationId, command.Deadline));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<PermitIntrospectionResponse>(cancellationToken);
            return result is { Active: true } ? result.ValidUntil : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }
    }
}
