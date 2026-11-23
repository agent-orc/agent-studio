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
