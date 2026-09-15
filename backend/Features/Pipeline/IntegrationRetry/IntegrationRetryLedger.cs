using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Durable bookkeeping for the bounded automatic integration retries of one
/// card (AGT-2824). <c>pipeline-execution.json</c> keeps a single row per step
/// id, so a replayed merge overwrites its predecessor and cannot carry a
/// counter. This sidecar is the counter, scoped to the reviewed delivery SHA so
/// a new delivery always starts with a full budget.
/// </summary>
public sealed record IntegrationRetryLedgerRecord
{
    public int Version { get; init; } = 1;
    /// <summary>Failure code the budget was opened for.</summary>
    public string FailureCode { get; init; } = "";
    /// <summary>Reviewed delivery the budget belongs to.</summary>
    public string DeliverySha { get; init; } = "";
    /// <summary>Automatic and operator attempts already spent on <see cref="DeliverySha"/>.</summary>
    public int Attempts { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    /// <summary>Trigger of the last attempt: <c>backstop</c> or <c>operator</c>.</summary>
    public string? LastAttemptTrigger { get; init; }
    /// <summary>True once the bounded budget is spent and the park reason is recorded.</summary>
    public bool Parked { get; init; }
    public DateTimeOffset? ParkedAtUtc { get; init; }
    public string? ParkedReason { get; init; }
}

public static class IntegrationRetryLedger
{
    public const string FileName = "integration-retry.json";
    public const string BackstopTrigger = "backstop";
    public const string OperatorTrigger = "operator";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);

    public static IntegrationRetryLedgerRecord? Read(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<IntegrationRetryLedgerRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationRetryLedger: malformed or unreadable ledger");
            return null;
        }
    }

    /// <summary>
    /// Records that an attempt is being spent. Returns the persisted record so
    /// the caller can log the attempt number it actually owns.
    /// </summary>
    public static IntegrationRetryLedgerRecord RecordAttempt(
        string taskFolder,
        string failureCode,
        string deliverySha,
        string trigger,
        DateTimeOffset attemptAtUtc)
    {
        var current = Read(taskFolder);
        var continues = current is not null
                        && string.Equals(current.DeliverySha, deliverySha, StringComparison.OrdinalIgnoreCase);
        var next = new IntegrationRetryLedgerRecord
        {
            FailureCode = failureCode,
            DeliverySha = deliverySha,
            Attempts = (continues ? current!.Attempts : 0) + 1,
            LastAttemptAtUtc = attemptAtUtc.ToUniversalTime(),
            LastAttemptTrigger = trigger,
        };
        Write(taskFolder, next);
        return next;
    }

    /// <summary>Marks the budget spent and stores the operator-facing park reason.</summary>
    public static IntegrationRetryLedgerRecord RecordPark(
        string taskFolder,
        string failureCode,
        string deliverySha,
        string reason,
        DateTimeOffset parkedAtUtc)
    {
        var current = Read(taskFolder);
        var continues = current is not null
                        && string.Equals(current.DeliverySha, deliverySha, StringComparison.OrdinalIgnoreCase);
        var next = new IntegrationRetryLedgerRecord
        {
            FailureCode = failureCode,
            DeliverySha = deliverySha,
            Attempts = continues ? current!.Attempts : 0,
            LastAttemptAtUtc = continues ? current!.LastAttemptAtUtc : null,
            LastAttemptTrigger = continues ? current!.LastAttemptTrigger : null,
            Parked = true,
            ParkedAtUtc = parkedAtUtc.ToUniversalTime(),
            ParkedReason = reason,
        };
        Write(taskFolder, next);
        return next;
    }

    /// <summary>
    /// Drops the ledger once the card no longer carries a gate environment
    /// failure - the delivery integrated, or the replay produced a different,
    /// decided outcome that this budget does not govern.
    /// </summary>
    public static void Clear(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationRetryLedger: best-effort ledger cleanup");
        }
    }

    public static void Write(string taskFolder, IntegrationRetryLedgerRecord record)
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
                SilentCatch.Note(ex, "IntegrationRetryLedger: temporary file cleanup");
            }
        }
    }
}
