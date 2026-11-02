using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AOW-15: review validates the delivery's <c>.agent-studio/project.yml</c> at
/// the exact subject SHA right after the completion gate, so the agent gets the
/// issues in its own round instead of at the merge gate.
/// </summary>
public partial class ReviewDecisionOrchestratorCompletionGateTests
{
    [Fact]
    public async Task TaskDone_InvalidProjectDefinitionAtSubject_ReissuesWithTheIssuesBeforeTheBuildGate()
    {
        const string slug = "definition-invalid-job";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        SeedGitRepository();
        Directory.CreateDirectory(Path.Combine(_watchPath, ".agent-studio"));
        File.WriteAllText(Path.Combine(_watchPath, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [shell]\ncommands:\n  build: []\n  test: []\n  lint: []\n");
        RunGit(_watchPath, "add", ".agent-studio/project.yml");
        RunGit(_watchPath, "commit", "-q", "-m", "add project definition without prepare");
        WriteRemoteSubject(slug, HeadSha(), "chain-definition");
        var buildGate = new FakeBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 1, string.Empty, "build gate passed", true, false));
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(folder), "an invalid project definition should reissue to 2-ready");
        Assert.Equal(0, buildGate.CallCount);
        Assert.Equal(0, aspect.Invocations);
        var written = string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.Contains("[prepare-path-invalid]", written);
        Assert.Contains("commands.prepare is required", written);
    }

    [Fact]
    public async Task TaskDone_ValidProjectDefinitionAtSubject_ContinuesToTheBuildGate()
    {
        const string slug = "definition-valid-job";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        SeedGitRepository();
        Directory.CreateDirectory(Path.Combine(_watchPath, ".agent-studio"));
        File.WriteAllText(Path.Combine(_watchPath, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [shell]\ncommands:\n  prepare: .agent-studio/prepare\n  build: []\n  test: []\n  lint: []\n");
        File.WriteAllText(Path.Combine(_watchPath, ".agent-studio", "prepare"), "#!/bin/sh\nset -eu\n");
        RunGit(_watchPath, "add", ".agent-studio");
        RunGit(_watchPath, "commit", "-q", "-m", "add minimal project definition");
        WriteRemoteSubject(slug, HeadSha(), "chain-definition-valid");
        var buildGate = new FakeBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 1, string.Empty, "build gate passed", true, false));
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(1, buildGate.CallCount);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)));
    }

    private string HeadSha()
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _watchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }
}
