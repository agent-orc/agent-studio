using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.QualityStudio;

/// <summary>A frozen Review Plane range and its optional Quality Studio verdict.</summary>
public sealed record QualityChangeReviewRound(
    int Round, string AttemptId, string RunAttemptId, string? RepositoryId,
    string BaseSha, string HeadSha, int CommitCount, string Mode,
    DateTime ReviewedAtUtc, JsonElement? Document, string? UnavailableReason = null);

/// <summary>
/// Task-local, attempt-named evidence. The base comes from the workspace proof,
/// so a later integration-ref movement cannot silently change an older range.
/// </summary>
public static class QualityChangeReviewStore
{
    public const string ArtifactName = "change-review-verdict.v1.json";
    private const string Schema = "https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v1.schema.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static QualityChangeReviewRound? Project(TaskInfo task, ReviewAttemptDto review,
        Contract.ReviewReportRequest report, string? repositoryId, DateTime receivedAt)
    {
        var baseSha = report.Workspace.MergeBaseSha;
        var headSha = report.Workspace.ExpectedResultSha;
        if (!ReviewSubjectStore.IsValidResultSha(baseSha)
            || !ReviewSubjectStore.IsValidResultSha(headSha)
            || !string.Equals(headSha, review.Subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
            return null;

        var generation = task.Commits
            .Where(commit => string.Equals(commit.DeliveryAttemptId, review.SourceRunAttemptId,
                StringComparison.OrdinalIgnoreCase))
            .Select(commit => commit.DeliveryGeneration ?? 0).DefaultIfEmpty(0).Max();
        if (generation == 0)
            generation = task.Commits.Where(commit => string.Equals(commit.ResultSha, headSha,
                    StringComparison.OrdinalIgnoreCase))
                .Select(commit => commit.DeliveryGeneration ?? 0).DefaultIfEmpty(0).Max();
        var count = task.Commits.Count(commit => (commit.DeliveryGeneration ?? 0) <= generation
            && string.IsNullOrWhiteSpace(commit.SupersededBySha));
        var mode = report.QualityStudioCodeQualityMode is "shadow" or "decide"
            ? report.QualityStudioCodeQualityMode : "off";
        JsonElement? document = null;
        string? unavailable = null;
        var artifact = report.Artifacts.FirstOrDefault(item => item.Name == ArtifactName);
        if (artifact?.ContentBase64 is { } encoded)
        {
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > 65_536) throw new InvalidDataException("verdict document exceeds 64 KiB");
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(digest, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("verdict artifact digest differs");
                using var parsed = JsonDocument.Parse(bytes);
                var root = parsed.RootElement;
                if (root.GetProperty("schema").GetString() != Schema
                    || root.GetProperty("schemaVersion").GetInt32() != 1
                    || !string.Equals(root.GetProperty("repository").GetString(), repositoryId, StringComparison.Ordinal)
                    || !string.Equals(root.GetProperty("baseSha").GetString(), baseSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(root.GetProperty("headSha").GetString(), headSha, StringComparison.OrdinalIgnoreCase)
                    || !IsVerdict(root.GetProperty("verdict").GetString())
                    || string.IsNullOrWhiteSpace(root.GetProperty("policyHash").GetString())
                    || string.IsNullOrWhiteSpace(root.GetProperty("ruleSetHash").GetString())
                    || string.IsNullOrWhiteSpace(root.GetProperty("catalogueVersion").GetString())
                    || root.GetProperty("findings").ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("verdict artifact does not match the fenced range");
                document = root.Clone();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException or KeyNotFoundException)
            {
                unavailable = ex.Message;
            }
        }
        else if (mode is "shadow" or "decide") unavailable = "Review Plane verdict artifact is missing";

        var round = new QualityChangeReviewRound(generation, review.AttemptId,
            review.SourceRunAttemptId, repositoryId, baseSha!.ToLowerInvariant(),
            headSha.ToLowerInvariant(), count, mode, receivedAt, document, unavailable);
        Write(task.FolderPath, round);
        return round;
    }

    public static IReadOnlyList<QualityChangeReviewRound> Read(string taskFolder)
    {
        var directory = Path.Combine(TaskPaths.ResultsDir(taskFolder), "quality-studio");
        if (!Directory.Exists(directory)) return [];
        var rounds = new List<QualityChangeReviewRound>();
        foreach (var path in Directory.EnumerateFiles(directory, "review-*.json"))
        {
            try
            {
                var round = JsonSerializer.Deserialize<QualityChangeReviewRound>(File.ReadAllText(path), Json);
                if (round is not null) rounds.Add(round);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                SilentCatch.Note(ex, "QualityChangeReviewStore: unreadable round artifact");
            }
        }
        return rounds.OrderBy(round => round.Round).ThenBy(round => round.ReviewedAtUtc).ToArray();
    }

    public static string? LatestVerdict(string taskFolder)
    {
        var latest = Read(taskFolder).LastOrDefault();
        var current = ReviewSubjectStore.Read(taskFolder);
        if (latest?.Mode != "decide" || latest.Document is not { } document
            || current is not null && !string.Equals(current.ResultSha, latest.HeadSha,
                StringComparison.OrdinalIgnoreCase)) return null;
        return document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty("verdict", out var verdict) ? verdict.GetString() : null;
    }

    private static void Write(string taskFolder, QualityChangeReviewRound round)
    {
        var directory = Path.Combine(TaskPaths.ResultsDir(taskFolder), "quality-studio");
        Directory.CreateDirectory(directory);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(round.AttemptId)))[..16].ToLowerInvariant();
        var path = Path.Combine(directory, $"review-{id}.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(round, Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsVerdict(string? verdict) => verdict is "pass" or "concerns" or "block"
        or "provider-unavailable" or "timeout" or "auth-failure" or "unparseable";
}
