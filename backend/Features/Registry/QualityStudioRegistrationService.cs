using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using AgentStudio.Projects;
using AgentStudio.Secrets;
using AgentStudio.Shared;
using AgentStudio.Security;

namespace AgentStudio.Registry;

/// <summary>Maintains the durable Quality Studio repository mapping for each Studio project.</summary>
public sealed class QualityStudioRegistrationService(
    HttpClient client, IConfiguration configuration, ISecretProvider secrets,
    ProjectRegistry projects, ProjectSettingsService settings,
    ILogger<QualityStudioRegistrationService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool Connected => !string.IsNullOrWhiteSpace(configuration["QualityStudio:BaseUrl"]);

    public async Task<QualityStudioRegistration> ReconcileProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await gate.WaitAsync(timeout.Token);
        try
        {
            var project = projects.FindById(projectId)
                ?? throw new KeyNotFoundException($"Unknown projectId '{projectId}'");
            var current = settings.Get(project.DisplayName).QualityStudio;
            if (!Connected)
                return Save(project, current?.RepositoryId, "not-registered", "Quality Studio is not connected.");
            if (project.Archived && current?.State == "archived") return current;

            try
            {
                var origin = Origin();
                var token = await TokenAsync(timeout.Token);
                var externalRef = $"agent-studio:{project.Id}";
                var repositoryId = current?.State == "archived" ? null : current?.RepositoryId;
                if (string.IsNullOrWhiteSpace(repositoryId))
                    repositoryId = await LookupAsync(origin, token, externalRef, timeout.Token);

                if (project.Archived)
                {
                    if (repositoryId is not null)
                    {
                        using var response = await SendAsync(origin, token, HttpMethod.Delete,
                            $"api/repos/{Uri.EscapeDataString(repositoryId)}", null, timeout.Token);
                        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                            return Save(project, repositoryId, "archive-pending", await ReasonAsync(response, timeout.Token));
                    }
                    return Save(project, repositoryId, "archived", null);
                }

                if (repositoryId is not null)
                    return Save(project, repositoryId, "registered", null);

                var rootPath = CheckoutPath(project.Id);
                using var registration = await SendAsync(origin, token, HttpMethod.Post, "api/repos", new
                {
                    id = project.Id.ToLowerInvariant(),
                    displayName = project.DisplayName,
                    rootPath,
                    externalRefs = new[] { new { system = "agent-studio", id = project.Id } },
                }, timeout.Token);
                if (registration.IsSuccessStatusCode)
                    return Save(project, project.Id.ToLowerInvariant(), "registered", null);
                if (registration.StatusCode == HttpStatusCode.Conflict)
                {
                    repositoryId = await LookupAsync(origin, token, externalRef, timeout.Token);
                    if (repositoryId is not null)
                        return Save(project, repositoryId, "registered", null);
                }
                return Save(project, null, "not-registered", await ReasonAsync(registration, timeout.Token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "quality-studio-registration-failed projectId={ProjectId}", project.Id);
                return Save(project, current?.RepositoryId,
                    project.Archived ? "archive-pending" : "not-registered", FailureReason(ex));
            }
        }
        finally { gate.Release(); }
    }

    private QualityStudioRegistration Save(ProjectRecord project, string? repositoryId, string state, string? reason)
    {
        var result = new QualityStudioRegistration(repositoryId, state, reason);
        settings.SetQualityStudioRegistration(project.DisplayName, result);
        return result;
    }

    private Uri Origin()
    {
        var raw = configuration["QualityStudio:BaseUrl"];
        if (!Uri.TryCreate(raw?.TrimEnd('/') + "/", UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Quality Studio URL is invalid.");
        return origin;
    }

    private string CheckoutPath(string projectId)
    {
        var template = configuration["QualityStudio:CheckoutPathTemplate"]
            ?? "${RUNNER_WORKDIR}/{projectId}/repo";
        var workdir = configuration["QualityStudio:RunnerWorkdir"]
            ?? Environment.GetEnvironmentVariable("RUNNER_WORKDIR");
        if (template.Contains("${RUNNER_WORKDIR}", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(workdir))
            throw new InvalidOperationException("RUNNER_WORKDIR is not configured for Quality Studio registration.");
        var path = template.Replace("${RUNNER_WORKDIR}", workdir ?? string.Empty, StringComparison.Ordinal)
            .Replace("{projectId}", projectId, StringComparison.Ordinal);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Quality Studio checkout path template must resolve to an absolute path.");
        return Path.GetFullPath(path);
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (!SecurityProfiles.IsNetworked(configuration)
            && !string.IsNullOrWhiteSpace(configuration["QualityStudio:ApiToken"]))
            return configuration["QualityStudio:ApiToken"]!;
        var reference = configuration["QualityStudio:ApiTokenReference"]
            ?? "secret://workspace/quality-studio-api-token";
        try
        {
            return (await secrets.GetForPlatformAsync(SecretReference.Parse(reference),
                "quality-studio-registration", ct)).Value;
        }
        catch (SecretNotFoundException) when (!string.IsNullOrWhiteSpace(configuration["QualityStudio:ApiToken"]))
        {
            // Existing local installations may still use the legacy token setting.
            return configuration["QualityStudio:ApiToken"]!;
        }
    }

    private async Task<string?> LookupAsync(Uri origin, string token, string externalRef, CancellationToken ct)
    {
        using var response = await SendAsync(origin, token, HttpMethod.Get,
            $"api/v1/repos?externalRef={Uri.EscapeDataString(externalRef)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            root = root.GetArrayLength() == 0 ? default : root[0];
        else if (root.TryGetProperty("repositories", out var repositories))
            root = repositories.GetArrayLength() == 0 ? default : repositories[0];
        else if (root.TryGetProperty("repository", out var repository))
            root = repository;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id)
            ? id.GetString() : null;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri origin, string token, HttpMethod method,
        string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(origin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Client-Id", configuration["QualityStudio:ClientId"] ?? "agent-studio");
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, ct);
    }

    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            foreach (var key in new[] { "code", "reason", "error" })
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "registration-failed";
        }
        catch (JsonException ex) { SilentCatch.Note(ex, "Quality Studio error response was not JSON"); }
        return $"Quality Studio returned HTTP {(int)response.StatusCode}.";
    }

    private static string FailureReason(Exception ex) => ex switch
    {
        SecretNotFoundException => "Quality Studio service credential is missing.",
        OperationCanceledException => "Quality Studio did not respond in time.",
        HttpRequestException => "Quality Studio is unreachable.",
        _ => ex.Message,
    };
}

public sealed class QualityStudioRegistrationReconciler(
    QualityStudioRegistrationService registration, ProjectRegistry projects,
    ILogger<QualityStudioRegistrationReconciler> logger) : BackgroundService
{
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    public void Queue(string projectId) => queue.Writer.TryWrite(projectId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timerTask = EnqueuePeriodicAsync(stoppingToken);
        try
        {
            await foreach (var projectId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { await registration.ReconcileProjectAsync(projectId, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "quality-studio-reconcile-failed projectId={ProjectId}", projectId);
                }
            }
        }
        finally
        {
            try { await timerTask; }
            catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
            { SilentCatch.Note(ex, "Quality Studio reconcile timer stopped with the host"); }
        }
    }

    private async Task EnqueuePeriodicAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            if (registration.Connected)
                foreach (var project in projects.List()) Queue(project.Id);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
