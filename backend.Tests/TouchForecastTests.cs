using AgentStudio.Dispatch;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TouchForecastTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private const string A = "backend/Features/Tasks/Task.cs";
    private const string B = "backend/Features/Tasks/Partner.cs";
    private const string C = "frontend/src/app/features/board/board.ts";
    private static CoChangeIndex Index(params string[] tracked) => TouchForecast.BuildIndex("tip", At, tracked, []);

    [Fact]
    public void Tracked_source_paths_seed_the_expansion_before_prompt_inference()
    {
        var index = TouchForecast.BuildIndex("tip", At, [A, B, C], [[A, B], [A, B], [A, B]]);
        var cited = TouchForecast.Compute(C, index, [], [A, "removed.cs"]);
        Assert.Equal([A], cited.CitedFiles);
        Assert.Equal(new[] { A, B }.Order(), cited.ForecastFiles);
        Assert.Equal([C], TouchForecast.Compute(C, index, [], ["removed.cs"]).CitedFiles);
    }

    [Fact]
    public void CitesExactPathsAndUniqueNamesWithoutSubstringOrAmbiguousNameMatches()
    {
        var index = Index(A, B, C, "runner/Task.cs", "docs/not-code.md");
        var result = TouchForecast.Compute($"Edit `{A}` and Partner.cs. Ignore Task.cs and Otherboard.ts.", index, []);
        Assert.Equal(new[] { B, A }.Order(StringComparer.Ordinal), result.CitedFiles);
        Assert.Equal("cited", result.Confidence);
        Assert.DoesNotContain(C, result.ForecastFiles);
        Assert.Equal(index.Revision, result.IndexRevision);
    }

    [Fact]
    public void ReplacementIntegrationInventoryRebuildsPathAndUniqueNameLookups()
    {
        var index = Index(A);
        Assert.Equal(new[] { A }, TouchForecast.Compute("Task.cs", index, []).CitedFiles);
        var replacement = index with { TrackedFiles = [B, "runner/Task.cs", "other/Task.cs"] };
        var result = TouchForecast.Compute($"{A} Task.cs Partner.cs", replacement, []);
        Assert.Equal(new[] { B }, result.CitedFiles);
        Assert.Equal(new[] { "backend/Features/Tasks" }, result.Modules);
        Assert.Empty(TouchForecast.Compute("backend/Features/Tasks/", index with { TrackedFiles = [] }, []).Modules);
        // The original immutable snapshot remains usable alongside the new one.
        Assert.Equal(new[] { A }, TouchForecast.Compute("Task.cs", index, []).CitedFiles);
    }

    [Fact]
    public void CoChangeRequiresThreeSharedCommitsAndHalfOfTheCitedFilesHistory()
    {
        var commits = new IReadOnlyList<string>[] { [A, B, C], [A, B, C], [A, B], [A], [A], [A], [B] };
        var index = TouchForecast.BuildIndex("tip", At, [A, B, C], commits);
        Assert.Equal(new[] { B }, index.Expansion[A]);
        Assert.Equal(new[] { A }, index.Expansion[B]);
        Assert.Empty(index.Expansion[C]);
        var result = TouchForecast.Compute(A, index, []);
        Assert.Equal(new[] { A, B }.Order(StringComparer.Ordinal), result.ForecastFiles);
    }

    [Fact]
    public void CoChangeIsDirectionalAndIgnoresLargeCommitsAndDuplicatePaths()
    {
        var large = Enumerable.Range(0, 41).Select(i => $"src/File{i}.cs").Append(A).Append(C).ToArray();
        List<IReadOnlyList<string>> commits = [[A, A, B], [A, B], [A, B], [B], [B], [B], [B], large, large, large];
        var index = TouchForecast.BuildIndex("tip", At, [A, B, C], commits);
        Assert.Equal(new[] { B }, index.Expansion[A]);
        Assert.Empty(index.Expansion[B]);
        Assert.DoesNotContain(C, index.Expansion.Keys);
    }

    [Fact]
    public void ExpansionDoesNotRecurseOrIncludeDeletedFiles()
    {
        var index = Index(A, B, C) with { Expansion = new() { [A] = [B, "deleted.cs"], [B] = [C] } };
        Assert.Equal(new[] { A, B }.Order(StringComparer.Ordinal), TouchForecast.Compute(A, index, []).ForecastFiles);
    }

    [Theory]
    [InlineData("frontend/src/app/features/board/x.ts", "frontend/src/app/features/board")]
    [InlineData("frontend/src/app/services/x.ts", "frontend/src/app/services")]
    [InlineData("frontend/e2e/fixtures/x.ts", "frontend/e2e/fixtures")]
    [InlineData("backend/Features/Tasks/x.cs", "backend/Features/Tasks")]
    [InlineData("backend/Shared/Models/x.cs", "backend")]
    [InlineData("src/sub/x.cs", "src")]
    [InlineData("root.cs", "root.cs")]
    public void MapsDossierModules(string path, string expected) => Assert.Equal(expected, TouchForecast.Module(path));

    [Fact]
    public void HubsStayInFilesButNeverDefineModules()
    {
        var hub = TouchForecast.DefaultHubFiles[0];
        var result = TouchForecast.Compute($"{hub} {C}", Index(hub, C), TouchForecast.DefaultHubFiles);
        Assert.Contains(hub, result.ForecastFiles);
        Assert.Equal(new[] { "frontend/src/app/features/board" }, result.Modules);
        Assert.Empty(TouchForecast.Compute("backend/Host/", Index(hub), TouchForecast.DefaultHubFiles).Modules);
    }

    [Fact]
    public void DirectoryCitationProvidesOnlyModuleConfidenceAndNoInventedFiles()
    {
        var result = TouchForecast.Compute("Change backend/Features/Tasks/", Index(A, B), []);
        Assert.Equal("module-only", result.Confidence);
        Assert.Equal(new[] { "backend/Features/Tasks" }, result.Modules);
        Assert.Empty(result.ForecastFiles);
        Assert.Equal("none", TouchForecast.Compute("Unrelated words", Index(A), []).Confidence);
        Assert.Equal("none", TouchForecast.Compute("backend", Index(A), []).Confidence);
    }

    [Theory]
    [InlineData("cited", AgentStudio.OrchestrationDecisions.DispatchForecastConfidence.High)]
    [InlineData("module-only", AgentStudio.OrchestrationDecisions.DispatchForecastConfidence.Low)]
    [InlineData("none", AgentStudio.OrchestrationDecisions.DispatchForecastConfidence.None)]
    public void Decision_consumers_preserve_the_recorded_confidence(string confidence,
        AgentStudio.OrchestrationDecisions.DispatchForecastConfidence expected)
    {
        var forecast = new TouchForecastV1([], [], [], [], confidence, "index");
        Assert.Equal(expected, TouchForecast.ForDecision(forecast).Confidence);
        Assert.Equal(AgentStudio.OrchestrationDecisions.DispatchForecastConfidence.None, TouchForecast.ForDecision(null).Confidence);
    }

    [Fact]
    public void AccuracyUsesSetsAndKeepsUndefinedDenominatorsNull()
    {
        Assert.Equal(new ForecastAccuracy(0.5, 1), TouchForecast.Accuracy([A, A, B], [A]));
        Assert.Equal(new ForecastAccuracy(1, 0.5), TouchForecast.Accuracy([A], [A, B]));
        Assert.Equal(new ForecastAccuracy(null, 0), TouchForecast.Accuracy([], [A]));
        Assert.Equal(new ForecastAccuracy(0, null), TouchForecast.Accuracy([A], []));
    }
}
