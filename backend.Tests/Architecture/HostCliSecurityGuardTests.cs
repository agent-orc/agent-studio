using System.Diagnostics;
using AgentStudio.Cli;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class HostCliSecurityGuardTests
{
    private static readonly string[] DirectClaudeLaunchers =
    [
        "backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs",
        "backend/Features/Tasks/TitleGenerationService.cs",
        "backend/Features/Prompts/PromptEnhancementService.cs",
        "backend/Features/Review/SummaryGenerationService.cs",
        "backend/Features/Runner/ReviewDecisionOrchestrator.cs",
        "backend/Features/Runner/AspectRunnerService.cs",
        "backend/Features/Supervisor/SoftReasoningHostedService.cs",
    ];

    [Fact]
    public void Host_cli_launchers_have_no_permission_skip_and_use_empty_workspaces()
    {
        var root = RepositoryRoot.Find();
        var features = Path.Combine(root, "backend", "Features");
        var offenders = Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("--dangerously-skip-permissions",
                StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file)).ToArray();
        Assert.Empty(offenders);

        foreach (var relative in DirectClaudeLaunchers)
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            Assert.Contains("HostOneShotIsolation.Workspace", source);
            Assert.Contains("WorkingDirectory = workspace.Path", source);
            Assert.True(source.Contains("HostOneShotIsolation.AddClaudeArguments", StringComparison.Ordinal)
                || source.Contains("AdHocClaudeInvoker.BuildArgs", StringComparison.Ordinal), relative);
            Assert.DoesNotContain("ArgumentList.Add(\"--tools\")", source);
            Assert.DoesNotContain("--allowedTools", source);
        }
    }

    [Fact]
    public void Host_claude_arguments_disable_tools_and_reject_tool_overrides()
    {
        var start = new ProcessStartInfo();
        HostOneShotIsolation.AddClaudeArguments(start.ArgumentList, "claude-test",
            ["--tools", "", "--max-budget-usd", "0.10"]);
        var args = start.ArgumentList.ToArray();
        Assert.Equal("", args[Array.LastIndexOf(args, "--tools") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            new ProcessStartInfo().ArgumentList, "claude-test", ["--tools", "Bash"]));
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            new ProcessStartInfo().ArgumentList, "claude-test", ["--allowedTools", "Bash"]));
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            new ProcessStartInfo().ArgumentList, "claude-test", ["--dangerously-skip-permissions"]));
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            new ProcessStartInfo().ArgumentList, "claude-test",
            ["--max-budget-usd", "--dangerously-skip-permissions"]));
        using var workspace = new HostOneShotIsolation.Workspace();
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
    }

    [Fact]
    public async Task Networked_host_claude_does_not_resume_owner_session()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Security:Profile"] = "networked" }).Build();
        var oneShot = new ClaudeOneShot(config, NullLogger<ClaudeOneShot>.Instance,
            new CliUsageParserRegistry([new ClaudeUsageParser()]), new CliModelRegistry());

        var result = await oneShot.RunAsync(new CliOneShotRequest("claude", "claude-test", "hello")
        {
            ExtraArgs = ["-r", Guid.NewGuid().ToString("D")]
        });

        Assert.False(result.Ok);
        Assert.Contains("session resume is disabled", result.Error);
    }
}
