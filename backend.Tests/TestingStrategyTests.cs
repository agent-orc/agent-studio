using System.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3965: <c>testing.strategy</c> maps onto the existing per-card tier,
/// integration placement and merge-window selection, and a fast or balanced
/// project whose develop moved without a post-merge run says so.
/// </summary>
public sealed class TestingStrategyTests : IDisposable
{
    private const string ProjectName = "Strategy Project";
    private const string Tip = "1111111111111111111111111111111111111111";
    private const string Covered = "2222222222222222222222222222222222222222";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "testing-strategy-" + Guid.NewGuid().ToString("N"));
    private ProjectSettingsService? _settings;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(TestingStrategies.Fast, PerCardTestModes.Affected, IntegrationTestModes.PostMerge,
        PostMergeWindowSuiteModes.Affected)]
    [InlineData(TestingStrategies.Balanced, PerCardTestModes.Affected, IntegrationTestModes.PostMerge,
        PostMergeWindowSuiteModes.Routine)]
    [InlineData(TestingStrategies.Strict, PerCardTestModes.Full, IntegrationTestModes.PreMerge, null)]
    public void EachStrategyMapsOntoTheExistingSwitches(string strategy, string perCard, string mode,
        string? windowSuites)
    {
        var settings = new ProjectSettings { Testing = new ProjectTestingSettings { Strategy = strategy } };

        Assert.Equal(strategy, TestingStrategies.Resolve(settings));
        Assert.Equal(perCard, PerCardTestModes.Resolve(settings));
        Assert.Equal(mode, IntegrationTestModes.Resolve(settings));
        Assert.Equal(windowSuites, TestingStrategies.WindowSuites(strategy));
        Assert.Equal(strategy != TestingStrategies.Strict, TestingStrategies.ExpectsPostMergeRuns(strategy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("post-merge")]
    public void AProjectWithoutAStoredStrategyResolvesToFast(string? stored)
    {
        Assert.Equal(TestingStrategies.Fast, TestingStrategies.Resolve(null));
        Assert.Equal(TestingStrategies.Fast, TestingStrategies.Resolve(new ProjectSettings()));
        Assert.Equal(TestingStrategies.Fast, TestingStrategies.Resolve(new ProjectSettings
        {
            Testing = new ProjectTestingSettings { Strategy = stored },
        }));
    }

    [Fact]
    public void EveryStrategyHasItsOwnSentence()
    {
        var sentences = TestingStrategies.All.Select(TestingStrategies.Sentence).ToArray();

        Assert.Equal(3, sentences.Distinct(StringComparer.Ordinal).Count());
        Assert.All(sentences, sentence => Assert.EndsWith(".", sentence));
        Assert.Contains("full suite in the batch gate", TestingStrategies.Sentence(TestingStrategies.Strict));
        Assert.Contains("every routine suite", TestingStrategies.Sentence(TestingStrategies.Balanced));
        Assert.Contains("nightly", TestingStrategies.Sentence(TestingStrategies.Fast));
    }

    [Fact]
    public void StrictExpectsNoPostMergeRun()
        => Assert.Equal(PostMergeCoverageStates.NotExpected,
            PostMergeCoveragePolicy.Precheck(TestingStrategies.Strict, null, Tip, _clock.GetUtcNow()));

    [Fact]
    public void ExactMeasuredTipIsCurrentAndARecentMeasurementLeavesNewerTipsPending()
    {
        var now = _clock.GetUtcNow();
        Assert.Equal(PostMergeCoverageStates.Current, PostMergeCoveragePolicy.Precheck(TestingStrategies.Fast,
            new PostMergeLastReport(Tip, now.AddDays(-3)), Tip, now));
        Assert.Equal(PostMergeCoverageStates.Pending, PostMergeCoveragePolicy.Precheck(TestingStrategies.Balanced,
            new PostMergeLastReport(Covered, now.AddHours(-5)), Tip, now));
        Assert.Null(PostMergeCoveragePolicy.Precheck(TestingStrategies.Fast,
            new PostMergeLastReport(Covered, now.AddHours(-6)), Tip, now));
        Assert.Null(PostMergeCoveragePolicy.Precheck(TestingStrategies.Fast, null, Tip, now));
        Assert.Equal(PostMergeCoverageStates.Unknown,
            PostMergeCoveragePolicy.Precheck(TestingStrategies.Fast, null, null, now));
    }

    [Fact]
    public void LivenessDoesNotReplaceACompletedMeasurement()
    {
        var now = _clock.GetUtcNow();
        var state = PostMergeProjectState.Empty("p") with
        {
            Runs =
            [
                Run("a", Covered, "green", now.AddHours(-9), now.AddHours(-8)),
                Run("b", Tip, "skipped", now.AddHours(-7), now.AddHours(-7)),
            ],
        };
        Assert.Equal(new PostMergeLastReport(Tip, now.AddHours(-7)), PostMergeCoveragePolicy.LastReport(state));

        var running = state with { ActiveRun = Run("c", Covered, "running", now.AddHours(-1), null) };
        Assert.Equal(new PostMergeLastReport(Covered, now.AddHours(-1)), PostMergeCoveragePolicy.LastReport(running));
        Assert.Null(PostMergeCoveragePolicy.LastReport(PostMergeProjectState.Empty("p")));
        Assert.Null(PostMergeCoveragePolicy.LastMeasurement(running));
        var measured = state with { Runs = [Run("m", Covered, "green", now.AddHours(-9), now.AddHours(-8))
            with { Commands = ["dotnet test"], Verdicts = [new("dotnet test", 0, false)] }, .. state.Runs] };
        Assert.Equal(Covered, PostMergeCoveragePolicy.LastMeasurement(measured)!.TipSha);
        Assert.Equal(Covered, PostMergeCoveragePolicy.LastMeasurement(measured with
            { Runs = [.. measured.Runs, Run("infra", Tip, "inconclusive", now, now)] })!.TipSha);
    }

    [Fact]
    public void TheWarningAppearsWhenDevelopMovedWithoutARunAndClearsWhenARunReports()
    {
        var (service, git, runs, record) = Service(TestingStrategies.Fast);
        var facts = new OperatorPostMergeCoverageFactSource(service.Missing);
        git.TipSha = Tip;
        git.Uncovered = true;

        var missing = service.Evaluate(ProjectName)!;
        Assert.Equal(PostMergeCoverageStates.Missing, missing.State);
        Assert.Contains("No post-merge run was ever reported for " + ProjectName, missing.Message);
        Assert.Contains("develop moved to 111111111111", missing.Message);
        Assert.Contains("choose strict", missing.Message);
        Assert.Null(git.LastCovered);
        var fact = Assert.Single(facts.Collect(_clock.GetUtcNow().UtcDateTime));
        Assert.Equal(OperatorDecisionTypes.PostMergeRunsMissing, fact.Type);
        Assert.Equal(ProjectName, fact.Project);
        Assert.Equal(missing.Message, fact.Reason);
        Assert.Equal("Post-merge runs missing", fact.Title);
        Assert.Equal([OperatorOperations.OwnerDecide], OperatorOperations.For(fact.Type));

        // A window run on the current tip covers develop and clears both.
        var now = _clock.GetUtcNow();
        Assert.NotNull(runs.Record(record.Id, Run("w1", Tip, "running", now, null)));
        Assert.NotNull(runs.Record(record.Id, Run("w1", Tip, "green", now, now.AddMinutes(9)) with
        {
            Commands = ["dotnet test"], Verdicts = [new PostMergeCommandVerdict("dotnet test", 0, false)],
        }));
        Assert.Equal(PostMergeCoverageStates.Current, service.Evaluate(ProjectName)!.State);
        Assert.Empty(facts.Collect(now.UtcDateTime));
        Assert.Empty(service.Missing());

        // Develop moves on; within six hours of that report nothing is missing.
        git.TipSha = Covered;
        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(PostMergeCoverageStates.Pending, service.Evaluate(ProjectName)!.State);

        // Past six hours without another report the warning returns, now
        // naming the last report, as a new occurrence.
        _clock.Advance(TimeSpan.FromHours(2));
        var again = service.Evaluate(ProjectName)!;
        Assert.Equal(PostMergeCoverageStates.Missing, again.State);
        Assert.Equal(Tip, git.LastCovered);
        Assert.Contains("in the last 6 hours (last at 2026-11-22 12:09 UTC on 111111111111)", again.Message);
        var second = Assert.Single(facts.Collect(_clock.GetUtcNow().UtcDateTime));
        Assert.NotEqual(fact.Key, second.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStartedSuiteWithoutTestResultsIsNotACompletedMeasurement(bool timedOut)
    {
        var now = _clock.GetUtcNow();
        var run = Run("infra", Tip, "red", now, now) with
        {
            Commands = ["dotnet test"],
            Verdicts = [new("dotnet test", timedOut ? 124 : 127, timedOut) { SuiteId = "routine" }],
        };
        Assert.Null(PostMergeCoveragePolicy.LastMeasurement(PostMergeProjectState.Empty("p") with { Runs = [run] }));
        var measured = run with { Verdicts = [new("dotnet test", 1, false, FailedTests: ["Feature.Tests.Fails"]) { SuiteId = "routine" }] };
        Assert.Equal(Tip, PostMergeCoveragePolicy.LastMeasurement(PostMergeProjectState.Empty("p") with { Runs = [measured] })!.TipSha);
    }

    [Fact]
    public void SaturatedWindowRaisesOneStableOwnerOccurrenceAfterThreeWindows()
    {
        var (service, git, runs, record) = Service(TestingStrategies.Fast);
        git.TipSha = Tip;
        git.Uncovered = false;
        var now = _clock.GetUtcNow();
        runs.ObserveWindow(record.Id, new(Tip, now.AddMinutes(-91), now, 30, "waiting for gate capacity"));
        var facts = new OperatorPostMergeCoverageFactSource(service.Missing);
        var coverage = service.Evaluate(ProjectName)!;
        Assert.Contains("three times", coverage.Message);
        Assert.Contains("gate capacity", coverage.Message);
        var first = Assert.Single(facts.Collect(now.UtcDateTime));
        _clock.Advance(TimeSpan.FromMinutes(1));
        service.Evaluate(ProjectName);
        Assert.Equal(first.Key, Assert.Single(facts.Collect(_clock.GetUtcNow().UtcDateTime)).Key);
    }

    [Fact]
    public void ReadServesThePublishedCoverageWithoutGitAndDropsItWhenTheStrategyChanges()
    {
        var (service, git, _, _) = Service(TestingStrategies.Fast);
        git.TipSha = Tip;
        git.Uncovered = true;

        var pending = service.Read(ProjectName)!;
        Assert.Equal(PostMergeCoverageStates.Unknown, pending.State);
        Assert.Contains("has not checked develop", pending.Message);
        Assert.Equal(0, git.Walks);
        Assert.Empty(service.Missing());

        service.RefreshAll();
        Assert.Equal(PostMergeCoverageStates.Missing, service.Read(ProjectName)!.State);
        Assert.Single(service.Missing());
        Assert.Equal(1, git.Walks);

        // Strict answers at once and the stale missing entry no longer counts.
        _settings!.SetTestingStrategy(ProjectName, TestingStrategies.Strict);
        Assert.Equal(PostMergeCoverageStates.NotExpected, service.Read(ProjectName)!.State);
        Assert.Empty(service.Missing());
        Assert.Equal(1, git.Walks);
    }

    [Fact]
    public void YoungUncoveredCommitsDoNotWarnAndTheAnswerIsAskedAgainLater()
    {
        var (service, git, _, _) = Service(null);
        git.TipSha = Tip;
        git.Uncovered = false;

        Assert.Equal(PostMergeCoverageStates.Pending, service.Evaluate(ProjectName)!.State);
        Assert.Equal(PostMergeCoverageStates.Pending, service.Evaluate(ProjectName)!.State);
        Assert.Equal(1, git.Walks);

        git.Uncovered = true;
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PostMergeCoverageStates.Missing, service.Evaluate(ProjectName)!.State);
        Assert.Equal(PostMergeCoverageStates.Missing, service.Evaluate(ProjectName)!.State);
        Assert.Equal(2, git.Walks);
    }

    [Fact]
    public void StrictNeverWarnsAndAnUnreadableDevelopIsNotReportedAsMissing()
    {
        var (strict, strictGit, _, _) = Service(TestingStrategies.Strict);
        strictGit.TipSha = Tip;
        strictGit.Uncovered = true;
        Assert.Equal(PostMergeCoverageStates.NotExpected, strict.Evaluate(ProjectName)!.State);
        Assert.Empty(strict.Missing());
        Assert.Equal(0, strictGit.Walks);

        var (balanced, balancedGit, _, _) = Service(TestingStrategies.Balanced);
        balancedGit.TipSha = null;
        var unknown = balanced.Evaluate(ProjectName)!;
        Assert.Equal(PostMergeCoverageStates.Unknown, unknown.State);
        Assert.Contains("cannot read develop", unknown.Message);
    }

    [Fact]
    public void GitAnswersWhetherAnUncoveredFirstParentCommitIsOlderThanTheLimit()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        GitCmd(repo, null, "init", "-q", "-b", "develop");
        var now = _clock.GetUtcNow();
        var first = CommitAt(repo, "a.txt", now.AddHours(-30));
        var second = CommitAt(repo, "b.txt", now.AddHours(-8));
        var third = CommitAt(repo, "c.txt", now.AddHours(-1));
        var git = new GitService(NullLogger<GitService>.Instance, Scanner(), new ConfigurationBuilder().Build());
        var limit = now - PostMergeCoveragePolicy.Limit;

        Assert.True(git.HasFirstParentCommitBefore(repo, third, null, limit));
        Assert.True(git.HasFirstParentCommitBefore(repo, third, first, limit));
        Assert.False(git.HasFirstParentCommitBefore(repo, third, second, limit));
        Assert.False(git.HasFirstParentCommitBefore(repo, third, third, limit));
        Assert.Null(git.HasFirstParentCommitBefore(repo, "3333333333333333333333333333333333333333", null, limit));
        Assert.Null(git.HasFirstParentCommitBefore(repo, "develop; rm", null, limit));
    }

    private (PostMergeCoverageService Service, FakeDevelopGit Git, PostMergeRunStore Runs, ProjectRecord Record)
        Service(string? strategy)
    {
        var workspace = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(workspace, "projects", "strategy");
        Directory.CreateDirectory(watchPath);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = watchPath,
                ["TaskRepository"] = workspace,
            })
            .Build();
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var record = projects.EnsureProjectForStorage(watchPath, ProjectName, DefaultWorkspace.Id, _clock);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        _settings = settings;
        if (strategy is not null) settings.SetTestingStrategy(ProjectName, strategy);
        var runs = new PostMergeRunStore(Path.Combine(workspace, "post-merge"));
        var git = new FakeDevelopGit();
        return (new PostMergeCoverageService(projects, runs, settings, git, _clock), git, runs, record);
    }

    private static PostMergeRunRecord Run(string id, string tip, string status, DateTimeOffset started,
        DateTimeOffset? finished)
        => new(id, "p", "window", tip, null, null, null, 1, [], [], status, started, finished);

    private static TaskScannerService Scanner()
    {
        var config = new ConfigurationBuilder().Build();
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
    }

    private static string CommitAt(string repo, string file, DateTimeOffset at)
    {
        File.WriteAllText(Path.Combine(repo, file), file);
        GitCmd(repo, null, "add", file);
        GitCmd(repo, at, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", file);
        return GitCmd(repo, null, "rev-parse", "HEAD").Trim();
    }

    private static string GitCmd(string repo, DateTimeOffset? at, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (at is { } date)
        {
            var stamp = date.ToUnixTimeSeconds() + " +0000";
            start.Environment["GIT_AUTHOR_DATE"] = stamp;
            start.Environment["GIT_COMMITTER_DATE"] = stamp;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class FakeDevelopGit : IDevelopCoverageGit
    {
        public string? TipSha { get; set; }
        public bool? Uncovered { get; set; }
        public int Walks { get; private set; }
        public string? LastCovered { get; private set; }

        public string? Tip(string project, string branch) => branch == "develop" ? TipSha : null;

        public bool? HasUncoveredCommitBefore(string project, string tip, string? coveredTip, DateTimeOffset before)
        {
            Walks++;
            LastCovered = coveredTip;
            return Uncovered;
        }
    }
}
