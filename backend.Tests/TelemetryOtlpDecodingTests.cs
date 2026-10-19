using System.Text;
using AgentStudio.Telemetry;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: decodes recorded OTLP fixtures; their timestamps are data and never compared with "now".

/// <summary>
/// OTLP parsing (IP-15). The protobuf fixtures are serialized by the official
/// opentelemetry-proto bindings (Fixtures/otlp/generate_fixtures.py), so the
/// hand-written wire reader is checked against an independent encoder. The
/// JSON fixtures carry the same messages and must decode to the same records.
/// </summary>
public sealed class TelemetryOtlpDecodingTests
{
    private const long T0 = 1_792_152_000_000_000_000; // 2026-10-16T12:00:00Z
    private const long Minute = 60_000_000_000;
    private const long Received = T0 + 5 * Minute;
    private static readonly OtlpLimits Limits = OtlpLimits.From(new TelemetryOptions());

    [Fact]
    public void Protobuf_metrics_fixture_decodes_every_metric_kind()
    {
        var result = OtlpProtobufDecoder.DecodeMetrics(Fixture("metrics.pb"), Factory());

        Assert.Equal(1, result.Rejected);
        Assert.Equal("Number data point has no finite value.", result.RejectionMessage);
        Assert.Equal(
            ["process.memory.usage", "http.server.requests", "http.server.duration", "db.query.duration", "queue.wait"],
            result.Items.Select(item => item.Name));

        var memory = result.Items[0];
        Assert.Equal((MetricKind.Gauge, "By", 5_242_880d, 1d, T0), (memory.Kind, memory.Unit, memory.Value, memory.Count, memory.TimeUnixNano));
        Assert.Equal(new Dictionary<string, string>
        {
            ["deployment.environment"] = "stage",
            ["otel.scope.name"] = "shop.http",
            ["service.name"] = "shop-api",
            ["state"] = "used",
        }, memory.Attributes.Values);

        var requests = result.Items[1];
        Assert.Equal((MetricKind.Sum, MetricTemporality.Cumulative, 42d, T0 - 10 * Minute),
            (requests.Kind, requests.Temporality, requests.Value, requests.StartTimeUnixNano));
        Assert.Equal("200", requests.Attributes.Values["http.status_code"]);
        Assert.Equal("/cart", requests.Attributes.Values["http.route"]);

        var duration = result.Items[2];
        Assert.Equal((MetricKind.Histogram, MetricTemporality.Delta, 100d, 4d, 10d, 40d),
            (duration.Kind, duration.Temporality, duration.Value, duration.Count, duration.Min, duration.Max));

        var queries = result.Items[3];
        Assert.Equal((MetricKind.Summary, MetricTemporality.Cumulative, 30d, 3d), (queries.Kind, queries.Temporality, queries.Value, queries.Count));

        var wait = result.Items[4];
        Assert.Equal((MetricKind.ExponentialHistogram, MetricTemporality.Delta, 8d, 2d, 3d, 5d),
            (wait.Kind, wait.Temporality, wait.Value, wait.Count, wait.Min, wait.Max));
    }

    [Fact]
    public void Json_metrics_fixture_decodes_to_the_same_records_as_protobuf()
    {
        var protobuf = OtlpProtobufDecoder.DecodeMetrics(Fixture("metrics.pb"), Factory());
        var json = OtlpJsonDecoder.DecodeMetrics(Fixture("metrics.json"), Factory());

        Assert.Equal(protobuf.Rejected, json.Rejected);
        Assert.Equal(protobuf.Items.Select(Comparable), json.Items.Select(Comparable));
    }

    [Fact]
    public void Protobuf_logs_fixture_decodes_bodies_severity_ids_and_times()
    {
        var result = OtlpProtobufDecoder.DecodeLogs(Fixture("logs.pb"), Factory());

        Assert.Equal(0, result.Rejected);
        Assert.Equal(3, result.Items.Count);

        var placed = result.Items[0];
        Assert.Equal((T0, 9, "Information", "order placed"), (placed.TimeUnixNano, placed.SeverityNumber, placed.SeverityText, placed.Body));
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", placed.TraceId);
        Assert.Equal("0102030405060708", placed.SpanId);
        Assert.Equal("42", placed.Attributes.Values["order.id"]);
        Assert.Equal("[\"new\",\"express\"]", placed.Attributes.Values["tags"]);
        Assert.Equal("Shop.Checkout", placed.Attributes.Values["otel.scope.name"]);
        Assert.Equal("shop-api", placed.Attributes.Values["service.name"]);

        var failed = result.Items[1];
        Assert.Equal((17, "Error", "payment.failed"), (failed.SeverityNumber, failed.SeverityText, failed.EventName));
        Assert.Equal("{\"message\":\"payment failed\",\"code\":402}", failed.Body);
        Assert.Null(failed.TraceId);

        var low = result.Items[2];
        Assert.Equal((T0 + 2 * Minute, 13, "warning", "disk low"), (low.TimeUnixNano, low.SeverityNumber, low.SeverityText, low.Body));
    }

    [Fact]
    public void Json_logs_fixture_decodes_to_the_same_records_as_protobuf()
    {
        var protobuf = OtlpProtobufDecoder.DecodeLogs(Fixture("logs.pb"), Factory());
        var json = OtlpJsonDecoder.DecodeLogs(Fixture("logs.json"), Factory());

        Assert.Equal(protobuf.Items.Select(Comparable), json.Items.Select(Comparable));
    }

    [Fact]
    public void Protobuf_unknown_fields_are_skipped()
    {
        // Field 99 (varint) and field 98 (length-delimited) at the top level, before the real payload.
        var body = new byte[] { 0x98, 0x06, 0x07, 0x92, 0x06, 0x02, 0xAA, 0xBB }.Concat(Fixture("logs.pb")).ToArray();

        Assert.Equal(3, OtlpProtobufDecoder.DecodeLogs(body, Factory()).Items.Count);
    }

    [Theory]
    [InlineData(new byte[] { 0x0A, 0x05, 0x01 })] // length beyond the buffer
    [InlineData(new byte[] { 0x0A })] // tag without length
    [InlineData(new byte[] { 0x0B })] // start-group wire type
    [InlineData(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })] // eleven-byte varint
    [InlineData(new byte[] { 0x00 })] // field number zero
    public void Malformed_protobuf_is_rejected(byte[] body)
        => Assert.Throws<OtlpFormatException>(() => OtlpProtobufDecoder.DecodeLogs(body, Factory()));

    [Fact]
    public void Truncated_fixture_is_rejected()
    {
        var body = Fixture("metrics.pb");
        Assert.Throws<OtlpFormatException>(() => OtlpProtobufDecoder.DecodeMetrics(body.AsSpan(0, body.Length - 3), Factory()));
    }

    [Fact]
    public void Deeply_nested_any_value_is_rejected()
    {
        var value = "{\"stringValue\":\"leaf\"}";
        for (var depth = 0; depth < 12; depth++) value = "{\"arrayValue\":{\"values\":[" + value + "]}}";
        var body = "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"body\":" + value + "}]}]}]}";

        Assert.Throws<OtlpFormatException>(() => OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), Factory()));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"resourceLogs\":{}}")]
    [InlineData("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"timeUnixNano\":\"soon\"}]}]}]}")]
    [InlineData("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"traceId\":\"xyz\"}]}]}]}")]
    public void Malformed_json_is_rejected(string body)
        => Assert.Throws<OtlpFormatException>(() => OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), Factory()));

    [Fact]
    public void Json_accepts_snake_case_names_numeric_int64_and_enum_names()
    {
        const string body = """
            {"resource_metrics":[{"scope_metrics":[{"metrics":[{"name":"jobs","sum":{
              "aggregation_temporality":"AGGREGATION_TEMPORALITY_DELTA",
              "data_points":[{"as_int":7,"time_unix_nano":1792152000000000000}]}}]}]}]}
            """;

        var sample = Assert.Single(OtlpJsonDecoder.DecodeMetrics(Encoding.UTF8.GetBytes(body), Factory()).Items);

        Assert.Equal((MetricTemporality.Delta, 7d, T0), (sample.Temporality, sample.Value, sample.TimeUnixNano));
    }

    [Fact]
    public void Missing_timestamps_use_the_receive_time_and_names_are_required()
    {
        const string body = """
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"up","gauge":{"dataPoints":[{"asDouble":1}]}},
              {"gauge":{"dataPoints":[{"asDouble":1},{"asDouble":2}]}}]}]}]}
            """;

        var result = OtlpJsonDecoder.DecodeMetrics(Encoding.UTF8.GetBytes(body), Factory());

        Assert.Equal(Received, Assert.Single(result.Items).TimeUnixNano);
        Assert.Equal(2, result.Rejected);
    }

    [Fact]
    public void Limits_truncate_bodies_and_values_and_cap_attribute_count()
    {
        var factory = new OtlpRecordFactory(new OtlpLimits(MaxAttributes: 2, MaxAttributeValueChars: 4, MaxBodyChars: 5), Received);
        const string body = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"a","value":{"stringValue":"123456"}}]},
              "scopeLogs":[{"logRecords":[{"body":{"stringValue":"abcdefgh"},
                "attributes":[{"key":"b","value":{"stringValue":"x"}},{"key":"c","value":{"stringValue":"y"}}]}]}]}]}
            """;

        var log = Assert.Single(OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), factory).Items);

        Assert.Equal("abcde…", log.Body);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1234…", ["b"] = "x" }, log.Attributes.Values);
    }

    private static OtlpRecordFactory Factory() => new(Limits, Received);

    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "otlp", name));

    private static object Comparable(MetricSample sample) => (sample with { Attributes = TelemetryAttributes.Empty }, sample.Attributes.Json);

    private static object Comparable(TelemetryLogRecord log) => (log with { Attributes = TelemetryAttributes.Empty }, log.Attributes.Json);
}
