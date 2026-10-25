using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Secrets;

namespace AgentStudio.Projects;

/// <summary>Persistent data requested by one repository-owned environment declaration.</summary>
public sealed record HostedDatabaseBinding(string SecretRef, string EnvironmentVariable);
public sealed record HostedVolumeDeclaration(string Name, string MountPath);
public sealed record HostedDataDeclaration(HostedDatabaseBinding? Database, IReadOnlyList<HostedVolumeDeclaration> Volumes)
{
    public static HostedDataDeclaration Empty { get; } = new(null, []);
    private static readonly Regex Name = new("^[a-z][a-z0-9-]{0,62}$", RegexOptions.Compiled);
    private static readonly Regex Variable = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex Mount = new("^/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public static HostedDataDeclaration Parse(JsonElement root, string template, string environment, string file)
    {
        if (!root.TryGetProperty("data", out var data)) return Empty;
        if (template != "container" || data.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Environment {file} data requires a container and an object.");
        if (data.EnumerateObject().Any(property => property.Name is not ("database" or "volumes")))
            throw new JsonException($"Environment {file} data has an unknown field.");

        HostedDatabaseBinding? database = null;
        if (data.TryGetProperty("database", out var db))
        {
            if (db.ValueKind != JsonValueKind.Object || db.EnumerateObject().Any(property => property.Name is not ("secretRef" or "environmentVariable"))
                || !db.TryGetProperty("secretRef", out var referenceValue) || referenceValue.ValueKind != JsonValueKind.String
                || !db.TryGetProperty("environmentVariable", out var variableValue) || variableValue.ValueKind != JsonValueKind.String)
                throw new JsonException($"Environment {file} database requires secretRef and environmentVariable.");
            var reference = referenceValue.GetString();
            var variable = variableValue.GetString();
            if (!SecretReference.TryParse(reference, out var parsed) || parsed.Scope.Kind != SecretScopeKind.Environment
                || parsed.Scope.Environment != environment || !Variable.IsMatch(variable ?? ""))
                throw new JsonException($"Environment {file} database must name a secret in this environment and a portable variable.");
            database = new(reference!, variable!);
        }

        var volumes = new List<HostedVolumeDeclaration>();
        if (data.TryGetProperty("volumes", out var volumeList))
        {
            if (volumeList.ValueKind != JsonValueKind.Array)
                throw new JsonException($"Environment {file} volumes must be an array.");
            foreach (var volume in volumeList.EnumerateArray())
            {
                if (volume.ValueKind != JsonValueKind.Object || volume.EnumerateObject().Any(property => property.Name is not ("name" or "mountPath"))
                    || !volume.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String
                    || !volume.TryGetProperty("mountPath", out var mountValue) || mountValue.ValueKind != JsonValueKind.String)
                    throw new JsonException($"Environment {file} volume requires name and mountPath.");
                var name = nameValue.GetString() ?? "";
                var mount = mountValue.GetString() ?? "";
                if (!Name.IsMatch(name) || !Mount.IsMatch(mount) || mount.Split('/').Any(segment => segment is "." or "..")
                    || volumes.Any(item => item.Name == name || item.MountPath == mount
                        || mount.StartsWith(item.MountPath + "/", StringComparison.Ordinal)
                        || item.MountPath.StartsWith(mount + "/", StringComparison.Ordinal)))
                    throw new JsonException($"Environment {file} volumes need unique, safe names and mount paths.");
                volumes.Add(new(name, mount));
            }
        }
        return new(database, volumes);
    }
}

/// <summary>Resolve the URI into the AGT-3222 workload allowlist without exposing a value.</summary>
public static class HostedDataBinding
{
    public static IReadOnlyList<string> DeclaredSecretNames(string project, string environment,
        IReadOnlyList<string> names, HostedDataDeclaration data)
    {
        if (data.Database is null) return names;
        var reference = SecretReference.Parse(data.Database.SecretRef);
        if (!string.Equals(reference.Scope.Project, project, StringComparison.OrdinalIgnoreCase)
            || reference.Scope.Environment != environment)
            throw new JsonException("Database secret reference must belong to this project and environment.");
        return names.Append(reference.Name).Distinct(StringComparer.Ordinal).ToArray();
    }
}
