using System.Text.Json;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.OrchestrationDecisions;

public static class BatchGateAdvisorPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public static bool TryApply(string response, BatchPlanDecision deterministic,
        IReadOnlyList<string> eligible, int minimum, int maximum,
        DateTimeOffset now, int maximumHoldMinutes,
        out BatchPlanDecision adjusted, out string error)
    {
        adjusted = deterministic;
        error = "invalid JSON";
        if (response.Length > 8_000 || response.TrimStart().StartsWith("```", StringComparison.Ordinal))
            return false;
        BatchAdvisorAnswer? answer;
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var properties = document.RootElement.EnumerateObject()
                .Select(property => property.Name).ToArray();
            string[] required = ["verdict", "order", "subset", "holdUntil", "reasons"];
            if (properties.Length != required.Length
                || properties.Distinct(StringComparer.Ordinal).Count() != required.Length
                || required.Any(field => !properties.Contains(field, StringComparer.Ordinal)))
            {
                error = "advisor JSON schema mismatch";
                return false;
            }
            answer = JsonSerializer.Deserialize<BatchAdvisorAnswer>(response, Json);
        }
        catch (JsonException) { return false; }
        if (answer is null || answer.Verdict is not ("approve" or "adjust" or "veto")
            || answer.Reasons is null || answer.Order is null || answer.Subset is null
            || answer.Reasons.Count > 8
            || answer.Reasons.Any(reason => reason.Length > 240))
        {
            error = "invalid advisor verdict or reasons";
            return false;
        }
        if (answer.HoldUntil is { } hold
            && (hold < now || hold > now.AddMinutes(maximumHoldMinutes)))
        {
            error = "hold exceeds configured look-ahead window";
            return false;
        }
        if (answer.Verdict == "approve" && answer.HoldUntil is not null)
        {
            error = "approve cannot hold the batch";
            return false;
        }
        var selected = answer.Subset.Count == 0
            ? deterministic.ProposedMembers : answer.Subset;
        if (selected.Count < minimum || selected.Count > maximum
            || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count
            || selected.Any(key => !eligible.Contains(key, StringComparer.Ordinal)))
        {
            error = "advisor subset is outside eligible size bounds";
            return false;
        }
        if (answer.Order.Count > 0)
        {
            if (answer.Order.Count != selected.Count
                || answer.Order.Distinct(StringComparer.Ordinal).Count() != answer.Order.Count
                || answer.Order.Any(key => !selected.Contains(key, StringComparer.Ordinal)))
            {
                error = "advisor order does not match subset";
                return false;
            }
            selected = answer.Order;
        }
        if (answer.Verdict == "veto")
        {
            if (answer.HoldUntil is null)
            {
                error = "veto requires a bounded holdUntil";
                return false;
            }
            adjusted = deterministic with
            {
                Decision = "wait", Reason = "advisor veto: " + string.Join("; ", answer.Reasons),
                CloseAtUtc = answer.HoldUntil,
            };
            error = "";
            return true;
        }
        if (answer.Verdict == "adjust")
            adjusted = deterministic with
            {
                ProposedMembers = selected,
                Reason = "advisor adjustment: " + string.Join("; ", answer.Reasons),
                CloseAtUtc = answer.HoldUntil ?? deterministic.CloseAtUtc,
                Decision = answer.HoldUntil is null ? deterministic.Decision : "wait",
            };
        error = "";
        return true;
    }
}
