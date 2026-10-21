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
        Assert.True(requests.IsMonotonic);
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
        var factory = new OtlpRecordFactory(Limits with { MaxAttributes = 2, MaxAttributeValueChars = 4, MaxBodyChars = 5 }, Received);
        const string body = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"a","value":{"stringValue":"123456"}}]},
              "scopeLogs":[{"logRecords":[{"body":{"stringValue":"abcdefgh"},
                "attributes":[{"key":"b","value":{"stringValue":"x"}},{"key":"c","value":{"stringValue":"y"}}]}]}]}]}
            """;

        var log = Assert.Single(OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), factory).Items);

        Assert.Equal("abcde…", log.Body);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1234…", ["b"] = "x" }, log.Attributes.Values);
    }

    [Fact]
    public void Records_without_own_attributes_share_one_set_per_resource_and_scope()
    {
        const string body = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"shop-api"}}]},
              "scopeLogs":[{"scope":{"name":"a"},"logRecords":[{"body":{"stringValue":"1"}},{"body":{"stringValue":"2"}},
                  {"body":{"stringValue":"3"},"attributes":[{"key":"order.id","value":{"intValue":"7"}}]}]},
                {"scope":{"name":"b"},"logRecords":[{"body":{"stringValue":"4"}}]}]}]}
            """;

        var logs = OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), Factory()).Items;

        Assert.Same(logs[0].Attributes, logs[1].Attributes);
        Assert.True(logs[0].Attributes.IsShared);
        Assert.False(logs[2].Attributes.IsShared);
        Assert.Equal(new Dictionary<string, string>
        {
            ["order.id"] = "7", ["otel.scope.name"] = "a", ["service.name"] = "shop-api"
        }, logs[2].Attributes.Values);
        // The merged set refers to the strings of the shared set instead of copying them.
        Assert.Same(logs[0].Attributes.Items[1].Value, logs[2].Attributes.Items[2].Value);
        Assert.NotSame(logs[0].Attributes, logs[3].Attributes);
        Assert.Equal("b", logs[3].Attributes.Values["otel.scope.name"]);
    }

    [Fact]
    public void Attribute_byte_budget_caps_the_merged_set_and_lets_replacements_fit()
    {
        var factory = new OtlpRecordFactory(Limits with { MaxAttributeBytes = 20 }, Received);
        const string body = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"a","value":{"stringValue":"1234567890"}}]},
              "scopeLogs":[{"logRecords":[
                {"attributes":[{"key":"b","value":{"stringValue":"123456789"}},{"key":"c","value":{"stringValue":"1234"}}]},
                {"attributes":[{"key":"a","value":{"stringValue":"x"}},{"key":"b","value":{"stringValue":"123456789"}}]}]}]}]}
            """;

        var logs = OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), factory).Items;

        // a (11 bytes) plus b (10) would pass 20 bytes, so b is dropped and c (5) still fits.
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1234567890", ["c"] = "1234" }, logs[0].Attributes.Values);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "x", ["b"] = "123456789" }, logs[1].Attributes.Values);
    }

    [Fact]
    public void Record_cap_stops_decoding_at_the_first_record_past_the_limit()
    {
        var capped = Limits with { MaxRecords = 3 };

        Assert.Equal(3, OtlpProtobufDecoder.DecodeMetrics(PointFlood(points: 3, resourceAttributes: 0), new OtlpRecordFactory(capped, Received)).Items.Count);
        var error = Assert.Throws<OtlpLimitException>(()
            => OtlpProtobufDecoder.DecodeMetrics(PointFlood(points: 4, resourceAttributes: 0), new OtlpRecordFactory(capped, Received)));
        Assert.Contains("more than 3 data points or log records", error.Message);
        var logs = "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{},{},{},{}]}]}]}";
        Assert.Throws<OtlpLimitException>(() => OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(logs), new OtlpRecordFactory(capped, Received)));
    }

    [Fact]
    public void Amplified_body_stops_at_a_request_cap_long_before_it_is_fully_decoded()
    {
        // 128 resource attributes of 1000 characters, then 350,000 points that each bring one attribute:
        // the shape that used to build one full attribute dictionary per point and allocate gigabytes.
        var body = PointFlood(points: 350_000, resourceAttributes: 128);
        Assert.True(body.Length < new TelemetryOptions().MaxRequestBytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<OtlpLimitException>(() => OtlpProtobufDecoder.DecodeMetrics(body, Factory()));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 256L * 1024 * 1024, $"decoding allocated {allocated} bytes before it stopped");
    }

    [Fact]
    public void Memory_cap_stops_decoding_when_records_carry_wide_attributes()
    {
        var factory = new OtlpRecordFactory(Limits with { MaxDecodedBytes = 1024 * 1024 }, Received);

        var error = Assert.Throws<OtlpLimitException>(() => OtlpProtobufDecoder.DecodeMetrics(WideAttributeFlood(points: 5_000), factory));

        Assert.Contains("bytes of memory", error.Message);
    }

    [Fact]
    public void Resource_attribute_flood_with_empty_scopes_costs_linear_work()
    {
        // About 1.6 million five-byte resource attributes and 130,000 empty scopes fit in 8 MiB. Every scope
        // used to merge the whole resource list again: about 2 * 10^11 steps, an hour of CPU for one request.
        var body = ResourceFlood(attributes: 1_600_000, emptyScopes: 130_000);
        Assert.True(body.Length <= new TelemetryOptions().MaxRequestBytes, $"the flood is {body.Length} bytes");
        var factory = new OtlpRecordFactory(Limits with { MaxScopes = 200_000 }, Received);

        Assert.Empty(OtlpProtobufDecoder.DecodeMetrics(body, factory).Items);

        // One unit per attribute entry read or skipped and per scope: the work grows with the body, not with its square.
        Assert.True(factory.Work < 2_000_000, $"decoding took {factory.Work} work units");
        // With the default limits the same body stops at the scope cap.
        var error = Assert.Throws<OtlpLimitException>(() => OtlpProtobufDecoder.DecodeMetrics(body, Factory()));
        Assert.Contains("more than 4096 resources and instrumentation scopes", error.Message);
    }

    [Fact]
    public void Attribute_lists_are_decoded_only_up_to_the_attribute_cap()
    {
        // One point with 50,000 attributes of 100 characters: only the first MaxAttributes become strings.
        var attributes = new MemoryStream();
        for (var index = 0; index < 50_000; index++) attributes.Write(Field(7, KeyValue("point." + index, new string('p', 100))));
        byte[] point = Field(1, [.. attributes.ToArray(), 0x21, .. BitConverter.GetBytes(1.5d)]);
        var body = Field(1, Field(2, Field(2, [.. Text(1, "wide"), .. Field(5, point)])));
        var factory = Factory();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sample = Assert.Single(OtlpProtobufDecoder.DecodeMetrics(body, factory).Items);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Limits.MaxAttributes, sample.Attributes.Count);
        Assert.True(allocated < 1024 * 1024, $"decoding allocated {allocated} bytes");
        Assert.True(factory.Work < 51_000, $"decoding took {factory.Work} work units");
    }

    [Fact]
    public void Json_attribute_lists_stop_at_the_cap_and_resource_floods_cost_linear_work()
    {
        var resource = string.Join(",", Enumerable.Repeat("{\"key\":\"k\"}", 300_000));
        var scopes = string.Join(",", Enumerable.Repeat("{}", 100_000));
        var record = "{\"attributes\":[" + string.Join(",", Enumerable.Range(0, 1000)
            .Select(index => $"{{\"key\":\"a{index}\",\"value\":{{\"stringValue\":\"v\"}}}}")) + "]}";
        var body = $"{{\"resourceLogs\":[{{\"resource\":{{\"attributes\":[{resource}]}},\"scopeLogs\":[{scopes},{{\"logRecords\":[{record}]}}]}}]}}";
        var factory = new OtlpRecordFactory(Limits with { MaxAttributes = 4, MaxScopes = 200_000 }, Received);

        var log = Assert.Single(OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), factory).Items);

        // The resource keeps its first entry (k); the record's first four entries fill the set up to four keys.
        Assert.Equal(["a0", "a1", "a2", "k"], log.Attributes.Items.Select(item => item.Key));
        Assert.True(factory.Work < 410_000, $"decoding took {factory.Work} work units");
    }

    [Fact]
    public void Nested_values_stop_once_their_text_passes_the_value_limit()
    {
        var values = string.Join(",", Enumerable.Repeat("{\"stringValue\":\"abc\"}", 100_000));
        var body = $"{{\"resourceLogs\":[{{\"scopeLogs\":[{{\"logRecords\":[{{\"attributes\":[{{\"key\":\"list\",\"value\":{{\"arrayValue\":{{\"values\":[{values}]}}}}}}]}}]}}]}}]}}";
        var factory = new OtlpRecordFactory(Limits with { MaxAttributeValueChars = 16 }, Received);

        var log = Assert.Single(OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), factory).Items);

        Assert.Equal("[\"abc\",\"abc\",\"ab…", log.Attributes.Values["list"]);
    }

    [Fact]
    public void Decoding_stops_when_the_request_is_cancelled()
    {
        var factory = new OtlpRecordFactory(Limits, Received, new CancellationToken(canceled: true));

        Assert.ThrowsAny<OperationCanceledException>(()
            => OtlpProtobufDecoder.DecodeMetrics(ResourceFlood(attributes: 100_000, emptyScopes: 0), factory));
        Assert.True(factory.Work < 10_000, $"decoding took {factory.Work} work units before it stopped");
    }

    [Fact]
    public void A_shared_resource_set_counts_for_every_log_record_toward_the_stored_byte_budget()
    {
        // One resource with 15 attributes of 1,024 '<', one scope, 20,000 empty log records: about 57 KB of body.
        // Every record shares the resource set, but the writer stores its JSON in every row.
        var body = SharedSetLogFlood(records: 20_000);
        Assert.True(body.Length < 64 * 1024, $"the flood is {body.Length} bytes");
        var factory = Factory();

        var error = Assert.Throws<OtlpLimitException>(() => OtlpProtobufDecoder.DecodeLogs(body, factory));

        Assert.Contains($"would store more than {Limits.MaxStoredBytes} bytes", error.Message);
        // '<' is stored as itself, not as a six-byte \u003C escape, so a record stores about 15 KiB of attributes.
        var accepted = OtlpProtobufDecoder.DecodeLogs(SharedSetLogFlood(records: 100), Factory());
        var json = accepted.Items[0].Attributes.Json;
        Assert.Contains(new string('<', 1024), json);
        Assert.InRange(json.Length, 15 * 1024, 16 * 1024);
        Assert.Equal(accepted.Items.Sum(log => log.StoredBytes(log.Attributes.JsonBytes)), accepted.StoredBytes);
        Assert.True(accepted.StoredBytes > 100L * 15 * 1024);
    }

    [Fact]
    public void Points_with_one_own_attribute_each_count_their_merged_set_toward_the_stored_byte_budget()
    {
        // 15 resource attributes of 1,000 '<' plus k=v on every point: each point renders its own 15 KB set.
        var body = PointFlood(points: 20_000, resourceAttributes: 15, fill: '<');
        var factory = Factory();

        var error = Assert.Throws<OtlpLimitException>(() => OtlpProtobufDecoder.DecodeMetrics(body, factory));

        Assert.Contains("would store more than", error.Message);
        var accepted = OtlpProtobufDecoder.DecodeMetrics(PointFlood(points: 1_000, resourceAttributes: 15, fill: '<'), Factory());
        Assert.Equal(1_000, accepted.Items.Count);
        Assert.InRange(accepted.StoredBytes, 1_000L * 15 * 1000, Limits.MaxStoredBytes);
    }

    [Theory]
    [InlineData("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"body\":{\"stringValue\":\"\\uD800\"}}]}]}]}")]
    [InlineData("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"severityText\":\"\\uDC00x\"}]}]}]}")]
    [InlineData("{\"resourceLogs\":[{\"resource\":{\"attributes\":[{\"key\":\"\\uD800\"}]}}]}")]
    [InlineData("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"body\":{\"doubleValue\":1e400}}]}]}]}")]
    public void Json_lone_surrogates_and_out_of_range_numbers_are_format_errors(string body)
        => Assert.Throws<OtlpFormatException>(() => OtlpJsonDecoder.DecodeLogs(Encoding.UTF8.GetBytes(body), Factory()));

    [Fact]
    public void Json_metric_value_out_of_the_double_range_is_a_format_error()
    {
        const string body = "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[{\"name\":\"m\",\"gauge\":{\"dataPoints\":[{\"asDouble\":1e400}]}}]}]}]}";

        var error = Assert.Throws<OtlpFormatException>(() => OtlpJsonDecoder.DecodeMetrics(Encoding.UTF8.GetBytes(body), Factory()));

        Assert.Contains("asDouble", error.Message);
    }

    [Fact]
    public void A_repeated_protobuf_value_field_is_decoded_once_and_the_last_one_wins()
    {
        // An AnyValue with 10,000 array values (field 5) and then one string: only the string is decoded.
        var array = Field(5, Field(1, Text(1, new string('a', 100))));
        var value = new MemoryStream();
        for (var index = 0; index < 10_000; index++) value.Write(array);
        value.Write(Text(1, "last"));
        var record = Field(2, [.. Field(6, [.. Text(1, "k"), .. Field(2, value.ToArray())])]);
        var body = Field(1, Field(2, record));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var log = Assert.Single(OtlpProtobufDecoder.DecodeLogs(body, Factory()).Items);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("last", log.Attributes.Values["k"]);
        Assert.True(allocated < 1024 * 1024, $"decoding allocated {allocated} bytes");
    }

    /// <summary>
    /// ExportMetricsServiceRequest with one resource of <paramref name="resourceAttributes"/> wide
    /// attributes and one gauge of <paramref name="points"/> points, each with the attribute k=v.
    /// </summary>
    internal static byte[] PointFlood(int points, int resourceAttributes, char fill = 'x')
    {
        var resource = new MemoryStream();
        for (var index = 0; index < resourceAttributes; index++)
            resource.Write(Field(1, KeyValue("resource." + index, new string(fill, 1000))));
        byte[] point = Field(1, [.. Field(7, KeyValue("k", "v")), 0x21, .. BitConverter.GetBytes(1.5d)]);
        var gauge = new MemoryStream();
        for (var index = 0; index < points; index++) gauge.Write(point);
        byte[] metric = [.. Text(1, "flood"), .. Field(5, gauge.ToArray())];
        byte[] resourceMetrics = [.. Field(1, resource.ToArray()), .. Field(2, Field(2, metric))];
        return Field(1, resourceMetrics);
    }

    /// <summary>
    /// ExportLogsServiceRequest with one resource of 15 attributes of 1,024 '<', one scope, and
    /// <paramref name="records"/> empty log records that all share the resource set.
    /// </summary>
    internal static byte[] SharedSetLogFlood(int records)
    {
        var resource = new MemoryStream();
        for (var index = 0; index < 15; index++) resource.Write(Field(1, KeyValue("r" + index, new string('<', 1024))));
        var scope = new MemoryStream();
        byte[] record = Field(2, []);
        for (var index = 0; index < records; index++) scope.Write(record);
        return Field(1, [.. Field(1, resource.ToArray()), .. Field(2, scope.ToArray())]);
    }

    /// <summary>One gauge whose points each carry a 1000-character attribute of their own.</summary>
    private static byte[] WideAttributeFlood(int points)
    {
        var gauge = new MemoryStream();
        for (var index = 0; index < points; index++)
            gauge.Write(Field(1, [.. Field(7, KeyValue("wide", new string('w', 1000))), 0x21, .. BitConverter.GetBytes(1.5d)]));
        return Field(1, Field(2, Field(2, [.. Text(1, "wide"), .. Field(5, gauge.ToArray())])));
    }

    /// <summary>
    /// One resource of <paramref name="attributes"/> five-byte attributes (key <c>k</c>, no value)
    /// followed by <paramref name="emptyScopes"/> empty ScopeMetrics.
    /// </summary>
    private static byte[] ResourceFlood(int attributes, int emptyScopes)
    {
        var resource = new MemoryStream();
        byte[] attribute = Field(1, Text(1, "k"));
        for (var index = 0; index < attributes; index++) resource.Write(attribute);
        var resourceMetrics = new MemoryStream();
        resourceMetrics.Write(Field(1, resource.ToArray()));
        byte[] scope = Field(2, []);
        for (var index = 0; index < emptyScopes; index++) resourceMetrics.Write(scope);
        return Field(1, resourceMetrics.ToArray());
    }

    private static byte[] KeyValue(string key, string value) => [.. Text(1, key), .. Field(2, Text(1, value))];

    private static byte[] Text(int field, string value) => Field(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Field(int field, byte[] payload) => [.. Varint((ulong)field << 3 | 2), .. Varint((ulong)payload.Length), .. payload];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        for (; value >= 0x80; value >>= 7) bytes.Add((byte)(value | 0x80));
        bytes.Add((byte)value);
        return [.. bytes];
    }

    private static OtlpRecordFactory Factory() => new(Limits, Received);

    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "otlp", name));

    private static object Comparable(MetricSample sample) => (sample with { Attributes = TelemetryAttributes.Empty }, sample.Attributes.Json);

    private static object Comparable(TelemetryLogRecord log) => (log with { Attributes = TelemetryAttributes.Empty }, log.Attributes.Json);
}
