using System.Text.Json;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskSwitchTraceTests
{
    [Fact]
    public void CorrelationIds_AreCanonicalAndRejectUntrustedText()
    {
        const string id = "67cb677e-9fdb-48b1-8615-8709f729c4fd";
        var trace = TaskSwitchTrace.Begin(id, id);
        try
        {
            Assert.Equal("67cb677e9fdb48b186158709f729c4fd", trace.RequestId);
            Assert.Equal(trace.RequestId, trace.SwitchId);
            using var json = JsonDocument.Parse(trace.Summary("ok", 200, 42, (2, 7, 0)));
            Assert.Equal(42, json.RootElement.GetProperty("bytes").GetInt64());
            Assert.Equal(2, json.RootElement.GetProperty("gitSpawns").GetInt32());
        }
        finally { trace.Restore(); }

        var rejected = TaskSwitchTrace.ParseId("secret/path\nheader");
        Assert.True(Guid.TryParseExact(rejected, "N", out _));
        Assert.DoesNotContain("secret", rejected);
    }

    [Fact]
    public void StageCapture_IsBoundedAndRestoresAmbientScope()
    {
        Assert.Null(TaskSwitchTrace.Current);
        var trace = TaskSwitchTrace.Begin(null, null);
        try
        {
            using (TaskSwitchTrace.Span("sidecar.prompt")) TaskSwitchTrace.FileRead();
            using var json = JsonDocument.Parse(trace.Summary("ok", 200, 0, null));
            Assert.Equal(1, json.RootElement.GetProperty("stages")
                .GetProperty("sidecar.prompt").GetProperty("count").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("stages")
                .GetProperty("sidecar.prompt").GetProperty("files").GetInt32());
            Assert.DoesNotContain("prompt text", json.RootElement.GetRawText());
        }
        finally { trace.Restore(); }
        Assert.Null(TaskSwitchTrace.Current);
    }

    [Fact]
    public async Task TracedResult_PreservesJsonResponseBody()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var trace = TaskSwitchTrace.Begin(null, null);
        trace.Restore();
        var result = new TaskSwitchTracedResult(
            Results.Json(new { hello = "world" }), trace, (0, 0, 0), 0, NullLogger.Instance);

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("world", document.RootElement.GetProperty("hello").GetString());
        using var summary = JsonDocument.Parse(trace.Summary("ok", 200, context.Response.Body.Length, (0, 0, 0)));
        Assert.True(summary.RootElement.GetProperty("stages").TryGetProperty("serialize.write", out _));
    }
}
