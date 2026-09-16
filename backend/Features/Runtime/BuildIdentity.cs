using System.Reflection;
using System.Text.Json;

namespace AgentStudio.Runtime;

public sealed record ReleaseArtifactIdentity(
    string Name,
    string Version,
    string Tag,
    string Commit,
    string Integrity);

public sealed record BuildIdentity(
    int SchemaVersion,
    string Application,
    string Tag,
    string Version,
    string Commit,
    bool Dirty,
    DateTimeOffset? BuiltAt,
    string Integrity,
    ReleaseArtifactIdentity CodingAgentRunner,
    ReleaseArtifactIdentity CodingAgentChat,
    bool Legacy = false)
{
    public static BuildIdentity Load(IConfiguration configuration, Assembly? assembly = null)
    {
        assembly ??= typeof(BuildIdentity).Assembly;
        var configured = configuration["Release:BuildManifestPath"]
            ?? Environment.GetEnvironmentVariable("ATP_BUILD_MANIFEST");
        var beside = Path.Combine(AppContext.BaseDirectory, "build-manifest.json");
        // The Update Service points ATP_BUILD_MANIFEST at the run folder's
        // intended manifest so a backend it restarts reports the release being
        // installed rather than the one the build copied next to the assembly.
        // That path lives as long as the run folder, and the environment is
        // inherited by anything the backend later spawns, so a pointed-at file
        // that is gone falls back to the manifest beside the assembly instead
        // of degrading a released installation to the legacy identity.
        var path = string.IsNullOrWhiteSpace(configured) ? beside : Path.GetFullPath(configured);
        if (!File.Exists(path)) path = beside;

        if (File.Exists(path))
        {
            var manifest = JsonSerializer.Deserialize<BuildIdentity>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null) throw new InvalidDataException($"Build manifest is empty: {path}");
            Validate(manifest);
            return manifest;
        }

        // Explicit migration identity for pre-contract installations. It is
        // deliberately dirty/untagged and can never pass release preflight.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commit = Environment.GetEnvironmentVariable("ATP_DEPLOY_SHA")
            ?? informational?.Split('+', 2).ElementAtOrDefault(1)?.Split('.', 2)[0]
            ?? "unknown";
        var version = assembly.GetName().Version?.ToString(3) ?? "unknown";
        return new BuildIdentity(
            1, "Agent Studio", "untagged", version, commit, true, null,
            "unverified", Missing("CodingAgentRunner"), Missing("coding-agent-chat"), Legacy: true);
    }

    public static void Validate(BuildIdentity manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported build manifest schemaVersion.");
        Required(manifest.Application, "application");
        Required(manifest.Tag, "tag");
        Required(manifest.Version, "version");
        Required(manifest.Commit, "commit");
        Required(manifest.Integrity, "integrity");
        if (!string.Equals(manifest.Application, "Agent Studio", StringComparison.Ordinal))
            throw new InvalidDataException("Build manifest application must be Agent Studio.");
        if (!manifest.Legacy && !string.Equals(manifest.Tag, $"v{manifest.Version}", StringComparison.Ordinal))
            throw new InvalidDataException("Build manifest tag/version mismatch.");
        if (!manifest.Legacy && manifest.BuiltAt is null)
            throw new InvalidDataException("Build manifest is missing builtAt.");
        ValidateIntegrity(manifest.Integrity, "integrity", manifest.Legacy);
        ValidateArtifact(manifest.CodingAgentRunner, "codingAgentRunner", "CodingAgentRunner", manifest.Legacy);
        ValidateArtifact(manifest.CodingAgentChat, "codingAgentChat", "coding-agent-chat", manifest.Legacy);
    }

    private static ReleaseArtifactIdentity Missing(string name) =>
        new(name, "unknown", "untagged", "unknown", "unverified");

    private static void ValidateArtifact(ReleaseArtifactIdentity artifact, string field, string expectedName, bool legacy)
    {
        if (artifact is null) throw new InvalidDataException($"Build manifest is missing {field}.");
        Required(artifact.Name, $"{field}.name");
        Required(artifact.Version, $"{field}.version");
        Required(artifact.Tag, $"{field}.tag");
        Required(artifact.Commit, $"{field}.commit");
        Required(artifact.Integrity, $"{field}.integrity");
        if (!string.Equals(artifact.Name, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException($"Build manifest {field}.name mismatch.");
        if (!legacy && !string.Equals(artifact.Tag, $"v{artifact.Version}", StringComparison.Ordinal))
            throw new InvalidDataException($"Build manifest {field} tag/version mismatch.");
        ValidateIntegrity(artifact.Integrity, $"{field}.integrity", legacy);
    }

    private static void ValidateIntegrity(string value, string field, bool legacy)
    {
        if (legacy && value == "unverified") return;
        if (!value.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Build manifest {field} is invalid.");
    }

    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Build manifest is missing {field}.");
    }
}
