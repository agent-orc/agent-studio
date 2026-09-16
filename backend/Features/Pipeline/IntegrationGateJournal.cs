using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// One durable record of a merge that is currently sitting on the integration
/// branch without a gate verdict (AGT-2849).
///
/// <para>
/// The pre-develop build gate is the only step that publishes a commit before it
/// is verified: the merge is created first, and only the gate decides whether it
/// stays or is rolled back. A process that dies inside that window leaves an
/// un-gated merge on the integration branch with nobody left to judge it. This
/// entry is what the next process reads to find that merge again.
/// </para>
/// </summary>
public sealed record IntegrationGateJournalEntry
{
    public string Project { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;

    /// <summary>The registered project checkout, never the integration worktree: the worktree slot is recreated per integration.</summary>
    public string RepoRoot { get; init; } = string.Empty;

    public string IntegrationBranch { get; init; } = "develop";

    /// <summary>
    /// The exact rollback anchor observed before the merge ran. The local branch
    /// tip when it existed, otherwise the <c>origin/&lt;branch&gt;</c> tip the
    /// merge recreated the branch from.
    /// </summary>
    public string? PreMergeTip { get; init; }

    /// <summary>The merge result under gate, or null while the merge itself had not returned yet.</summary>
    public string? GatedSha { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Gate-evidence prefix the verdict for <see cref="GatedSha"/> is written under.</summary>
    public string Step { get; init; } = IntegrationGateJournal.PreDevelopBuildGateStep;
}

/// <summary>
/// Reads and writes the in-flight gate record of one job folder
/// (<c>post-steps/integration-gate.inflight.json</c>).
///
/// <para>
/// It lives next to the numbered gate-evidence logs because it answers the same
/// question in the other direction: the logs say which subject reached a
/// verdict, this file says which subject was promised one. An entry that is
/// still present when a process starts is by definition an interrupted run -
/// every path that reaches a verdict clears it, including the rollback path.
/// </para>
/// </summary>
public static class IntegrationGateJournal
{
    public const string PreDevelopBuildGateStep = "pre-develop-build-gate";
    private const string FileName = "integration-gate.inflight.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string jobFolderPath)
        => Path.Combine(jobFolderPath, "post-steps", FileName);

    /// <summary>
    /// Records that a gate is about to take responsibility for the integration
    /// branch. Best-effort: a journal that cannot be written must not refuse an
    /// integration the operator already accepted.
    /// </summary>
    public static void Open(string jobFolderPath, IntegrationGateJournalEntry entry)
        => Write(jobFolderPath, entry);

    /// <summary>
    /// Completes the entry with the merge result the gate will judge. No-op when
    /// no entry is open, so a caller never resurrects a cleared journal.
    /// </summary>
    public static void RecordMergeResult(string jobFolderPath, string? gatedSha, string? preMergeTip)
    {
        if (Read(jobFolderPath) is not { } entry) return;
        Write(jobFolderPath, entry with
        {
            GatedSha = string.IsNullOrWhiteSpace(gatedSha) ? entry.GatedSha : gatedSha,
            PreMergeTip = string.IsNullOrWhiteSpace(preMergeTip) ? entry.PreMergeTip : preMergeTip,
        });
    }

    public static IntegrationGateJournalEntry? Read(string jobFolderPath)
    {
        try
        {
            var path = PathFor(jobFolderPath);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<IntegrationGateJournalEntry>(
                File.ReadAllText(path),
                Json);
            return string.IsNullOrWhiteSpace(entry?.RepoRoot) ? null : entry;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationGateJournal: a corrupt in-flight record is ignored");
            return null;
        }
    }

    /// <summary>Clears the entry once the gate has reached a verdict, in either direction.</summary>
    public static void Clear(string jobFolderPath)
    {
        try
        {
            var path = PathFor(jobFolderPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationGateJournal: clearing the in-flight record is best-effort");
        }
    }

    private static void Write(string jobFolderPath, IntegrationGateJournalEntry entry)
    {
        try
        {
            var path = PathFor(jobFolderPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, Json));
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationGateJournal: writing the in-flight record is best-effort");
        }
    }
}
