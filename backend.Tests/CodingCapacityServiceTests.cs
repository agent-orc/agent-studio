using AgentStudio.Operator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodingCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fleet_counts_watched_projects_and_keeps_unwatched_dependency_heads(bool scoped)
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
            var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config,
                () => [head, waiting, Card("OUTSIDE", "unwatched", TaskStates.Ready)], timeProvider: clock);
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
            if (scoped) Assert.Empty(report.Fleet.Heads);
            else Assert.Equal("HEAD", Assert.Single(report.Fleet.Heads).CardKey);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
