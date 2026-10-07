using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Setup;

/// <summary>
/// The versioned installation manifest (D6 option A). It owns desired placement,
/// the release pin and identities by name. Secrets never enter it; principals
/// are recorded by name so a re-run can prove it preserved them.
/// </summary>
internal sealed record InstallationManifest(
    int Schema,
    string InstallationId,
    string Journey,
    string Mode,
    string Target,
    string ReleaseVersion,
    string Phase,
    IReadOnlyList<string> Principals,
    string? ProjectOrigin,
    string? FirstRunner,
    int? CodingSlots,
    int? ReviewSlots,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? RelocatedFromSet = null)
{
    public const int CurrentSchema = 1;
    public const string FileName = "installation.json";
    public const string CheckpointFileName = "checkpoints.jsonl";

    public const string PhaseInstalling = "installing";
    public const string PhaseUpdating = "updating";
    public const string PhaseComplete = "complete";
    public const string PhaseAwaitingAcceptance = "awaiting-acceptance";
    public const string PhaseUninstalled = "uninstalled";
}

internal sealed record ManifestRequest(
    string Journey,
    string Mode,
    string Target,
    string ReleaseVersion,
    string Verb,
    IReadOnlyList<string> Principals,
    string? ProjectOrigin = null,
    string? FirstRunner = null,
    int? CodingSlots = null,
    int? ReviewSlots = null);

internal enum ManifestAction
{
    Create,
    Resume,
    Reinstall,
    Update,
    Reject,
}

internal sealed record ManifestDecision(ManifestAction Action, InstallationManifest? Next, string? Reason);

/// <summary>Pure reconcile policy for a re-run against an existing manifest.</summary>
internal static class ManifestPolicy
{
    public static ManifestDecision Decide(
        InstallationManifest? existing,
        ManifestRequest request,
        Func<string> newId,
        DateTime now)
    {
        if (existing is null)
        {
            if (request.Verb != "install")
                return new(ManifestAction.Reject, null, "No installation manifest exists; run install first.");
            return new(ManifestAction.Create, new InstallationManifest(
                InstallationManifest.CurrentSchema, newId(), request.Journey, request.Mode, request.Target,
                request.ReleaseVersion, InstallationManifest.PhaseInstalling, request.Principals,
                request.ProjectOrigin, request.FirstRunner, request.CodingSlots, request.ReviewSlots,
                now, now), null);
        }

        if (existing.Schema > InstallationManifest.CurrentSchema)
            return new(ManifestAction.Reject, null,
                $"Installation manifest schema {existing.Schema} is newer than this setup ({InstallationManifest.CurrentSchema}). Use the matching or newer agent-studio-setup.");
        if (existing.Mode != request.Mode || existing.Target != request.Target)
            return new(ManifestAction.Reject, null,
                $"Installation {existing.InstallationId} is --mode {existing.Mode} --target {existing.Target}; a different role needs its own host or the relocate-authority journey.");

        var preserved = existing with
        {
            Principals = existing.Principals.Union(request.Principals, StringComparer.Ordinal).ToArray(),
            ProjectOrigin = existing.ProjectOrigin ?? request.ProjectOrigin,
            FirstRunner = existing.FirstRunner ?? request.FirstRunner,
            CodingSlots = request.CodingSlots ?? existing.CodingSlots,
            ReviewSlots = request.ReviewSlots ?? existing.ReviewSlots,
            UpdatedUtc = now,
        };
        if (request.ProjectOrigin is not null && existing.ProjectOrigin is not null
            && !string.Equals(existing.ProjectOrigin, request.ProjectOrigin, StringComparison.Ordinal))
            return new(ManifestAction.Reject, null,
                $"Installation {existing.InstallationId} owns project origin {existing.ProjectOrigin}; change it through the project registry, not the installer.");

        var comparison = CompareVersions(request.ReleaseVersion, existing.ReleaseVersion);
        if (request.Verb == "install")
        {
            if (existing.Phase == InstallationManifest.PhaseUpdating)
                return new(ManifestAction.Reject, null,
                    $"An interrupted update to {existing.ReleaseVersion} must be retried with update.");
            if (existing.Phase == InstallationManifest.PhaseInstalling)
            {
                return comparison == 0
                    ? new(ManifestAction.Resume, preserved, null)
                    : new(ManifestAction.Reject, null,
                        $"An interrupted install of {existing.ReleaseVersion} must be retried with the same release, not {request.ReleaseVersion}.");
            }
            if (existing.Phase == InstallationManifest.PhaseUninstalled)
            {
                return comparison < 0
                    ? new(ManifestAction.Reject, null,
                        $"The preserved data belongs to {existing.ReleaseVersion}; installing older {request.ReleaseVersion} over it is not supported.")
                    : new(ManifestAction.Reinstall, preserved with
                    {
                        ReleaseVersion = request.ReleaseVersion,
                        Phase = InstallationManifest.PhaseInstalling,
                    }, null);
            }
            return comparison == 0
                ? new(ManifestAction.Resume, preserved, null)
                : new(ManifestAction.Reject, null,
                    $"Installation {existing.InstallationId} runs {existing.ReleaseVersion}; use update or rollback to change it to {request.ReleaseVersion}.");
        }

        if (request.Verb == "update" && existing.Phase == InstallationManifest.PhaseUpdating)
            return comparison == 0
                ? new(ManifestAction.Resume, preserved, null)
                : new(ManifestAction.Reject, null,
                    $"An interrupted update to {existing.ReleaseVersion} must be retried with the same release.");
        if (request.Verb == "update" && existing.Phase == InstallationManifest.PhaseInstalling)
            return new(ManifestAction.Reject, null,
                $"Complete the interrupted install of {existing.ReleaseVersion} before updating.");
        if (request.Verb == "update" && comparison <= 0)
            return new(ManifestAction.Reject, null,
                $"update requires a newer release than {existing.ReleaseVersion}; use rollback for the retained previous release.");
        return new(ManifestAction.Update, preserved with
        {
            ReleaseVersion = request.ReleaseVersion,
            Phase = InstallationManifest.PhaseUpdating,
        }, null);
    }

    internal static int CompareVersions(string left, string right)
    {
        var a = ParseSemver(left);
        var b = ParseSemver(right);
        var core = a.Core.CompareTo(b.Core);
        if (core != 0) return core;
        if (a.Prerelease.Length == 0) return b.Prerelease.Length == 0 ? 0 : 1;
        if (b.Prerelease.Length == 0) return -1;
        for (var index = 0; index < Math.Min(a.Prerelease.Length, b.Prerelease.Length); index++)
        {
            var leftPart = a.Prerelease[index];
            var rightPart = b.Prerelease[index];
            var leftNumeric = long.TryParse(leftPart, out var leftNumber);
            var rightNumeric = long.TryParse(rightPart, out var rightNumber);
            var comparison = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1 : rightNumeric ? 1
                : string.CompareOrdinal(leftPart, rightPart);
            if (comparison != 0) return comparison;
        }
        return a.Prerelease.Length.CompareTo(b.Prerelease.Length);
    }

    private static (Version Core, string[] Prerelease) ParseSemver(string value)
    {
        var match = Regex.Match(value, @"^(?<core>\d+\.\d+\.\d+)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) throw new ArgumentException($"Invalid release version: {value}");
        return (Version.Parse(match.Groups["core"].Value),
            match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : []);
    }
}

internal static class ManifestStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<InstallationManifest?> ReadAsync(string root)
    {
        var path = Path.Combine(root, InstallationManifest.FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<InstallationManifest>(await File.ReadAllTextAsync(path))
            : null;
    }

    public static async Task WriteAsync(string root, InstallationManifest manifest)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, InstallationManifest.FileName);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Appends one checkpoint with provenance. Checkpoints are evidence of what
    /// this installer actually observed on this host, never a projected pass.
    /// </summary>
    public static async Task CheckpointAsync(string root, InstallationManifest manifest, string checkpoint,
        string outcome, string? detail = null)
    {
        Directory.CreateDirectory(root);
        var line = JsonSerializer.Serialize(new
        {
            checkpoint,
            outcome,
            detail,
            installationId = manifest.InstallationId,
            journey = manifest.Journey,
            releaseVersion = manifest.ReleaseVersion,
            setupVersion = ReleaseArtifacts.CurrentVersion(),
            host = Environment.MachineName,
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            atUtc = DateTime.UtcNow,
        });
        var path = Path.Combine(root, InstallationManifest.CheckpointFileName);
        await File.AppendAllTextAsync(path, line + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
