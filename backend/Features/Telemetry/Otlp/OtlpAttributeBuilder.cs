using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Telemetry;

/// <summary>
/// Size limits applied while decoding, so one request can neither store
/// unbounded text nor build unbounded memory before the intake refuses it.
/// </summary>
public sealed record OtlpLimits(int MaxAttributes, int MaxAttributeValueChars, int MaxBodyChars,
    int MaxAttributeBytes, int MaxRecords, long MaxDecodedBytes, int MaxScopes, long MaxStoredBytes)
{
    public static OtlpLimits From(TelemetryOptions options)
        => new(options.MaxAttributesPerRecord, options.MaxAttributeValueChars, options.MaxLogBodyChars,
            options.MaxAttributeBytesPerRecord, options.MaxRecordsPerRequest, options.MaxDecodedBytesPerRequest,
            options.MaxScopesPerRequest, options.MaxStoredBytesPerRequest);
}

/// <summary>
/// Merges resource and record attributes; a record attribute replaces a
/// resource attribute with the same key. Values are truncated, and the set is
/// capped at <see cref="OtlpLimits.MaxAttributes"/> keys and
/// <see cref="OtlpLimits.MaxAttributeBytes"/> UTF-8 bytes of keys and values,
/// which also caps the size of a series key. An attribute that does not fit is
/// dropped; attributes added earlier keep their place.
/// </summary>
internal sealed class OtlpAttributeBuilder
{
    private const int MaxKeyChars = 256;
    private readonly OtlpLimits _limits;
    private readonly Dictionary<string, string> _values;
    private int _bytes;

    /// <summary>Starts from <paramref name="seed"/>, the shared resource and scope set, when given.</summary>
    public OtlpAttributeBuilder(OtlpLimits limits, TelemetryAttributes? seed = null)
    {
        _limits = limits;
        _values = new Dictionary<string, string>(seed?.Count ?? 0, StringComparer.Ordinal);
        if (seed is null) return;
        foreach (var (key, value) in seed.Items)
        {
            _values[key] = value;
            _bytes += Bytes(key, value);
        }
    }

    /// <summary>True once an attribute was added or replaced after the seed.</summary>
    public bool Changed { get; private set; }

    public OtlpAttributeBuilder AddRange(IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var (key, value) in values) Add(key, value);
        return this;
    }

    public void Add(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyChars) return;
        value = Truncate(value, _limits.MaxAttributeValueChars);
        var replaced = _values.TryGetValue(key, out var previous);
        if (!replaced && _values.Count >= _limits.MaxAttributes) return;
        var total = _bytes + Bytes(key, value) - (replaced ? Bytes(key, previous!) : 0);
        if (total > _limits.MaxAttributeBytes || (replaced && previous == value)) return;
        _values[key] = value;
        _bytes = total;
        Changed = true;
    }

    public TelemetryAttributes Build(bool shared = false) => TelemetryAttributes.From(_values, shared);

    public static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        var cut = char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars;
        return value[..cut] + "…";
    }

    /// <summary>
    /// UTF-8 bytes past which JSON text certainly holds more than
    /// <paramref name="maxChars"/> + 1 characters (a UTF-16 character takes at
    /// most three bytes), so a value cut there truncates to the same text.
    /// </summary>
    public static long JsonByteBudget(int maxChars) => 3L * maxChars + 3;

    /// <summary>True once the writer is past <paramref name="budget"/>; further array and list entries are skipped.</summary>
    public static bool IsFull(Utf8JsonWriter writer, long budget) => writer.BytesCommitted + writer.BytesPending > budget;

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Bytes(string key, string value) => Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
}
