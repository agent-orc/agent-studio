using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Durable sidecar for <see cref="ParkedBlockerRecord"/>, written next to
/// <c>task.json</c> in the job folder. Same shape as the quota-wait marker: the
/// folder moves with the file, so the marker survives lane moves, restarts, and
/// the scanner cache without inventing another store.
/// </summary>
public static class ParkedBlockerMarker
{
    public const string FileName = "parked-blocker.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Write(string jobFolder, ParkedBlockerRecord record, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, FileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist parked-blocker marker in {Folder}", jobFolder);
        }
    }

    public static ParkedBlockerRecord? TryRead(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ParkedBlockerRecord>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read parked-blocker marker in {Folder}", jobFolder);
            return null;
        }
    }

    public static void Clear(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear parked-blocker marker in {Folder}", jobFolder);
        }
    }

    /// <summary>
    /// Board projection. <paramref name="now"/> makes the aging explicit rather
    /// than leaving every consumer to subtract timestamps itself, and the last
    /// probe verdict is read from the marker so no request path runs git.
    /// </summary>
    public static ParkedBlockerStatus? ToStatus(ParkedBlockerRecord? record, DateTime now)
    {
        if (record is null) return null;
        var parkedAt = record.ParkedAt == default ? now : record.ParkedAt.ToUniversalTime();
        var age = now - parkedAt;
        var evaluatedAt = record.LastEvaluation?.At;
        return new ParkedBlockerStatus(
            record.BlockerType,
            record.Condition.Kind,
            record.Condition.Description,
            parkedAt,
            (long)Math.Max(0, age.TotalSeconds),
            record.Reason,
            record.LastEvaluation?.Status ?? ParkedBlockerStatuses.Blocked,
            evaluatedAt,
            record.LastEvaluation?.Detail ?? "No recall sweep has evaluated this blocker yet.")
        {
            Lane = record.Lane,
            Decision = ToDecisionStatus(record.Decision),
            NeedsInputFile = record.NeedsInputFile,
            EvaluationAgeSeconds = evaluatedAt is null
                ? null
                : (long)Math.Max(0, (now - evaluatedAt.Value.ToUniversalTime()).TotalSeconds),
            EvaluationStale = ParkedBlockerCatalog.IsEvaluationStale(evaluatedAt),
            RequiresDecisionCard = ParkedBlockerCatalog.RequiresDecisionCard(record.BlockerType),
        };
    }

    private static ParkedDecisionStatus? ToDecisionStatus(ParkedDecisionRequest? decision)
        => decision is null
            ? null
            : new ParkedDecisionStatus(
                decision.QuestionId,
                decision.Question,
                decision.Options
                    .Select(option => new ParkedDecisionOptionStatus(
                        option.Id, option.Label, option.Consequences, option.Recommended))
                    .ToList(),
                decision.Documents,
                decision.DecisionCardKey);
}
