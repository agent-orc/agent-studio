using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionExcerptWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-excerpt-" + Guid.NewGuid().ToString("N"));
    private readonly ArtifactClassifier _classifier = new();

    public RetentionExcerptWriterTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task CliExcerptContainsFixedSectionsErrorsMetricsAndCommands()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        var lines = Enumerable.Range(1, 800).Select(index => index switch
        {
            250 => "[12:00:00] command: dotnet test",
            400 => "[12:01:00] ERROR failed exit code 1",
            600 => "[12:02:00] token usage 123",
            _ => $"line {index}",
        }).ToArray();
        var path = Path.Combine(_root, "logs", "cli-output.log");
        await File.WriteAllLinesAsync(path, lines);
        var file = Item("logs/cli-output.log");

        var excerpt = await new RetentionExcerptWriter().WriteAsync(_root, [file]);

        foreach (var heading in new[] { "## Source", "## Summary", "## Head", "## Errors", "## Tail", "## Metrics", "## Timestamps and duration", "## Commands", "## Inventory" })
            Assert.Contains(heading, excerpt);
        Assert.Contains("ERROR failed exit code 1", excerpt);
        Assert.Contains("`dotnet test`", excerpt);
        Assert.Contains("Token lines: 1", excerpt);
    }

    [Theory]
    [InlineData("review/code-review-stdout.log", "verdict: pass", true)]
    [InlineData("review/code-review-stdout.log", "finding: fix this", true)]
    [InlineData("review/code-review-stdout.log", "ordinary chatter", false)]
    public async Task ReviewExcerptKeepsOnlyVerdictAndFindingLines(string relative, string line, bool expected)
    {
        Directory.CreateDirectory(Path.Combine(_root, "review"));
        await File.WriteAllTextAsync(Path.Combine(_root, relative), line);
        var excerpt = await new RetentionExcerptWriter().WriteAsync(_root, [Item(relative)]);
        Assert.Equal(expected, excerpt.Contains($"- {line}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("results/report.md", "full report", true)]
    [InlineData("results/trace.zip", "binary-ish", false)]
    [InlineData("results/screenshot.png", "image-ish", false)]
    public async Task ResultsExcerptKeepsReportsButOnlyInventoriesHeavyBinary(string relative, string content, bool expectedFull)
    {
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        await File.WriteAllTextAsync(Path.Combine(_root, relative), content);
        var excerpt = await new RetentionExcerptWriter().WriteAsync(_root, [Item(relative)]);
        Assert.Equal(expectedFull, excerpt.Contains($"### {relative}", StringComparison.Ordinal));
    }

    private RetentionFile Item(string relative)
    {
        var info = new FileInfo(Path.Combine(_root, relative));
        return new(relative, info.Length, info.LastWriteTimeUtc, _classifier.Classify(relative));
    }
}
