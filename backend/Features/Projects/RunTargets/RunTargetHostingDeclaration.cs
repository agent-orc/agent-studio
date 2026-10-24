using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>
/// Boundary validation for the optional hosting fields of a container
/// environment whose destination is <c>edge</c>. The run-target host validates
/// the same values again before it writes a Caddyfile or compose file.
/// </summary>
public static partial class RunTargetHostingDeclaration
{
    [GeneratedRegex(@"\A(?=.{1,253}\z)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z")]
    public static partial Regex Domain();

    [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]{0,63}\z")]
    public static partial Regex VariableName();

    [GeneratedRegex(@"\A/[A-Za-z0-9._~/-]{0,255}\z")]
    public static partial Regex HealthPath();

    public static RunTargetHosting Parse(JsonElement value, string file, string? healthUrl,
        IReadOnlyList<string> secretRefs)
    {
        var domains = value.TryGetProperty("domains", out var domainValues)
            ? domainValues.EnumerateArray().Select(item => (item.GetString() ?? "").ToLowerInvariant()).ToArray()
            : [];
        if (domains.Any(item => !Domain().IsMatch(item)) || domains.Distinct(StringComparer.Ordinal).Count() != domains.Length)
            throw new JsonException($"Environment {file} has an invalid or repeated domain.");
        var edge = value.TryGetProperty("edge", out var edgeValue) ? edgeValue.GetString() ?? "" : "acme";
        if (!RunTargetVocabulary.EdgeModes.Contains(edge))
            throw new JsonException($"Environment {file} edge must be 'acme' or 'cloudflare'.");
        var healthPath = value.TryGetProperty("healthPath", out var pathValue)
            ? pathValue.GetString() ?? ""
            : healthUrl is not null && Uri.TryCreate(healthUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "/";
        if (!HealthPath().IsMatch(healthPath) || healthPath.Contains("..", StringComparison.Ordinal))
            throw new JsonException($"Environment {file} health path must be an absolute path without traversal.");
        // The host mounts each secret as a file and names it in NAME_FILE; a
        // plain variable must not shadow either name.
        var secretNames = secretRefs.Select(SecretEnvironmentName)
            .SelectMany(name => new[] { name, name + "_FILE" }).ToHashSet(StringComparer.Ordinal);
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty("variables", out var variableValues))
        {
            foreach (var property in variableValues.EnumerateObject())
            {
                var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : null;
                if (!VariableName().IsMatch(property.Name) || text is null || text.Length > 4096
                    || text.IndexOfAny(['\r', '\n', '\0']) >= 0 || secretNames.Contains(property.Name))
                    throw new JsonException($"Environment {file} variable {property.Name} is invalid or shadows a secret.");
                variables[property.Name] = text;
            }
        }
        var cpus = 1.0;
        var memoryMb = 512;
        var pids = 256;
        if (value.TryGetProperty("resources", out var resources))
        {
            if (resources.TryGetProperty("cpus", out var cpuValue)) cpus = cpuValue.GetDouble();
            if (resources.TryGetProperty("memoryMb", out var memoryValue)) memoryMb = memoryValue.GetInt32();
            if (resources.TryGetProperty("pidsLimit", out var pidsValue)) pids = pidsValue.GetInt32();
        }
        if (cpus is < 0.1 or > 64 || memoryMb is < 32 or > 262144 || pids is < 16 or > 32768)
            throw new JsonException($"Environment {file} resources are out of range.");
        return new(domains, edge, healthPath, variables,
            new(Math.Round(cpus, 2, MidpointRounding.AwayFromZero), memoryMb, pids));
    }

    /// <summary>Same mapping the AGT-3176 deploy job uses for a secret reference.</summary>
    public static string SecretEnvironmentName(string secretRef)
        => secretRef.Replace('-', '_').Replace('.', '_').ToUpper(CultureInfo.InvariantCulture);
}
