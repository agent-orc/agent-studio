using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentStudio.Review;

/// <summary>Versioned fingerprints of the evidence used by a published Result.</summary>
public sealed record SummaryEvidenceVersion(
    int Schema, string? ResultSha, string Digest, string RunSources, string LogFingerprint, int LogLength,
    Dictionary<string, string> Facts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex LogTimestamp = new(
        @"(?m)^\[\d{4}-\d{2}-\d{2}T[^\]\r\n]+\] (?=\[)", RegexOptions.CultureInvariant);

    private static readonly Regex DeliveryReceipt = new(
        @"(?m)^\[system\] \[runner-log-delivery:[a-fA-F0-9]+\][ \t]*\n?", RegexOptions.CultureInvariant);

    internal static (SummaryEvidenceVersion Version, JsonObject Facts, string Log) Capture(
        TaskInfo task, string? resultSha, SummaryEvidenceBundle evidence, SummaryLogSources sources,
        string? authoredStatus, string? dossierPath)
    {
        // Routine movement through the working/review lanes has the same
        // assessment semantics. Completion and escalation are substantive.
        var lane = evidence.Lane is TaskStates.Completed or TaskStates.Escalated or TaskStates.Archive
            ? evidence.Lane : "active";
        var facts = JsonSerializer.SerializeToNode(evidence with
        {
            Lane = lane,
            LaneHistory = [],
            Pipeline = evidence.Pipeline is { Steps.Count: > 0 } pipeline
                ? pipeline with { Attempt = 0, Completed = false } : null,
        }, Json)!.AsObject();
        facts.Remove("laneHistory");
        facts.Remove("statusHeader");
        if (facts["pipeline"] is JsonObject steps)
        {
            steps.Remove("attempt");
            steps.Remove("completed");
        }

        var log = DeliveryReceipt.Replace(LogTimestamp.Replace(sources.Log.ReplaceLineEndings("\n"), ""), "").TrimEnd();
        var logFingerprint = SummaryAssessmentStore.Fingerprint(log);
        var runSources = Hash(new
        {
            task.Title,
            TaskPrompt = sources.TaskPrompt?.ReplaceLineEndings("\n").Trim(),
            Deliverables = sources.Deliverables.Select(item => new { item.Path, item.Bytes, item.ContentFingerprint }),
            AuthoredStatus = authoredStatus?.ReplaceLineEndings("\n").Trim(),
            DossierPath = dossierPath,
        });
        var fingerprints = facts.ToDictionary(field => field.Key, field => Hash(field.Value), StringComparer.Ordinal);
        const int schema = 1;
        return (new SummaryEvidenceVersion(schema, resultSha,
            Hash(new { schema, resultSha, runSources, logFingerprint, fingerprints }),
            runSources, logFingerprint, log.Length, fingerprints), facts, log);
    }

    internal string? DeltaFrom(SummaryEvidenceVersion prior, JsonObject currentFacts, string log,
        string priorMarkdown, string priorOutcome, int budgetBytes)
    {
        if (Schema != prior.Schema || ResultSha is null || ResultSha != prior.ResultSha
            || RunSources != prior.RunSources || prior.Facts is null || Digest == prior.Digest
            || !Facts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(prior.Facts.Keys))
            return null;

        string? additionalRunLog = null;
        if (LogFingerprint != prior.LogFingerprint)
        {
            if (prior.LogLength < 0 || prior.LogLength >= log.Length || log[prior.LogLength] != '\n'
                || log.Length - prior.LogLength > budgetBytes
                || SummaryAssessmentStore.Fingerprint(log[..prior.LogLength]) != prior.LogFingerprint)
                return null;
            additionalRunLog = log[prior.LogLength..].Trim();
        }

        var changes = new JsonObject();
        foreach (var field in currentFacts)
            if (Facts[field.Key] != prior.Facts[field.Key])
                changes[field.Key] = field.Value?.DeepClone();

        var delta = JsonSerializer.Serialize(new
        {
            instruction = "Update the prior Result using the changed evidence below. Each changed field replaces the previous field, including null and empty values. Use any additional run log as new evidence. Keep supported run details. Reassess contradictions from the current facts. Return the full Result and assessment.",
            resultSha = ResultSha,
            priorEvidenceDigest = prior.Digest,
            evidenceDigest = Digest,
            priorOutcome,
            priorResult = priorMarkdown,
            changedEvidence = changes,
            additionalRunLog,
        }, Json);
        return Encoding.UTF8.GetByteCount(delta) <= budgetBytes ? delta : null;
    }

    private static string Hash<T>(T value)
        => SummaryAssessmentStore.Fingerprint(JsonSerializer.Serialize(value, Json));
}
