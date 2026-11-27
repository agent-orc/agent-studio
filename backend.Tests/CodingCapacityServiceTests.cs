using AgentStudio.Operator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodingCapacityServiceTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "watched")]
    [InlineData(true, "watched")]
    [InlineData(false, "second")]
    [InlineData(true, "second")]
    public void Fleet_counts_watched_projects_and_keeps_unwatched_dependency_heads(bool scoped, string? duplicateProject)
    {
        var root = Path.Combine(Path.GetTempPath(), "coding-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
                ["WatchPaths:0:Name"] = "watched",
                ["WatchPaths:0:Path"] = root,
                ["WatchPaths:1:Name"] = "second",
                ["WatchPaths:1:Path"] = Path.Combine(root, "second"),
            }).Build();
            TaskInfo Card(string key, string project, string state) => new()
            {
                Id = key, Key = key, ProjectName = project, State = state,
                WatchPath = Path.Combine(root, project), FolderPath = Path.Combine(root, project, state, key),
            };
            var head = Card("HEAD", "unwatched", TaskStates.Escalated);
            var waiting = Card("WAITING", "watched", TaskStates.Ready) with
            {
                References = new() { DependsOn = [new("HEAD")] },
            };
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
            var cards = new List<TaskInfo> { head, waiting, Card("OUTSIDE", "unwatched", TaskStates.Ready) };
            if (duplicateProject is not null)
                cards.Add(Card("waiting", duplicateProject, TaskStates.Ready) with { Id = "stale-copy" });
            var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config,
                () => cards, timeProvider: clock);
            scanner.SetIndexCache(cache);
            cache.GetSnapshot();
            var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
            var quota = new QuotaService(NullLogger<QuotaService>.Instance, [], config,
                new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance), clock: clock);
            var selection = new ModelSelectionService(quota,
                new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config),
                new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance),
                new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, config), settings, timeProvider: clock);
            var service = new CodingCapacityService(scanner, settings,
                new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new V1ReviewExecutorRegistry(clock),
                quota, selection, new OverlapDispatchGuard(clock,
                    NullLogger<OverlapDispatchGuard>.Instance, new OverlapPredictionLedger(Path.Combine(root, "overlap"))),
                new CapacityIdleTracker(), new TaskListGitProjectionCache());

            var report = service.Read(clock.GetUtcNow().UtcDateTime,
                scoped ? project => project == "watched" : null, includeQuota: false)!;

            Assert.Equal(1, report.Fleet.Ready);
            Assert.Equal(report.Projects.Sum(project => project.Ready), report.Fleet.Ready);
            Assert.Equal("WAITING", Assert.Single(report.Fleet.Cards).CardKey);
            Assert.Contains(Assert.Single(report.Fleet.Cards).Blocks,
                block => block.Class == "dependency" && block.Dependency == "HEAD");
            var watched = Assert.Single(report.Projects, project => project.Project == "watched");
            Assert.Equal(1, watched.Ready);
            Assert.Equal(0, watched.ClaimableNow);
            if (!scoped) Assert.Equal(0, Assert.Single(report.Projects, project => project.Project == "second").Ready);
            if (scoped) Assert.Empty(report.Fleet.Heads);
            else Assert.Equal("HEAD", Assert.Single(report.Fleet.Heads).CardKey);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
