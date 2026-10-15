using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>Operator-supplied suite results for a published candidate tree.</summary>
public sealed record ExternalPublishedVerification(
    string CandidateSha, string IntegrationBranch, string VerificationHost,
    string SuiteSummary, string LogHash, DateTime RecordedAtUtc);

public static class ExternalPublishedVerificationStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string FileName = "external-published-verifications.json";

    public static IReadOnlyList<ExternalPublishedVerification> Read(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return [];
        var path = Path.Combine(projectFolder, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ExternalPublishedVerification>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ExternalPublishedVerificationStore: unreadable record");
            return [];
        }
    }

    public static bool Append(string projectFolder, ExternalPublishedVerification record)
    {
        lock (Gate)
        {
            var records = Read(projectFolder);
            if (records.Any(existing => existing.CandidateSha == record.CandidateSha
                && string.Equals(existing.IntegrationBranch, record.IntegrationBranch, StringComparison.OrdinalIgnoreCase)))
                return false;
            var path = Path.Combine(projectFolder, FileName);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(projectFolder);
                File.WriteAllText(temporary, JsonSerializer.Serialize(records.Append(record).ToList(), Json));
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
