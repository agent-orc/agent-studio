using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// A managed database the run zone provisions for a hosted app (AGT-3877,
/// IP-D5 option B). <see cref="Variable"/> names the connection string: the
/// app container gets <c>&lt;Variable&gt;_FILE</c>, the path of a tmpfs file
/// that holds it, like every other run-zone secret.
/// </summary>
public sealed record ProjectDatabaseDefinition(string Name, string Engine, string Version, string Size,
    string Variable, IReadOnlyList<string> Environments)
{
    /// <summary>The major version, the part a deploy may never change on existing data.</summary>
    public string Major => ProjectDatabaseVocabulary.MajorOf(Version);

    public bool AppliesTo(string environment)
        => Environments.Count == 0 || Environments.Contains(environment, StringComparer.Ordinal);
}

/// <summary>Container limits the run zone applies for one declared size.</summary>
public sealed record ProjectDatabaseResources(double Cpus, int MemoryMb, int PidsLimit);

/// <summary>
/// Keys and values of the <c>databases</c> section, shared by the reader, the
/// validator, Studio, and the JSON schema sync test.
/// </summary>
public static partial class ProjectDatabaseVocabulary
{
    public static readonly IReadOnlyList<string> Keys = ["name", "engine", "version", "size", "variable", "environments"];
    public static readonly IReadOnlyList<string> Engines = ["postgres"];
    public static readonly IReadOnlyList<string> Sizes = ["small", "medium", "large"];

    /// <summary>PostgreSQL majors the run zone provisions; each has an official <c>postgres:&lt;version&gt;-alpine</c> image.</summary>
    public static readonly IReadOnlyList<string> PostgresMajors = ["14", "15", "16", "17"];

    public const string DefaultSize = "small";
    public const string DefaultVariable = "DATABASE_URL";

    /// <summary>A PostgreSQL identifier that is also safe as a database, role and file name.</summary>
    public const string NamePattern = "^[a-z][a-z0-9_]{0,30}$";

    /// <summary>A major version, or a major and minor version such as <c>16.4</c>.</summary>
    public const string VersionPattern = @"^[1-9][0-9]?(?:\.(?:0|[1-9][0-9]{0,2}))?$";

    /// <summary>Secret names Studio generates for managed database credentials; a repository variable cannot use them.</summary>
    public const string CredentialPrefix = "MANAGED_DB_";

    [GeneratedRegex(NamePattern, RegexOptions.CultureInvariant)]
    public static partial Regex Name();

    [GeneratedRegex(VersionPattern, RegexOptions.CultureInvariant)]
    public static partial Regex Version();

    public static string MajorOf(string version)
    {
        var dot = version.IndexOf('.');
        return dot < 0 ? version : version[..dot];
    }

    /// <summary>The secret Studio generates for one database's password, for example <c>MANAGED_DB_ORDERS_PASSWORD</c>.</summary>
    public static string CredentialName(string database)
        => CredentialPrefix + database.ToUpper(CultureInfo.InvariantCulture) + "_PASSWORD";

    public static ProjectDatabaseResources ResourcesFor(string size) => size switch
    {
        "medium" => new(1.0, 1024, 512),
        "large" => new(2.0, 4096, 1024),
        _ => new(0.5, 512, 256),
    };
}

public static partial class ProjectDefinitionReader
{
    private static ProjectDatabaseDefinition[] ParseDatabases(IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
        => SequenceMaps(lines, "databases", 2, 4, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectDatabaseVocabulary.Keys, $"databases[{index}]", issues);
                return new ProjectDatabaseDefinition(Get(item, "name") ?? "", Get(item, "engine") ?? "",
                    Get(item, "version") ?? "", Get(item, "size") ?? ProjectDatabaseVocabulary.DefaultSize,
                    Get(item, "variable") ?? ProjectDatabaseVocabulary.DefaultVariable, List(item, "environments"));
            }).ToArray();
}

public static partial class ProjectDefinitionValidator
{
    private static void ValidateDatabases(IReadOnlyList<ProjectDatabaseDefinition> databases,
        IReadOnlyList<ProjectVariableDefinition> variables, HashSet<string> environments,
        List<ProjectDefinitionIssue> issues)
    {
        for (var i = 0; i < databases.Count; i++)
        {
            var database = databases[i];
            var path = $"databases[{i}]";
            if (!ProjectDatabaseVocabulary.Name().IsMatch(database.Name))
                issues.Add(new($"{path}.name", "database-name-invalid",
                    "Database names start with a lower-case letter and use lower-case letters, digits or underscores (up to 31)."));
            if (!ProjectDatabaseVocabulary.Engines.Contains(database.Engine, StringComparer.Ordinal))
                issues.Add(new($"{path}.engine", "database-engine-invalid",
                    $"Database engine must be one of {string.Join(", ", ProjectDatabaseVocabulary.Engines)}."));
            if (!ProjectDatabaseVocabulary.Version().IsMatch(database.Version))
                issues.Add(new($"{path}.version", "database-version-invalid",
                    "Database version is a major version or major.minor, such as 16 or 16.4."));
            else if (database.Engine == "postgres"
                     && !ProjectDatabaseVocabulary.PostgresMajors.Contains(database.Major, StringComparer.Ordinal))
                issues.Add(new($"{path}.version", "database-version-unsupported",
                    $"PostgreSQL major version must be one of {string.Join(", ", ProjectDatabaseVocabulary.PostgresMajors)}."));
            if (!ProjectDatabaseVocabulary.Sizes.Contains(database.Size, StringComparer.Ordinal))
                issues.Add(new($"{path}.size", "database-size-invalid",
                    $"Database size must be one of {string.Join(", ", ProjectDatabaseVocabulary.Sizes)}."));
            if (!EnvironmentName().IsMatch(database.Variable) || database.Variable.Any(char.IsLower))
                issues.Add(new($"{path}.variable", "database-variable-invalid",
                    "variable names the connection string with upper-case letters, digits and underscores, such as DATABASE_URL."));
            else if (ProjectDeploymentVocabulary.IsReservedVariableName(database.Variable)
                     || ProjectDeploymentVocabulary.IsReservedVariableName(database.Variable + "_FILE"))
                issues.Add(new($"{path}.variable", "database-variable-reserved",
                    $"Variable '{database.Variable}' controls the loader or shell on the host and cannot name a connection string."));
            if (database.Environments.Any(environment => !environments.Contains(environment)))
                issues.Add(new($"{path}.environments", "database-environment-unknown", "Database environments must be declared in environments."));
            if (database.Environments.Distinct(StringComparer.Ordinal).Count() != database.Environments.Count)
                issues.Add(new($"{path}.environments", "database-environment-duplicate", "Database environment scopes must be unique."));

            var scope = database.Environments.Count > 0 ? database.Environments : environments.ToArray();
            for (var j = 0; j < i; j++)
            {
                var earlier = databases[j];
                var shared = scope.Where(earlier.AppliesTo).ToArray();
                if (shared.Length == 0) continue;
                if (earlier.Name == database.Name)
                    issues.Add(new($"{path}.name", "database-duplicate",
                        $"Database '{database.Name}' is declared twice for {string.Join(", ", shared)}."));
                else if (earlier.Variable == database.Variable)
                    issues.Add(new($"{path}.variable", "database-variable-duplicate",
                        $"Databases '{earlier.Name}' and '{database.Name}' share the variable {database.Variable}; give each its own variable."));
            }
            // The app sees the connection string as VARIABLE_FILE; neither name may be another variable.
            var clash = variables.FirstOrDefault(variable => (variable.Name == database.Variable
                    || variable.Name == database.Variable + "_FILE")
                && (variable.Environments.Count == 0 || database.Environments.Count == 0
                    || variable.Environments.Any(database.AppliesTo)));
            if (clash is not null)
                issues.Add(new($"{path}.variable", "database-variable-shadowed",
                    $"variables declares {clash.Name}, which the connection string of '{database.Name}' would shadow."));
        }
        for (var i = 0; i < variables.Count; i++)
        {
            if (variables[i].Secret?.StartsWith(ProjectDatabaseVocabulary.CredentialPrefix, StringComparison.OrdinalIgnoreCase) == true)
                issues.Add(new($"variables[{i}].secret", "variable-secret-reserved",
                    $"Secret names starting with {ProjectDatabaseVocabulary.CredentialPrefix} belong to Studio's managed database credentials."));
        }
    }
}
