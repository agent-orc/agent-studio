using AgentRunner;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// A Task Server outage is exactly when the log buffer is at risk of unbounded
/// growth: every failed flush re-queues its batch while the live run keeps
/// producing output. This pins the hard cap so a backlog sheds oldest-first
/// instead of turning a transient outage into a memory blow-up.
/// </summary>
public class LogShipperCapTests
{
    private static LogShipper NewShipper(out System.Collections.Generic.List<string> diag)
    {
        var messages = new System.Collections.Generic.List<string>();
        diag = messages;
        using var http = new HttpClient { BaseAddress = new Uri("http://task-server-unused") };
        var client = new TaskServerClient(http, "runner-under-test");
        var lease = new RunLeaseInfoDto(
            "AGT-1", "runner-under-test", "runner", "host", 1, "backend",
            "lease-1", 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2));
        return new LogShipper(client, "AGT-1", lease, messages.Add);
    }

    [Fact]
    public void Pending_buffer_is_capped_and_drops_oldest_when_the_backlog_grows()
    {
        var shipper = NewShipper(out _);

        for (var i = 0; i < 25_000; i++)
            shipper.Add("stdout", $"line-{i}");

        Assert.True(shipper.PendingCount <= 20_000,
            $"pending buffer exceeded its cap: {shipper.PendingCount}");
        Assert.True(shipper.DroppedCount >= 5_000,
            $"expected the overflow to be dropped, dropped {shipper.DroppedCount}");
        Assert.Equal(25_000, shipper.PendingCount + shipper.DroppedCount);
    }

    [Fact]
    public void Output_within_the_cap_is_fully_retained()
    {
        var shipper = NewShipper(out _);

        for (var i = 0; i < 100; i++)
            shipper.Add("stdout", $"line-{i}");

        Assert.Equal(100, shipper.PendingCount);
        Assert.Equal(0, shipper.DroppedCount);
    }

    [Fact]
    public async Task Oversized_command_frame_is_shipped_as_parseable_bounded_json()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "runner-under-test");
        var lease = new RunLeaseInfoDto(
            "AGT-1", "runner-under-test", "runner", "host", 1, "backend",
            "lease-1", 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2));
        var shipper = new LogShipper(client, "AGT-1", lease, _ => { });
        var command = "rg -n \"needle\" .";
        var frame = JsonSerializer.Serialize(new
        {
            type = "item.completed",
            item = new
            {
                id = "item_13",
                type = "command_execution",
                command,
                aggregated_output = new string('x', 100 * 1024),
                exit_code = 0,
                status = "completed",
            },
        });

        shipper.Add("stdout", frame);
        Assert.True(await shipper.FlushAsync(CancellationToken.None));

        var shipped = Assert.Single(Assert.IsType<LogIngestRequest>(handler.Request).Lines).Text;
        Assert.True(shipped.Length <= 64 * 1024, $"shipped line was {shipped.Length} chars");
        using var parsed = JsonDocument.Parse(shipped);
        var item = parsed.RootElement.GetProperty("item");
        Assert.Equal("item_13", item.GetProperty("id").GetString());
        Assert.Equal("command_execution", item.GetProperty("type").GetString());
        Assert.Equal(command, item.GetProperty("command").GetString());
        Assert.Contains("payload cut at the 64 KiB log line cap", item.GetProperty("aggregated_output").GetString());
        Assert.True(item.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Oversized_plain_text_keeps_the_plain_truncation_marker()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "runner-under-test");
        var lease = new RunLeaseInfoDto(
            "AGT-1", "runner-under-test", "runner", "host", 1, "backend",
            "lease-1", 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2));
        var shipper = new LogShipper(client, "AGT-1", lease, _ => { });

        shipper.Add("stdout", new string('x', 100 * 1024));
        Assert.True(await shipper.FlushAsync(CancellationToken.None));

        var shipped = Assert.Single(Assert.IsType<LogIngestRequest>(handler.Request).Lines).Text;
        Assert.EndsWith(" [runner: event payload truncated]", shipped, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public LogIngestRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Request = JsonSerializer.Deserialize<LogIngestRequest>(body, Json);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new LogIngestResponse("AGT-1", 1), Json),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
