using System.Buffers;
using System.Text.Json;

namespace AgentStudio.Telemetry;

/// <summary>
/// Decodes OTLP/HTTP protobuf bodies (<c>ExportMetricsServiceRequest</c> and
/// <c>ExportLogsServiceRequest</c>, opentelemetry-proto v1). Field numbers are
/// those of the published .proto files. Fields may arrive in any order, so a
/// message whose meaning depends on a sibling field is read in two passes.
/// Attribute lists stop at <see cref="OtlpLimits.MaxAttributes"/> entries; later
/// entries are skipped as raw bytes. Arrays and key-value lists inside a value
/// stop once their JSON text is certainly longer than the value limit.
/// </summary>
internal static class OtlpProtobufDecoder
{
    private const int MaxValueDepth = 8;

    private readonly record struct PointLayout(int Attributes, int Count, int Sum, int Flags, int Min, int Max);

    private static readonly PointLayout NumberPoint = new(7, 0, 0, 8, 0, 0);
    private static readonly PointLayout HistogramPoint = new(9, 4, 5, 10, 11, 12);
    private static readonly PointLayout ExponentialHistogramPoint = new(1, 4, 5, 10, 12, 13);
    private static readonly PointLayout SummaryPoint = new(7, 4, 5, 8, 0, 0);

    public static OtlpDecodeResult<MetricSample> DecodeMetrics(ReadOnlySpan<byte> body, OtlpRecordFactory factory)
    {
        var reader = new ProtoWireReader(body);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.LengthDelimited) ReadResourceMetrics(reader.ReadBytes(), factory);
            else reader.Skip(type);
        }
        return factory.Metrics();
    }

    public static OtlpDecodeResult<TelemetryLogRecord> DecodeLogs(ReadOnlySpan<byte> body, OtlpRecordFactory factory)
    {
        var reader = new ProtoWireReader(body);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.LengthDelimited) ReadResourceLogs(reader.ReadBytes(), factory);
            else reader.Skip(type);
        }
        return factory.Logs();
    }

    private static void ReadResourceMetrics(ReadOnlySpan<byte> data, OtlpRecordFactory factory)
    {
        var resource = factory.Resource(ReadResource(data, factory));
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 2 && type == ProtoWireType.LengthDelimited) ReadScopeMetrics(reader.ReadBytes(), resource, factory);
            else reader.Skip(type);
        }
    }

    private static void ReadScopeMetrics(ReadOnlySpan<byte> data, TelemetryAttributes resource, OtlpRecordFactory factory)
    {
        var scoped = WithScope(data, resource, factory);
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 2 && type == ProtoWireType.LengthDelimited) ReadMetric(reader.ReadBytes(), scoped, factory);
            else reader.Skip(type);
        }
    }

    private static void ReadMetric(ReadOnlySpan<byte> data, TelemetryAttributes scope, OtlpRecordFactory factory)
    {
        factory.Charge();
        string name = "", unit = "";
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.LengthDelimited) name = reader.ReadString();
            else if (field == 3 && type == ProtoWireType.LengthDelimited) unit = reader.ReadString();
            else reader.Skip(type);
        }
        reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (type != ProtoWireType.LengthDelimited)
            {
                reader.Skip(type);
                continue;
            }
            switch (field)
            {
                case 5: ReadPoints(reader.ReadBytes(), name, unit, MetricKind.Gauge, NumberPoint, scope, factory); break;
                case 7: ReadPoints(reader.ReadBytes(), name, unit, MetricKind.Sum, NumberPoint, scope, factory); break;
                case 9: ReadPoints(reader.ReadBytes(), name, unit, MetricKind.Histogram, HistogramPoint, scope, factory); break;
                case 10: ReadPoints(reader.ReadBytes(), name, unit, MetricKind.ExponentialHistogram, ExponentialHistogramPoint, scope, factory); break;
                case 11: ReadPoints(reader.ReadBytes(), name, unit, MetricKind.Summary, SummaryPoint, scope, factory); break;
                default: reader.Skip(type); break;
            }
        }
    }

    /// <summary>Gauge, Sum, Histogram, ExponentialHistogram, or Summary: points in field 1, temporality in field 2.</summary>
    private static void ReadPoints(ReadOnlySpan<byte> data, string name, string unit, MetricKind kind, PointLayout layout,
        TelemetryAttributes scope, OtlpRecordFactory factory)
    {
        var temporality = kind == MetricKind.Summary ? MetricTemporality.Cumulative : MetricTemporality.Unspecified;
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 2 && type == ProtoWireType.Varint && kind != MetricKind.Summary)
                temporality = reader.ReadVarint() switch { 1 => MetricTemporality.Delta, 2 => MetricTemporality.Cumulative, _ => MetricTemporality.Unspecified };
            else reader.Skip(type);
        }
        reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.LengthDelimited)
                factory.AddPoint(name, unit, kind, temporality, scope, ReadPoint(reader.ReadBytes(), kind, layout, factory));
            else reader.Skip(type);
        }
    }

    private static OtlpPointFields ReadPoint(ReadOnlySpan<byte> data, MetricKind kind, PointLayout layout, OtlpRecordFactory factory)
    {
        var point = new OtlpPointFields();
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == layout.Attributes && type == ProtoWireType.LengthDelimited)
                AddAttribute(point.Attributes, reader.ReadBytes(), factory);
            else if (field == 2 && type == ProtoWireType.Fixed64) point.StartTimeUnixNano = Nanos(reader.ReadFixed64());
            else if (field == 3 && type == ProtoWireType.Fixed64) point.TimeUnixNano = Nanos(reader.ReadFixed64());
            else if (layout.Count == 0 && field == 4 && type == ProtoWireType.Fixed64) point.Number = reader.ReadDouble();
            else if (layout.Count == 0 && field == 6 && type == ProtoWireType.Fixed64) point.Number = unchecked((long)reader.ReadFixed64());
            else if (field == layout.Count && type == ProtoWireType.Fixed64) point.Count = reader.ReadFixed64();
            else if (field == layout.Sum && type == ProtoWireType.Fixed64) point.Sum = reader.ReadDouble();
            else if (field == layout.Min && type == ProtoWireType.Fixed64) point.Min = reader.ReadDouble();
            else if (field == layout.Max && type == ProtoWireType.Fixed64) point.Max = reader.ReadDouble();
            else if (field == layout.Flags && type == ProtoWireType.Varint) point.Flags = (uint)reader.ReadVarint();
            else reader.Skip(type);
        }
        if (kind == MetricKind.Summary && point.Count is null) point.Count = 0;
        return point;
    }

    private static void ReadResourceLogs(ReadOnlySpan<byte> data, OtlpRecordFactory factory)
    {
        var resource = factory.Resource(ReadResource(data, factory));
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 2 && type == ProtoWireType.LengthDelimited) ReadScopeLogs(reader.ReadBytes(), resource, factory);
            else reader.Skip(type);
        }
    }

    private static void ReadScopeLogs(ReadOnlySpan<byte> data, TelemetryAttributes resource, OtlpRecordFactory factory)
    {
        var scoped = WithScope(data, resource, factory);
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 2 && type == ProtoWireType.LengthDelimited) factory.AddLog(scoped, ReadLogRecord(reader.ReadBytes(), factory));
            else reader.Skip(type);
        }
    }

    private static OtlpLogFields ReadLogRecord(ReadOnlySpan<byte> data, OtlpRecordFactory factory)
    {
        var log = new OtlpLogFields();
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == ProtoWireType.Fixed64: log.TimeUnixNano = Nanos(reader.ReadFixed64()); break;
                case 11 when type == ProtoWireType.Fixed64: log.ObservedTimeUnixNano = Nanos(reader.ReadFixed64()); break;
                case 2 when type == ProtoWireType.Varint: log.SeverityNumber = (int)Math.Min(reader.ReadVarint(), 1000); break;
                case 3 when type == ProtoWireType.LengthDelimited: log.SeverityText = reader.ReadString(); break;
                case 5 when type == ProtoWireType.LengthDelimited: log.Body = AnyValueText(reader.ReadBytes(), factory.Limits.MaxBodyChars); break;
                case 6 when type == ProtoWireType.LengthDelimited: AddAttribute(log.Attributes, reader.ReadBytes(), factory); break;
                case 9 when type == ProtoWireType.LengthDelimited: log.TraceId = OtlpRecordFactory.Identifier(reader.ReadBytes(), 16); break;
                case 10 when type == ProtoWireType.LengthDelimited: log.SpanId = OtlpRecordFactory.Identifier(reader.ReadBytes(), 8); break;
                case 12 when type == ProtoWireType.LengthDelimited: log.EventName = reader.ReadString(); break;
                default: reader.Skip(type); break;
            }
        }
        return log;
    }

    /// <summary>Resource attributes from field 1 of ResourceMetrics or ResourceLogs, at most <see cref="OtlpLimits.MaxAttributes"/>.</summary>
    private static List<KeyValuePair<string, string>> ReadResource(ReadOnlySpan<byte> data, OtlpRecordFactory factory)
    {
        var attributes = new List<KeyValuePair<string, string>>();
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field != 1 || type != ProtoWireType.LengthDelimited)
            {
                reader.Skip(type);
                continue;
            }
            var resource = new ProtoWireReader(reader.ReadBytes());
            while (resource.TryReadTag(out var inner, out var innerType))
            {
                if (inner == 1 && innerType == ProtoWireType.LengthDelimited) AddAttribute(attributes, resource.ReadBytes(), factory);
                else resource.Skip(innerType);
            }
        }
        return attributes;
    }

    /// <summary>The shared set of the resource plus the instrumentation scope name (field 1 of ScopeMetrics or ScopeLogs).</summary>
    private static TelemetryAttributes WithScope(ReadOnlySpan<byte> data, TelemetryAttributes resource, OtlpRecordFactory factory)
    {
        var scopeName = "";
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field != 1 || type != ProtoWireType.LengthDelimited)
            {
                reader.Skip(type);
                continue;
            }
            var scope = new ProtoWireReader(reader.ReadBytes());
            while (scope.TryReadTag(out var inner, out var innerType))
            {
                if (inner == 1 && innerType == ProtoWireType.LengthDelimited) scopeName = scope.ReadString();
                else scope.Skip(innerType);
            }
        }
        return factory.Scope(resource, scopeName);
    }

    /// <summary>Decodes one KeyValue into <paramref name="attributes"/> unless the list is full.</summary>
    private static void AddAttribute(List<KeyValuePair<string, string>> attributes, ReadOnlySpan<byte> data, OtlpRecordFactory factory)
    {
        if (!factory.TakesAttribute(attributes.Count)) return;
        var limits = factory.Limits;
        string key = "", value = "";
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.LengthDelimited) key = reader.ReadString();
            else if (field == 2 && type == ProtoWireType.LengthDelimited) value = AnyValueText(reader.ReadBytes(), limits.MaxAttributeValueChars);
            else reader.Skip(type);
        }
        attributes.Add(KeyValuePair.Create(key, OtlpAttributeBuilder.Truncate(value, limits.MaxAttributeValueChars)));
    }

    /// <summary>Scalar AnyValue as text; arrays and key-value lists as compact JSON, cut once longer than <paramref name="maxChars"/>.</summary>
    private static string AnyValueText(ReadOnlySpan<byte> data, int maxChars)
    {
        var text = "";
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == ProtoWireType.LengthDelimited: text = reader.ReadString(); break;
                case 2 when type == ProtoWireType.Varint: text = reader.ReadVarint() != 0 ? "true" : "false"; break;
                case 3 when type == ProtoWireType.Varint: text = OtlpAttributeBuilder.Number(unchecked((long)reader.ReadVarint())); break;
                case 4 when type == ProtoWireType.Fixed64: text = OtlpAttributeBuilder.Number(reader.ReadDouble()); break;
                case 5 or 6 when type == ProtoWireType.LengthDelimited: text = ComplexJson(reader.ReadBytes(), field == 6, maxChars); break;
                case 7 when type == ProtoWireType.LengthDelimited: text = Convert.ToBase64String(reader.ReadBytes()); break;
                default: reader.Skip(type); break;
            }
        }
        return text;
    }

    private static string ComplexJson(ReadOnlySpan<byte> data, bool keyValueList, int maxChars)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var budget = OtlpAttributeBuilder.JsonByteBudget(maxChars);
            if (keyValueList) WriteKeyValueList(data, writer, 1, budget);
            else WriteArray(data, writer, 1, budget);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteAnyValue(ReadOnlySpan<byte> data, Utf8JsonWriter writer, int depth, long budget)
    {
        if (depth > MaxValueDepth) throw new OtlpFormatException("AnyValue nesting is too deep.");
        var written = false;
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (written)
            {
                reader.Skip(type);
                continue;
            }
            written = true;
            switch (field)
            {
                case 1 when type == ProtoWireType.LengthDelimited: writer.WriteStringValue(reader.ReadString()); break;
                case 2 when type == ProtoWireType.Varint: writer.WriteBooleanValue(reader.ReadVarint() != 0); break;
                case 3 when type == ProtoWireType.Varint: writer.WriteNumberValue(unchecked((long)reader.ReadVarint())); break;
                case 4 when type == ProtoWireType.Fixed64:
                    var number = reader.ReadDouble();
                    if (double.IsFinite(number)) writer.WriteNumberValue(number);
                    else writer.WriteStringValue(OtlpAttributeBuilder.Number(number));
                    break;
                case 5 when type == ProtoWireType.LengthDelimited: WriteArray(reader.ReadBytes(), writer, depth + 1, budget); break;
                case 6 when type == ProtoWireType.LengthDelimited: WriteKeyValueList(reader.ReadBytes(), writer, depth + 1, budget); break;
                case 7 when type == ProtoWireType.LengthDelimited: writer.WriteStringValue(Convert.ToBase64String(reader.ReadBytes())); break;
                default:
                    written = false;
                    reader.Skip(type);
                    break;
            }
        }
        if (!written) writer.WriteNullValue();
    }

    private static void WriteArray(ReadOnlySpan<byte> data, Utf8JsonWriter writer, int depth, long budget)
    {
        if (depth > MaxValueDepth) throw new OtlpFormatException("AnyValue nesting is too deep.");
        writer.WriteStartArray();
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.LengthDelimited && !OtlpAttributeBuilder.IsFull(writer, budget))
                WriteAnyValue(reader.ReadBytes(), writer, depth, budget);
            else reader.Skip(type);
        }
        writer.WriteEndArray();
    }

    private static void WriteKeyValueList(ReadOnlySpan<byte> data, Utf8JsonWriter writer, int depth, long budget)
    {
        if (depth > MaxValueDepth) throw new OtlpFormatException("AnyValue nesting is too deep.");
        writer.WriteStartObject();
        var reader = new ProtoWireReader(data);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field != 1 || type != ProtoWireType.LengthDelimited || OtlpAttributeBuilder.IsFull(writer, budget))
            {
                reader.Skip(type);
                continue;
            }
            var pair = new ProtoWireReader(reader.ReadBytes());
            string key = "";
            ReadOnlySpan<byte> value = default;
            while (pair.TryReadTag(out var inner, out var innerType))
            {
                if (inner == 1 && innerType == ProtoWireType.LengthDelimited) key = pair.ReadString();
                else if (inner == 2 && innerType == ProtoWireType.LengthDelimited) value = pair.ReadBytes();
                else pair.Skip(innerType);
            }
            writer.WritePropertyName(key);
            WriteAnyValue(value, writer, depth, budget);
        }
        writer.WriteEndObject();
    }

    /// <summary>fixed64 nanoseconds; values beyond the signed range are marked invalid with -1.</summary>
    private static long Nanos(ulong value) => value > long.MaxValue ? -1 : (long)value;
}
