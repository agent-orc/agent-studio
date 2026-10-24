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

    public static bool IsDigest(string? value) => value is not null && Digest().IsMatch(value);
    public static bool IsDomain(string value) => Domain().IsMatch(value);
    public static bool IsSecretRef(string value) => SecretRef().IsMatch(value);

    public static string SecretEnvironmentName(string secretRef)
        => secretRef.Replace('-', '_').Replace('.', '_').ToUpper(CultureInfo.InvariantCulture);

    /// <summary>Variable that tells the app where its secret file is mounted, for example <c>DATABASE_URL_FILE</c>.</summary>
    public static string SecretFileVariable(string secretRef) => SecretEnvironmentName(secretRef) + "_FILE";

    /// <summary>
    /// Directory name of a project's own secret root, the same escaping Studio
    /// uses for its per-project files. Null for a name that has no safe directory.
    /// </summary>
    public static string? SecretScope(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || project.IndexOf('\0') >= 0) return null;
        var scope = Uri.EscapeDataString(project.ToLowerInvariant());
        return scope is "." or ".." ? null : scope;
    }

    /// <summary>
    /// Stable key of one project environment on this host. The slugs only make
    /// it readable; 80 hash bits keep it distinct. With the 12-character
    /// revision the compose project name stays within Docker's 63 characters.
    /// </summary>
    public static string AppKey(string project, string environmentId)
        => $"rt-{Slug(project, 14)}-{Slug(environmentId, 10)}-{Hash(project + "\n" + environmentId, 20)}";

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
    public static string? Invalid(RunTargetEnvironmentSpec spec, bool requireArtifact = true)
    {
        if (requireArtifact && !IsDigest(spec.Artifact)) return "artifact must be pinned by sha256 digest";
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
        if (SecretScope(spec.Project) is null) return "project has no secret root";
        var secretNames = spec.SecretRefs.Select(SecretFileVariable).ToArray();
        if (secretNames.Any(name => !VariableName().IsMatch(name) || spec.Variables.ContainsKey(name)
            || spec.Variables.ContainsKey(name[..^"_FILE".Length]))
            || secretNames.Distinct(StringComparer.Ordinal).Count() != secretNames.Length)
            return "secret reference maps to a variable name that is invalid, shadowed or repeated";
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
