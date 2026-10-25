using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class HostCliSecurityGuardTests
{
    [Fact]
    public void Host_cli_launchers_have_no_permission_skip_and_use_empty_workspaces()
    {
        var root = RepositoryRoot.Find();
        var features = Path.Combine(root, "backend", "Features");
        var offenders = Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) != "HostCliSpawnGuard.cs")
            .Where(file => File.ReadAllText(file).Contains("--dangerously-skip-permissions",
                StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file)).ToArray();
        Assert.Empty(offenders);

        var directClaudeLaunchers = Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("HostOneShotIsolation.Workspace", StringComparison.Ordinal))
            .ToArray();
        Assert.True(directClaudeLaunchers.Length >= 7, "The host Claude launcher inventory unexpectedly shrank.");
        foreach (var file in directClaudeLaunchers)
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);
            Assert.Contains("HostOneShotIsolation.Workspace", source);
            Assert.Contains("WorkingDirectory = workspace.Path", source);
            Assert.True(source.Contains("HostOneShotIsolation.AddClaudeArguments", StringComparison.Ordinal)
                || source.Contains("AdHocClaudeInvoker.BuildArgs", StringComparison.Ordinal), relative);
            Assert.Contains("HostCliSpawnGuard.Start(psi", source);
            Assert.True(source.Contains("CliTypes.Claude)", StringComparison.Ordinal)
                || source.Contains("NormalizeReviewCliType(cli)", StringComparison.Ordinal)
                || source.Contains("cliType)", StringComparison.Ordinal)
                || source.Contains("Path.GetFileNameWithoutExtension(cli.Replace", StringComparison.Ordinal), relative);
            Assert.DoesNotContain("ArgumentList.Add(\"--tools\")", source);
            Assert.DoesNotContain("--allowedTools", source);
        }
    }

    [Fact]
    public void Legacy_review_and_supervisor_launchers_use_the_active_security_profile()
    {
        var root = RepositoryRoot.Find();
        foreach (var relative in new[]
        {
            "backend/Features/Runner/ReviewDecisionOrchestrator.cs",
            "backend/Features/Runner/AspectRunnerService.cs",
            "backend/Features/Supervisor/SoftReasoningHostedService.cs",
        })
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            Assert.DoesNotContain("HostCliSpawnGuard.Start(psi, networked: true", source);
            Assert.Contains("SecurityProfiles.IsNetworked", source);
        }
    }

    [Fact]
    public void Host_claude_arguments_disable_tools_and_reject_tool_overrides()
    {
        var start = new ProcessStartInfo { CreateNoWindow = true };
        HostOneShotIsolation.AddClaudeArguments(start.ArgumentList, "claude-test",
            ["--tools", "", "--max-budget-usd", "0.10"]);
        var args = start.ArgumentList.ToArray();
        Assert.Equal("", args[Array.LastIndexOf(args, "--tools") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("{}", args[Array.LastIndexOf(args, "--mcp-config") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            (new ProcessStartInfo { CreateNoWindow = true }).ArgumentList, "claude-test", ["--tools", "Bash"]));
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            (new ProcessStartInfo { CreateNoWindow = true }).ArgumentList, "claude-test", ["--allowedTools", "Bash"]));
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            (new ProcessStartInfo { CreateNoWindow = true }).ArgumentList, "claude-test", ["--dangerously-skip-permissions"]));
        Assert.Throws<ArgumentException>(() => HostOneShotIsolation.AddClaudeArguments(
            (new ProcessStartInfo { CreateNoWindow = true }).ArgumentList, "claude-test",
            ["--max-budget-usd", "--dangerously-skip-permissions"]));
        using var workspace = new HostOneShotIsolation.Workspace();
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void Networked_spawn_boundary_accepts_only_isolated_tool_free_claude()
    {
        using var workspace = new HostOneShotIsolation.Workspace();
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = workspace.Path,
            CreateNoWindow = true,
        };
        HostOneShotIsolation.AddClaudeArguments(start.ArgumentList, "claude-test");
        HostCliSpawnGuard.Validate(start, networked: true);
        start.ArgumentList.Add("--permission-mode");
        start.ArgumentList.Add("bypassPermissions");
        Assert.Throws<InvalidOperationException>(() => HostCliSpawnGuard.Validate(start, networked: true));
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
        start.ArgumentList.RemoveAt(start.ArgumentList.IndexOf("--strict-mcp-config"));
        Assert.Throws<InvalidOperationException>(() => HostCliSpawnGuard.Validate(start, networked: true));
        foreach (var provider in new[] { "codex", "gemini" })
            Assert.Throws<InvalidOperationException>(() => HostCliSpawnGuard.Validate(
                new ProcessStartInfo(provider) { CreateNoWindow = true }, networked: true));
        Assert.Throws<InvalidOperationException>(() => HostCliSpawnGuard.Validate(
            new ProcessStartInfo("renamed-provider") { CreateNoWindow = true },
            networked: true, provider: CliTypes.Codex));
        var car = new DecoratingCliProcessSpawner(null, networked: true);
        Assert.Throws<InvalidOperationException>(() => car.Spawn(
            new ProcessStartInfo("agentapi") { CreateNoWindow = true }));
    }

    [Fact]
    public async Task Networked_pty_refuses_interactive_cli_before_spawn()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PtySession.SpawnAsync("claude", networked: true));
        Assert.Contains("Host interactive CLI execution is disabled", error.Message);
    }

    [Fact]
    public void Every_host_provider_process_launcher_uses_the_spawn_boundary()
    {
        var root = RepositoryRoot.Find();
        var features = Path.Combine(root, "backend", "Features");
        var unguarded = Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("HostOneShotIsolation.Workspace", StringComparison.Ordinal)
                    && source.Contains("Process.Start(psi)", StringComparison.Ordinal)
                    && !source.Contains("HostCliSpawnGuard.Start(psi", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(root, file));
        Assert.Empty(unguarded);
        var literalLaunches = Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return Regex.IsMatch(source,
                    "(?:FileName\\s*=|new\\s+ProcessStartInfo\\s*\\()\\s*\"(?:claude|codex|gemini)(?:\\.exe|\\.cmd)?\"",
                    RegexOptions.IgnoreCase)
                    && Regex.IsMatch(source, "(?:Process\\.Start\\s*\\(|\\.Start\\s*\\()")
                    && !source.Contains("HostCliSpawnGuard", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(root, file));
        Assert.Empty(literalLaunches);
        var carSpawner = File.ReadAllText(Path.Combine(features, "Cli", "Execution", "CarProcessSpawner.cs"));
        Assert.Contains("HostCliSpawnGuard.Validate(startInfo, networked)", carSpawner);
        var commitMessageLauncher = File.ReadAllText(Path.Combine(features, "Git", "GitService.cs"));
        Assert.Contains("HostCliSpawnGuard.Start(psi, SecurityProfiles.IsNetworked(_config), CliTypes.Codex)", commitMessageLauncher);
        var ptyLaunches = Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file),
                    "await\\s+PtySession\\.SpawnAsync\\((?<arguments>[\\s\\S]*?)\\);",
                    RegexOptions.None)
                .Select(match => (File: file, Arguments: match.Groups["arguments"].Value)))
            .ToArray();
        Assert.True(ptyLaunches.Length >= 5, "The host PTY launch inventory unexpectedly shrank.");
        Assert.Empty(ptyLaunches.Where(site =>
            !site.Arguments.Contains("networked:", StringComparison.Ordinal))
            .Select(site => Path.GetRelativePath(root, site.File)));
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
