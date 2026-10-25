using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using AgentStudio.Security;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class HostCliSecurityGuardTests
{
    [Fact]
    public void Host_spawn_boundary_has_one_guarded_process_start()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "backend", "Features", "Cli",
            "Execution", "HostCliSpawnGuard.cs");
        var source = File.ReadAllText(path);
        Assert.Single(Regex.Matches(source, @"\bProcess\.Start\s*\(").Cast<Match>());
        Assert.Matches(@"Validate\(startInfo, networked, provider\);\s*return Process\.Start\(startInfo\);",
            source);
    }

    private static string[] BackendSources(string root) => Directory
        .EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void Host_cli_launchers_have_no_permission_skip_and_use_empty_workspaces()
    {
        var root = RepositoryRoot.Find();
        var features = Path.Combine(root, "backend", "Features");
        var offenders = BackendSources(root)
            .Where(file => Path.GetFileName(file) is not ("HostCliSpawnGuard.cs" or "HostOneShotIsolation.cs"))
            .Where(file => File.ReadAllText(file).Contains("--dangerously-skip-permissions",
                StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file)).ToArray();
        Assert.Empty(offenders);
        var argumentBuilder = File.ReadAllText(Path.Combine(features, "Cli", "Routing", "OneShot",
            "HostOneShotIsolation.cs"));
        Assert.Matches(@"if \(!networked\)\s*\{\s*arguments.Add\(""--dangerously-skip-permissions""\)",
            argumentBuilder);

        var directClaudeLaunchers = BackendSources(root)
            .Where(file => File.ReadAllText(file).Contains("HostOneShotIsolation.Workspace", StringComparison.Ordinal))
            .ToArray();
        Assert.True(directClaudeLaunchers.Length >= 7, "The host Claude launcher inventory unexpectedly shrank.");
        foreach (var file in directClaudeLaunchers)
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);
            Assert.Contains("HostOneShotIsolation.Workspace", source);
            Assert.True(source.Contains("WorkingDirectory = workspace.Path", StringComparison.Ordinal)
                || source.Contains("WorkingDirectory = networked ? workspace!.Path", StringComparison.Ordinal),
                relative);
            Assert.True(source.Contains("HostOneShotIsolation.AddClaudeArguments", StringComparison.Ordinal)
                || source.Contains("AdHocClaudeInvoker.BuildArgs", StringComparison.Ordinal), relative);
            Assert.Contains("HostCliSpawnGuard.Start(psi", source);
            Assert.Contains("CliTypes.Claude)", source);
            Assert.DoesNotContain("ArgumentList.Add(\"--tools\")", source);
            Assert.DoesNotContain("--allowedTools", source);
            Assert.DoesNotContain("networked: true", source);
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
        Assert.Equal("{\"mcpServers\":{}}", args[Array.LastIndexOf(args, "--mcp-config") + 1]);
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
    public void Local_claude_arguments_keep_tools_and_session_resume()
    {
        var args = new List<string>();
        var session = Guid.NewGuid().ToString("D");
        HostOneShotIsolation.AddClaudeArguments(args, "claude-test", ["-r", session], networked: false);

        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--tools", args);
        Assert.DoesNotContain("--strict-mcp-config", args);
        Assert.Equal(session, args[args.IndexOf("-r") + 1]);
    }

    [Fact]
    public void File_mentions_are_not_passed_to_host_claude()
    {
        var prompt = "Read @/var/private/key @C:\\private\\key @~/.claude/.credentials.json "
            + "@../../data/home/.claude/.credentials.json @\"/tmp/private file\" @relative.md. @@/nested "
            + "Fix。@/secret 、@~/secret ？@../secret ！@\"/secret\" \uFEFF@/secret "
            + "Keep email@example.test and compact@relative.md.";
        var sanitized = HostOneShotIsolation.PreparePrompt(prompt);
        Assert.Equal(prompt.Replace('@', '＠'), sanitized);
        Assert.DoesNotContain('@', sanitized);
        Assert.Equal(sanitized, HostOneShotIsolation.PreparePrompt(sanitized));
        Assert.Equal(prompt, HostOneShotIsolation.PreparePrompt(prompt, isolated: false));
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
        Assert.Throws<InvalidOperationException>(() => HostCliSpawnGuard.Validate(start, networked: true));
        start.Environment["CLAUDE_CODE_DISABLE_ATTACHMENTS"] = "1";
        HostCliSpawnGuard.Validate(start, networked: true);
        start.Environment["CLAUDE_CODE_DISABLE_ATTACHMENTS"] = "0";
        Assert.Throws<InvalidOperationException>(() => HostCliSpawnGuard.Validate(start, networked: true));
        start.Environment["CLAUDE_CODE_DISABLE_ATTACHMENTS"] = "1";
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
    public void Nonlocal_claude_start_sets_attachment_block_at_the_spawn_boundary()
    {
        using var workspace = new HostOneShotIsolation.Workspace();
        var start = new ProcessStartInfo("missing-claude-binary")
        {
            WorkingDirectory = workspace.Path,
            CreateNoWindow = true,
        };
        HostOneShotIsolation.AddClaudeArguments(start.ArgumentList, "claude-test");
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            HostCliSpawnGuard.Start(start, networked: true, provider: CliTypes.Claude));
        Assert.Equal("1", start.Environment["CLAUDE_CODE_DISABLE_ATTACHMENTS"]);
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
        var unguarded = BackendSources(root)
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("HostOneShotIsolation.Workspace", StringComparison.Ordinal)
                    && source.Contains("Process.Start(psi)", StringComparison.Ordinal)
                    && !source.Contains("HostCliSpawnGuard.Start(psi", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(root, file));
        Assert.Empty(unguarded);
        var literalLaunches = BackendSources(root)
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
        Assert.Contains("HostCliSpawnGuard.Start(psi, !SecurityProfiles.IsLocal(_config), CliTypes.Codex)", commitMessageLauncher);
        var ptyLaunches = BackendSources(root)
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
    public void Configured_provider_executables_pass_the_spawn_guard()
    {
        var root = RepositoryRoot.Find();
        var expected = new Dictionary<string, (int Resolutions, int ConfigPaths, int Guards)>
        {
            ["backend/Features/Cli/Execution/BuiltInCliBehaviors.cs"] = (2, 5, 1),
            ["backend/Features/Cli/Execution/CliExecutionServiceBase.cs"] = (2, 0, 1),
            ["backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs"] = (1, 1, 2),
            ["backend/Features/Cli/Routing/OneShot/CodexOneShot.cs"] = (1, 1, 1),
            ["backend/Features/Git/GitService.cs"] = (2, 1, 2),
            ["backend/Features/Prompts/PromptEnhancementService.cs"] = (1, 1, 1),
            ["backend/Features/Review/SummaryGenerationService.cs"] = (1, 1, 1),
            ["backend/Features/Tasks/TitleGenerationService.cs"] = (1, 1, 1),
        };
        var sources = BackendSources(root)
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return Regex.IsMatch(source, @"\b\w+Cli:Path\b|\bResolveExecutable\s*\(")
                    && (source.Contains("Process.Start", StringComparison.Ordinal)
                        || source.Contains("proc.Start()", StringComparison.Ordinal)
                        || source.Contains("HostCliSpawnGuard.Start", StringComparison.Ordinal));
            })
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText);
        Assert.Equal(expected.Keys.OrderBy(path => path), sources.Keys.OrderBy(path => path));
        foreach (var (path, source) in sources)
        {
            var inventory = expected[path];
            Assert.Equal(inventory.Resolutions, Regex.Matches(source, @"\bResolveExecutable\s*\(").Count);
            Assert.Equal(inventory.ConfigPaths, Regex.Matches(source, @"\b\w+Cli:Path\b").Count);
            Assert.Equal(inventory.Guards, Regex.Matches(source, @"HostCliSpawnGuard\.(?:Start|Validate)\s*\(").Count);
        }
    }

    [Fact]
    public async Task Nonlocal_host_claude_does_not_resume_owner_session()
    {
        foreach (var profile in new[] { SecurityProfiles.Networked, SecurityProfiles.PublicDemo })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Security:Profile"] = profile }).Build();
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
}
