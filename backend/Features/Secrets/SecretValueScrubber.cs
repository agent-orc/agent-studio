using System.Text;

namespace AgentStudio.Secrets;

/// <summary>
/// Masks values the provider issued to workloads before workload output is
/// stored in logs or task folders. Pattern redaction (CredentialRedactor)
/// cannot recognise an arbitrary database password; this knows the exact
/// value because it was registered at resolve time. Each value is masked
/// verbatim, line by line when it spans lines, and in its base64 form, as
/// <c>[secret:&lt;reference&gt;]</c>. Entries expire a day after their last
/// resolution and the set is bounded.
/// </summary>
public sealed class SecretValueScrubber
{
    public const int MinimumMaskedLength = 6;
    private const int MaximumEntries = 4096;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private Entry[] _ordered = [];

    public SecretValueScrubber(TimeProvider clock) => _clock = clock;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Register(SecretReference reference, string value)
    {
        var mask = "[secret:" + reference.Unpinned() + "]";
        var expires = _clock.GetUtcNow() + Retention;
        lock (_gate)
        {
            foreach (var form in Forms(value))
                _entries[form] = new Entry(form, mask, expires);
            Prune();
            _ordered = _entries.Values.OrderByDescending(entry => entry.Text.Length).ToArray();
        }
    }

    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        Entry[] entries;
        lock (_gate) entries = _ordered;
        if (entries.Length == 0) return text;
        var now = _clock.GetUtcNow();
        var result = text;
        foreach (var entry in entries)
        {
            if (entry.ExpiresAt <= now) continue;
            if (result.Contains(entry.Text, StringComparison.Ordinal))
                result = result.Replace(entry.Text, entry.Mask, StringComparison.Ordinal);
        }
        return result;
    }

    private static IEnumerable<string> Forms(string value)
    {
        if (value.Length >= MinimumMaskedLength) yield return value;
        var lines = value.Split(["\r\n", "\n"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1)
            foreach (var line in lines.Where(line => line.Length >= MinimumMaskedLength))
                yield return line;
        if (value.Length >= MinimumMaskedLength)
            yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var expired in _entries.Values.Where(entry => entry.ExpiresAt <= now).Select(entry => entry.Text).ToArray())
            _entries.Remove(expired);
        if (_entries.Count <= MaximumEntries) return;
        foreach (var oldest in _entries.Values.OrderBy(entry => entry.ExpiresAt).Take(_entries.Count - MaximumEntries)
                     .Select(entry => entry.Text).ToArray())
            _entries.Remove(oldest);
    }

    private sealed record Entry(string Text, string Mask, DateTimeOffset ExpiresAt);
}
