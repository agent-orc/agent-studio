using System.Text.Json;
using AgentStudio.Cli;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewUsageAuditTests
{
    [Theory]
    [InlineData("claude", "result", "claude-opus-5-5", "claude-sonnet-5")]
    [InlineData("codex", "turn.completed", "gpt-6-sol", "gpt-6.1-sol")]
    public void ObservedModel_PreservesThePinAndCanonicalMismatch(string cli, string type, string pinned, string observed)
    {
        var frame = JsonSerializer.Serialize(new { type, model = observed,
            usage = new { input_tokens = 1000, cached_input_tokens = 900, cache_read_input_tokens = 900, output_tokens = 20 } });
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(frame, pinned, cli);
        ICliUsageParser parser = cli == "claude" ? new ClaudeUsageParser() : new CodexUsageParser();
        using var json = JsonDocument.Parse(frame);
        var canonical = Assert.Single(parser.ParseAll(json.RootElement, pinned, new CliModelRegistry()));
        var row = Assert.Single(usage.ModelUsage!);
        Assert.Equal(canonical.Model, row.Model);
        Assert.Equal(canonical.PinnedModel, usage.PinnedModel);
        Assert.Equal(canonical.ModelMismatch, row.ModelMismatch);
        Assert.True(usage.ModelMismatch);
        Assert.Equal(observed, usage.Model);
        Assert.Equal("observed", row.ModelSource);
        Assert.Equal(canonical.Input, usage.InputTokens);
        Assert.Equal(canonical.CacheRead, usage.CacheReadTokens);
    }

    [Fact]
    public void ClaudeSingleModelMetadata_AttributesAggregateUsageLikeTheCanonicalParser()
    {
        const string frame = """{"type":"result","usage":{"input_tokens":10,"output_tokens":2},"modelUsage":{"claude-sonnet-5":{"costUSD":0.1}}}""";
        using var json = JsonDocument.Parse(frame);
        var expected = Assert.Single(new ClaudeUsageParser().ParseAll(json.RootElement, "claude-opus-5-5", new CliModelRegistry()));
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(frame, "claude-opus-5-5", "claude");
        Assert.Equal(expected.Model, usage.Model);
        Assert.Equal(expected.Input, usage.InputTokens);
        Assert.True(usage.ModelMismatch);
        Assert.Equal("observed", usage.ModelSource);
    }

    [Fact]
    public void ClaudeMultiModelTotals_MatchCanonicalRowsAndReplaceRepeatedResults()
    {
        var result = """
            {"type":"result","session_id":"review-session","usage":{"input_tokens":99999},"modelUsage":{
              "claude-opus-5-5":{"inputTokens":10,"outputTokens":2,"cacheReadInputTokens":100,"cacheCreationInputTokens":30},
              "claude-sonnet-5":{"input_tokens":20,"output_tokens":3,"cache_read_input_tokens":200,"cache_creation_input_tokens":40}}}
            """.ReplaceLineEndings("");
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(string.Join('\n', result, result, result), "claude-opus-5-5", "claude");
        using var json = JsonDocument.Parse(result);
        var canonical = new ClaudeUsageParser().ParseAll(json.RootElement, "claude-opus-5-5", new CliModelRegistry());
        Assert.Equal(2, usage.ModelUsage!.Count);
        foreach (var expected in canonical)
        {
            var actual = Assert.Single(usage.ModelUsage, row => row.Model == expected.Model);
            Assert.Equal(expected.Input, actual.InputTokens);
            Assert.Equal(expected.Output, actual.OutputTokens);
            Assert.Equal(expected.CacheRead, actual.CacheReadTokens);
            Assert.Equal(expected.CacheWrite, actual.CacheCreationTokens);
            Assert.Equal(expected.ModelMismatch, actual.ModelMismatch);
        }
        Assert.Equal(30, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(300, usage.CacheReadTokens);
        Assert.Equal(70, usage.CacheCreationTokens);
        Assert.Null(usage.Model);
        Assert.Equal("per-model", usage.ModelSource);
        Assert.True(usage.ModelMismatch);
    }

    [Theory]
    [InlineData("0", 1000, 0)]
    [InlineData("900", 100, 900)]
    [InlineData("1000", 0, 1000)]
    public void KnownCodexSplit_ConservesProviderInputExactlyOnce(string cached, long input, long cache)
    {
        var frame = "{\"type\":\"turn.completed\",\"model\":\"gpt-6-sol\",\"usage\":{\"input_tokens\":1000,\"cached_input_tokens\":" + cached + ",\"output_tokens\":20}}";
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(string.Join('\n', frame, frame), "gpt-6-sol", "codex");
        Assert.True(usage.InputIncludesCached);
        Assert.Equal(input, usage.InputTokens);
        Assert.Equal(cache, usage.CacheReadTokens);
        Assert.Equal(1000, usage.InputTokens + usage.CacheReadTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Single(usage.ModelUsage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"cached_input_tokens\":null")]
    [InlineData(",\"cached_input_tokens\":\"0\"")]
    [InlineData(",\"cached_input_tokens\":false")]
    [InlineData(",\"cached_input_tokens\":{}")]
    [InlineData(",\"cached_input_tokens\":-1")]
    [InlineData(",\"cached_input_tokens\":0.5")]
    [InlineData(",\"cached_input_tokens\":1001")]
    public void MissingOrInvalidCodexCache_IsUnknownRatherThanZero(string cacheField)
    {
        var frame = "{\"type\":\"turn.completed\",\"model\":\"gpt-6-sol\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":20" + cacheField + "}}";
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(frame, "gpt-6-sol", "codex");
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(0, usage.CacheReadTokens);
        Assert.Null(usage.InputIncludesCached);
        Assert.Null(Assert.Single(usage.ModelUsage!).InputIncludesCached);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void MissingObservedModel_RetainsExplicitRequestedFallback(string cli)
    {
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(
            """{"type":"result","usage":{"input_tokens":10,"output_tokens":2}}""", "requested-model", cli);
        Assert.Null(usage.Model);
        Assert.Equal("requested-model", usage.PinnedModel);
        Assert.Equal("requested-fallback", usage.ModelSource);
        Assert.False(usage.ModelMismatch);
        var row = Assert.Single(usage.ModelUsage!);
        Assert.Null(row.Model);
        Assert.Equal("requested-model", row.PinnedModel);
        Assert.Equal("requested-fallback", row.ModelSource);
    }

    [Fact]
    public void LatestResult_ReplacesEarlierTotalsAndLateAssistantFrames()
    {
        const string first = """{"type":"result","usage":{"input_tokens":10,"output_tokens":2},"model":"claude-opus-5-5"}""";
        const string last = """{"type":"result","usage":{"input_tokens":20,"output_tokens":3},"model":"claude-sonnet-5"}""";
        const string late = """{"type":"assistant","message":{"id":"late","usage":{"input_tokens":999}}}""";
        var usage = RemoteReviewAgentCommandRunner.ParseUsage(string.Join('\n', first, last, last, late), "claude-opus-5-5", "claude");
        Assert.Equal(20, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal("claude-sonnet-5", usage.Model);
        Assert.Single(usage.ModelUsage!);
    }

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
