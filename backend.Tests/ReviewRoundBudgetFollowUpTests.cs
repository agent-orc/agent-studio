using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewRoundBudgetFollowUpTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "review-budget-card-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public void DegradedDelivery_CreatesExactlyOneLinkedCardAcrossReportReplay()
    {
        var watchPath = Path.Combine(_workspace, "projects", "demo");
        Directory.CreateDirectory(watchPath);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
            }).Build();
        var scanner = new TaskScannerService(config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var sourceId = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Id = "source",
            Title = "Source delivery",
            WatchPath = watchPath,
            TargetState = TaskStates.AutoReview,
            PromptMarkdown = "Implement the source delivery.",
        });
        Assert.NotNull(sourceId);
        var source = Assert.IsType<TaskInfo>(scanner.FindJob(sourceId!, watchPath));
        var verdicts = new ReviewVerdictDto[]
        {
            new("code-quality", "block", "quality:concerns", "Different method has an open issue.",
                EvidenceChecked: "backend/Feature.cs", Missing: "Fix the method."),
        };
        var budget = new ReviewRoundBudgetDecision(3, 4, ["code-quality"], "code-quality");
        var report = new ReviewReportRequest(
            "executor", "instance", "lease", 1, "key", "Pass", null,
            "Review budget spent.", null!, null!, [], [], verdicts);
        RemoteReviewSettlementJournal.Write(source.FolderPath, new RemoteReviewSettlementEntry
        {
            AttemptId = "review-3",
            TaskKey = source.Key ?? source.Id,
            IdempotencyKey = "key",
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Report = report,
            ReviewBudgetDecision = budget,
            ReviewBudgetOriginalVerdicts = verdicts,
        });
        var journal = Assert.IsType<RemoteReviewSettlementEntry>(
            RemoteReviewSettlementJournal.Read(source.FolderPath, "review-3").Entry);

        Assert.True(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(
            source, journal, mutations, scanner));
        var first = Assert.Single(ReviewRoundBudgetStore.Read(source.FolderPath).Rounds).FollowUpTaskKey;
        Assert.True(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(
            source, journal, mutations, scanner));
        var replay = Assert.Single(ReviewRoundBudgetStore.Read(source.FolderPath).Rounds).FollowUpTaskKey;
        ReviewRoundBudgetStore.Record(source.FolderPath, ReviewRoundBudgetStore.Read(source.FolderPath),
            new DeliveredReviewRound("review-4", ["code-quality"], ["code-quality"],
                SpentBy: "code-quality"));
        var laterRound = V1ReviewPlaneEndpoints.CreateReviewBudgetFollowUpCard(
            source, "review-4", verdicts, budget with { RoundNumber = 4 }, mutations, scanner);

        Assert.NotNull(first);
        Assert.Equal(first, replay);
        Assert.Equal(first, laterRound);
        var created = Assert.Single(scanner.ScanAllJobs(), task =>
            task.CreationSource == "review-budget-follow-up");
        Assert.Equal(first, created.Key ?? created.Id);
        Assert.Contains(source.Key ?? source.Id, created.References?.FollowUpOf ?? []);
        var updatedSource = Assert.IsType<TaskInfo>(scanner.FindJob(source.Id, watchPath));
        Assert.Equal([first!], updatedSource.References?.RaisedFollowUps);
        Assert.All(ReviewRoundBudgetStore.Read(updatedSource.FolderPath).Rounds,
            round => Assert.Equal(first, round.FollowUpTaskKey));
    }
}
