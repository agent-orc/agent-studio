using System.Text.RegularExpressions;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Parses the legacy review model's last complete typed verdict.</summary>
public static class LegacyReviewDecisionPolicy
{
    private static readonly Regex Decision = new(
        @"\[\[\s*ORCHESTRATOR_DECISION\s*:\s*(?<body>[^\]]+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LegacyReviewChoice? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var matches = Decision.Matches(output);
        if (matches.Count == 0) return null;

        var fields = ParseFields(matches[^1].Groups["body"].Value);
        if (fields is null) return null;
        var action = fields.GetValueOrDefault("action")?.Trim().ToLowerInvariant();
        var reason = fields.GetValueOrDefault("reason")?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) return null;
        action = action switch
        {
            "reissue" => "reissue",
            "escalate" => "escalate",
            "accept-as-done" or "accept_as_done" or "accept" => "accept-as-done",
            _ => null,
        };
        return action is null ? null : new LegacyReviewChoice(action, reason);
    }

    private static Dictionary<string, string>? ParseFields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            if (fields.ContainsKey(key)) return null;
            fields[key] = value;
        }
        return fields.Count == 0 ? null : fields;
    }
}

public sealed record LegacyReviewChoice(string Action, string Reason);
