using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewUsageAuditTests
{
    [Fact]
    public void InterruptedClaudeReview_UsesDistinctAssistantMessages()
    {
        const string first = """{"type":"assistant","message":{"id":"one","usage":{"input_tokens":10,"output_tokens":2,"cache_read_input_tokens":100}}}""";
        const string second = """{"type":"assistant","message":{"id":"two","usage":{"input_tokens":20,"output_tokens":3,"cache_read_input_tokens":200}}}""";
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(string.Join('\n', first, first, second), "claude-opus-5-5", "claude");
        Assert.Equal(30, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(300, usage.CacheReadTokens);
    }

    [Fact]
    public void ClaudeTerminalUsage_SupersedesAssistantUsage()
    {
        const string output = """
            {"type":"assistant","message":{"id":"one","usage":{"input_tokens":10,"output_tokens":2}}}
            {"type":"result","usage":{"input_tokens":30,"output_tokens":5,"cache_read_input_tokens":300}}
            """;
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(output, "claude-opus-5-5", "claude");
        Assert.Equal(30, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(300, usage.CacheReadTokens);
    }

    [Fact]
    public void CodexCachedInput_IsNormalizedOnce()
    {
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(
            """{"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":900,"output_tokens":20}}""",
            "gpt-6-sol", "codex");
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(900, usage.CacheReadTokens);
        Assert.True(usage.InputIncludesCached);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("{\"value\":\"one\"}")]
    [InlineData("[\"one\"]")]
    [InlineData("true")]
    [InlineData("null")]
    public void NonStringMessageId_IsCountedAsAnUnnamedFrameInsteadOfThrowing(string id)
    {
        var frame = "{\"type\":\"assistant\",\"message\":{\"id\":" + id
            + ",\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}}";
        const string named = """{"type":"assistant","message":{"id":"two","usage":{"input_tokens":20,"output_tokens":3}}}""";

        var usage = RemoteReviewAgentCommandRunner.ParseUsage(string.Join('\n', frame, named), "claude-opus-5-5", "claude");

        Assert.Equal(30, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
    }

    [Fact]
    public void NonObjectProtocolLines_CarryNoTokenFacts()
    {
        const string output = """
            42
            "plain text"
            {"type":"assistant","message":"not an object"}
            {"type":"assistant","message":{"id":"one","usage":{"input_tokens":10,"output_tokens":2}}}
            """;

        var usage = RemoteReviewAgentCommandRunner.ParseUsage(output, "claude-opus-5-5", "claude");

        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(2, usage.OutputTokens);
    }
}
