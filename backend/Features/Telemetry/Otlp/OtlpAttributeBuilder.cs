using System.Globalization;

namespace AgentStudio.Telemetry;

/// <summary>Size limits applied while decoding, so one request cannot store unbounded text.</summary>
public sealed record OtlpLimits(int MaxAttributes, int MaxAttributeValueChars, int MaxBodyChars)
{
    public static OtlpLimits From(TelemetryOptions options)
        => new(options.MaxAttributesPerRecord, options.MaxAttributeValueChars, options.MaxLogBodyChars);
}

/// <summary>
/// Merges resource and record attributes; a record attribute replaces a
/// resource attribute with the same key. Values are truncated and the set is
/// capped at <see cref="OtlpLimits.MaxAttributes"/> keys.
/// </summary>
internal sealed class OtlpAttributeBuilder(OtlpLimits limits)
{
    private const int MaxKeyChars = 256;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public OtlpAttributeBuilder AddRange(IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var (key, value) in values) Add(key, value);
        return this;
    }

    public void Add(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyChars) return;
        if (!_values.ContainsKey(key) && _values.Count >= limits.MaxAttributes) return;
        _values[key] = Truncate(value, limits.MaxAttributeValueChars);
    }

    public TelemetryAttributes Build() => TelemetryAttributes.From(_values);

    public IReadOnlyList<KeyValuePair<string, string>> ToList() => _values.ToList();

    public static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        var cut = char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars;
        return value[..cut] + "…";
    }

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
