using System.Text;
using System.Text.Json;

namespace AgentStudio.Telemetry;

public enum OtlpEncoding { Protobuf, Json }

/// <summary>
/// OTLP/HTTP response bodies. A success carries an
/// <c>Export*ServiceResponse</c> whose <c>partial_success</c> (field 1) holds
/// the rejected count (field 1) and an error message (field 2); errors carry a
/// <c>google.rpc.Status</c> with code (field 1) and message (field 2). Both use
/// the request's encoding, as OTLP/HTTP requires.
/// </summary>
public static class OtlpResponses
{
    public const string ProtobufContentType = "application/x-protobuf";
    public const string JsonContentType = "application/json";

    public static string ContentType(OtlpEncoding encoding)
        => encoding == OtlpEncoding.Protobuf ? ProtobufContentType : JsonContentType;

    /// <param name="rejectedField">JSON name of the rejected count: rejectedDataPoints or rejectedLogRecords.</param>
    public static byte[] Success(OtlpEncoding encoding, long rejected, string? message, string rejectedField)
    {
        if (rejected <= 0 && string.IsNullOrEmpty(message))
            return encoding == OtlpEncoding.Protobuf ? [] : "{}"u8.ToArray();
        if (encoding == OtlpEncoding.Json)
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["partialSuccess"] = new Dictionary<string, object>
                {
                    [rejectedField] = rejected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["errorMessage"] = message ?? ""
                }
            });
        var inner = new List<byte>();
        if (rejected > 0) WriteVarintField(inner, 1, (ulong)rejected);
        if (!string.IsNullOrEmpty(message)) WriteBytesField(inner, 2, Encoding.UTF8.GetBytes(message));
        var outer = new List<byte>();
        WriteBytesField(outer, 1, inner.ToArray());
        return outer.ToArray();
    }

    /// <param name="code">google.rpc.Code, for example 3 INVALID_ARGUMENT or 14 UNAVAILABLE.</param>
    public static byte[] Status(OtlpEncoding encoding, int code, string message)
    {
        if (encoding == OtlpEncoding.Json)
            return JsonSerializer.SerializeToUtf8Bytes(new { code, message });
        var bytes = new List<byte>();
        WriteVarintField(bytes, 1, (ulong)code);
        WriteBytesField(bytes, 2, Encoding.UTF8.GetBytes(message));
        return bytes.ToArray();
    }

    private static void WriteVarintField(List<byte> target, int field, ulong value)
    {
        WriteVarint(target, (ulong)field << 3);
        WriteVarint(target, value);
    }

    private static void WriteBytesField(List<byte> target, int field, byte[] value)
    {
        WriteVarint(target, ((ulong)field << 3) | 2);
        WriteVarint(target, (ulong)value.Length);
        target.AddRange(value);
    }

    private static void WriteVarint(List<byte> target, ulong value)
    {
        while (value >= 0x80)
        {
            target.Add((byte)(value | 0x80));
            value >>= 7;
        }
        target.Add((byte)value);
    }
}
