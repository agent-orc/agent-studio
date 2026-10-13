using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class HistoricalIntegrationVerificationSweepTests
{
    [Theory]
    [InlineData(true, true, false, true, false, false, false, IntegrationRecordClasses.IntegratedVerified)]
    [InlineData(true, true, true, true, false, false, false, IntegrationRecordClasses.IntegratedHistorical)]
    [InlineData(false, false, true, false, true, true, false, IntegrationRecordClasses.NoCodeExpected)]
    [InlineData(true, false, true, true, false, false, true, IntegrationRecordClasses.ContentOnFence)]
    [InlineData(false, false, true, false, false, false, false, IntegrationRecordClasses.NoAttributionLegacy)]
    [InlineData(false, false, true, true, false, true, false, IntegrationRecordClasses.GenuinelyMissing)]
    public void Policy_ClassifiesSixWayMatrix(
        bool hasCommits,
        bool allIntegrated,
        bool historical,
        bool hasCommitAttributionField,
        bool noCodeExpected,
        bool hasDeliverables,
        bool hasFence,
        string expected)
    {
        Assert.Equal(
            expected,
            HistoricalIntegrationVerificationPolicy.Classify(new(
                hasCommits,
                allIntegrated,
                historical,
                hasCommitAttributionField,
                noCodeExpected,
                hasDeliverables,
                hasFence)));
    }

    [Theory]
    [InlineData(IntegrationRecordClasses.IntegratedVerified, false)]
    [InlineData(IntegrationRecordClasses.IntegratedHistorical, false)]
    [InlineData(IntegrationRecordClasses.NoCodeExpected, false)]
    [InlineData(IntegrationRecordClasses.NoAttributionLegacy, false)]
    [InlineData(IntegrationRecordClasses.ContentOnFence, true)]
    [InlineData(IntegrationRecordClasses.GenuinelyMissing, true)]
    public void OperatorVisibleDetector_OnlyReturnsActionableHistoricalClasses(
        string classification,
        bool expected)
    {
        var task = new TaskInfo
        {
            IntegrationRecords =
            [
                new TaskIntegrationRecord
                {
                    Id = HistoricalIntegrationVerificationSweep.RecordId,
                    Classification = classification,
                    RecordedAtUtc = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
                },
            ],
        };

        Assert.Equal(
            expected,
            TaskIntegrationRecordDetector.LatestOperatorVisibleVerification(task) is not null);
    }

    [Fact]
    public void CandidateSelection_ExtendsLegacyScopeToAcceptanceStartedAlertPopulation()
    {
        var task = new TaskInfo
        {
            Id = "legacy-alert-card",
            Key = "ASS-859",
            State = TaskStates.Archive,
            Mode = TaskModes.Coding,
        };
        var acceptanceStarted = new TimelineEvent
        {
            Kind = TimelineEventKinds.IntegrationStarted,
            Ts = new DateTime(2026, 6, 9, 10, 0, 0, DateTimeKind.Utc),
        };

        Assert.True(TaskIntegrationRecordDetector.HasNativeRecord(null, [acceptanceStarted]));
        Assert.True(HistoricalIntegrationVerificationPolicy.ShouldEvaluate(
            task,
            mergeStep: null,
            [acceptanceStarted]));
        Assert.False(HistoricalIntegrationVerificationPolicy.ShouldEvaluate(
            task with
            {
                IntegrationRecords =
                [
                    new TaskIntegrationRecord
                    {
                        Id = HistoricalIntegrationVerificationSweep.PreviousRecordId,
                        Classification = IntegrationRecordClasses.IntegratedHistorical,
                    },
                ],
            },
            mergeStep: null,
            [acceptanceStarted]));
    }

    [Fact]
    public async Task RunOnceAsync_V2AddressesDuplicateIdsByFolder_AndPreservesV1Record()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "historical-integration-duplicate-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reportPath = Path.Combine(root, ".metadata", "migrations", HistoricalIntegrationVerificationSweep.ReportFileName);
            var firstFolder = SeedFlatTask(
                root,
                "ASS-324",
                "human-decision-needed-die-zeilen-sind-nicht-aussichtlichen-verteilt",
                existingRecord: new TaskIntegrationRecord
                {
                    Id = HistoricalIntegrationVerificationSweep.PreviousRecordId,
                    Classification = IntegrationRecordClasses.IntegratedHistorical,
                    RecordedAtUtc = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
                    Evidence = "Existing v1 verification.",
                });
            var secondFolder = SeedFlatTask(
                root,
                "ASS-1309",
                "human-decision-needed-die-zeilen-sind-nicht-aussichtlichen-verteilt");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = root,
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:Path"] = root,
                    ["WatchPaths:0:RootPath"] = root,
                    ["WatchPaths:0:RepositoryPath"] = root,
                }).Build();
            var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
            timeline.Append(firstFolder, new TimelineEvent
            {
                Kind = TimelineEventKinds.IntegrationStarted,
                Ts = new DateTime(2026, 6, 9, 10, 0, 0, DateTimeKind.Utc),
            });
            timeline.Append(secondFolder, new TimelineEvent
            {
                Kind = TimelineEventKinds.IntegrationStarted,
                Ts = new DateTime(2026, 6, 9, 10, 0, 0, DateTimeKind.Utc),
            });
            var scanner = new TaskScannerService(
                configuration,
                NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
            var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
            var mutations = new TaskMutationService(
                scanner,
                new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
                new AgentStudio.Registry.ProjectRegistry(
                    configuration,
                    NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
                new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
                NullLogger<TaskMutationService>.Instance,
                git: git);
            var sweep = new HistoricalIntegrationVerificationSweep(
                scanner,
                mutations,
                git,
                new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration),
                new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance),
                timeline,
                reportPath,
                batchSize: 10,
                new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)),
                NullLogger<HistoricalIntegrationVerificationSweep>.Instance);

            var report = await sweep.RunOnceAsync();

            Assert.True(report.Completed);
            Assert.Equal(2, report.CandidateCards);
            Assert.Equal(1, report.RecordsWritten);
            Assert.Equal(1, report.Counts[IntegrationRecordClasses.IntegratedHistorical]);
            Assert.Equal(1, report.Counts[IntegrationRecordClasses.NoAttributionLegacy]);
            Assert.Equal(
                [HistoricalIntegrationVerificationSweep.PreviousRecordId],
                ReadRecords(firstFolder).Select(record => record.Id));
            var secondRecord = Assert.Single(ReadRecords(secondFolder));
            Assert.Equal(HistoricalIntegrationVerificationSweep.RecordId, secondRecord.Id);
            Assert.Equal(IntegrationRecordClasses.NoAttributionLegacy, secondRecord.Classification);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) { SilentCatch.Note(ex, "HistoricalIntegrationVerificationSweepTests duplicate-id cleanup"); }
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunOnceAsync_ClassifiesInBatches_WritesCompactReport_AndIsIdempotent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "historical-integration-sweep-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var taskStore = Path.Combine(root, "task-store");
        var completed = Path.Combine(taskStore, TaskStates.Completed);
        var reportPath = Path.Combine(taskStore, ".metadata", "migrations", "report.json");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(completed);
        try
        {
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "user.name", "test");
            File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
            Git(repo, "add", "README.md");
            Git(repo, "commit", "-q", "-m", "seed");
            Git(repo, "checkout", "-q", "-b", "develop");

            Git(repo, "checkout", "-q", "-b", "agent-studio/salvage/runner/AGT-3001/old");
            File.WriteAllText(Path.Combine(repo, "historical.cs"), "complete");
            Git(repo, "add", "historical.cs");
            Git(repo, "commit", "-q", "-m", "wip(runner): salvage before teardown - outcome Done");
            var obsoleteFenceSha = Git(repo, "rev-parse", "HEAD").Trim();

            Git(repo, "checkout", "-q", "develop");
            File.WriteAllText(Path.Combine(repo, "historical.cs"), "complete");
            Git(repo, "add", "historical.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-3001): integrated historical delivery");
            var historicalSha = Git(repo, "rev-parse", "HEAD").Trim();

            File.WriteAllText(Path.Combine(repo, "verified.cs"), "complete");
            Git(repo, "add", "verified.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-3002): integrated current delivery");
            var verifiedSha = Git(repo, "rev-parse", "HEAD").Trim();

            Git(repo, "checkout", "-q", "-b", "agent-studio/results/runner/AGT-3003/fence-1");
            File.WriteAllText(Path.Combine(repo, "fenced.cs"), "not integrated");
            Git(repo, "add", "fenced.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-3003): fenced delivery");
            var fencedSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "checkout", "-q", "develop");

            SeedTask(completed, "agt-3001", "AGT-3001", "2026-08-09T12:00:00Z", commits:
            [
                Commit(obsoleteFenceSha, "wip(runner): salvage before teardown - outcome Done", "round-1", "historical.cs"),
                Commit(historicalSha, "feat(AGT-3001): integrated historical delivery", "round-2", "historical.cs"),
            ]);
            SeedTask(completed, "agt-3002", "AGT-3002", "2026-08-10T02:00:00Z", commits:
            [
                Commit(verifiedSha, "feat(AGT-3002): integrated current delivery", "round-1", "verified.cs"),
            ]);
            SeedTask(completed, "agt-3003", "AGT-3003", "2026-08-09T12:00:00Z", commits:
            [
                Commit(
                    fencedSha,
                    "feat(AGT-3003): fenced delivery",
                    "round-1",
                    "fenced.cs",
                    "agent-studio/results/runner/AGT-3003/fence-1"),
            ]);
            var reportOnly = SeedTask(
                completed,
                "agt-3004",
                "AGT-3004",
                "2026-08-09T12:00:00Z",
                mode: TaskModes.Research);
            Directory.CreateDirectory(Path.Combine(reportOnly, "results"));
            File.WriteAllText(Path.Combine(reportOnly, "results", "report.md"), "# Findings");
            SeedTask(completed, "agt-3005", "AGT-3005", "2026-08-09T12:00:00Z");

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = taskStore,
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:Path"] = taskStore,
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                }).Build();
            var scanner = new TaskScannerService(
                configuration,
                NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
            var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
            var settings = new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance,
                configuration);
            settings.SetIntegrationBranch("Fixture", "develop");
            var mutations = new TaskMutationService(
                scanner,
                new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
                new AgentStudio.Registry.ProjectRegistry(
                    configuration,
                    NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
                new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
                NullLogger<TaskMutationService>.Instance,
                git: git);
            var sweep = new HistoricalIntegrationVerificationSweep(
                scanner,
                mutations,
                git,
                settings,
                new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance),
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                reportPath,
                batchSize: 2,
                new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero)),
                NullLogger<HistoricalIntegrationVerificationSweep>.Instance);

            var first = await sweep.RunOnceAsync();
            var reportAfterFirstRun = File.ReadAllText(reportPath);
            var tasksAfterFirstRun = Directory
                .EnumerateFiles(completed, "task.json", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
            var second = await sweep.RunOnceAsync();

            Assert.True(first.Completed);
            Assert.Equal(5, first.ScannedCards);
            Assert.Equal(5, first.CandidateCards);
            Assert.Equal(5, first.RecordsWritten);
            Assert.Equal(3, first.BatchCount);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.IntegratedHistorical]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.IntegratedVerified]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.NoCodeExpected]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.ContentOnFence]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.GenuinelyMissing]);
            Assert.Equal(2, first.OperatorItems.Count);
            Assert.All(first.OperatorItems, item =>
                Assert.True(IntegrationRecordClasses.IsOperatorVisible(item.Classification)));
            Assert.True(second.AlreadyCompleted);
            Assert.Equal(0, second.RecordsWritten);
            Assert.Equal(reportAfterFirstRun, File.ReadAllText(reportPath));
            Assert.All(tasksAfterFirstRun, row => Assert.Equal(row.Value, File.ReadAllText(row.Key)));

            foreach (var id in Enumerable.Range(3001, 5))
            {
                var task = scanner.FindJob($"agt-{id}", taskStore);
                var record = Assert.Single(task!.IntegrationRecords);
                Assert.Equal(HistoricalIntegrationVerificationSweep.RecordId, record.Id);
            }
            Assert.Equal(
                IntegrationRecordClasses.IntegratedHistorical,
                scanner.FindJob("agt-3001", taskStore)!.IntegrationRecords.Single().Classification);
            var historicalRecord = scanner.FindJob("agt-3001", taskStore)!.IntegrationRecords.Single();
            Assert.DoesNotContain(obsoleteFenceSha, historicalRecord.CommitShas);
            Assert.Contains(historicalSha, historicalRecord.CommitShas);
            Assert.Single(
                scanner.FindJob("agt-3003", taskStore)!.IntegrationRecords.Single().FenceRefs);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) { SilentCatch.Note(ex, "HistoricalIntegrationVerificationSweepTests cleanup"); }
        }
    }

    private static string SeedTask(
        string lane,
        string id,
        string key,
        string enteredLaneAt,
        IReadOnlyList<object>? commits = null,
        string mode = TaskModes.Coding)
    {
        var folder = Path.Combine(lane, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = $"Fixture {key}",
            state = TaskStates.Completed,
            order = 1,
            agent = "codex",
            mode,
            createdAt = enteredLaneAt,
            enteredLaneAt,
            commits = commits ?? [],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Fixture task.");
        return folder;
    }

    private static string SeedFlatTask(
        string root,
        string key,
        string id,
        TaskIntegrationRecord? existingRecord = null)
    {
        Assert.True(TaskStorageLayout.TryParseKeyNumber(key, out var number));
        var folder = TaskStorageLayout.JobDir(root, number, key);
        Directory.CreateDirectory(folder);
        var task = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["key"] = key,
            ["title"] = $"Fixture {key}",
            ["state"] = TaskStates.Archive,
            ["order"] = 1,
            ["agent"] = "codex",
            ["mode"] = TaskModes.Coding,
            ["createdAt"] = "2026-06-09T09:00:00Z",
            ["enteredLaneAt"] = "2026-06-09T10:00:00Z",
        };
        if (existingRecord is not null)
            task["integrationRecords"] = new[] { existingRecord };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Fixture task.");
        return folder;
    }

    private static IReadOnlyList<TaskIntegrationRecord> ReadRecords(string folder)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        return document.RootElement.GetProperty("integrationRecords")
            .Deserialize<List<TaskIntegrationRecord>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static object Commit(
        string sha,
        string message,
        string runAttemptId,
        string file,
        string? branch = null)
        => new
        {
            sha,
            shortSha = sha[..7],
            message,
            branch,
            filesChanged = 1,
            files = new[] { file },
            at = "2026-08-09T10:00:00Z",
            runAttemptId,
        };

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
