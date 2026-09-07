using AgentStudio.TaskServer;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Retention.Tests;

/// <summary>
/// The first production apply deleted 445 tracked bus logs and the attempt-authority archives but committed
/// none of them: the pathspec <c>.metadata/attempt-authority.archive-*.json</c> is a literal to git, so the
/// commit aborted and every deletion stayed unstaged.
/// </summary>
public sealed class RetentionRuntimeCommitTests : IDisposable
{
    private readonly RetentionTestWorkspace _fixture = new(initializeGit: true);
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ApplyCommitsDeletionsOfTrackedRuntimeFilesAndSucceedsWithWarningsOnly()
    {
        SeedTrackedRuntimeFile("logs/bus/2026-01-01.jsonl", DateTimeOffset.UtcNow.AddDays(-200));
        SeedTrackedRuntimeFile("logs/bus/2026-01-02.jsonl", DateTimeOffset.UtcNow.AddDays(-200));
        SeedTrackedRuntimeFile(".metadata/attempt-authority.archive-2026-01-01.json", DateTimeOffset.UtcNow.AddDays(-200));
        _fixture.CommitAll("seed runtime");

        var exitCode = await RunAsync("apply");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(_fixture.Workspace, "logs", "bus", "2026-01-01.jsonl")));
        Assert.False(File.Exists(Path.Combine(_fixture.Workspace, ".metadata", "attempt-authority.archive-2026-01-01.json")));

        // Nothing may be left behind in the index or the working tree for the rotated paths.
        var staged = Git("diff", "--cached", "--name-only");
        Assert.True(string.IsNullOrWhiteSpace(staged), $"index still holds: {staged}");

        var committed = Git("show", "--name-only", "--format=", "HEAD");
        Assert.Contains("logs/bus/2026-01-01.jsonl", committed, StringComparison.Ordinal);
        Assert.Contains("logs/bus/2026-01-02.jsonl", committed, StringComparison.Ordinal);
        Assert.Contains(".metadata/attempt-authority.archive-2026-01-01.json", committed, StringComparison.Ordinal);
        Assert.Contains("rotate runtime artifacts", Git("log", "-1", "--format=%s"), StringComparison.Ordinal);

        var tracked = Git("ls-files");
        Assert.DoesNotContain("logs/bus/", tracked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrackedRuntimeFilesAreDeletedWithoutFailingTheRun()
    {
        // Never committed, so there is nothing for git to stage - this must not abort the run.
        SeedTrackedRuntimeFile("logs/bus/untracked.jsonl", DateTimeOffset.UtcNow.AddDays(-200));

        var exitCode = await RunAsync("apply");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(_fixture.Workspace, "logs", "bus", "untracked.jsonl")));
        Assert.Empty(LatestReport().Errors);
    }

    private void SeedTrackedRuntimeFile(string relativePath, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_fixture.Workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"runtime\":true}\n");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
    }

    private async Task<int> RunAsync(string operation)
    {
        var args = new[] { "retention", operation, "--workspace", _fixture.Workspace, "--archive", _fixture.Archive, "--policy", "default", "--json" };
        return await RetentionCommand.RunAsync(TaskServerCommandLine.Parse(args).Retention!, default);
    }

    private RetentionCliReport LatestReport()
    {
        var path = Directory.EnumerateFiles(Path.Combine(_fixture.Workspace, ".metadata", "retention-runs"), "*.json")
            .OrderBy(item => item, StringComparer.Ordinal).Last();
        return JsonSerializer.Deserialize<RetentionCliReport>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _fixture.Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
