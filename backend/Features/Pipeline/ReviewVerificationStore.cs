using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Classes of the Remote Review build/test aspect, mirrored from
/// <see cref="RemoteBuildTestGateClass"/> as durable strings so the record
/// survives an enum rename.
/// </summary>
public static class ReviewBuildTestGateClasses
{
    public const string Passed = "passed";
    public const string NotApplicable = "not-applicable";
    public const string Failed = "failed";
}

/// <summary>
/// What a settled Remote Review actually verified, recorded beside the task.
/// The local integration gate consumes it to tell "the merge result is exactly
/// the state the review just built and tested" apart from "the integration
/// branch moved underneath the review", instead of repeating the full suite on
/// every card (AGT-2839).
///
/// <para>
/// IntegrationTipSha records the ref fetched before review commands. TestedTreeSha
/// records the tree those commands tested. Legacy records missing either field
/// keep the full gate. MergeBaseSha remains review context, never tip identity.
/// </para>
/// </summary>
public sealed record ReviewVerificationRecord
{
    public int Version { get; init; } = 1;
    public string TaskKey { get; init; } = "";

    /// <summary>ReviewAttempt whose verdict this record makes reusable.</summary>
    public string AttemptId { get; init; } = "";
    public string SubjectId { get; init; } = "";

    /// <summary>Terminal review outcome, e.g. <c>Pass</c>.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>Immutable delivery SHA the review materialized and verified.</summary>
    public string ResultSha { get; init; } = "";

    /// <summary>Integration line the review resolved its baseline against.</summary>
    public string? IntegrationRef { get; init; }

    /// <summary>Merge base the review computed on <see cref="IntegrationRef"/>.</summary>
    public string? MergeBaseSha { get; init; }

    /// <summary>Integration tip captured before the verification commands.</summary>
    public string? IntegrationTipSha { get; init; }

    /// <summary>Exact Git tree tested by the review commands.</summary>
    public string? TestedTreeSha { get; init; }

    /// <summary>One of <see cref="ReviewBuildTestGateClasses"/>.</summary>
    public string BuildTestGate { get; init; } = ReviewBuildTestGateClasses.Failed;

    public DateTimeOffset VerifiedAtUtc { get; init; }
}

/// <summary>
/// Reads and writes <c>logs/review-verification.json</c> beside a task. Same
/// placement contract as <see cref="ReviewSubjectStore"/>: the record travels
/// with the task folder through every lane move, so the acceptance-retry merge
/// finds the same facts the immediate integration used.
/// </summary>
public static class ReviewVerificationStore
{
    public const string FileName = "review-verification.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);

    public static void Write(string taskFolder, ReviewVerificationRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(record);

        var path = PathFor(taskFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "ReviewVerificationStore: temporary file cleanup");
            }
        }
    }

    /// <summary>
    /// The record beside <paramref name="taskFolder"/>, or null when the task
    /// never had a settled Remote Review or the file is unreadable. A missing
    /// record is a normal state, not an error: the gate then runs in full.
    /// </summary>
    public static ReviewVerificationRecord? Read(string taskFolder)
    {
        if (string.IsNullOrWhiteSpace(taskFolder)) return null;
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReviewVerificationRecord>(
                File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ReviewVerificationStore: unreadable record falls back to the full gate");
            return null;
        }
    }
}
