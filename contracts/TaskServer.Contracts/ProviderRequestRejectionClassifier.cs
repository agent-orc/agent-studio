using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Extracts a provider's bounded 4xx model-request refusal. This is deliberately
/// narrower than CLI argument/configuration parsing: it requires a structured
/// provider error plus an HTTP 400, 403, or 404 refusal signal.
/// </summary>
public static partial class ProviderRequestRejectionClassifier
{
    private static readonly HashSet<int> RejectionStatuses = [400, 403, 404];

    public static bool TryClassify(string? providerTerminalEvent, out ProviderRequestRejection rejection)
    {
        rejection = null!;
        if (string.IsNullOrWhiteSpace(providerTerminalEvent)) return false;

        // A CLI stdout stream may contain one JSON frame per line. Prefer the
        // last refusal frame, matching the terminal-event extractor, then try
        // the complete text for providers that prefix a single JSON payload.
        foreach (var candidate in providerTerminalEvent
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Reverse()
                     .Append(providerTerminalEvent))
        {
            if (TryClassifyCandidate(candidate, out rejection)) return true;
        }
        return false;
    }

    private static bool TryClassifyCandidate(string providerTerminalEvent, out ProviderRequestRejection rejection)
    {
        rejection = null!;
        var fields = new EvidenceFields();
        CollectJson(providerTerminalEvent, fields, depth: 0);
        fields.Status ??= HttpStatusRegex().Match(providerTerminalEvent) is { Success: true } status
                         && int.TryParse(status.Groups["status"].Value, out var parsed)
            ? parsed
            : null;

        if (fields.Status is not { } httpStatus || !RejectionStatuses.Contains(httpStatus)) return false;

        var message = Clean(fields.Message);
        var code = Clean(fields.Code);
        var type = Clean(fields.ErrorType);
        var parameter = Clean(fields.Parameter);
        var explicitRequestError = string.Equals(type, "invalid_request_error", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(code, "unsupported_parameter", StringComparison.OrdinalIgnoreCase);
        var unavailableModelOrFeature = ModelOrFeatureUnavailableRegex().IsMatch(message ?? string.Empty);
        if (!explicitRequestError && !unavailableModelOrFeature) return false;
        if (message is null) return false;

        rejection = new ProviderRequestRejection(code, parameter, message, httpStatus);
        return true;
    }

    private static void CollectJson(string text, EvidenceFields fields, int depth)
    {
        if (depth > 5) return;
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) return;

        try
        {
            using var document = JsonDocument.Parse(trimmed[start..(end + 1)]);
            Collect(document.RootElement, fields, depth);
        }
        catch (JsonException)
        {
            // A provider may prefix its JSON error with "API Error: 403".
            // The outer status regex still applies; malformed content is not
            // promoted into a typed request rejection.
        }
    }

    private static void Collect(JsonElement element, EvidenceFields fields, int depth)
    {
        if (depth > 5) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    // Provider CLIs commonly wrap the real JSON error in an
                    // outer message/result string. Prefer its bounded inner
                    // fields over the serialized envelope text.
                    if (!string.IsNullOrWhiteSpace(text) && text.Contains('{'))
                        CollectJson(text, fields, depth + 1);
                    if (name.Equals("code", StringComparison.OrdinalIgnoreCase)) fields.Code ??= text;
                    else if (name.Equals("param", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("parameter", StringComparison.OrdinalIgnoreCase)) fields.Parameter ??= text;
                    else if (name.Equals("message", StringComparison.OrdinalIgnoreCase)
                             || (name.Equals("result", StringComparison.OrdinalIgnoreCase)
                                 && text?.Contains('{') != true)) fields.Message ??= text;
                    else if (name.Equals("type", StringComparison.OrdinalIgnoreCase)
                             && !IsEnvelopeType(text)) fields.ErrorType ??= text;
                }
                else if ((name.Equals("status", StringComparison.OrdinalIgnoreCase)
                          || name.Equals("status_code", StringComparison.OrdinalIgnoreCase))
                         && value.TryGetInt32(out var status))
                {
                    fields.Status ??= status;
                }
                Collect(value, fields, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Collect(item, fields, depth + 1);
        }
    }

    private static bool IsEnvelopeType(string? value)
        => value is "error" or "result" or "turn.failed" or "response.failed";

    private static string? Clean(string? value, int maximum = 1000)
    {
        var cleaned = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length == 0 ? null : cleaned.Length <= maximum ? cleaned : cleaned[..maximum];
    }

    private sealed class EvidenceFields
    {
        public int? Status { get; set; }
        public string? ErrorType { get; set; }
        public string? Code { get; set; }
        public string? Parameter { get; set; }
        public string? Message { get; set; }
    }

    [GeneratedRegex(@"(?:\bHTTP\s*|\bstatus(?:_code|\s+code)?\s*[:=]?\s*)(?<status>400|403|404)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpStatusRegex();

    [GeneratedRegex(@"\b(?:model|feature|parameter|access[ _-]?program)\b.{0,100}\b(?:not enabled|not available|not supported|unsupported|not found|does not exist|not allowed)\b|\b(?:not enabled|not available|not supported|unsupported)\b.{0,100}\b(?:model|feature|parameter|access[ _-]?program)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelOrFeatureUnavailableRegex();
}
