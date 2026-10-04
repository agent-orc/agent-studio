using Xunit;
using AgentStudio.TestSupport;

namespace TaskServer.Tests;

public sealed class RemoteReviewArchitectureTests
{
    [Fact]
    public void Task_server_authoritative_review_path_contains_no_project_process_or_checkout_primitive()
    {
        var root = FindRepositoryRoot();
        var taskServer = Path.Combine(root, "task-server");
        var sources = Directory.EnumerateFiles(taskServer, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();
        string[] forbidden =
        [
            "Process.Start(",
            "ProcessRunner",
            "GitWorkspace",
            "CliOneShot",
            "git clone",
            "dotnet test",
            "npm test",
        ];

        foreach (var token in forbidden)
            Assert.DoesNotContain(sources, source =>
                source.Text.Contains(token, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
        => RepositoryRoot.Find();
}
