using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTaskboard.UpdateService.Installation;

/// <summary>
/// AGT-2947 (Dossier AGT-W63 D6 option A, item I06): one versioned
/// installation manifest. It records the desired and observed version of
/// every component of an installation (authority, engine, edge and runner
/// hosts), the single updater that owns each placement, the retained prior
/// release, the verified backup taken before the switch, and the upgrade
/// phase so an interrupted upgrade resumes instead of restarting blind.
///
/// The Docker lifecycle scripts write this file at
/// <c>/etc/agent-orchestrator/installation-manifest.json</c>; the shape is
/// documented in docs/operations/setup/installation-upgrade-contract.md.
/// </summary>
public sealed record InstallationManifest(
    int SchemaVersion,
    string InstallationId,
    string Placement,
    string Updater,
    InstallationRelease Desired,
    InstallationRelease? Observed,
    InstallationRelease? Prior,
    InstallationBackup? Backup,
    InstallationUpgradeProgress Progress,
    IReadOnlyList<InstallationHost> Hosts)
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static InstallationManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<InstallationManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Installation manifest is empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Installation manifest schema {manifest.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
        return manifest;
    }
}

/// <summary>
/// One release of the authority-side service set. Components share the
/// release version on the Compose placement; each keeps its own image digest.
/// </summary>
public sealed record InstallationRelease(
    string Version,
    IReadOnlyList<InstallationComponent> Components,
    int? StoreSchemaVersion = null,
    InstallationProtocolRange? Protocol = null,
    string? RuntimeMode = null,
    DateTimeOffset? ObservedAt = null);

/// <summary>Role is one of authority, engine or edge.</summary>
public sealed record InstallationComponent(string Role, string Image, string? ImageDigest);

public sealed record InstallationProtocolRange(int Current, int MinimumSupported, int MaximumSupported)
{
    public bool Covers(int version) => version >= MinimumSupported && version <= MaximumSupported;
}

public sealed record InstallationBackup(
    string BackupId,
    string Sha256,
    bool Verified,
    int? StoreSchemaVersion,
    DateTimeOffset CreatedAt);

/// <summary>
/// <see cref="Phase"/> is the last phase that completed for
/// <see cref="TargetVersion"/>. <see cref="Outcome"/> is <c>in-progress</c>
/// until a terminal verdict is written.
/// </summary>
public sealed record InstallationUpgradeProgress(
    string Operation,
    string TargetVersion,
    string Phase,
    string Outcome,
    string? Reason,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A runner host as seen by the authority. <see cref="State"/> is
/// <c>current</c>, <c>pending</c> (offline during the upgrade) or
/// <c>incompatible</c>.
/// </summary>
public sealed record InstallationHost(
    string HostId,
    string? RunnerVersion,
    int ProtocolVersion,
    bool Online,
    string State,
    DateTimeOffset? LastSeenAt);
