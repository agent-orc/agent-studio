using System.Text;
using System.Text.Json;

namespace AgentStudio.Clients;

/// <summary>
/// Append-only JSONL audit log for permanent client-identity deletion (single
/// and bulk purge). Lives at <c>&lt;TaskRepository&gt;/identities/.audit/identity-deletions.jsonl</c>,
/// next to the identity files it describes.
///
/// Best-effort like <see cref="AgentStudio.Tasks.MergeAuditLog"/>: a write
/// failure is logged but never blocks the deletion, which already committed
/// to the identity store by the time this is called.
/// </summary>
public sealed class ClientIdentityAuditLog
{
    public const string AuditFolderName = ".audit";
    public const string FileName = "identity-deletions.jsonl";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ClientIdentityStore _store;
    private readonly ILogger<ClientIdentityAuditLog> _logger;
    private readonly object _gate = new();

    public ClientIdentityAuditLog(ClientIdentityStore store, ILogger<ClientIdentityAuditLog> logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool Append(ClientIdentityDeletionRecord record)
    {
        try
        {
            var dir = Path.Combine(_store.IdentitiesFolder, AuditFolderName);
            lock (_gate)
            {
                Directory.CreateDirectory(dir);
                var line = JsonSerializer.Serialize(record, WriteOpts) + Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, FileName), line, Encoding.UTF8);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClientIdentityAuditLog: failed to append record for id={Id}", record.Id);
            return false;
        }
    }
}

/// <summary>One row per permanently deleted client identity, single or purge-batch.</summary>
public sealed record ClientIdentityDeletionRecord(
    string Id,
    string DisplayName,
    string Actor,
    DateTime At,
    string Reason);
