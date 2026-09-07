using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Watcher;

/// <summary>
/// Stable identity for a finding. A fingerprint must survive restarts, repeated
/// sweeps, and re-detection of the same underlying fault, so it is built only
/// from the parts that describe the fault, never from counts or timestamps.
/// </summary>
public static partial class WatcherFingerprint
{
    /// <summary>
    /// ASCII unit separator. It cannot occur inside a normalized part, so
    /// joining with it keeps ("a", "bc") distinct from ("ab", "c").
    /// </summary>
    private const char UnitSeparator = '\u001f';

    /// <summary>Hex blobs long enough to be a sha, a guid, or a temp-file suffix.</summary>
    [GeneratedRegex(@"\b[0-9a-f]{7,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HexRun();

    /// <summary>Runs of digits that vary per occurrence (ports, pids, byte counts, durations).</summary>
    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Reduce free-text failure output to the part that identifies the fault.
    /// Two occurrences of the same fault differing only in a sha, a pid, or a
    /// duration must normalize to the same string.
    /// </summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var collapsed = Whitespace().Replace(text.Trim().ToLowerInvariant(), " ");
        // Hex first: a sha is also a digit run, and replacing digits first
        // would leave the letter fragments behind as false discriminators.
        collapsed = HexRun().Replace(collapsed, "#");
        return DigitRun().Replace(collapsed, "#");
    }

    /// <summary>
    /// Build the fingerprint for a rule from already-stable parts. The rule id
    /// stays readable in front of the digest so an operator can tell which
    /// detector produced a case without a lookup.
    /// </summary>
    public static string Compute(string rule, params string?[] parts)
    {
        // The unit separator cannot occur in a normalized part, so ("a","bc")
        // and ("ab","c") cannot collide.
        var joined = string.Join(UnitSeparator, parts.Select(part => part ?? string.Empty));
        return $"{rule}:{Digest(joined)}";
    }

    /// <summary>Short, collision-resistant hex digest used inside a fingerprint and for evidence packs.</summary>
    public static string Digest(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(24);
        for (var index = 0; index < 12; index++)
            builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>
    /// Digest of an evidence pack. Order-independent per item so a collector
    /// that enumerates sources in a different order does not invalidate the
    /// audit anchor of an unchanged pack.
    /// </summary>
    public static string DigestEvidence(IEnumerable<WatcherEvidenceItem> items)
    {
        var lines = items
            .Select(item => string.Join(
                UnitSeparator,
                item.Label,
                item.Source,
                NormalizeText(item.Value),
                item.Missing ? "missing" : "present"))
            .OrderBy(line => line, StringComparer.Ordinal);
        return Digest(string.Join('\n', lines));
    }
}
