using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Review;

/// <summary>
/// Reads and writes <c>summary-assessment.json</c> in the job folder. Every
/// write binds the assessment to the fingerprint of the status.md it was
/// generated with, and <see cref="ReadFor"/> serves it only while status.md
/// still matches. Other status.md writers (external completion, escalation
/// stubs, remote delivery failure, the result scaffold, acceptance
/// integration sections) therefore never show under an older assessment,
/// without each of them having to know about this file.
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

    /// <summary>
    /// Stable fingerprint of a status.md body. A byte-order mark, line-ending
    /// style and trailing whitespace do not change it; any content change does.
    /// </summary>
    public static string Fingerprint(string statusMarkdown)
    {
        var normalized = statusMarkdown.TrimStart('﻿').ReplaceLineEndings("\n").TrimEnd();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static void Write(string jobFolder, SummaryAssessment assessment, string statusMarkdown)
    {
        var target = PathFor(jobFolder);
        var temp = target + ".tmp";
        var bound = assessment with { StatusFingerprint = Fingerprint(statusMarkdown) };
        File.WriteAllText(temp, JsonSerializer.Serialize(bound, Options));
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Returns the assessment only when it was generated with exactly
    /// <paramref name="statusMarkdown"/>; null when status.md is absent, was
    /// replaced by another writer, or the file is absent or unreadable.
    /// </summary>
    public static SummaryAssessment? ReadFor(string jobFolder, string? statusMarkdown)
    {
        if (statusMarkdown is null) return null;
        var assessment = Read(jobFolder);
        if (assessment?.StatusFingerprint is not { } stored) return null;
        return string.Equals(stored, Fingerprint(statusMarkdown), StringComparison.Ordinal) ? assessment : null;
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
