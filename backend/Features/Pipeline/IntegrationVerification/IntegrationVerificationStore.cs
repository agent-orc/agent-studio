using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3002 - the card's latest answer to "did a gate pass on the tree that
/// carries this delivery". Written by the integration lane for every
/// successful integration and for every contained delivery it could not
/// verify; read by the integration projection, the accepted-integration
/// backstop, and the acceptance rail.
/// </summary>
public sealed record IntegrationVerificationRecord
{
    public int Version { get; init; } = 1;

    /// <summary>One of <see cref="IntegrationVerificationStates"/>.</summary>
    public string State { get; init; } = IntegrationVerificationStates.Unverified;

    /// <summary>Exact integration-branch SHA the verdict is about.</summary>
    public string? Sha { get; init; }

    public string IntegrationBranch { get; init; } = "";

    /// <summary>One of <see cref="IntegrationVerificationEvidence"/>.</summary>
    public string Evidence { get; init; } = IntegrationVerificationEvidence.None;

    /// <summary>Verdict of the gate behind <see cref="Evidence"/>, when a gate is behind it.</summary>
    public string? GateVerdict { get; init; }

    /// <summary>
    /// A gate reached a product verdict on this tree and it failed. The
    /// once-per-tree rule is spent: recovery returns the card to Human Review
    /// instead of running the gate again.
    /// </summary>
    public bool GateFailed { get; init; }

    public string Reason { get; init; } = "";

    public DateTimeOffset RecordedAtUtc { get; init; }

    public TaskIntegrationVerification ToProjection() => new()
    {
        State = State,
        Sha = Sha,
        Evidence = Evidence,
        GateVerdict = GateVerdict,
        GateFailed = GateFailed,
        Reason = Reason,
    };
}

/// <summary>
/// Reads and writes <see cref="IntegrationVerificationRecord"/> at the task
/// folder root, beside <c>pipeline-execution.json</c>. Writes are atomic
/// (temp file plus move); a missing or malformed file reads as "no record".
/// </summary>
public static class IntegrationVerificationStore
{
    public const string FileName = "integration-verification.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string taskFolder) => Path.Combine(taskFolder, FileName);

    public static void Write(string taskFolder, IntegrationVerificationRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(record);

        var path = PathFor(taskFolder);
        Directory.CreateDirectory(taskFolder);
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
                SilentCatch.Note(ex, "IntegrationVerificationStore: temporary file cleanup");
            }
        }
    }

    public static IntegrationVerificationRecord? Read(string taskFolder)
    {
        if (string.IsNullOrWhiteSpace(taskFolder)) return null;
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<IntegrationVerificationRecord>(
                File.ReadAllText(path), Json);
            return record is not null
                   && (record.State == IntegrationVerificationStates.Verified
                       || record.State == IntegrationVerificationStates.Unverified)
                ? record
                : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationVerificationStore: malformed or unreadable verification record");
            return null;
        }
    }
}
