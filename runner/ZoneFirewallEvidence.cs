using System.Globalization;

namespace AgentRunner;

/// <summary>Counts matching local REJECT rules before and after one metadata probe.</summary>
public static class ZoneFirewallEvidence
{
    public static bool DenyHit(string before, string after, string bridge, string address)
    {
        if (!ZonePolicyParser.TryNormalizeCidr(address, out var target)
            || !ZonePolicyParser.TryParseCidr(target, out var value, out _)) return false;
        var old = Counters(before, bridge, value);
        var current = Counters(after, bridge, value);
        return current.Any(item => old.TryGetValue(item.Key, out var previous) && item.Value > previous);
    }

    private static Dictionary<string, long> Counters(string saved, string bridge, uint address)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in saved.Split('\n'))
        {
            if (!line.StartsWith("[", StringComparison.Ordinal)) continue;
            var close = line.IndexOf(']');
            var colon = line.IndexOf(':');
            if (close < 0 || colon < 0 || colon > close
                || !long.TryParse(line.AsSpan(1, colon - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var packets)) continue;
            var rule = line[(close + 1)..].TrimStart();
            if (!rule.StartsWith($"-A {ZonePolicyRules.ForwardChain} -i {bridge} ", StringComparison.Ordinal)
                || !rule.Contains("-j REJECT", StringComparison.Ordinal)) continue;
            var parts = rule.Split(' ');
            var match = Array.IndexOf(parts, "--ctorigdst");
            if (match < 0) match = Array.IndexOf(parts, "-d");
            if (match < 0 || match + 1 == parts.Length
                || !ZonePolicyParser.TryParseCidr(parts[match + 1], out var start, out var end)
                || address < start || address > end) continue;
            counts[rule] = packets;
        }
        return counts;
    }
}
