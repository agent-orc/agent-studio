using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Telemetry;

/// <summary>
/// Decodes OTLP/JSON bodies. Keys are lowerCamelCase as the OTLP specification
/// requires; like any proto3 JSON parser, the original snake_case field names
/// are accepted too. 64-bit integers may be strings or numbers, enums may be
/// integers or names, and trace and span ids are hex strings. Attribute lists
/// stop at <see cref="OtlpLimits.MaxAttributes"/> entries; later entries are
/// skipped without reading them. A string with an escaped lone surrogate or a
/// number outside the double range is a format error (400), not a server error.
/// </summary>
internal static class OtlpJsonDecoder
{
    private const int MaxValueDepth = 8;
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };
    private static readonly ConcurrentDictionary<string, string> SnakeNames = new(StringComparer.Ordinal);

    public static OtlpDecodeResult<MetricSample> DecodeMetrics(ReadOnlyMemory<byte> body, OtlpRecordFactory factory)
        => Guarded(() => DecodeMetricsDocument(body, factory));

    public static OtlpDecodeResult<TelemetryLogRecord> DecodeLogs(ReadOnlyMemory<byte> body, OtlpRecordFactory factory)
        => Guarded(() => DecodeLogsDocument(body, factory));

    /// <summary>
    /// <see cref="JsonElement.GetString"/> throws <see cref="InvalidOperationException"/> on an
    /// escaped lone surrogate such as <c>"\uD800"</c>; both it and a number that does not fit
    /// are faults of the request body.
    /// </summary>
    private static T Guarded<T>(Func<T> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new OtlpFormatException("OTLP JSON body has an invalid string or number: " + ex.Message);
        }
    }

    private static OtlpDecodeResult<MetricSample> DecodeMetricsDocument(ReadOnlyMemory<byte> body, OtlpRecordFactory factory)
    {
        using var document = Parse(body);
        foreach (var resourceMetrics in Array(document.RootElement, "resourceMetrics"))
        {
            var resource = factory.Resource(ReadResource(resourceMetrics, factory));
            foreach (var scopeMetrics in Array(resourceMetrics, "scopeMetrics"))
            {
                var scoped = WithScope(scopeMetrics, resource, factory);
                foreach (var metric in Array(scopeMetrics, "metrics")) ReadMetric(metric, scoped, factory);
            }
        }
        return factory.Metrics();
    }

    private static OtlpDecodeResult<TelemetryLogRecord> DecodeLogsDocument(ReadOnlyMemory<byte> body, OtlpRecordFactory factory)
    {
        using var document = Parse(body);
        foreach (var resourceLogs in Array(document.RootElement, "resourceLogs"))
        {
            var resource = factory.Resource(ReadResource(resourceLogs, factory));
            foreach (var scopeLogs in Array(resourceLogs, "scopeLogs"))
            {
                var scoped = WithScope(scopeLogs, resource, factory);
                foreach (var record in Array(scopeLogs, "logRecords")) factory.AddLog(scoped, ReadLogRecord(record, factory));
            }
        }
        return factory.Logs();
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            var document = JsonDocument.Parse(body, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new OtlpFormatException("OTLP JSON body must be an object.");
            }
            return document;
        }
        catch (JsonException ex)
        {
            throw new OtlpFormatException("OTLP JSON body is not valid JSON: " + ex.Message);
        }
    }

    private static void ReadMetric(JsonElement metric, TelemetryAttributes scope, OtlpRecordFactory factory)
    {
        factory.Charge();
        RequireObject(metric, "metric");
        var name = String(metric, "name");
        var unit = String(metric, "unit");
        foreach (var (property, kind) in new[]
        {
            ("gauge", MetricKind.Gauge), ("sum", MetricKind.Sum), ("histogram", MetricKind.Histogram),
            ("exponentialHistogram", MetricKind.ExponentialHistogram), ("summary", MetricKind.Summary)
        })
        {
            if (Object(metric, property) is not { } data) continue;
            var temporality = kind == MetricKind.Summary ? MetricTemporality.Cumulative
                : Enum(data, "aggregationTemporality", "AGGREGATION_TEMPORALITY_") switch
                {
                    1 => MetricTemporality.Delta,
                    2 => MetricTemporality.Cumulative,
                    _ => MetricTemporality.Unspecified
                };
            foreach (var point in Array(data, "dataPoints"))
                factory.AddPoint(name, unit, kind, temporality, scope, ReadPoint(point, kind, factory),
                    kind == MetricKind.Sum && data.TryGetProperty("isMonotonic", out var monotonic) && monotonic.ValueKind == JsonValueKind.True);
            return;
        }
    }

    private static OtlpPointFields ReadPoint(JsonElement element, MetricKind kind, OtlpRecordFactory factory)
    {
        RequireObject(element, "data point");
        var point = new OtlpPointFields
        {
            StartTimeUnixNano = Nanos(element, "startTimeUnixNano"),
            TimeUnixNano = Nanos(element, "timeUnixNano"),
            Flags = (uint)Math.Clamp(Int64(element, "flags") ?? 0, 0, uint.MaxValue)
        };
        point.Attributes.AddRange(Attributes(element, factory));
        if (kind is MetricKind.Gauge or MetricKind.Sum)
        {
            point.Number = Double(element, "asDouble") ?? Int64(element, "asInt");
            return point;
        }
        point.Count = UInt64(element, "count") ?? (kind == MetricKind.Summary ? 0 : null);
        point.Sum = Double(element, "sum");
        point.Min = Double(element, "min");
        point.Max = Double(element, "max");
        return point;
    }

    private static OtlpLogFields ReadLogRecord(JsonElement element, OtlpRecordFactory factory)
    {
        RequireObject(element, "log record");
        var log = new OtlpLogFields
        {
            TimeUnixNano = Nanos(element, "timeUnixNano"),
            ObservedTimeUnixNano = Nanos(element, "observedTimeUnixNano"),
            SeverityNumber = SeverityNumber(element),
            SeverityText = String(element, "severityText"),
            Body = TryProperty(element, "body", out var body) ? AnyValueText(body, factory.Limits.MaxBodyChars) : "",
            TraceId = HexIdentifier(element, "traceId", 16),
            SpanId = HexIdentifier(element, "spanId", 8),
            EventName = String(element, "eventName")
        };
        log.Attributes.AddRange(Attributes(element, factory));
        return log;
    }

    private static List<KeyValuePair<string, string>> ReadResource(JsonElement container, OtlpRecordFactory factory)
    {
        RequireObject(container, "resource entry");
        return Object(container, "resource") is { } resource ? Attributes(resource, factory) : [];
    }

    private static TelemetryAttributes WithScope(JsonElement container, TelemetryAttributes resource, OtlpRecordFactory factory)
    {
        RequireObject(container, "scope entry");
        return factory.Scope(resource, Object(container, "scope") is { } scope ? String(scope, "name") : "");
    }

    private static List<KeyValuePair<string, string>> Attributes(JsonElement element, OtlpRecordFactory factory)
    {
        var maxChars = factory.Limits.MaxAttributeValueChars;
        var attributes = new List<KeyValuePair<string, string>>();
        foreach (var pair in Array(element, "attributes"))
        {
            if (!factory.TakesAttribute(attributes.Count)) continue;
            RequireObject(pair, "attribute");
            var value = TryProperty(pair, "value", out var any) ? AnyValueText(any, maxChars) : "";
            attributes.Add(KeyValuePair.Create(String(pair, "key"), OtlpAttributeBuilder.Truncate(value, maxChars)));
        }
        return attributes;
    }

    /// <summary>Scalar AnyValue as text; arrays and key-value lists as compact JSON, cut once longer than <paramref name="maxChars"/>.</summary>
    private static string AnyValueText(JsonElement value, int maxChars)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        if (TryProperty(value, "stringValue", out var text)) return text.ValueKind == JsonValueKind.String ? text.GetString()! : text.GetRawText();
        if (TryProperty(value, "boolValue", out var boolean)) return boolean.ValueKind == JsonValueKind.True ? "true" : "false";
        if (TryProperty(value, "intValue", out _)) return OtlpAttributeBuilder.Number(Int64(value, "intValue") ?? 0);
        if (TryProperty(value, "doubleValue", out _)) return OtlpAttributeBuilder.Number(Double(value, "doubleValue") ?? 0);
        if (TryProperty(value, "bytesValue", out _)) return String(value, "bytesValue");
        if (!TryProperty(value, "arrayValue", out _) && !TryProperty(value, "kvlistValue", out _)) return "";
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteAnyValue(value, writer, 0, OtlpAttributeBuilder.JsonByteBudget(maxChars));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteAnyValue(JsonElement value, Utf8JsonWriter writer, int depth, long budget)
    {
        if (depth > MaxValueDepth) throw new OtlpFormatException("AnyValue nesting is too deep.");
        if (value.ValueKind != JsonValueKind.Object) { writer.WriteNullValue(); return; }
        if (TryProperty(value, "stringValue", out var text)) writer.WriteStringValue(text.ValueKind == JsonValueKind.String ? text.GetString() : text.GetRawText());
        else if (TryProperty(value, "boolValue", out var boolean)) writer.WriteBooleanValue(boolean.ValueKind == JsonValueKind.True);
        else if (TryProperty(value, "intValue", out _)) writer.WriteNumberValue(Int64(value, "intValue") ?? 0);
        else if (TryProperty(value, "doubleValue", out _))
        {
            var number = Double(value, "doubleValue") ?? 0;
            if (double.IsFinite(number)) writer.WriteNumberValue(number);
            else writer.WriteStringValue(OtlpAttributeBuilder.Number(number));
        }
        else if (TryProperty(value, "bytesValue", out _)) writer.WriteStringValue(String(value, "bytesValue"));
        else if (TryProperty(value, "arrayValue", out var array))
        {
            writer.WriteStartArray();
            foreach (var item in Array(array, "values"))
                if (!OtlpAttributeBuilder.IsFull(writer, budget)) WriteAnyValue(item, writer, depth + 1, budget);
            writer.WriteEndArray();
        }
        else if (TryProperty(value, "kvlistValue", out var list))
        {
            writer.WriteStartObject();
            foreach (var pair in Array(list, "values"))
            {
                if (OtlpAttributeBuilder.IsFull(writer, budget)) continue;
                RequireObject(pair, "key-value");
                writer.WritePropertyName(String(pair, "key"));
                if (TryProperty(pair, "value", out var inner)) WriteAnyValue(inner, writer, depth + 1, budget);
                else writer.WriteNullValue();
            }
            writer.WriteEndObject();
        }
        else writer.WriteNullValue();
    }

    private static int SeverityNumber(JsonElement element)
    {
        if (!TryProperty(element, "severityNumber", out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out var number) ? number : 0;
        if (value.ValueKind != JsonValueKind.String) throw Malformed("severityNumber");
        var name = value.GetString()!;
        if (!name.StartsWith("SEVERITY_NUMBER_", StringComparison.Ordinal)) return 0;
        var level = name["SEVERITY_NUMBER_".Length..];
        if (level.Length == 0) return 0;
        var step = level.Length > 1 && char.IsAsciiDigit(level[^1]) ? level[^1] - '1' : 0;
        var baseNumber = TelemetrySeverity.Parse(char.IsAsciiDigit(level[^1]) ? level[..^1] : level);
        return baseNumber is { } found && step is >= 0 and <= 3 ? found + step : 0;
    }

    private static int Enum(JsonElement element, string name, string prefix)
    {
        if (!TryProperty(element, name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out var number) ? number : 0;
        if (value.ValueKind != JsonValueKind.String) throw Malformed(name);
        return value.GetString() switch
        {
            var text when text == prefix + "DELTA" => 1,
            var text when text == prefix + "CUMULATIVE" => 2,
            _ => 0
        };
    }

    private static string? HexIdentifier(JsonElement element, string name, int length)
    {
        var text = String(element, name);
        if (text.Length == 0) return null;
        if (text.Length != length * 2 || !text.All(char.IsAsciiHexDigit)) throw Malformed(name);
        return OtlpRecordFactory.Identifier(Convert.FromHexString(text), length);
    }

    private static long Nanos(JsonElement element, string name)
    {
        var value = UInt64(element, name);
        return value is null ? 0 : value > long.MaxValue ? -1 : (long)value.Value;
    }

    private static ulong? UInt64(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw Malformed(name);
    }

    private static long? Int64(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw Malformed(name);
    }

    private static double? Double(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        // A JSON number literal is never NaN or infinite; one that does not fit a double is malformed.
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Malformed(name);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text is "NaN") return double.NaN;
            if (text is "Infinity") return double.PositiveInfinity;
            if (text is "-Infinity") return double.NegativeInfinity;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        throw Malformed(name);
    }

    private static string String(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Malformed(name);
    }

    private static JsonElement? Object(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Object ? value : throw Malformed(name);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Malformed(name);
    }

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new OtlpFormatException($"OTLP JSON {what} must be an object.");
    }

    private static bool TryProperty(JsonElement element, string camelName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        return element.TryGetProperty(camelName, out value)
               || element.TryGetProperty(SnakeNames.GetOrAdd(camelName, Snake), out value);
    }

    private static string Snake(string camelName)
    {
        var builder = new StringBuilder(camelName.Length + 8);
        foreach (var ch in camelName)
        {
            if (char.IsAsciiLetterUpper(ch)) builder.Append('_').Append(char.ToLowerInvariant(ch));
            else builder.Append(ch);
        }
        return builder.ToString();
    }

    private static OtlpFormatException Malformed(string name) => new($"OTLP JSON field '{name}' has an unexpected type or value.");
}
