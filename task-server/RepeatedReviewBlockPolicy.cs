using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal static class RepeatedReviewBlockPolicy
{
    internal static RepeatedReviewBlockDiagnosis? Diagnose(string currentPayloadJson,
        IEnumerable<string> priorDecisionPayloadsNewestFirst, int maximumRounds)
        => RepeatedReviewBlockRules.Diagnose(ReadVerdicts(currentPayloadJson),
            priorDecisionPayloadsNewestFirst.Select(ReadVerdicts), maximumRounds);

    private static IReadOnlyList<ReviewVerdictDto>? ReadVerdicts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReviewOrchestrationPayloadDto>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Verdicts;
        }
        catch (JsonException)
        {
            // Malformed legacy payloads carry no usable recurrence facts.
            return null;
        }
    }
}
