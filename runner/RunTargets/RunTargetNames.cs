using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.RunTargets;

/// <summary>
/// Host-side naming and validation. Everything that reaches a compose file, a
/// Caddyfile, or a docker argument passes through here, whatever Studio sent.
/// </summary>
public static partial class RunTargetNames
{
    public const string EdgeContainer = "rt-edge";
    public const string EdgeNetwork = "rt-edge";

    [GeneratedRegex(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z")]
    private static partial Regex Digest();

    [GeneratedRegex(@"\A(?=.{1,253}\z)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z")]
    private static partial Regex Domain();

    [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]{0,63}\z")]
    private static partial Regex VariableName();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex SecretRef();

    [GeneratedRegex(@"\A/[A-Za-z0-9._~/-]{0,255}\z")]
    private static partial Regex HealthPath();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    /// <summary>
    /// Names the docker compose process or the container runtime reads from its
    /// own environment. A secret that maps to one of them would change how the
    /// host runs docker, so such a reference is refused.
    /// </summary>
    private static readonly HashSet<string> ReservedEnvironmentNames = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "SHELL", "TMPDIR", "LANG", "LD_PRELOAD", "LD_LIBRARY_PATH",
    };

    public static bool IsDigest(string? value) => value is not null && Digest().IsMatch(value);
    public static bool IsDomain(string value) => Domain().IsMatch(value);
    public static bool IsSecretRef(string value) => SecretRef().IsMatch(value);

    public static string SecretEnvironmentName(string secretRef)
        => secretRef.Replace('-', '_').Replace('.', '_').ToUpper(CultureInfo.InvariantCulture);

    /// <summary>Stable key of one project environment on this host.</summary>
    public static string AppKey(string project, string environmentId)
        => $"rt-{Slug(project, 20)}-{Slug(environmentId, 16)}-{Hash(project + "\n" + environmentId, 8)}";

    public static string AppNetwork(string appKey) => appKey + "-net";

    /// <summary>
    /// Content address of what runs in the container. Domains and edge mode are
    /// not part of it: they change routing only, never the app container.
    /// </summary>
    public static string Revision(RunTargetEnvironmentSpec spec)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            spec.Artifact, spec.ContainerPort, spec.HealthPath,
            spec.Variables.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new[] { item.Key, item.Value }),
            spec.SecretRefs.Order(StringComparer.Ordinal),
            spec.Resources,
        });
        return Hash(canonical, 12);
    }

    /// <summary>Returns the first reason the spec cannot be hosted, or null.</summary>
    public static string? Invalid(RunTargetEnvironmentSpec spec)
    {
        if (!IsDigest(spec.Artifact)) return "artifact must be pinned by sha256 digest";
        if (spec.ContainerPort is < 1 or > 65535) return "invalid container port";
        if (!HealthPath().IsMatch(spec.HealthPath) || spec.HealthPath.Contains("..", StringComparison.Ordinal))
            return "invalid health path";
        if (spec.Edge is not ("acme" or "cloudflare")) return "unknown edge mode";
        if (spec.Domains.Any(item => !IsDomain(item))) return "invalid domain";
        if (spec.Resources.Cpus is < 0.1 or > 64 || spec.Resources.MemoryMb is < 32 or > 262144
            || spec.Resources.PidsLimit is < 16 or > 32768) return "resources out of range";
        if (spec.Variables.Any(item => !VariableName().IsMatch(item.Key)
            || item.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)) return "invalid variable";
        if (spec.SecretRefs.Any(item => !IsSecretRef(item))) return "invalid secret reference";
        var secretNames = spec.SecretRefs.Select(SecretEnvironmentName).ToArray();
        if (secretNames.Any(name => !VariableName().IsMatch(name) || ReservedEnvironmentNames.Contains(name)
            || name.StartsWith("DOCKER_", StringComparison.Ordinal) || name.StartsWith("COMPOSE_", StringComparison.Ordinal)
            || spec.Variables.ContainsKey(name)) || secretNames.Distinct(StringComparer.Ordinal).Count() != secretNames.Length)
            return "secret reference maps to a reserved or repeated name";
        return null;
    }

    private static string Slug(string value, int max)
    {
        var slug = NotSlug().Replace(value.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > max) slug = slug[..max].TrimEnd('-');
        return slug.Length == 0 ? "x" : slug;
    }

    private static string Hash(string value, int length)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length].ToLowerInvariant();
}
