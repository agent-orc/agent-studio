using System.Text;
using System.Text.Json;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Searches every timeline row without retaining the unbounded ledger.</summary>
internal static class OperatorCandidateAuditLogSearch
{
    private const int MaxRowBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // Null means an oversized row prevented a complete answer. Repair must not
    // append when it cannot prove the event absent.
    internal static bool? ContainsRecord(string path, string recordId)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunk = new byte[8192];
        var row = new byte[MaxRowBytes];
        var id = Encoding.UTF8.GetBytes(recordId);
        var length = 0;
        var oversized = false;
        var indeterminate = false;
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            foreach (var value in chunk.AsSpan(0, count))
            {
                if (value == (byte)'\n')
                {
                    if (oversized) indeterminate = true;
                    else if (Matches(row.AsSpan(0, length), id)) return true;
                    length = 0;
                    oversized = false;
                }
                else if (!oversized)
                {
                    if (length == row.Length) oversized = true;
                    else row[length++] = value;
                }
            }
        }
        if (oversized) indeterminate = true;
        else if (length > 0 && Matches(row.AsSpan(0, length), id)) return true;
        return indeterminate ? null : false;
    }

    private static bool Matches(ReadOnlySpan<byte> row, ReadOnlySpan<byte> id)
    {
        if (row.IndexOf(id) < 0) return false;
        try
        {
            var entry = JsonSerializer.Deserialize<TimelineEvent>(row, ReadOptions);
            return entry?.Kind == TimelineEventKinds.IntegrationVerificationRecorded
                && entry.Details?.GetValueOrDefault("recordId") == Encoding.UTF8.GetString(id);
        }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "Operator candidate audit lookup skips a malformed timeline row.");
            return false;
        }
    }
}
