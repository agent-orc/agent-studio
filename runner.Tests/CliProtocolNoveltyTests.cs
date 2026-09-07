using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using CodingAgentRunner.Events;
using Xunit;

namespace AgentRunner.Tests;

public sealed class CliProtocolNoveltyTests
{
    [Fact]
    public void Unknown_structured_frames_emit_scrubbed_per_type_and_total_counters()
    {
        var tracker = new CliProtocolNoveltyTracker("codex");
        const string firstRaw =
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"future_widget\",\"secret\":\"do-not-ship\"}}";
        const string secondRaw =
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"future_widget\",\"secret\":\"different\"}}";

        Assert.True(tracker.TryObserve(new CliRunEvent.Unknown("future", firstRaw), out var first));
        Assert.True(tracker.TryObserve(new CliRunEvent.Unknown("future", secondRaw), out var second));

        Assert.Equal("item.completed/future_widget", first.FrameType);
        Assert.Equal(1, first.Occurrence);
        Assert.Equal(1, first.TotalUnknownFrames);
        Assert.Equal(2, second.Occurrence);
        Assert.Equal(2, second.TotalUnknownFrames);
        Assert.NotEqual(first.PayloadSha256, second.PayloadSha256);
        Assert.DoesNotContain("do-not-ship", first.ToMarker(), StringComparison.Ordinal);
        Assert.True(ProtocolNoveltyTelemetry.TryParseMarker(first.ToMarker(), out var roundTrip));
        Assert.Equal(first, roundTrip);
    }

    [Fact]
    public void Truncated_json_frame_is_visible_as_malformed_instead_of_silently_dropped()
    {
        var tracker = new CliProtocolNoveltyTracker("claude");

        Assert.True(tracker.TryObserveRaw("{\"type\":\"future.frame\",\"payload\":", out var telemetry));

        Assert.Equal("<malformed-json>", telemetry.FrameType);
        Assert.Equal(1, telemetry.TotalUnknownFrames);
    }

    [Fact]
    public void Unstructured_stderr_is_not_misreported_as_a_provider_frame()
    {
        var tracker = new CliProtocolNoveltyTracker("codex");

        Assert.False(tracker.TryObserveRaw("codex: connection closed", out _));
        Assert.False(tracker.TryObserveRaw("[[TASK_DONE]]", out _));
        Assert.False(tracker.TryObserveRaw("[[  TASK_NEEDS_INPUT:choose-column]]", out _));
    }

    [Fact]
    public void CodexTodoListLifecycleIsPartOfTheKnownVersionedVocabulary()
    {
        var tracker = new CliProtocolNoveltyTracker("codex");
        foreach (var frameType in new[] { "item.started", "item.updated", "item.completed" })
        {
            var raw = "{\"type\":\"" + frameType
                + "\",\"item\":{\"type\":\"todo_list\",\"items\":[{\"text\":\"Verify\",\"completed\":false}]}}";
            Assert.False(tracker.TryObserveFrame(raw, out _));
        }
    }

    [Fact]
    public void Codex_error_frame_is_a_known_typed_provider_failure()
    {
        const string raw =
            "{\"type\":\"error\",\"message\":\"Selected model is at capacity. Please try a different model.\"}";
        var tracker = new CliProtocolNoveltyTracker("codex");

        Assert.False(tracker.TryObserveFrame(raw, out _));
        var mapped = RunnerCodexEventAdapter.MapKnownError(
            new CliRunEvent.Unknown("unknown frame", raw));

        var failed = Assert.IsType<CliRunEvent.TurnFailed>(mapped);
        Assert.Equal("Selected model is at capacity. Please try a different model.", failed.Reason);
    }

    [Fact]
    public void V1_event_classification_uses_the_protocol_novelty_kind()
    {
        var marker = new ProtocolNoveltyTelemetry(
            "codex",
            "0.7.0",
            "future.frame",
            1,
            1,
            new string('a', 64)).ToMarker();

        var kind = TaskServerClient.ClassifyV1Event(
            new CliOutputLine(DateTime.UtcNow, "system", marker));

        Assert.Equal(LifecycleEventKinds.ProtocolUnknownFrame, kind);
    }

    [Fact]
    public async Task Legacy_log_ingest_also_posts_a_typed_timeline_diagnostic()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(http, "runner-protocol-test");
        var timestamp = new DateTime(2026, 8, 11, 10, 30, 0, DateTimeKind.Utc);
        var marker = new ProtocolNoveltyTelemetry(
            "claude",
            "0.7.0",
            "future.frame",
            1,
            1,
            new string('b', 64)).ToMarker();

        var response = await client.IngestLogsAsync(
            new LogIngestRequest(
                "AGT-2639",
                [new CliOutputLine(timestamp, "system", marker)],
                RunnerId: "runner-protocol-test",
                LeaseId: "lease-1",
                FencingToken: 7,
                AttemptId: "attempt-1"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(["/api/runner/logs", "/api/runner/events"], handler.Paths);
        var diagnostic = Assert.IsType<RunnerEventIngestRequest>(handler.EventRequest);
        Assert.Equal("diagnostic", diagnostic.Kind);
        Assert.Equal("cli-frame-unknown", diagnostic.Code);
        Assert.Equal("warning", diagnostic.Severity);
        Assert.Contains("future.frame", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, diagnostic.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public List<string> Paths { get; } = [];
        public object? EventRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            if (path == "/api/runner/events")
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                EventRequest = JsonSerializer.Deserialize<RunnerEventIngestRequest>(json, Json);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var body = JsonSerializer.Serialize(new LogIngestResponse("AGT-2639", 1), Json);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        }
    }
}
