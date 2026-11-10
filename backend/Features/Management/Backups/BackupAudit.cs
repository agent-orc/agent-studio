using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Security;

namespace AgentStudio.Management;

/// <summary>
/// Backup records of the control plane audit chain (stream
/// <see cref="ControlPlaneAuditStreams.Backup"/>, AGT-3737). The actor is the
/// principal of a manual command, <see cref="Schedule"/> for the scheduler, and
/// <c>offline:&lt;account&gt;</c> for the offline tools. Every record carries
/// the trigger and a one-line <c>summary</c> in its details; the Access area
/// shows them to the Owner.
/// </summary>
public static partial class BackupAudit
{
    public const string Schedule = "schedule";
    public const string ManualPrefix = "manual:";
    /// <summary>Trigger of the offline tools of <see cref="StudioBackupCommand"/>.</summary>
    public const string Offline = "offline";

    public const string Create = "backup.create";
    public const string Upload = "backup.upload";
    public const string Verify = "backup.verify";
    public const string Retention = "backup.retention";
    public const string Decrypt = "backup.decrypt";
    public const string Restore = "backup.restore";

    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Denied = "denied";

    public static readonly IReadOnlyList<string> Operations = [Create, Upload, Verify, Retention, Decrypt, Restore];

    /// <summary>The audit actor of a backup trigger: <c>manual:&lt;actor&gt;</c> names the principal; any other trigger stands for itself.</summary>
    public static string Actor(string trigger)
        => trigger.StartsWith(ManualPrefix, StringComparison.Ordinal) && trigger.Length > ManualPrefix.Length
            ? trigger[ManualPrefix.Length..]
            : trigger;

    public static ControlPlaneAuditRecord Record(string trigger, string operation, string outcome, string? target,
        string summary, object? facts = null)
        => new(ControlPlaneAuditStreams.Backup, Actor(trigger), operation, outcome, target,
            Details: DetailsOf(trigger, summary, facts));

    /// <summary>A record of the offline tools: the actor is the account that ran them.</summary>
    public static ControlPlaneAuditRecord OfflineRecord(string actor, string operation, string outcome, string? target,
        string summary, object? facts = null)
        => new(ControlPlaneAuditStreams.Backup, actor, operation, outcome, target,
            Details: DetailsOf(Offline, summary, facts));

    // The writer serializes a plain object with the serializer defaults; camel case keeps
    // trigger, summary and the facts readable as the members the audit view looks up.
    private static JsonElement DetailsOf(string trigger, string summary, object? facts)
        => JsonSerializer.SerializeToElement(new BackupAuditDetails(trigger, summary, facts), JsonSerializerOptions.Web);

    /// <summary>
    /// Error text for a caller without installation-wide operate permission:
    /// absolute host paths become <c>&lt;path&gt;</c>, so the message keeps
    /// its cause without the layout of the host.
    /// </summary>
    public static string? WithoutHostPaths(string? text)
        => string.IsNullOrEmpty(text) ? text : WindowsPath().Replace(UnixPath().Replace(text, "<path>"), "<path>");

    // A slash that starts a path: not inside a word, a URL (scheme or host), or a relative path.
    [GeneratedRegex(@"(?<![\w.:/~\\-])/[^\s'"",;:()\[\]{}<>]+")]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"(?<![\w])[A-Za-z]:\\[^\s'"",;()\[\]{}<>]*")]
    private static partial Regex WindowsPath();
}

/// <summary>The details member of a backup audit record.</summary>
public sealed record BackupAuditDetails(string Trigger, string Summary, object? Facts);
