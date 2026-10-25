using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Tests;

public sealed class CodexOneShotTests
{
    [Fact]
    public async Task Networked_profile_refuses_host_codex_before_spawn()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Security:Profile"] = "networked",
                ["CodexCli:Path"] = "missing-codex-executable" }).Build();
        var parsers = new CliUsageParserRegistry([new CodexUsageParser()]);
        var oneShot = new CodexOneShot(config, NullLogger<CodexOneShot>.Instance,
            parsers, new CliModelRegistry());

        var result = await oneShot.RunAsync(new CliOneShotRequest("codex", "gpt-5.5", "hello"));

        Assert.False(result.Ok);
        Assert.Contains("Remote runner required", result.Error);
    }

    [Fact]
    public void BuildStartInfo_OrchestratorChatOutsideGit_AddsSkipGitRepoCheck()
    {
        var nonRepository = Path.Combine(Path.GetTempPath(), "codex-chat-non-repo");
        var request = new CliOneShotRequest("codex", "gpt-5.5", "hello")
        {
            WorkingDirectory = nonRepository,
            Source = "orchestrator-chat",
        };

        var startInfo = CodexOneShot.BuildStartInfo("codex", request);

        Assert.Equal(nonRepository, startInfo.WorkingDirectory);
        Assert.Contains("--skip-git-repo-check", startInfo.ArgumentList);
        Assert.Contains("read-only", startInfo.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_NonChatSource_KeepsGitRepoCheck()
    {
        var request = new CliOneShotRequest("codex", "gpt-5.5", "hello")
        {
            Source = "review-decision",
        };

        var startInfo = CodexOneShot.BuildStartInfo("codex", request);

        Assert.DoesNotContain("--skip-git-repo-check", startInfo.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_InlineImagePaths_AreAttachedBeforeStdinPrompt()
    {
        var request = new CliOneShotRequest("codex", "gpt-5.4-mini", "inspect");
        var startInfo = CodexOneShot.BuildStartInfo(
            "codex",
            request,
            ["/tmp/before.png", "/tmp/after.png"]);

        var args = startInfo.ArgumentList.ToArray();
        Assert.Equal(2, args.Count(arg => arg == "--image"));
        Assert.Contains("/tmp/before.png", args);
        Assert.Contains("/tmp/after.png", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void ParseOutput_ExtractsFinalReplyAndSparkUsage()
    {
        const string stdout = """
            {"type":"thread.started","thread_id":"019-test"}
            {"type":"item.completed","item":{"type":"reasoning","text":"hidden"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"[[ASPECT_VERDICT: status=pass; summary=Spark handled the summary.]]"}}
            {"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":800,"output_tokens":42,"reasoning_output_tokens":9}}
            """;
        var started = DateTime.UtcNow;

        var result = CodexOneShot.ParseOutput(
            0, stdout, "", "gpt-5.3-codex-spark", started, started.AddMilliseconds(250),
            new CodexUsageParser(), new CliModelRegistry());

        Assert.True(result.Ok);
        Assert.Contains("Spark handled the summary", result.ParsedText);
        Assert.Equal("gpt-5.3-codex-spark", result.Usage!.Model);
        Assert.Equal(400, result.Usage.InputTokens);
        Assert.Equal(800, result.Usage.CacheReadTokens);
        Assert.True(result.Usage.InputIncludesCached);
        Assert.Equal(42, result.Usage.OutputTokens);
        Assert.Equal(9, result.RichUsage!.ReasoningOutput);
    }

    [Fact]
    public void ParseOutput_TurnFailureFailsEvenWhenProcessExitIsZero()
    {
        const string stdout = """
            {"type":"turn.failed","error":{"message":"model is unavailable"}}
            """;
        var now = DateTime.UtcNow;

        var result = CodexOneShot.ParseOutput(
            0, stdout, "", "gpt-5.3-codex-spark", now, now,
            new CodexUsageParser(), new CliModelRegistry());

        Assert.False(result.Ok);
        Assert.Equal("model is unavailable", result.Error);
    }
}
