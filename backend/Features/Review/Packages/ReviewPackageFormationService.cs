using AgentStudio.OrchestrationDecisions;
using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Review;

/// <summary>
/// Package formation tick beside review admission (AGT-3400 S4). For every
/// project with <c>review.package: shadow</c> it reads the Dossier cards,
/// applies <see cref="ReviewPackageFormationPolicy"/>, logs each decision and
/// creates the package subject of each formed package. It reads tasks and
/// writes only to the package store: no lane, verdict, card subject or
/// settlement changes.
/// </summary>
public sealed class ReviewPackageFormationService(
    TaskScannerService scanner,
    ProjectSettingsService settings,
    IReviewPackageDeliveryFacts deliveries,
    IReviewPackageAuthority packages,
    ReviewPackageStore store,
    ReviewPackagePlanBuilder plans,
    TimeProvider clock,
    ILogger<ReviewPackageFormationService> logger)
{
    /// <summary>A card check older than this no longer joins a package, so enabling shadow does not replay history.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    private static readonly HashSet<string> CodingLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Ready, TaskStates.Progress, TaskStates.FailedPickup, TaskStates.CodeNotComplete,
    };

    public async Task<IReadOnlyList<ReviewPackageFormationDecision>> TickAsync(CancellationToken ct)
    {
        var decisions = new List<ReviewPackageFormationDecision>();
        var projects = settings.GetAll()
            .Where(entry => ReviewPackageSetting.Resolve(entry.Value) == ReviewPackageModes.Shadow)
            .Select(entry => entry.Key)
            .ToArray();
        if (projects.Length == 0) return decisions;
        var tasks = scanner.ScanAllAutomationJobs();
        foreach (var project in projects)
        {
            try
            {
                decisions.AddRange(await TickProjectAsync(project, tasks
                    .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(task.Dossier?.Path))
                    .ToArray(), ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidDataException or ArgumentException
                                           or HttpRequestException or ReviewPackageConflictException)
            {
                logger.LogWarning(ex, "review-package-formation-failed project={Project}", project);
            }
        }
        return decisions;
    }

    private async Task<IReadOnlyList<ReviewPackageFormationDecision>> TickProjectAsync(
        string project, IReadOnlyList<TaskInfo> tasks, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cards = new Dictionary<string, (TaskInfo Task, ReviewPackageDelivery? Delivery)>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ReviewPackageCandidate>();
        foreach (var task in tasks)
        {
            var key = Key(task);
            var delivery = CodingLanes.Contains(task.State) ? null : deliveries.Read(task, now - Lookback);
            if (delivery is not null
                && await packages.PackageOfAsync(key, delivery.RunAttemptId, ct).ConfigureAwait(false) is not null)
                continue;
            var stage = Stage(task, delivery, now);
            if (stage is null) continue;
            cards[key] = (task, delivery);
            candidates.Add(new ReviewPackageCandidate(key, task.Dossier!.Path, stage,
                stage == ReviewPackageCandidateStages.CardCheckPassed ? delivery!.CardCheckPassedAtUtc : null,
                delivery?.Paths ?? [], delivery?.ChangedLines ?? 0, delivery?.SizeKnown ?? false,
                task.Dossier.ImplementationIndex,
                task.References.DependsOn.Select(reference => reference.Key).ToArray()));
        }

        var decisions = AgentStudio.Runner.MonolithTransitionAdapter.ReviewPackageFormation(
            new ReviewPackageFormationInput(now, candidates, ReviewPackageLimits.Default)).ToList();
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            ReviewPackageSubjectDto? created = null;
            if (decision.Decision == ReviewPackageFormationDecisions.Form)
            {
                created = await CreateAsync(project, decision, cards, ct).ConfigureAwait(false);
                if (created is null)
                    decisions[index] = decision = decision with
                    {
                        Decision = ReviewPackageFormationDecisions.PerCard,
                        Reason = "no per-card review plan names a model or integration ref for the package aspects",
                    };
            }
            store.AppendFormationDecision(project, decision);
            if (created is not null)
                logger.LogInformation(
                    "review-package-formed project={Project} package={Package} dossier={Dossier} members={Members} reason={Reason}",
                    project, created.PackageId, created.DossierPath, string.Join(",", decision.Members), decision.Reason);
        }
        return decisions;
    }

    /// <summary>
    /// Formation stage of a Dossier card: coding lanes are never waited for,
    /// a current delivery whose card check passed inside the lookback is
    /// ready, and a card in Auto Review without one is in review.
    /// </summary>
    internal static string? Stage(TaskInfo task, ReviewPackageDelivery? delivery, DateTimeOffset now)
    {
        if (CodingLanes.Contains(task.State)) return ReviewPackageCandidateStages.Coding;
        if (delivery?.CardCheckPassedAtUtc is { } passed)
            return now - passed <= Lookback ? ReviewPackageCandidateStages.CardCheckPassed : null;
        return task.State == TaskStates.AutoReview ? ReviewPackageCandidateStages.InReview : null;
    }

    private async Task<ReviewPackageSubjectDto?> CreateAsync(string project, ReviewPackageFormationDecision decision,
        IReadOnlyDictionary<string, (TaskInfo Task, ReviewPackageDelivery? Delivery)> cards, CancellationToken ct)
    {
        var members = decision.Members.Select((key, order) =>
        {
            var (task, delivery) = cards[key];
            return new ReviewPackageMemberDto(key, delivery!.RunAttemptId, delivery.ResultSha, order,
                task.Id, task.Dossier?.ImplementationIndex,
                task.References.DependsOn.Select(reference => reference.Key).ToArray(),
                delivery.CardCheckAttemptId, delivery.CardCheckPassedAtUtc,
                delivery.Paths.Count, delivery.ChangedLines, task.Title,
                string.Join("\n", task.AcceptanceScope?.Criteria ?? []), delivery.ResultRef);
        }).ToArray();
        var ordered = decision.Members.Select(key => cards[key].Delivery!).ToArray();
        var latest = ordered.LastOrDefault(delivery => delivery.CardPlan?.IntegrationRef is not null) ?? ordered[^1];
        var plan = plans.Build(project, decision.DossierPath, members,
            ordered.Select(delivery => delivery.CardPlan).OfType<ReviewPlanDto>().ToArray(),
            latest.RepositoryId, latest.RepositoryUrl, latest.CardPlan?.IntegrationRef);
        if (plan is null) return null;
        var digest = ReviewPackageMembershipDigest.Compute(decision.DossierPath, members);
        return await packages.CreateAsync(new CreateReviewPackageSubjectRequest(project, decision.DossierPath,
            ReviewPackageModes.Shadow, members, plan, ReviewPackageMembershipDigest.IdempotencyKey(digest)), ct)
            .ConfigureAwait(false);
    }

    private static string Key(TaskInfo task) => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key;
}

/// <summary>Runs the formation tick every 30 seconds, like the batch gate pilot.</summary>
public sealed class ReviewPackageFormationHostedService(
    ReviewPackageFormationService formation,
    ILogger<ReviewPackageFormationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await formation.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "review-package-formation-tick-failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
