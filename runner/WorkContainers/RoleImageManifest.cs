using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.WorkContainers;

/// <summary>One role's pinned image and the digest it rolls back to.</summary>
public sealed record RoleImagePin(string Current, string? Previous, string? Release);

/// <summary>
/// Digest-pinned role images for coding, review and gate containers. The
/// release pipeline writes the current digest; promotion keeps the prior one
/// so the host can roll back without a registry lookup. Tags are rejected:
/// a mutable tag would let two attempts of the same release run different code.
/// </summary>
public sealed record RoleImageManifest(int SchemaVersion, Dictionary<string, RoleImagePin> Roles)
{
    public static readonly string[] KnownRoles = ["coding", "review", "gate"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // registry/name@sha256:<64 hex>, or a bare local image ID for an offline host.
    private static readonly Regex PinnedReference = new(
        "^(?:[a-z0-9][a-z0-9._/:-]*@)?sha256:[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsPinned(string? reference)
        => reference is not null && PinnedReference.IsMatch(reference);

    public static RoleImageManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<RoleImageManifest>(json, Json)
                       ?? throw new InvalidDataException("Role image manifest is empty.");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported role image manifest schema {manifest.SchemaVersion}.");
        foreach (var (role, pin) in manifest.Roles ?? throw new InvalidDataException("Role image manifest has no roles."))
        {
            if (!KnownRoles.Contains(role, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown work container role '{role}'.");
            if (!IsPinned(pin.Current))
                throw new InvalidDataException($"Role '{role}' image is not digest pinned.");
            if (pin.Previous is not null && !IsPinned(pin.Previous))
                throw new InvalidDataException($"Role '{role}' rollback image is not digest pinned.");
        }
        return manifest;
    }

    public static RoleImageManifest? TryLoad(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public string? Resolve(string role) => Roles.TryGetValue(role, out var pin) ? pin.Current : null;

    /// <summary>Install a newly released digest; the replaced digest becomes the rollback target.</summary>
    public RoleImageManifest Promote(string role, string image, string? release)
    {
        if (!KnownRoles.Contains(role, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown work container role '{role}'.");
        if (!IsPinned(image)) throw new ArgumentException("A promoted role image must be digest pinned.");
        var roles = new Dictionary<string, RoleImagePin>(Roles, StringComparer.Ordinal);
        roles[role] = roles.TryGetValue(role, out var old)
            ? old.Current == image ? old with { Release = release ?? old.Release } : new RoleImagePin(image, old.Current, release)
            : new RoleImagePin(image, null, release);
        return this with { Roles = roles };
    }

    /// <summary>Swap current and previous. A second rollback returns to the newer digest.</summary>
    public RoleImageManifest Rollback(string role)
    {
        if (!Roles.TryGetValue(role, out var pin) || pin.Previous is null)
            throw new InvalidOperationException($"Role '{role}' has no rollback image.");
        var roles = new Dictionary<string, RoleImagePin>(Roles, StringComparer.Ordinal)
        {
            [role] = new RoleImagePin(pin.Previous, pin.Current, null),
        };
        return this with { Roles = roles };
    }

    public static RoleImageManifest Empty() => new(1, new Dictionary<string, RoleImagePin>(StringComparer.Ordinal));
}
