

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Gemini stream-json -> CliRunEvent mapping. Fixtures come
/// from real <c>gemini -p ... -o stream-json --skip-trust -y</c> output
/// captured 2026-05-03 against gemini-cli.
/// </summary>
public class GeminiEventAdapterTests
{
    private const string Jk = "::gemini-test";

    [Fact]
    public void InitFrame_EmitsSessionStarted()
    {
        const string frame = """{"type":"init","timestamp":"2026-05-03T15:13:54.504Z","session_id":"2cd99867-7c64-44bf-9344-dd92df418270","model":"auto-gemini-3"}""";
        var ss = Assert.IsType<CliRunEvent.SessionStarted>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("2cd99867-7c64-44bf-9344-dd92df418270", ss.SessionId);
    }

    [Fact]
    public void UserMessage_IsIgnored()
    {
        const string frame = """{"type":"message","role":"user","content":"hi"}""";
        Assert.Empty(GeminiEventAdapter.Map(frame, Jk));
    }

    [Fact]
    public void AssistantMessage_EmitsOutputDelta()
    {
        const string frame = """{"type":"message","role":"assistant","content":"Hello, how are you?","delta":true}""";
        var od = Assert.IsType<CliRunEvent.OutputDelta>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("Hello, how are you?", od.Text);
    }

    [Fact]
    public void ToolCall_EmitsToolStarted()
    {
        const string frame = """{"type":"tool_call","name":"ReadFile","input":{"file_path":"a.cs"}}""";
        var ts = Assert.IsType<CliRunEvent.ToolStarted>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("ReadFile", ts.ToolName);
        Assert.Equal("a.cs", ts.Argument);
    }

    [Fact]
    public void ToolResult_Success_EmitsToolCompleted()
    {
        const string frame = """{"type":"tool_result","name":"ReadFile","output":"line one\nline two"}""";
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("ReadFile", tc.ToolName);
        Assert.False(tc.IsError);
        Assert.Equal("line one", tc.FirstLine);
    }

    [Fact]
    public void ToolResult_Error_FlagsIsError()
    {
        const string frame = """{"type":"tool_result","name":"Bash","error":"boom","output":""}""";
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.True(tc.IsError);
    }

    [Fact]
    public void ResultSuccess_EmitsTurnCompleted_WithUsageStats()
    {
        const string frame = """{"type":"result","status":"success","stats":{"total_tokens":14187,"input_tokens":14151,"output_tokens":36,"cached":500,"input":13651,"tool_calls":0,"models":{"gemini-2.5-pro":{"total_tokens":14187,"input_tokens":14151,"output_tokens":36,"cached":500,"input":13651}}}}""";
        var tc = Assert.IsType<CliRunEvent.TurnCompleted>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.NotNull(tc.UsageSummary);
        Assert.Contains("input=14151", tc.UsageSummary);
        Assert.Contains("output=36", tc.UsageSummary);
        Assert.Contains("cached=500", tc.UsageSummary);
        Assert.Contains("tool_calls=0", tc.UsageSummary);
    }

    [Fact]
    public void ResultError_EmitsTurnFailed()
    {
        const string frame = """{"type":"result","status":"error","reason":"rate limit"}""";
        var tf = Assert.IsType<CliRunEvent.TurnFailed>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("error", tf.Reason);
    }

    [Fact]
    public void UnknownType_EmitsUnknown()
    {
        const string frame = """{"type":"experimental","payload":1}""";
        var u = Assert.IsType<CliRunEvent.Unknown>(
            Assert.Single(GeminiEventAdapter.Map(frame, Jk).ToList()));
        Assert.Contains("experimental", u.Sample);
    }

    [Fact]
    public void NonJsonOrMalformed_EmitsNothing()
    {
        Assert.Empty(GeminiEventAdapter.Map("", Jk));
        Assert.Empty(GeminiEventAdapter.Map("not-json", Jk));
        Assert.Empty(GeminiEventAdapter.Map("{type:\"missing-quotes\"}", Jk));
        Assert.Empty(GeminiEventAdapter.Map("{\"type\":\"init\",", Jk));
    }
}
