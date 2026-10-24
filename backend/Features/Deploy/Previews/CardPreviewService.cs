using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <summary>
/// Reconciles green card deliveries into the normal fenced deploy queue. The
/// preview job fields are the handoff to the run zone: it builds SourceCommit
/// with BuildCommands, deploys the selected template, routes Address, captures
/// a PNG after health succeeds, and reports it with the lease token.
/// </summary>
public sealed class CardPreviewService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectEnvironmentService _environments;
    private readonly ProjectSettingsService _settings;
    private readonly TaskIntegrationStatusService _integration;
    private readonly GitService _git;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly ILogger<CardPreviewService> _logger;
    private readonly object _gate = new();

    public CardPreviewService(TaskScannerService scanner, ProjectEnvironmentService environments,
        ProjectSettingsService settings, TaskIntegrationStatusService integration, GitService git,
        IConfiguration configuration, TimeProvider clock, ILogger<CardPreviewService> logger)
    {
        _scanner = scanner;
        _environments = environments;
        _settings = settings;
        _integration = integration;
        _git = git;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    public void ReconcileOnce()
    {
        lock (_gate)
        {
            var tasks = _scanner.ScanAllAutomationJobs().Concat(_scanner.ScanArchivedJobs())
                .GroupBy(task => task.TaskKey, StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
            var active = tasks.Count(task => CardPreviewStore.Read(task.FolderPath)?.Preview.Status is
                "queued" or "deploying" or "updating" or "ready" or "failed" or "removing");
            var sharedLimit = _configuration.GetValue<int?>("Hosting:MaxEnvironments");
            var hosted = sharedLimit is null ? 0 : _scanner.GetWatchPaths().Sum(project =>
                _environments.Read(project.Name)?.Environments.Count(environment => environment.DeployedArtifact is not null) ?? 0);
            var maxActive = Math.Max(0, sharedLimit ?? _configuration.GetValue("Preview:MaxActive", 20)) - hosted;
            foreach (var task in tasks)
            {
                try
                {
                    var state = CardPreviewStore.Read(task.FolderPath);
                    if ((state is null || state.Preview.Status is "expired" or "removed")
                        && active >= maxActive) continue;
                    if (ReconcileCard(task, state)) active++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Preview reconciliation failed for {TaskKey}", task.TaskKey);
                }
            }
        }
    }

    private bool ReconcileCard(TaskInfo task, CardPreviewState? state)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var subject = ReviewSubjectStore.Read(task.FolderPath);
        var review = ReviewVerificationStore.Read(task.FolderPath);
        var greenSha = CardPreviewPolicy.IsGreenDelivery(subject, review) ? subject!.ResultSha : null;
        var integrated = (state is not null || greenSha is not null)
            && _integration.BuildForCompletion(task).Status == IntegrationStatuses.Integrated;
        var rejected = task.State == TaskStates.Archive;
        var action = CardPreviewPolicy.Decide(state?.Preview, greenSha, integrated, rejected, now);
        if (action == PreviewLifecycleAction.Remove)
        {
            Remove(task, state!, now, integrated || rejected ? "removed" : "expired");
            return false;
        }
        if (action == PreviewLifecycleAction.Sync)
        {
            Sync(task, state!);
            return false;
        }
        if (action == PreviewLifecycleAction.None) return false;
        var spec = Specification(task, subject!);
        if (spec is null)
        {
            if (state is not null) Sync(task, state);
            return false;
        }
        if (state?.Preview.JobId is { } prior)
            _environments.CancelJob(task.ProjectName, prior, "preview-update", abortClaimed: true);
        var job = _environments.QueuePreview(spec);
        CardPreviewStore.Write(task.FolderPath, new(new(spec.Address, state is null or { Preview.Status: "expired" or "removed" }
                ? "queued" : "updating",
            subject!.ResultSha, null, now, now + CardPreviewPolicy.Lifetime, job.Id), spec));
        _scanner.InvalidateCache();
        return state is null || state.Preview.Status is "expired" or "removed";
    }

    private void Sync(TaskInfo task, CardPreviewState state)
    {
        if (state.Preview.JobId is not { } id) return;
        var job = _environments.PreviewJob(task.ProjectName, id);
        if (job is null) return;
        if (job.Status is "failed" or "cancelled"
            && job.FinishedAt is { } finished
            && _clock.GetUtcNow().UtcDateTime >= finished.AddMinutes(5))
        {
            var retry = _environments.QueuePreview(state.Spec);
            CardPreviewStore.Write(task.FolderPath, state with
            {
                Preview = state.Preview with { Status = "queued", ScreenshotUrl = null, JobId = retry.Id },
            });
            _scanner.InvalidateCache();
            return;
        }
        var status = job.Status switch
        {
            "healthy" => "ready",
            "claimed" => "deploying",
            "failed" or "cancelled" => "failed",
            _ => state.Preview.Status == "updating" ? "updating" : "queued",
        };
        if (status == state.Preview.Status && job.ScreenshotUrl == state.Preview.ScreenshotUrl) return;
        CardPreviewStore.Write(task.FolderPath, state with
        {
            Preview = state.Preview with { Status = status, ScreenshotUrl = job.ScreenshotUrl },
        });
        _scanner.InvalidateCache();
    }

    private void Remove(TaskInfo task, CardPreviewState state, DateTime now, string reason)
    {
        if (state.Preview.Status == "removing" && state.Preview.JobId is { } removalId)
        {
            var removal = _environments.PreviewJob(task.ProjectName, removalId);
            if (removal?.Status == "healthy")
            {
                CardPreviewStore.Write(task.FolderPath, state with
                {
                    Preview = state.Preview with { Status = state.RemovalReason ?? reason,
                        ScreenshotUrl = null, JobId = null },
                    RemovalReason = null,
                });
                _scanner.InvalidateCache();
                return;
            }
            if (removal is not null && removal.Status is not ("failed" or "cancelled")) return;
            if (removal?.FinishedAt is { } finished && now < finished.AddMinutes(5)) return;
        }
        else if (state.Preview.JobId is { } previous)
        {
            _environments.CancelJob(task.ProjectName, previous, "preview-cleanup", abortClaimed: true);
        }
        var job = _environments.QueuePreviewRemoval(state.Spec);
        CardPreviewStore.Write(task.FolderPath, state with
        {
            Preview = state.Preview with { Status = "removing", ScreenshotUrl = null, JobId = job.Id },
            RemovalReason = reason,
        });
        _scanner.InvalidateCache();
    }

    private PreviewJobSpec? Specification(TaskInfo task, ReviewSubjectRecord subject)
    {
        var repo = _git.ResolveRepoRootForProject(task.ProjectName);
        if (repo is null) return null;
        var yaml = _git.GetFileAtCommit(repo, subject.ResultSha, ProjectPreparationPaths.Definition);
        var definition = yaml is null ? null : ProjectDefinitionReader.Parse(yaml, repo);
        if (definition is { IsValid: false }) return null;
        var artifacts = definition?.Definition?.Deployment?.Artifacts ?? [];
        var declarations = _environments.DeclarationsAtCommit(task.ProjectName, subject.ResultSha) ?? [];
        var preview = declarations.FirstOrDefault(item => item.Kind == "preview");
        var artifact = artifacts.FirstOrDefault(item => item.Type is "static" or "container");
        var template = preview?.Template ?? artifact?.Type;
        if (template is not ("static" or "container")) return null;
        var protectedSecrets = declarations.Where(item => item.Kind is "stage" or "production")
            .SelectMany(item => item.SecretRefs);
        if (preview is not null && !CardPreviewPolicy.SecretsIsolated(preview.SecretRefs, protectedSecrets))
        {
            _logger.LogWarning("Preview secret references overlap stage or production for {Project}", task.ProjectName);
            return null;
        }
        var commands = definition?.Definition?.Deployment?.PipelineBuild;
        if (commands is null || commands.Count == 0) commands = definition?.Definition?.Commands.Build;
        if (commands is null || commands.Count == 0) commands = _settings.Get(task.ProjectName).BuildProfile?.BuildCmds;
        if (commands is null || commands.Count == 0) return null;
        var card = CardPreviewPolicy.Slug(task.Key ?? task.TaskKey);
        var project = CardPreviewPolicy.Slug(task.ProjectName);
        var domain = _configuration["Preview:BaseDomain"] ?? "preview.internal";
        var address = CardPreviewPolicy.Address(card, project, domain);
        var host = preview?.TargetHostId ?? _configuration["Preview:HostId"] ?? "preview-host";
        var destination = template == "static" ? $"/srv/agent-studio/previews/{project}/{card}" : "8080";
        var origin = _git.ReadOriginUrlAt(repo);
        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && originUri.UserInfo.Length > 0)
            origin = null;
        return new(task.ProjectName, card, subject.ResultSha.ToLowerInvariant(), host, address,
            template, destination, commands, preview?.SecretRefs ?? [], preview?.ContainerPort ?? 8080)
        {
            SourceRepository = origin ?? repo,
            SourceRef = subject.ImmutableResultRef ?? subject.ResultSha,
            ArtifactPath = artifact?.Path ?? (template == "static" ? "dist" : null),
            Dockerfile = artifact?.Dockerfile ?? (template == "container" ? "Dockerfile" : null),
        };
    }
}

public sealed class CardPreviewSweep(CardPreviewService previews, ILogger<CardPreviewSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { previews.ReconcileOnce(); }
            catch (Exception ex) { logger.LogError(ex, "Card preview sweep failed"); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
