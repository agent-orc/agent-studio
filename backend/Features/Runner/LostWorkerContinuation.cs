using System.Text.Json;
namespace AgentStudio.Runner;

/// <summary>
/// The commit a continuation round starts its worktree from, saved on the card
/// so the next claim can hand it to the runner. Single use: the claim that reads
/// it clears it, because a later round of the same card must not silently start
/// on a stale generation's salvage. A review fix-round base lives until its run
/// completes (<see cref="ContinuationBaseStore.ConsumeForClaim"/>).
/// </summary>
/// <param name="Ref">Full <c>refs/heads/...</c> name of the salvage ref.</param>
public sealed record ContinuationBaseRecord(
    string Ref,
    string CommitSha,
    string Reason,
    string AttemptId,
    DateTime SavedAtUtc);

/// <summary>
/// File boundary for <see cref="ContinuationBaseRecord"/>
/// (<c>continuation-base.json</c> in the job folder). The file travels with the
/// folder through the lane move, exactly like <c>pending-intent.json</c>.
/// </summary>
public static class ContinuationBaseStore
{
    public const string FileName = "continuation-base.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool Save(string folderPath, ContinuationBaseRecord record)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            File.WriteAllText(
                Path.Combine(folderPath, FileName),
                JsonSerializer.Serialize(record, Json));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static ContinuationBaseRecord? Read(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, FileName);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<ContinuationBaseRecord>(File.ReadAllText(path), Json);
            return record is null
                   || string.IsNullOrWhiteSpace(record.Ref)
                   || string.IsNullOrWhiteSpace(record.CommitSha)
                ? null
                : record;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the record and removes it in one step.</summary>
    public static ContinuationBaseRecord? Consume(string folderPath)
    {
        var record = Read(folderPath);
        if (record is not null) Clear(folderPath);
        return record;
    }

    /// <summary>
    /// The claim path's read. Every base is single use and cleared by the claim
    /// that reads it, except a review fix round's (AGT-3056): a claim that fails
    /// before the agent starts must not leave the retry to begin silently on the
    /// integration branch, so that base stays until a run of the card completes
    /// (<see cref="ClearAfterCompletion"/>) or a later round replaces it.
    /// </summary>
    public static ContinuationBaseRecord? ConsumeForClaim(string folderPath)
    {
        var record = Read(folderPath);
        if (record is not null && !ReviewFixRoundBase.IsFixRound(record.Reason)) Clear(folderPath);
        return record;
    }

    /// <summary>
    /// Retires a review fix-round base once a run that started from it settled.
    /// A base of any other reason was already consumed by its claim, or belongs
    /// to a round this completion has just opened, and is left alone.
    /// </summary>
    public static void ClearAfterCompletion(string folderPath)
    {
        if (ReviewFixRoundBase.IsFixRound(Read(folderPath)?.Reason)) Clear(folderPath);
    }

    public static void Clear(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A stale base is bounded by its own verification: the runner starts
            // from the integration branch when the ref no longer resolves.
            SilentCatch.Note(exception, $"continuation base could not be cleared in {folderPath}");
        }
    }
}
