using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Publishing;

/// <summary>Authorizes publishing with a dedicated App key held by the server's secret provider.</summary>
public sealed class GitHubPublishClient(
    IConfiguration configuration, ProjectRegistry projects, ISecretProvider secrets,
    IHttpClientFactory clients, TimeProvider clock)
{
    private const string Settings = "Publishing:GitHubApp";

    internal GitHubRepositoryName RequireRepository(string project, string origin)
    {
        var registration = projects.FindByIdOrDisplayName(project);
        var repository = GitHubRepositoryName.TryParse(origin);
        if (registration is null || registration.Archived || repository is null
            || GitHubRepositoryName.TryParse(configuration[$"{Settings}:Projects:{registration.Id}"])?.Canonical
                != repository.Canonical)
            throw new InvalidOperationException("GitHub publishing is not authorized for this project's repository.");
        return repository;
    }

    internal Task<GitHubPublishDispatch> DispatchAsync(GitHubRepositoryName repository, string workflow, string branch)
        => WithTokenAsync(repository, "actions", RepositoryAccess.Write, async (session, ct) =>
        {
            using var response = await session.SendAsync(HttpMethod.Post,
                $"/actions/workflows/{Uri.EscapeDataString(workflow)}/dispatches", ct,
                JsonContent.Create(new { @ref = branch, return_run_details = true }));
            if (response.StatusCode == HttpStatusCode.NoContent) return new GitHubPublishDispatch(null, null);
            // A successful response has already accepted the dispatch. An unreadable receipt
            // must retain the reservation and let the run lookup recover it.
            try
            {
                using var receipt = await ReadJsonAsync(response, ct);
                return new GitHubPublishDispatch(Id(receipt.RootElement, "workflow_run_id"),
                    StringValue(receipt.RootElement, "html_url"));
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException)
            {
                return new GitHubPublishDispatch(null, null);
            }
        });

    internal Task<long?> FindRunAsync(GitHubRepositoryName repository, PublishWorkflowRun run)
        => WithTokenAsync<long?>(repository, "actions", RepositoryAccess.Read, async (session, ct) =>
        {
            var eventName = run.TargetId == "website" ? "workflow_dispatch" : "push";
            var gitRef = run.GitRef ?? (run.Version is null ? null : $"v{run.Version}");
            var query = $"?event={eventName}&per_page=10";
            if (gitRef is not null) query += $"&branch={Uri.EscapeDataString(gitRef)}";
            if (run.HeadSha is not null) query += $"&head_sha={Uri.EscapeDataString(run.HeadSha)}";
            using var response = await session.SendAsync(HttpMethod.Get,
                $"/actions/workflows/{Uri.EscapeDataString(run.Workflow)}/runs{query}", ct);
            using var document = await ReadJsonAsync(response, ct);
            foreach (var candidate in document.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                if (!TryTime(candidate, "created_at", out var createdAt) || createdAt < run.TriggeredAt.AddMinutes(-1)) continue;
                if (gitRef is not null && StringValue(candidate, "head_branch") != gitRef) continue;
                if (run.HeadSha is not null && StringValue(candidate, "head_sha") != run.HeadSha) continue;
                if (Id(candidate, "id") is { } id) return id;
            }
            return null;
        });

    internal Task<GitHubPublishRunStatus> GetRunAsync(GitHubRepositoryName repository, long runId)
        => WithTokenAsync(repository, "actions", RepositoryAccess.Read, async (session, ct) =>
        {
            using var response = await session.SendAsync(HttpMethod.Get,
                $"/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}", ct);
            using var document = await ReadJsonAsync(response, ct);
            var run = document.RootElement;
            var status = StringValue(run, "status");
            var conclusion = StringValue(run, "conclusion");
            if (Id(run, "id") != runId || string.IsNullOrEmpty(status)
                || (status == "completed" && string.IsNullOrEmpty(conclusion)))
                throw new InvalidOperationException("GitHub returned an incomplete workflow status.");
            return new GitHubPublishRunStatus(status, conclusion, StringValue(run, "html_url"));
        });

    internal Task<bool> IsWebsiteDeploymentInactiveAsync(GitHubRepositoryName repository, DateTimeOffset? reservedAt)
        => WithTokenAsync(repository, "deployments", RepositoryAccess.Read, async (session, ct) =>
        {
            using var response = await session.SendAsync(HttpMethod.Get, "/deployments?environment=github-pages&per_page=1", ct);
            using var document = await ReadJsonAsync(response, ct);
            var deployment = document.RootElement.EnumerateArray().FirstOrDefault();
            if (StringValue(deployment, "environment") != "github-pages"
                || Id(deployment, "id") is not { } id
                || !PublishActionService.DeploymentCanReleaseReservation(reservedAt,
                    TryTime(deployment, "created_at", out var createdAt) ? createdAt : null)) return false;

            using var statuses = await session.SendAsync(HttpMethod.Get,
                $"/deployments/{id.ToString(CultureInfo.InvariantCulture)}/statuses?per_page=1", ct);
            using var statusDocument = await ReadJsonAsync(statuses, ct);
            return StringValue(statusDocument.RootElement.EnumerateArray().FirstOrDefault(), "state") == "inactive";
        });

    private async Task<T> WithTokenAsync<T>(GitHubRepositoryName repository, string permission, string access,
        Func<Session, CancellationToken, Task<T>> operation)
    {
        if (!long.TryParse(configuration[$"{Settings}:AppId"], NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
            || appId <= 0)
            throw new InvalidOperationException("The GitHub publishing App is not configured.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        try
        {
            var key = await secrets.GetForPlatformAsync(
                new SecretReference(SecretScope.Workspace, SecretAccessPolicy.GitHubPublishAppPrivateKeyName),
                "github-publish-token-issue", ct);
            using var http = clients.CreateClient("github-app");
            var app = new GitHubAppClient(http, clock);
            var installation = await app.FindRepositoryInstallationAsync(appId, key.Value, repository, ct);
            if (installation is null or <= 0)
                throw new InvalidOperationException("The GitHub publishing App installation does not cover this repository.");
            var permissions = new Dictionary<string, string> { [permission] = access, ["metadata"] = RepositoryAccess.Read };
            var token = await app.CreateInstallationTokenAsync(appId, key.Value, installation.Value, repository, permissions, ct);
            return await operation(new Session(http, repository, token), ct);
        }
        catch (Exception ex) when (ex is SecretNotFoundException or SecretRevokedException)
        {
            throw new InvalidOperationException("The GitHub publishing App key is unavailable in the Studio secret store.");
        }
        catch (GitHubAppException ex)
        {
            throw new InvalidOperationException($"GitHub publishing authorization failed ({ex.Code}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The GitHub publishing request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new InvalidOperationException("The GitHub publishing request could not be completed.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
        {
            throw new InvalidOperationException("GitHub returned an unreadable publishing response.");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    private static string? StringValue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Id(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0 ? id : null;

    private static bool TryTime(JsonElement element, string name, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(StringValue(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    private sealed class Session(HttpClient http, GitHubRepositoryName repository, GitHubInstallationToken token)
    {
        internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct,
            HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method,
                $"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}{path}")
            { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("agent-studio");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;
            response.Dispose();
            throw new InvalidOperationException($"GitHub publishing {method} request failed with HTTP {(int)response.StatusCode}.");
        }
    }
}

internal sealed record GitHubPublishDispatch(long? RunId, string? Url);
internal sealed record GitHubPublishRunStatus(string Status, string? Conclusion, string? Url);
