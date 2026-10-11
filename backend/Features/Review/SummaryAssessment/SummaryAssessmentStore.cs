using System.Text.Json;

namespace AgentStudio.Review;

/// <summary>
/// Reads and writes <c>summary-assessment.json</c> in the job folder. The file
/// is replaced together with status.md by every summary generation, so the
/// Result tab never shows an assessment of an older status.md.
/// </summary>
public static class SummaryAssessmentStore
{
    public const string FileName = "summary-assessment.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string PathFor(string jobFolder) => Path.Combine(jobFolder, FileName);

    public static void Write(string jobFolder, SummaryAssessment assessment)
    {
        var target = PathFor(jobFolder);
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(assessment, Options));
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Returns null when the file is absent or unreadable; a torn file never breaks the Result read.</summary>
    public static SummaryAssessment? Read(string jobFolder)
    {
        var path = PathFor(jobFolder);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SummaryAssessment>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
