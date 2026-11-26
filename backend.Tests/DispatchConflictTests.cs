using System.Diagnostics;
using AgentStudio.Dispatch;
using AgentStudio.Git;
using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: commit dates and query windows are explicit fixture facts.
public sealed class DispatchConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-conflicts-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Dispatch:StoragePath"] = Path.Combine(_root, "ledger"),
          ["WatchPaths:0:Name"] = "project", ["WatchPaths:0:Path"] = Path.Combine(_root, "cards"),
          ["WatchPaths:0:RootPath"] = Repo }).Build();
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private GitService Service()
    {
        var config = Config;
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }
    [Fact]
    public void AppendIsImmutableConcurrentProjectScopedAndRangeIsHalfOpen()
    {
        var store = new DispatchStore(Config);
        var row = new DispatchConflictRecordV1("id", "project", "AGT-1", "merge-round", "base", "tip",
            Enumerable.Range(0, 45).Select(i => $"file-{i}.cs").ToArray(), ["AGT-2"], At);
        Parallel.For(0, 20, _ => store.AppendConflict(row));
        Assert.False(store.AppendConflict(row with { At = At.AddDays(1), ConflictedFiles = [] }));
        var read = Assert.Single(new DispatchStore(Config).ReadConflicts("PROJECT", At, At.AddTicks(1)));
        Assert.Equal(45, read.ConflictedFiles.Count);
        Assert.Equal(At, read.At);
        Assert.Empty(store.ReadConflicts("project", to: At));
        Assert.Empty(store.ReadConflicts("other"));
    }

    [Theory]
    [InlineData(true, true, 2, true, true, true)]
    [InlineData(false, true, 2, true, true, false)]
    [InlineData(true, false, 2, true, true, false)]
    [InlineData(true, true, 3, true, true, false)]
    [InlineData(true, true, 2, false, true, false)]
    [InlineData(true, true, 2, true, false, false)]
    public void SemanticRequiresPairIsolationAndOverlappingForecasts(bool red, bool window, int count,
        bool isolated, bool overlap, bool expected)
        => Assert.Equal(expected, DispatchConflictPolicy.IsSemanticPair(red, window, count, isolated, overlap ? ["a.cs"] : []));

    [Fact]
    [Trait("Category", "MachineBound")]
    public void GitPartnersUseOnlyOwningSubjectsInTheBaseToTipRangeAndKeepAllFiles()
    {
        Init();
        var files = Enumerable.Range(0, 25).Select(i => $"file-{i}.cs").ToArray();
        foreach (var file in files) File.WriteAllText(Path.Combine(Repo, file), "base");
        Commit("AGT-99: old changes");
        var baseSha = Git("rev-parse", "HEAD");
        foreach (var file in files) File.WriteAllText(Path.Combine(Repo, file), "partner");
        Commit("AGT-2: reference AGT-3 is not an owner");
        File.WriteAllText(Path.Combine(Repo, "unrelated.cs"), "other");
        Commit("AGT-4: unrelated");
        var tip = Git("rev-parse", "HEAD");
        var row = Service().RecordDispatchConflict(Repo, "project", "AGT-1", "integration-conflict", baseSha, tip, files, "report", At);
        Assert.Equal(new[] { "AGT-2" }, row.PartnerCards);
        Assert.Equal(25, row.ConflictedFiles.Count);
        Assert.Equal(2, row.DispatchDecisionIds.Count);
        Assert.All(row.DispatchDecisionIds.Values, value => Assert.Null(value));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void PersistedAttributionAndProvenanceRecoverPartnersWithoutCardSubjects()
    {
        Init();
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "base");
        Commit("base");
        var baseSha = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "first");
        Commit("generic implementation");
        var first = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "second");
        Commit("generic integration");
        var second = Git("rev-parse", "HEAD");
        AddCard("AGT-2", new { commits = new[] { new { sha = first } } });
        AddCard("AGT-3", new { provenance = new { merge = new { mergeCommit = second } } });
        var row = Service().RecordDispatchConflict(Repo, "project", "AGT-1", "integration-conflict",
            baseSha, second, ["shared.cs"], "attribution", At);
        Assert.Equal(new[] { "AGT-2", "AGT-3" }, row.PartnerCards);
    }

    private void AddCard(string key, object metadata)
    {
        var folder = Path.Combine(_root, "cards", TaskStates.Archive, key);
        Directory.CreateDirectory(folder);
        var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            System.Text.Json.JsonSerializer.Serialize(metadata))!;
        values["id"] = key;
        values["key"] = key;
        values["title"] = key;
        values["state"] = TaskStates.Archive;
        File.WriteAllText(Path.Combine(folder, "task.json"), System.Text.Json.JsonSerializer.Serialize(values));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void IntegrationReportKeepsItsCapWhileTheDispatchLedgerRecordsEveryConflictedFile()
    {
        Init();
        var files = Enumerable.Range(0, 25).Select(i => $"file-{i}.cs").ToArray();
        foreach (var file in files) File.WriteAllText(Path.Combine(Repo, file), "start\nbase\nend\n");
        Commit("base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "develop");
        foreach (var file in files) File.WriteAllText(Path.Combine(Repo, file), "start\npartner\nend\n");
        Commit("AGT-2: partner");
        var tip = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "task", baseSha);
        foreach (var file in files) File.WriteAllText(Path.Combine(Repo, file), "start\nown\nend\n");
        Commit("AGT-1: own");
        var delivery = Git("rev-parse", "HEAD");
        using var capture = IntegrationConflictCapture.Begin("project", "AGT-1", "develop", delivery, baseSha,
            ledger: new IntegrationConflictLedger(Path.Combine(_root, "legacy")));
        var result = Service().MergeBranchIntoIntegration(Repo, "task", "develop");
        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, result.Outcome);
        Assert.Equal(20, result.ConflictReport!.ConflictedFiles.Count);
        Assert.True(result.ConflictReport.ConflictedFilesTruncated);
        var row = Assert.Single(new DispatchStore(Config).ReadConflicts("project"));
        Assert.Equal(25, row.ConflictedFiles.Count);
        Assert.Equal(new[] { "AGT-2" }, row.PartnerCards);
        Assert.Equal(tip, row.IntegrationTipSha);
        Assert.Equal(baseSha, row.DeliveryBaseSha);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void RecoveryUsesCompleteReportEvidenceAndRecordsBothRoundKinds()
    {
        Init();
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "base");
        Commit("base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "develop");
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "partner");
        Commit("AGT-2: partner");
        var tip = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "task", baseSha);
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "own");
        Commit("AGT-1: own");
        var delivery = Git("rev-parse", "HEAD");
        var files = Enumerable.Range(0, 25).Select(i => $"file-{i}.cs").Append("shared.cs").ToArray();
        var service = Service();
        service.RecordDispatchConflict(Repo, "project", "AGT-1", "integration-conflict", baseSha, tip, files,
            delivery + ":" + tip, At);
        var subject = new AgentStudio.Pipeline.ReviewSubjectRecord { ResultSha = delivery, BaseSha = baseSha };
        var report = new IntegrationConflictReport { DeliverySha = delivery, IntegrationTipSha = tip,
            ConflictedFiles = files.Take(20).ToList(), ConflictedFilesTruncated = true };
        var card = new TaskInfo { Id = "AGT-1", Key = "AGT-1", ProjectName = "project" };
        service.TryRecordDispatchRecovery(card, subject, report, "merge-round", "merge");
        service.TryRecordDispatchRecovery(card, subject, report, "develop-sync", "sync");
        var rows = new DispatchStore(Config).ReadConflicts("project");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(26, row.ConflictedFiles.Count));
        Assert.All(rows, row => Assert.Contains("AGT-2", row.PartnerCards));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void BackfillFindsSyncAndResolutionCommitsAcrossRefsOnlyWithinThirtyDaysAndIsRepeatSafe()
    {
        Init();
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "base");
        Commit("base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "develop");
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "partner");
        Commit("AGT-2: partner");
        var tip = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "task", baseSha);
        File.WriteAllText(Path.Combine(Repo, "own.cs"), "own");
        Commit("AGT-1: own");
        Git("merge", "--no-ff", tip, "-m", "AGT-1: include latest develop");
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "resolution");
        Commit("AGT-1: resolve conflict");
        Git("checkout", "-qb", "old", baseSha);
        File.WriteAllText(Path.Combine(Repo, "old.cs"), "old");
        Commit("AGT-3: sync develop", "2026-09-01T12:00:00Z");
        var service = Service();
        Assert.Equal(2, service.BackfillDispatchConflicts(Repo, "project", At));
        Assert.Equal(0, service.BackfillDispatchConflicts(Repo, "project", At));
        var rows = new DispatchStore(Config).ReadConflicts("project");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.Backfilled));
        Assert.Contains(rows, row => row.PartnerCards.Contains("AGT-2"));
    }

    [Fact]
    public void AttributionReusesOnePublicationAndRefreshesAfterMetadataChanges()
    {
        var config = Config;
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(cache);
        cache.GetSnapshot();
        var service = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var before = service.ReadDispatchConflictAttribution();
        Assert.Same(before, service.ReadDispatchConflictAttribution());
        Assert.Null(before.Owners("project", "sha"));
        AddCard("AGT-2", new { commits = new[] { new { sha = "sha", supersededBySha = "replacement" } } });
        cache.Invalidate();
        var after = service.ReadDispatchConflictAttribution();
        Assert.NotSame(before, after);
        Assert.Same(after, service.ReadDispatchConflictAttribution());
        Assert.Equal(new[] { "AGT-2" }, after.Owners("PROJECT", "SHA"));
        Assert.Equal(new[] { "AGT-2" }, after.Owners("project", "replacement"));
        Assert.Null(after.Owners("other", "sha"));
        Assert.Null(before.Owners("project", "sha"));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void DeliveryConflictCollectionFailureDoesNotBlockDeliveryFacts()
    {
        Init();
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "base");
        Commit("base");
        var baseSha = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Repo, "shared.cs"), "sync");
        Commit("AGT-1: sync develop");
        var result = Git("rev-parse", "HEAD");
        // A file in place of the ledger root deterministically refuses its writes.
        File.WriteAllText(Path.Combine(_root, "ledger"), "unavailable");
        var service = Service();
        Assert.ThrowsAny<IOException>(() => service.BackfillDispatchConflicts(Repo, "project", At,
            baseSha, result, "AGT-1", backfilled: false));
        var collector = new DispatchForecastService(null!, null!, null!, null!, null!, service,
            new DispatchStore(Config), NullLogger<DispatchForecastService>.Instance);
        var forecast = TouchForecast.Compute("shared.cs", TouchForecast.BuildIndex(baseSha, At, ["shared.cs"], []), []);
        var delivery = collector.CollectDelivery(Repo, "project", "AGT-1", baseSha,
            new AgentStudio.Pipeline.ReviewSubjectRecord { BaseSha = baseSha, ResultSha = result },
            forecast, At, CancellationToken.None);
        Assert.Equal(new[] { "shared.cs" }, delivery.RealFiles);
        Assert.Equal(result, delivery.ResultSha);
        service.TryBackfillDispatchConflicts(Repo, "project", At, baseSha, "missing-ref", "AGT-1");
    }

    private void Init()
    {
        Directory.CreateDirectory(Repo);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
    }
    private string _commitDate = "2026-11-19T12:00:00Z";
    private void Commit(string subject, string date = "2026-11-19T12:00:00Z")
    {
        _commitDate = date;
        Git("add", ".");
        Git("-c", "core.hooksPath=/dev/null", "commit", "-qm", subject, "--date", date);
    }
    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Repo, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_COMMITTER_DATE"] = _commitDate;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
