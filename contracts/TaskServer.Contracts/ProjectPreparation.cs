using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public static class ProjectPreparationPaths
{
    public const string Definition = ".agent-studio/project.yml";
    public const string Script = ".agent-studio/prepare";
    public const string ManifestFileName = "preparation-manifest.json";

    /// <summary>Operator override for the product-owned cache root.</summary>
    public const string CacheRootVariable = "AGENT_STUDIO_CACHE_ROOT";

    /// <summary>
    /// Cache root of a service-managed runner host, owned by the runner service
    /// account and outside any temp filesystem.
    /// </summary>
    public const string ServiceCacheRoot = "/var/lib/agent-runner/cache";

    /// <summary>
    /// Where a preparation cache lives when no caller hands one in (AGT-2858).
    /// The temp root is deliberately not a candidate: a cache there has no
    /// owner, no bound, and is a legitimate target for every <c>/tmp</c> sweep on
    /// the host - which is how the M1 pilot cache reached 36 GB and then had to
    /// be deleted by hand.
    ///
    /// Resolution order: the operator's <see cref="CacheRootVariable"/>, then the
    /// service cache root when this host has one, then the per-user application
    /// data directory (<c>~/.local/share</c>, <c>%LOCALAPPDATA%</c>).
    /// </summary>
    public static string DefaultCacheRoot(Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var configured = readEnvironment(CacheRootVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());

        if (!OperatingSystem.IsWindows() && IsWritableDirectory(ServiceCacheRoot))
            return ServiceCacheRoot;

        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(localData, "agent-studio", "cache");
    }

    /// <summary>
    /// The cache root of one project below <see cref="DefaultCacheRoot"/>. The
    /// project segment keeps one project's eviction from touching another's.
    /// </summary>
    public static string ProjectCacheRoot(string projectId, Func<string, string?>? readEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var safe = new string(projectId
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());
        return Path.Combine(DefaultCacheRoot(readEnvironment), safe);
    }

    private static bool IsWritableDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            var probe = Path.Combine(path, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Host environment variables the preparation gate copies into the prepare
/// process. The portable list is the cross-platform minimum. Windows needs its
/// base variables on top, because MSBuild, NuGet, and the .NET SDK resolve user
/// and machine paths through them; without them NuGet.targets fails with
/// "Value cannot be null. (Parameter path1)". Neither list carries a secret.
/// </summary>
public static class PreparationHostEnvironment
{
    public static readonly IReadOnlyList<string> PortableKeys =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "TMPDIR",
        "TEMP",
        "TMP",
        "LANG",
        "LC_ALL",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
    ];

    public static readonly IReadOnlyList<string> WindowsKeys =
    [
        "SystemRoot",
        "windir",
        "ComSpec",
        "PATHEXT",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "APPDATA",
        "LOCALAPPDATA",
        "HOMEDRIVE",
        "HOMEPATH",
        "USERNAME",
        "COMPUTERNAME",
    ];

    public static IReadOnlyList<string> KeysFor(bool windows) =>
        windows ? [.. PortableKeys, .. WindowsKeys] : PortableKeys;
}

/// <summary>
/// How the gate starts the repository prepare script, or why it cannot.
/// </summary>
public sealed record PreparationEntryPoint(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? FailureSignature = null,
    string? FailureReason = null)
{
    public bool Resolved => FailureSignature is null;
}

/// <summary>
/// Pure entry-point decision for the repository prepare script. POSIX hosts run
/// the script through <c>/bin/sh</c>. Windows prefers a repository-owned
/// <c>prepare.ps1</c>, otherwise runs the extensionless POSIX script through Git
/// Bash, because <c>powershell -File</c> refuses a file without a PowerShell
/// extension. A Windows host with neither entry point fails with a named reason
/// instead of an opaque exit code.
/// </summary>
public static class PreparationEntryPointPolicy
{
    public const string WindowsEntryMissing = "prepare:windows-entry-missing";

    public static readonly IReadOnlyList<string> PowerShellArguments =
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    public static PreparationEntryPoint Resolve(
        string scriptPath,
        bool windows,
        Func<string, bool> fileExists,
        Func<string?> gitBash)
    {
        if (!windows) return new("/bin/sh", [scriptPath]);

        var powerShellScript = scriptPath + ".ps1";
        if (fileExists(powerShellScript))
            return new("powershell.exe", [.. PowerShellArguments, powerShellScript]);

        if (gitBash() is { } bash && !string.IsNullOrWhiteSpace(bash))
            return new(bash, ["-e", scriptPath.Replace('\\', '/')]);

        return new(
            string.Empty,
            [],
            WindowsEntryMissing,
            $"Windows preparation needs either '{Path.GetFileName(powerShellScript)}' next to the prepare script "
            + $"or Git Bash (bash.exe) to run '{Path.GetFileName(scriptPath)}'. Neither was found on this host.");
    }
}

public sealed record ProjectCommandSet(
    string Prepare,
    IReadOnlyList<string> Build,
    IReadOnlyList<string> Test,
    IReadOnlyList<string> Lint);

public sealed record ProjectTestSuite(
    string Id,
    string Category,
    int ExpectedDurationSeconds,
    string Command);

public sealed record ProjectDevServerRule(
    string Command,
    string? WorkingDirectory,
    string? HealthUrl,
    int StartupTimeoutSeconds,
    bool StopWithRun);

public sealed record ProjectReleaseIdentityRule(
    string Package,
    string Ecosystem,
    string? Source,
    string? Version,
    string? Integrity)
{
    public bool UsesLockFile => !string.IsNullOrWhiteSpace(Source);
}

public sealed record ProjectReleaseDefinition(
    IReadOnlyList<ProjectReleaseIdentityRule> Identity,
    IReadOnlyList<string> Restore);

public sealed record ProductProperties(
    bool? PublicFacing,
    bool? HtmlUi,
    bool? SeoRelevant,
    bool? PaymentApi,
    bool? PersonalData,
    bool? Authenticated,
    bool? PersistentData,
    bool? Realtime,
    bool? Localized,
    bool? Deployable);

public sealed record ExecutionReference(string Path, string? Pointer);

public sealed record ExecutionReferences(
    IReadOnlyList<ExecutionReference>? Build,
    IReadOnlyList<ExecutionReference>? Test,
    IReadOnlyList<ExecutionReference>? Lint,
    IReadOnlyList<ExecutionReference>? Start);

public sealed record ProjectComponent(
    string Id,
    string Path,
    string? Name,
    ProductProperties? Properties,
    ExecutionReferences? ExecutionRefs);

public sealed record PropertySelector(
    IReadOnlyList<string>? AllOf,
    IReadOnlyList<string>? AnyOf,
    IReadOnlyList<string>? NoneOf);

public sealed record QualityApplicability(
    string Id,
    IReadOnlyList<string> ComponentScope,
    PropertySelector? Selector,
    IReadOnlyList<string>? RuleIds,
    IReadOnlyList<string>? Domains);

public sealed record ProjectDefinition(
    string Id,
    string? Name,
    ProductProperties? Properties,
    IReadOnlyList<ProjectComponent> Components);

public sealed record QualityDefinition(
    IReadOnlyList<QualityApplicability> Applicability);

public sealed record ProjectExecutionDefinition(
    int SchemaVersion,
    IReadOnlyList<string> Stack,
    IReadOnlyDictionary<string, string> ToolVersions,
    ProjectCommandSet Commands,
    IReadOnlyList<ProjectTestSuite> TestSuites,
    IReadOnlyList<string> CachePaths,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string> Environment,
    ProjectDevServerRule? DevServer,
    string? Image,
    ProjectReleaseDefinition? Release,
    ProjectDefinition? Project,
    QualityDefinition? Quality);

public sealed record ProjectDefinitionIssue(string Path, string Code, string Message);

public sealed record ProjectDefinitionReadResult(
    ProjectExecutionDefinition? Definition,
    IReadOnlyList<ProjectDefinitionIssue> Issues,
    string? DefinitionSha256)
{
    public bool IsValid => Definition is not null && Issues.Count == 0;
}

/// <summary>
/// Reads the deliberately bounded YAML subset emitted by Agent Studio's project
/// generator. The format permits scalar maps, inline string lists, indented
/// string lists, and a sequence of test-suite maps. YAML anchors, tags, and
/// executable values are rejected instead of being interpreted.
/// </summary>
public static partial class ProjectDefinitionReader
{
    public static ProjectDefinitionReadResult ReadWorkspace(string workspace)
    {
        var path = Path.Combine(workspace, ProjectPreparationPaths.Definition.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return new(null,
                [new(ProjectPreparationPaths.Definition, "definition-missing", "Repository execution definition is missing.")],
                null);
        try
        {
            return Parse(File.ReadAllText(path), workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null,
                [new(ProjectPreparationPaths.Definition, "definition-unreadable", ex.Message)],
                null);
        }
    }

    public static ProjectDefinitionReadResult Parse(string yaml, string? workspace = null)
    {
        var issues = new List<ProjectDefinitionIssue>();
        if (string.IsNullOrWhiteSpace(yaml))
            return new(null, [new("$", "definition-empty", "Repository execution definition is empty.")], null);
        if (Regex.IsMatch(yaml, @"(^|[\s:\[,])(?:&|\*)[A-Za-z0-9_-]+", RegexOptions.Multiline)
            || yaml.Contains("!!", StringComparison.Ordinal))
            issues.Add(new("$", "yaml-feature-unsupported", "YAML anchors, aliases, and tags are not supported."));

        var lines = Tokenize(yaml, issues);
        var knownTopLevel = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "stack", "toolVersions", "commands", "testSuites",
            "cachePaths", "capabilities", "environment", "devServer", "image", "release",
            "project", "quality",
        };
        foreach (var line in lines.Where(line => line.Indent == 0))
        {
            var key = KeyValue(line.Text).Key;
            if (!knownTopLevel.Contains(key))
                issues.Add(new(key, "unknown-field", $"Top-level field '{key}' is not supported."));
        }
        var commandFields = Section(lines, "commands")
            .Where(line => line.Indent == 2 && !line.Text.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => KeyValue(line.Text).Key);
        if (commandFields.Any(key => key is not ("prepare" or "build" or "test" or "lint")))
            issues.Add(new("commands", "unknown-field", "Commands contain an unsupported field."));
        var schemaVersion = Scalar(lines, "schemaVersion");
        var stack = StringList(lines, "stack");
        var tools = StringMap(lines, "toolVersions");
        var commandPrepare = NestedScalar(lines, "commands", "prepare") ?? string.Empty;
        var commands = new ProjectCommandSet(
            commandPrepare,
            NestedStringList(lines, "commands", "build"),
            NestedStringList(lines, "commands", "test"),
            NestedStringList(lines, "commands", "lint"));
        var suites = TestSuites(lines, issues);
        var caches = StringList(lines, "cachePaths");
        var capabilities = StringList(lines, "capabilities");
        var environment = StringMap(lines, "environment");
        var image = Scalar(lines, "image");
        var devServer = DevServer(lines, issues);
        var release = Release(lines, issues);
        var project = Project(lines, issues);
        var quality = Quality(lines, issues);

        if (!int.TryParse(schemaVersion, out var version)) version = 0;
        var definition = new ProjectExecutionDefinition(
            version, stack, tools, commands, suites, caches, capabilities,
            environment, devServer, NullIfBlank(image), release, project, quality);
        issues.AddRange(ProjectDefinitionValidator.Validate(definition, workspace));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)))
            .ToLowerInvariant();
        return new(issues.Count == 0 ? definition : null, issues, digest);
    }

    private static IReadOnlyList<YamlLine> Tokenize(string yaml, List<ProjectDefinitionIssue> issues)
    {
        var result = new List<YamlLine>();
        var source = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < source.Length; index++)
        {
            var raw = source[index];
            if (raw.Contains('\t'))
            {
                issues.Add(new($"line:{index + 1}", "yaml-tab", "Use spaces for YAML indentation."));
                continue;
            }
            var trimmed = StripComment(raw).TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.TrimStart().StartsWith("---", StringComparison.Ordinal))
                continue;
            var indent = trimmed.Length - trimmed.TrimStart().Length;
            if (indent % 2 != 0)
                issues.Add(new($"line:{index + 1}", "yaml-indent", "Indentation must use multiples of two spaces."));
            result.Add(new YamlLine(index + 1, indent, trimmed.TrimStart()));
        }
        return result;
    }

    private static string StripComment(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (quote == '\0' && ch is '\'' or '"') quote = ch;
            else if (quote == ch) quote = '\0';
            else if (quote == '\0' && ch == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                return value[..index];
        }
        return value;
    }

    private static string? Scalar(IReadOnlyList<YamlLine> lines, string key)
        => lines.FirstOrDefault(line => line.Indent == 0 && KeyValue(line.Text).Key == key) is { } line
            ? Value(KeyValue(line.Text).Value)
            : null;

    private static string? NestedScalar(IReadOnlyList<YamlLine> lines, string section, string key)
    {
        var range = Section(lines, section);
        return range.FirstOrDefault(line => line.Indent == 2 && KeyValue(line.Text).Key == key) is { } line
            ? Value(KeyValue(line.Text).Value)
            : null;
    }

    private static IReadOnlyList<string> StringList(IReadOnlyList<YamlLine> lines, string key)
    {
        var top = lines.FirstOrDefault(line => line.Indent == 0 && KeyValue(line.Text).Key == key);
        if (top is null) return [];
        var inline = Value(KeyValue(top.Text).Value);
        if (!string.IsNullOrWhiteSpace(inline)) return ParseInlineList(inline);
        return Section(lines, key)
            .Where(line => line.Indent == 2 && line.Text.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => Value(line.Text[2..]))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static IReadOnlyList<string> NestedStringList(
        IReadOnlyList<YamlLine> lines,
        string section,
        string key)
    {
        var parent = Section(lines, section);
        var start = parent.FirstOrDefault(line => line.Indent == 2 && KeyValue(line.Text).Key == key);
        if (start is null) return [];
        var inline = Value(KeyValue(start.Text).Value);
        if (!string.IsNullOrWhiteSpace(inline)) return ParseInlineList(inline);
        var index = lines.IndexOf(start);
        return lines.Skip(index + 1)
            .TakeWhile(line => line.Indent > 2)
            .Where(line => line.Indent == 4 && line.Text.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => Value(line.Text[2..]))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> StringMap(IReadOnlyList<YamlLine> lines, string section)
        => Section(lines, section)
            .Where(line => line.Indent == 2 && !line.Text.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => KeyValue(line.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => Value(item.Value), StringComparer.Ordinal);

    private static IReadOnlyList<ProjectTestSuite> TestSuites(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var section = Section(lines, "testSuites");
        var suites = new List<ProjectTestSuite>();
        Dictionary<string, string>? current = null;
        foreach (var line in section)
        {
            if (line.Indent == 2 && line.Text.StartsWith("- ", StringComparison.Ordinal))
            {
                if (current is not null) suites.Add(ToSuite(current, suites.Count, issues));
                current = new(StringComparer.Ordinal);
                var pair = KeyValue(line.Text[2..]);
                if (!string.IsNullOrWhiteSpace(pair.Key)) current[pair.Key] = Value(pair.Value);
            }
            else if (line.Indent == 4 && current is not null)
            {
                var pair = KeyValue(line.Text);
                current[pair.Key] = Value(pair.Value);
            }
        }
        if (current is not null) suites.Add(ToSuite(current, suites.Count, issues));
        return suites;
    }

    private static ProjectTestSuite ToSuite(
        IReadOnlyDictionary<string, string> fields,
        int index,
        List<ProjectDefinitionIssue> issues)
    {
        fields.TryGetValue("expectedDurationSeconds", out var durationValue);
        if (!int.TryParse(durationValue, out var duration)) duration = 0;
        fields.TryGetValue("id", out var id);
        fields.TryGetValue("category", out var category);
        fields.TryGetValue("command", out var command);
        if (fields.Keys.Any(key => key is not ("id" or "category" or "expectedDurationSeconds" or "command")))
            issues.Add(new($"testSuites[{index}]", "unknown-field", "Test suites contain an unsupported field."));
        return new(id ?? string.Empty, category ?? string.Empty, duration, command ?? string.Empty);
    }

    private static ProjectDevServerRule? DevServer(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var values = StringMap(lines, "devServer");
        if (values.Count == 0) return null;
        values.TryGetValue("command", out var command);
        values.TryGetValue("workingDirectory", out var workingDirectory);
        values.TryGetValue("healthUrl", out var healthUrl);
        values.TryGetValue("startupTimeoutSeconds", out var timeoutValue);
        values.TryGetValue("stopWithRun", out var stopValue);
        var timeout = int.TryParse(timeoutValue, out var parsedTimeout) ? parsedTimeout : 0;
        var stop = bool.TryParse(stopValue, out var parsedStop) && parsedStop;
        if (values.Keys.Any(key => key is not ("command" or "workingDirectory" or "healthUrl" or "startupTimeoutSeconds" or "stopWithRun")))
            issues.Add(new("devServer", "unknown-field", "Dev server rules contain an unsupported field."));
        return new(command ?? string.Empty, NullIfBlank(workingDirectory), NullIfBlank(healthUrl), timeout, stop);
    }

    private static ProjectReleaseDefinition? Release(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var declared = lines.Any(line =>
            line.Indent == 0 && KeyValue(line.Text).Key == "release");
        if (!declared) return null;
        var section = Section(lines, "release");

        var fields = section
            .Where(line => line.Indent == 2 && !line.Text.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => KeyValue(line.Text).Key)
            .ToArray();
        if (fields.Any(key => key is not ("identity" or "restore")))
            issues.Add(new("release", "unknown-field", "Release contains an unsupported field."));

        var identities = new List<ProjectReleaseIdentityRule>();
        Dictionary<string, string>? current = null;
        var identityStart = section.FirstOrDefault(line =>
            line.Indent == 2 && KeyValue(line.Text).Key == "identity");
        if (identityStart is not null)
        {
            var start = lines.IndexOf(identityStart);
            foreach (var line in lines.Skip(start + 1).TakeWhile(line => line.Indent > 2))
            {
                if (line.Indent == 4 && line.Text.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (current is not null) identities.Add(ToReleaseIdentity(current, identities.Count, issues));
                    current = new(StringComparer.Ordinal);
                    var pair = KeyValue(line.Text[2..]);
                    if (!string.IsNullOrWhiteSpace(pair.Key)) current[pair.Key] = Value(pair.Value);
                }
                else if (line.Indent == 6 && current is not null)
                {
                    var pair = KeyValue(line.Text);
                    current[pair.Key] = Value(pair.Value);
                }
            }
            if (current is not null) identities.Add(ToReleaseIdentity(current, identities.Count, issues));
        }

        var restore = NestedStringList(lines, "release", "restore");
        return new(identities, restore);
    }

    private static ProjectReleaseIdentityRule ToReleaseIdentity(
        IReadOnlyDictionary<string, string> fields,
        int index,
        List<ProjectDefinitionIssue> issues)
    {
        if (fields.Keys.Any(key => key is not ("package" or "ecosystem" or "source" or "version" or "integrity")))
            issues.Add(new($"release.identity[{index}]", "unknown-field", "Release identity contains an unsupported field."));
        fields.TryGetValue("package", out var package);
        fields.TryGetValue("ecosystem", out var ecosystem);
        fields.TryGetValue("source", out var source);
        fields.TryGetValue("version", out var version);
        fields.TryGetValue("integrity", out var integrity);
        return new(package ?? string.Empty, ecosystem ?? string.Empty, NullIfBlank(source),
            NullIfBlank(version), NullIfBlank(integrity));
    }

    private static ProjectDefinition? Project(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var declared = lines.Any(line =>
            line.Indent == 0 && KeyValue(line.Text).Key == "project");
        if (!declared) return null;
        var section = Section(lines, "project");
        if (section.Count == 0) return null;

        var id = NestedScalar(lines, "project", "id");
        var name = NestedScalar(lines, "project", "name");
        var properties = ProjectProperties(lines, "project", issues);
        var components = ProjectComponents(lines, issues);

        if (string.IsNullOrWhiteSpace(id))
            issues.Add(new("project.id", "project-id-required", "Project id is required and must be a valid identifier."));

        return new(id ?? string.Empty, NullIfBlank(name), properties, components);
    }

    private static ProductProperties? ProjectProperties(
        IReadOnlyList<YamlLine> lines,
        string section,
        List<ProjectDefinitionIssue> issues)
    {
        var parent = Section(lines, section);
        var propStart = parent.FirstOrDefault(line =>
            line.Indent == 2 && KeyValue(line.Text).Key == "properties");
        if (propStart is null) return null;

        var allProperties = new Dictionary<string, bool>(StringComparer.Ordinal);
        var index = lines.IndexOf(propStart);
        foreach (var line in lines.Skip(index + 1).TakeWhile(line => line.Indent > 2))
        {
            if (line.Indent == 4 && !line.Text.StartsWith("- ", StringComparison.Ordinal))
            {
                var (key, value) = KeyValue(line.Text);
                if (!string.IsNullOrWhiteSpace(key) && bool.TryParse(value, out var boolValue))
                    allProperties[key] = boolValue;
            }
        }

        return new(
            Get("public-facing"),
            Get("html-ui"),
            Get("seo-relevant"),
            Get("payment-api"),
            Get("personal-data"),
            Get("authenticated"),
            Get("persistent-data"),
            Get("realtime"),
            Get("localized"),
            Get("deployable"));

        bool? Get(string key) => allProperties.TryGetValue(key, out var val) ? val : null;
    }

    private static IReadOnlyList<ProjectComponent> ProjectComponents(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var project = Section(lines, "project");
        var componentStart = project.FirstOrDefault(line =>
            line.Indent == 2 && KeyValue(line.Text).Key == "components");
        if (componentStart is null) return [];

        var components = new List<ProjectComponent>();
        var startIndex = lines.IndexOf(componentStart);
        int? currentComponentStart = null;

        for (var i = startIndex + 1; i < lines.Count && lines[i].Indent > 2; i++)
        {
            var line = lines[i];
            if (line.Indent == 4 && line.Text.StartsWith("- ", StringComparison.Ordinal))
            {
                if (currentComponentStart.HasValue)
                    components.Add(ParseComponent(lines, currentComponentStart.Value, i, issues));
                currentComponentStart = i;
            }
        }
        if (currentComponentStart.HasValue)
            components.Add(ParseComponent(lines, currentComponentStart.Value,
                lines.TakeWhile((_, idx) => idx == 0).Count(), issues));

        return components;
    }

    private static ProjectComponent ParseComponent(
        IReadOnlyList<YamlLine> lines,
        int startIndex,
        int endIndex,
        List<ProjectDefinitionIssue> issues)
    {
        var startLine = lines[startIndex];
        var (key, value) = KeyValue(startLine.Text[2..]);

        var id = key == "id" ? Value(value) : string.Empty;
        var path = string.Empty;
        var name = (string?)null;
        ProductProperties? properties = null;

        var nextIndex = startIndex + 1;
        while (nextIndex < endIndex && nextIndex < lines.Count && lines[nextIndex].Indent > 4)
        {
            var line = lines[nextIndex];
            if (line.Indent == 6)
            {
                var (k, v) = KeyValue(line.Text);
                if (k == "path") path = Value(v);
                else if (k == "name") name = NullIfBlank(Value(v));
            }
            nextIndex++;
        }

        return new(id, path, name, properties, null);
    }

    private static QualityDefinition? Quality(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var declared = lines.Any(line =>
            line.Indent == 0 && KeyValue(line.Text).Key == "quality");
        if (!declared) return null;
        var section = Section(lines, "quality");
        if (section.Count == 0) return null;

        var applicabilities = QualityApplicabilities(lines, issues);
        return new(applicabilities);
    }

    private static IReadOnlyList<QualityApplicability> QualityApplicabilities(
        IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        var quality = Section(lines, "quality");
        var applicStart = quality.FirstOrDefault(line =>
            line.Indent == 2 && KeyValue(line.Text).Key == "applicability");
        if (applicStart is null) return [];

        var applicabilities = new List<QualityApplicability>();
        var startIndex = lines.IndexOf(applicStart);
        Dictionary<string, object>? current = null;

        foreach (var line in lines.Skip(startIndex + 1).TakeWhile(line => line.Indent > 2))
        {
            if (line.Indent == 4 && line.Text.StartsWith("- ", StringComparison.Ordinal))
            {
                if (current is not null) applicabilities.Add(ToApplicability(current, applicabilities.Count, issues));
                current = new(StringComparer.Ordinal);
                var (key, value) = KeyValue(line.Text[2..]);
                if (!string.IsNullOrWhiteSpace(key)) current[key] = Value(value);
            }
            else if (line.Indent == 6 && current is not null)
            {
                var (key, value) = KeyValue(line.Text);
                if (!string.IsNullOrWhiteSpace(key)) current[key] = Value(value);
            }
        }
        if (current is not null) applicabilities.Add(ToApplicability(current, applicabilities.Count, issues));

        return applicabilities;
    }

    private static QualityApplicability ToApplicability(
        IReadOnlyDictionary<string, object> fields,
        int index,
        List<ProjectDefinitionIssue> issues)
    {
        fields.TryGetValue("id", out var idObj);
        var id = idObj?.ToString() ?? string.Empty;
        return new(id, [], null, null, null);
    }

    private static IReadOnlyList<YamlLine> Section(IReadOnlyList<YamlLine> lines, string name)
    {
        var start = lines.IndexOf(lines.FirstOrDefault(line =>
            line.Indent == 0 && KeyValue(line.Text).Key == name));
        if (start < 0) return [];
        return lines.Skip(start + 1).TakeWhile(line => line.Indent > 0).ToArray();
    }

    private static (string Key, string Value) KeyValue(string value)
    {
        var colon = value.IndexOf(':');
        return colon < 0
            ? (value.Trim(), string.Empty)
            : (value[..colon].Trim(), value[(colon + 1)..].Trim());
    }

    private static IReadOnlyList<string> ParseInlineList(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal)
            || !trimmed.EndsWith("]", StringComparison.Ordinal))
            return [Value(trimmed)];
        return trimmed[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Value).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
    }

    private static string Value(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            return trimmed[1..^1];
        return trimmed;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record YamlLine(int Number, int Indent, string Text);

    private static int IndexOf<T>(this IReadOnlyList<T> values, T? value)
    {
        if (value is null) return -1;
        for (var index = 0; index < values.Count; index++)
            if (EqualityComparer<T>.Default.Equals(values[index], value)) return index;
        return -1;
    }
}

public static partial class ProjectDefinitionValidator
{
    public static IReadOnlyList<ProjectDefinitionIssue> Validate(
        ProjectExecutionDefinition definition,
        string? workspace = null)
    {
        var issues = new List<ProjectDefinitionIssue>();
        if (definition.SchemaVersion is not (1 or 2))
            issues.Add(new("schemaVersion", "unsupported-version", $"schemaVersion must be 1 or 2, found {definition.SchemaVersion}."));

        if (definition.SchemaVersion == 1 && definition.Project is not null)
            issues.Add(new("project", "v1-does-not-support-project", "schemaVersion 1 does not support the project section. Use schemaVersion 2."));

        if (definition.SchemaVersion == 1 && definition.Quality is not null)
            issues.Add(new("quality", "v1-does-not-support-quality", "schemaVersion 1 does not support the quality section. Use schemaVersion 2."));

        if (definition.SchemaVersion == 2 && definition.Project is null)
            issues.Add(new("project", "v2-project-required", "schemaVersion 2 requires a project section."));
        if (definition.Stack.Count == 0 || definition.Stack.Any(string.IsNullOrWhiteSpace))
            issues.Add(new("stack", "stack-required", "At least one stack is required."));
        if (!SafeRelativePath(definition.Commands.Prepare))
            issues.Add(new("commands.prepare", "prepare-path-invalid", "Prepare must be a repository-relative path without traversal."));
        else if (workspace is not null && !File.Exists(Path.Combine(
                     workspace, definition.Commands.Prepare.Replace('/', Path.DirectorySeparatorChar))))
            issues.Add(new("commands.prepare", "prepare-script-missing", "The prepare script does not exist in the subject checkout."));

        foreach (var tool in definition.ToolVersions)
        {
            if (!ToolName().IsMatch(tool.Key))
                issues.Add(new($"toolVersions.{tool.Key}", "tool-name-invalid", "Tool names use letters, numbers, underscore, dot, or hyphen."));
            if (!SafeRelativePath(tool.Value))
                issues.Add(new($"toolVersions.{tool.Key}", "tool-manifest-invalid", "Tool version references must be repository-relative paths."));
            else if (workspace is not null && !File.Exists(Path.Combine(
                         workspace, tool.Value.Replace('/', Path.DirectorySeparatorChar))))
                issues.Add(new($"toolVersions.{tool.Key}", "tool-manifest-missing", "The referenced tool manifest does not exist in the subject checkout."));
        }

        var suiteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definition.TestSuites.Count; index++)
        {
            var suite = definition.TestSuites[index];
            if (string.IsNullOrWhiteSpace(suite.Id) || !suiteIds.Add(suite.Id))
                issues.Add(new($"testSuites[{index}].id", "suite-id-invalid", "Test suite ids are required and unique."));
            if (string.IsNullOrWhiteSpace(suite.Category))
                issues.Add(new($"testSuites[{index}].category", "suite-category-required", "A test suite category is required."));
            if (suite.ExpectedDurationSeconds <= 0)
                issues.Add(new($"testSuites[{index}].expectedDurationSeconds", "suite-duration-invalid", "Expected duration must be positive."));
            if (string.IsNullOrWhiteSpace(suite.Command))
                issues.Add(new($"testSuites[{index}].command", "suite-command-required", "A test suite command is required."));
        }

        foreach (var path in definition.CachePaths.Where(path => !SafeRelativePath(path)))
            issues.Add(new("cachePaths", "cache-path-invalid", $"Cache path '{path}' must be repository-relative without traversal."));
        foreach (var entry in definition.Environment)
        {
            if (!EnvironmentName().IsMatch(entry.Key))
                issues.Add(new($"environment.{entry.Key}", "environment-name-invalid", "Environment names must be portable shell identifiers."));
            if (SecretName().IsMatch(entry.Key))
                issues.Add(new($"environment.{entry.Key}", "secret-value-forbidden", "Secrets must be supplied by the executor profile, not committed in project.yml."));
        }
        if (definition.DevServer is { } server)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                issues.Add(new("devServer.command", "dev-server-command-required", "A dev server command is required."));
            if (server.StartupTimeoutSeconds <= 0)
                issues.Add(new("devServer.startupTimeoutSeconds", "dev-server-timeout-invalid", "The startup timeout must be positive."));
            if (!server.StopWithRun)
                issues.Add(new("devServer.stopWithRun", "dev-server-lifetime-invalid", "Dev servers must stop with the run."));
        }
        if (definition.Image is { } image && !SafeRelativePath(image))
            issues.Add(new("image", "image-path-invalid", "An optional image must be a repository-relative path without traversal."));
        if (definition.Release is { } release)
        {
            if (release.Identity.Count == 0)
                issues.Add(new("release.identity", "release-identity-required", "At least one release identity rule is required."));
            if (release.Restore.Count == 0 || release.Restore.Any(string.IsNullOrWhiteSpace))
                issues.Add(new("release.restore", "release-restore-required", "At least one release restore command is required."));

            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < release.Identity.Count; index++)
            {
                var identity = release.Identity[index];
                var path = $"release.identity[{index}]";
                if (string.IsNullOrWhiteSpace(identity.Package) || !packages.Add(identity.Package))
                    issues.Add(new($"{path}.package", "release-package-invalid", "Release package names are required and unique."));
                if (identity.Ecosystem is not ("nuget" or "npm"))
                    issues.Add(new($"{path}.ecosystem", "release-ecosystem-invalid", "Release ecosystem must be nuget or npm."));

                var hasSource = !string.IsNullOrWhiteSpace(identity.Source);
                var hasInlineIdentity = !string.IsNullOrWhiteSpace(identity.Version)
                                        || !string.IsNullOrWhiteSpace(identity.Integrity);
                if (hasSource == hasInlineIdentity)
                    issues.Add(new(path, "release-rule-kind-invalid", "Use either a lock-file source or both an exact version and registry integrity."));
                if (hasSource)
                {
                    if (!SafeRelativePath(identity.Source) || identity.Source!.Contains('\\'))
                        issues.Add(new($"{path}.source", "release-source-invalid", "Release identity sources must use repository-relative paths with forward slashes and no traversal."));
                    else if (workspace is not null && !File.Exists(Path.Combine(
                                 workspace, identity.Source!.Replace('/', Path.DirectorySeparatorChar))))
                        issues.Add(new($"{path}.source", "release-source-missing", "The release identity source does not exist in the subject checkout."));
                }
                else
                {
                    if (!ExactPackageVersion().IsMatch(identity.Version ?? string.Empty))
                        issues.Add(new($"{path}.version", "release-version-invalid", "An exact semantic package version is required."));
                    if (!IsPackageIntegrity(identity.Integrity))
                        issues.Add(new($"{path}.integrity", "release-integrity-invalid", "Registry integrity must use sha256- or sha512-prefixed content."));
                }
            }
        }
        return issues;
    }

    private static bool IsPackageIntegrity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value, "^sha(?:256|512)-[A-Za-z0-9+/=_-]+$", RegexOptions.CultureInvariant);

    private static bool SafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
        var normalized = value.Replace('\\', '/');
        return !normalized.Split('/').Any(segment => segment is ".." or "")
               && !normalized.StartsWith("~", StringComparison.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolName();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactPackageVersion();

    [GeneratedRegex("(?:TOKEN|PASSWORD|SECRET|PRIVATE_KEY|API_KEY)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();
}

public enum PreparationFailureKind
{
    None,
    Definition,
    ScriptMissing,
    ToolMissing,
    VersionMismatch,
    Cache,
    Network,
    Command,
    Timeout,
    Cancelled,
    /// <summary>
    /// A known host/environment misconfiguration (for example a missing
    /// Windows base variable) rather than a defect in the repository's own
    /// prepare command. Every kind other than <see cref="Command"/> and
    /// <see cref="Definition"/> already reads as
    /// <c>BuildTestGateFailureKind.Environment</c> to the build/test gate
    /// (CAC-18); this value names that class explicitly instead of silently
    /// falling into the generic bucket.
    /// </summary>
    Environment,
}

public sealed record PreparationCacheManifest(
    string Block,
    string Key,
    string State,
    string EntryPath,
    IReadOnlyList<string> Inputs,
    long ContentBytes = 0,
    int UnusedRunCount = 0,
    string? Warning = null,
    string? Recovery = null);

public sealed record ProjectPreparationManifest(
    int SchemaVersion,
    string? SubjectSha,
    string DefinitionSha256,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMs,
    bool Succeeded,
    string Command,
    IReadOnlyDictionary<string, string> ToolVersions,
    IReadOnlyDictionary<string, string> LockfileHashes,
    IReadOnlyList<PreparationCacheManifest> Caches,
    PreparationFailureKind FailureKind,
    string? FailureSignature,
    string? FailureReason,
    string? FailureOutputTail);

public sealed record ProjectPreparationResult(
    bool Configured,
    bool Succeeded,
    ProjectPreparationManifest? Manifest,
    IReadOnlyList<ProjectDefinitionIssue> DefinitionIssues,
    string Output,
    int? ExitCode,
    PreparationFailureKind FailureKind,
    string? FailureSignature,
    string? FailureReason)
{
    /// <summary>
    /// Resolved cache locations of this preparation, keyed by the environment
    /// variable that points at them (<c>NPM_CONFIG_CACHE</c>,
    /// <c>NUGET_PACKAGES</c>, <c>PLAYWRIGHT_BROWSERS_PATH</c>). Every later
    /// command of the same gate or coding run must receive them; without that
    /// binding the restore this preparation performed is invisible to a
    /// follow-up <c>dotnet build --no-restore</c>, which fails with NETSDK1064
    /// against a package folder that no longer exists (TE-52). Empty when
    /// preparation is not configured or failed.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Per-run root the <see cref="Environment"/> paths live in. It stays in
    /// place for the whole gate or coding run and is released afterwards with
    /// <see cref="ProjectPreparationExecutor.ReleaseRunRoot"/>. Null when there
    /// is nothing to release.
    /// </summary>
    public string? RunRoot { get; init; }

    public bool CacheHit => Manifest?.Caches.Count > 0
                            && Manifest.Caches.All(cache => cache.State == "hit");

    public static ProjectPreparationResult NotConfigured() =>
        new(false, true, null, [], string.Empty, null, PreparationFailureKind.None, null, null);
}

/// <summary>
/// Binds a finished preparation onto the commands that run after it inside the
/// same gate or coding run. The preparation's cache variables are applied last:
/// a host-owned or gate-owned value for the same variable would point the
/// follow-up command at a directory the prepare restore never wrote into.
/// </summary>
public static class PreparationCacheEnvironment
{
    /// <summary>Applies the resolved cache locations to a process launch.</summary>
    public static void Apply(
        ProcessStartInfo start,
        ProjectPreparationResult? preparation)
        => Apply((IDictionary<string, string>)start.Environment, preparation?.Environment);

    /// <summary>
    /// Applies the resolved cache locations to the environment overlay a CLI
    /// launch carries, for the coding-run path where the agent process - not the
    /// gate - runs build, test and lint.
    /// </summary>
    public static void Apply(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null) return;
        foreach (var entry in environment) target[entry.Key] = entry.Value;
    }
}

/// <summary>
/// Runs the repository-owned prepare script with product-owned technology
/// caches. Every prepare - hit or miss - works inside a private per-run folder
/// under <c>&lt;productCacheRoot&gt;/.runs/&lt;runId&gt;</c>, never inside a
/// published entry. Only a green prepare atomically publishes a new immutable
/// entry (by copy, so the per-run folder survives the publication), and a
/// failed run deletes its whole run root.
///
/// The per-run folder is what <see cref="ProjectPreparationResult.Environment"/>
/// points at, and it stays in place until the gate or coding run that asked for
/// the preparation calls <see cref="ReleaseRunRoot"/>. That is what keeps a
/// later <c>dotnet build --no-restore</c> resolvable against the packages the
/// prepare restored (TE-52).
/// </summary>
public static partial class ProjectPreparationExecutor
{
    /// <summary>Characters of the failed prepare output kept in the manifest.</summary>
    public const int ManifestTailLimit = 4_000;

    /// <summary>Characters of that tail repeated in the operator-facing reason.</summary>
    public const int ReasonTailLimit = 600;

    /// <summary>
    /// Consecutive successful preparations before an empty cache binding becomes
    /// a repository-definition warning. One empty run may be intentional; three
    /// establish that the prepare script is probably writing somewhere else.
    /// </summary>
    public const int UnusedCacheWarningThreshold = 3;

    /// <summary>
    /// How long an unreleased per-run cache folder is assumed to still belong to
    /// a live gate or coding run. Both are bounded far below this by their own
    /// timeouts, so anything older is the residue of a killed process.
    /// </summary>
    public static readonly TimeSpan RunRootRetention =
        ProjectPreparationCacheSweep.DefaultRunRootRetention;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    static ProjectPreparationExecutor()
    {
        Json.Converters.Add(new JsonStringEnumConverter());
    }

    public static async Task<ProjectPreparationResult> RunAsync(
        string workspace,
        string productCacheRoot,
        string manifestPath,
        string? subjectSha,
        Action<string>? log,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var definitionPath = Path.Combine(workspace, ProjectPreparationPaths.Definition.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(definitionPath)) return ProjectPreparationResult.NotConfigured();

        var read = ProjectDefinitionReader.ReadWorkspace(workspace);
        if (!read.IsValid || read.Definition is null || read.DefinitionSha256 is null)
            return new(true, false, null, read.Issues, string.Empty, null,
                PreparationFailureKind.Definition, "definition:invalid", "Repository execution definition is invalid.");

        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var previousManifest = ReadManifest(manifestPath);
        PruneStaleRunRoots(productCacheRoot, log);
        var runRoot = Path.Combine(productCacheRoot, ".runs", Guid.NewGuid().ToString("N"));
        var cacheBindings = BuildCacheBindings(workspace, productCacheRoot, runRoot, read.Definition, log);
        var selectedNodeBin = ResolveNvmNodeBin(read.Definition, workspace);
        var tools = await ReadToolVersionsAsync(
            read.Definition, workspace, selectedNodeBin, timeout, cancellationToken).ConfigureAwait(false);
        var versionFailure = VersionFailure(read.Definition, workspace, tools);
        if (versionFailure is not null)
        {
            var failedManifest = Manifest(read, subjectSha, started, stopwatch, false, tools,
                cacheBindings, versionFailure.Value.Kind, versionFailure.Value.Signature, versionFailure.Value.Reason,
                previousManifest: previousManifest);
            WriteManifest(manifestPath, failedManifest);
            DeleteBestEffort(runRoot);
            return new(true, false, failedManifest, [], string.Empty, null,
                versionFailure.Value.Kind, versionFailure.Value.Signature, versionFailure.Value.Reason);
        }

        var entryPoint = ScriptInvocation(workspace, read.Definition.Commands.Prepare);
        if (!entryPoint.Resolved)
        {
            var entryReason = entryPoint.FailureReason!;
            var failedManifest = Manifest(read, subjectSha, started, stopwatch, false, tools,
                cacheBindings, PreparationFailureKind.ScriptMissing, entryPoint.FailureSignature, entryReason,
                previousManifest: previousManifest);
            WriteManifest(manifestPath, failedManifest);
            DeleteBestEffort(runRoot);
            return new(true, false, failedManifest, [], string.Empty, null,
                PreparationFailureKind.ScriptMissing, entryPoint.FailureSignature, entryReason);
        }

        Directory.CreateDirectory(runRoot);
        var processStart = new ProcessStartInfo
        {
            FileName = entryPoint.FileName,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in entryPoint.Arguments) processStart.ArgumentList.Add(argument);
        processStart.Environment.Clear();
        CopySafeHostEnvironment(processStart.Environment);
        if (selectedNodeBin is not null)
        {
            var currentPath = processStart.Environment.TryGetValue("PATH", out var value) ? value : string.Empty;
            processStart.Environment["PATH"] = selectedNodeBin + Path.PathSeparator + currentPath;
            processStart.Environment["NVM_BIN"] = selectedNodeBin;
        }
        foreach (var entry in read.Definition.Environment) processStart.Environment[entry.Key] = entry.Value;
        foreach (var binding in cacheBindings)
            processStart.Environment[binding.EnvironmentVariable] = binding.WorkingPath;
        processStart.Environment["AGENT_STUDIO_PREPARATION"] = "1";
        processStart.Environment["AGENT_STUDIO_PREPARATION_MANIFEST"] = manifestPath;

        log?.Invoke($"project-prepare started command={read.Definition.Commands.Prepare} definition={read.DefinitionSha256}");
        string stdout = string.Empty;
        string stderr = string.Empty;
        int? exitCode = null;
        PreparationFailureKind failureKind;
        string? signature;
        string? reason;
        try
        {
            using var process = Process.Start(processStart)
                ?? throw new InvalidOperationException("Prepare process did not start.");
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMinutes(15));
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort containment */ }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* already gone */ }
                throw;
            }
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
            exitCode = process.ExitCode;
            (failureKind, signature, reason) = process.ExitCode == 0
                ? (PreparationFailureKind.None, null, null)
                : Classify(stderr + "\n" + stdout, process.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failureKind = PreparationFailureKind.Cancelled;
            signature = "prepare:cancelled";
            reason = "Preparation was cancelled.";
        }
        catch (OperationCanceledException)
        {
            failureKind = PreparationFailureKind.Timeout;
            signature = "prepare:timeout";
            reason = $"Preparation exceeded {timeout}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            stderr = ex.Message;
            failureKind = PreparationFailureKind.ScriptMissing;
            signature = "prepare:launch-failed";
            reason = ex.Message;
        }

        var succeeded = failureKind == PreparationFailureKind.None;
        if (succeeded) Publish(productCacheRoot, cacheBindings, log);
        else
        {
            if (failureKind == PreparationFailureKind.Cache)
                QuarantineCacheFailureEntries(productCacheRoot, cacheBindings, log);
            DeleteBestEffort(runRoot);
        }
        // AGT-2858: published entries used to accumulate without any bound. The
        // sweep runs after publication so the entries this preparation just
        // restored from or created are the ones it protects.
        ProjectPreparationCacheSweep.Run(
            productCacheRoot,
            PreparationCacheRetentionPolicy.FromEnvironment(),
            DateTime.UtcNow,
            cacheBindings.Select(binding => binding.EntryPath).ToArray(),
            log,
            RunRootRetention);
        // A failed prepare is unreadable while only its exit code survives. The
        // bounded tail goes into the manifest and, shortened, into the reason
        // that the gate hands to the card's integration failure detail.
        var outputTail = succeeded ? null : OutputTail(stdout, stderr);
        if (!succeeded) reason = ReasonWithOutputTail(reason, outputTail);
        stopwatch.Stop();
        var manifest = Manifest(read, subjectSha, started, stopwatch, succeeded, tools,
            cacheBindings, failureKind, signature, reason, outputTail, previousManifest);
        WriteManifest(manifestPath, manifest);
        log?.Invoke($"project-prepare completed succeeded={succeeded} durationMs={stopwatch.ElapsedMilliseconds} cacheHit={manifest.Caches.All(cache => cache.State == "hit")}");
        return new(true, succeeded, manifest, [], Bound(stdout, stderr), exitCode,
            failureKind, signature, reason)
        {
            Environment = succeeded
                ? cacheBindings.ToDictionary(
                    binding => binding.EnvironmentVariable,
                    binding => binding.WorkingPath,
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            RunRoot = succeeded ? runRoot : null,
        };
    }

    /// <summary>
    /// Releases the per-run cache folder a successful preparation handed to the
    /// gate or coding run. Call it once the last command of that gate or run has
    /// finished; the published immutable entries are untouched by it.
    /// </summary>
    public static void ReleaseRunRoot(ProjectPreparationResult? preparation)
    {
        if (preparation?.RunRoot is { Length: > 0 } runRoot) DeleteBestEffort(runRoot);
    }

    /// <summary>
    /// Drops per-run folders left behind by a process that died before it could
    /// release its own. Bounded by age so a run that is still using its folder
    /// is never touched: no preparation consumer outlives
    /// <see cref="RunRootRetention"/>.
    ///
    /// AGT-2858: the rule itself now lives in
    /// <see cref="ProjectPreparationCacheSweep.PruneRunRoots"/> together with the
    /// rest of the cache retention, so start-of-run and end-of-run reclamation
    /// cannot drift apart.
    /// </summary>
    private static void PruneStaleRunRoots(string productCacheRoot, Action<string>? log)
        => ProjectPreparationCacheSweep.PruneRunRoots(
            productCacheRoot, RunRootRetention, DateTime.UtcNow, log);

    public static (PreparationFailureKind Kind, string Signature, string Reason) Classify(
        string evidence,
        int? exitCode = null)
    {
        if (Contains(evidence, "command not found", "not recognized as an internal", "No such file or directory"))
            return (PreparationFailureKind.ToolMissing, "tool:missing", "A required preparation tool is missing.");
        if (Contains(evidence, "version mismatch", "does not match .nvmrc", "does not match global.json", "NETSDK1045"))
            return (PreparationFailureKind.VersionMismatch, "tool:version-mismatch", "A tool version does not match the repository manifest.");
        if (Contains(evidence, "integrity check failed", "cache corrupted", "EINTEGRITY"))
            return (PreparationFailureKind.Cache, "cache:integrity", "A preparation cache entry failed integrity checks.");
        if (Contains(evidence, "ENOTFOUND", "ECONNRESET", "unable to resolve host", "network is unreachable"))
            return (PreparationFailureKind.Network, "network:dependency-source", "A dependency source could not be reached.");
        // AGT-2833: two Windows signatures traced back to the same root cause
        // (a base Windows environment variable missing from the prepare host
        // environment, see PreparationHostEnvironment.WindowsKeys) but each
        // failing in a different tool, so each gets its own named reason
        // instead of falling through to an opaque exit-code signature.
        if (Contains(evidence, "Loading managed Windows PowerShell failed"))
            return (PreparationFailureKind.Environment, "prepare:powershell-environment",
                "Loading managed Windows PowerShell failed; the preparation host environment is missing a Windows base variable PowerShell needs.");
        if (Contains(evidence, "Value cannot be null. (Parameter 'path1')", "Value cannot be null. (Parameter path1)"))
            return (PreparationFailureKind.Environment, "prepare:windows-environment",
                "NuGet could not resolve a restore path; the preparation host environment is missing a Windows base variable NuGet needs.");
        var normalized = Regex.Replace(evidence ?? string.Empty, "\\s+", " ").Trim();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..16];
        return (PreparationFailureKind.Command, $"command:{exitCode ?? -1}:{digest}",
            $"Prepare command failed with exit code {exitCode?.ToString() ?? "unknown"}.");
    }

    private static IReadOnlyList<CacheBinding> BuildCacheBindings(
        string workspace,
        string cacheRoot,
        string runRoot,
        ProjectExecutionDefinition definition,
        Action<string>? log)
    {
        var bindings = new List<CacheBinding>();
        if (definition.Stack.Contains("node", StringComparer.OrdinalIgnoreCase)
            || Files(workspace, "package-lock.json").Count > 0)
            bindings.Add(Binding("npm", "NPM_CONFIG_CACHE", workspace, cacheRoot, runRoot,
                Files(workspace, "package-lock.json"), ToolInputs(definition.ToolVersions, "node"), log));
        if (definition.Stack.Contains("dotnet", StringComparer.OrdinalIgnoreCase)
            || Files(workspace, "*.csproj").Count > 0)
            bindings.Add(Binding("nuget", "NUGET_PACKAGES", workspace, cacheRoot, runRoot,
                Files(workspace, "packages.lock.json").Count > 0
                    ? Files(workspace, "packages.lock.json")
                    : Files(workspace, "*.csproj"), ToolInputs(definition.ToolVersions, "dotnet", "dotnetSdk"), log));
        if (definition.Stack.Contains("playwright", StringComparer.OrdinalIgnoreCase)
            || Files(workspace, "playwright.config.*").Count > 0)
            bindings.Add(Binding("playwright", "PLAYWRIGHT_BROWSERS_PATH", workspace, cacheRoot, runRoot,
                Files(workspace, "package-lock.json"), ToolInputs(definition.ToolVersions, "node"), log));
        return bindings;
    }

    private static IReadOnlyDictionary<string, string> ToolInputs(
        IReadOnlyDictionary<string, string> tools,
        params string[] names)
        => tools.Where(tool => names.Contains(tool.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(tool => tool.Key, tool => tool.Value, StringComparer.Ordinal);

    private static CacheBinding Binding(
        string block,
        string environmentVariable,
        string workspace,
        string cacheRoot,
        string runRoot,
        IReadOnlyList<string> inputs,
        IReadOnlyDictionary<string, string> tools,
        Action<string>? log)
    {
        var allInputs = inputs.Concat(tools.Values.Select(value => Path.Combine(workspace, value)))
            .Distinct(StringComparer.Ordinal).Where(File.Exists).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var key = ContentKey(block, workspace, allInputs);
        var entry = Path.Combine(cacheRoot, "entries", block, key);
        var content = Path.Combine(entry, "content");
        var working = Path.Combine(runRoot, block);
        var hit = false;
        string? recovery = null;
        using (AcquireEntryLock(cacheRoot, block, key))
        {
            var inspection = InspectEntry(entry, block, key);
            if (inspection == CacheEntryInspection.Hit)
            {
                CopyDirectory(content, working);
                hit = true;
                // AGT-2858: a hit is a use. Stamping the entry is what makes the
                // retention sweep's age and LRU rules measure last use rather than
                // publication date, so a cache that is still being hit never expires.
                ProjectPreparationCacheSweep.Touch(entry, DateTime.UtcNow);
            }
            else
            {
                if (inspection == CacheEntryInspection.LegacyEmpty)
                {
                    if (TryQuarantine(cacheRoot, block, key, entry, "legacy-empty"))
                        recovery = "legacy-empty-miss";
                }
                else if (inspection == CacheEntryInspection.Incomplete)
                {
                    if (TryQuarantine(cacheRoot, block, key, entry, "incomplete"))
                    {
                        recovery = "evicted-incomplete";
                        log?.Invoke($"project-prepare cache block={block} key={key} state=evicted-incomplete");
                    }
                }
                Directory.CreateDirectory(working);
            }
        }
        log?.Invoke($"project-prepare cache block={block} key={key} state={(hit ? "hit" : "miss")}" +
                    (recovery is null ? string.Empty : $" recovery={recovery}"));
        var relativeInputs = allInputs
            .Select(path => Path.GetRelativePath(workspace, path).Replace('\\', '/'))
            .ToArray();
        var inputHashes = allInputs.ToDictionary(
            path => Path.GetRelativePath(workspace, path).Replace('\\', '/'),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            StringComparer.Ordinal);
        return new(block, environmentVariable, key, entry, working, hit, recovery,
            relativeInputs, inputHashes);
    }

    private enum CacheEntryInspection
    {
        Miss,
        Hit,
        LegacyEmpty,
        Incomplete,
    }

    /// <summary>
    /// The single validity rule shared by lookup and publication. A published
    /// entry has a readable manifest for this block/key, a content directory,
    /// at least one file, and, when sizeBytes is present, exactly that many
    /// content bytes. A pre-fix, internally consistent empty entry is a legacy
    /// miss rather than corruption; every other partial or contradictory entry
    /// is incomplete.
    /// </summary>
    private static CacheEntryInspection InspectEntry(string entry, string block, string key)
    {
        if (!Directory.Exists(entry)) return CacheEntryInspection.Miss;
        var manifestPath = Path.Combine(entry, "manifest.json");
        var content = Path.Combine(entry, "content");
        if (!File.Exists(manifestPath) || !Directory.Exists(content))
            return CacheEntryInspection.Incomplete;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("block", out var manifestBlock)
                || !root.TryGetProperty("key", out var manifestKey)
                || !string.Equals(manifestBlock.GetString(), block, StringComparison.Ordinal)
                || !string.Equals(manifestKey.GetString(), key, StringComparison.Ordinal)
                || root.TryGetProperty("writeOnce", out var writeOnce) && writeOnce.ValueKind == JsonValueKind.False)
                return CacheEntryInspection.Incomplete;

            long? declaredSize = null;
            if (root.TryGetProperty("sizeBytes", out var size))
            {
                if (!size.TryGetInt64(out var bytes) || bytes < 0)
                    return CacheEntryInspection.Incomplete;
                declaredSize = bytes;
            }
            var actualSize = ProjectPreparationCacheSweep.Measure(content);
            var hasFiles = ContainsAnyFile(content);
            if (declaredSize is not null && declaredSize != actualSize)
                return CacheEntryInspection.Incomplete;
            if (!hasFiles)
                return actualSize == 0 && (declaredSize is null or 0)
                    ? CacheEntryInspection.LegacyEmpty
                    : CacheEntryInspection.Incomplete;
            return CacheEntryInspection.Hit;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return CacheEntryInspection.Incomplete;
        }
    }

    private static bool ContainsAnyFile(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ContentKey(string block, string workspace, IReadOnlyList<string> files)
    {
        using var stream = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.WriteByte(0);
        }
        Write("v1");
        Write(block);
        Write(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Write(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        foreach (var file in files)
        {
            Write(Path.GetRelativePath(workspace, file).Replace('\\', '/'));
            stream.Write(File.ReadAllBytes(file));
            stream.WriteByte(0);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static IReadOnlyList<string> Files(string root, string pattern)
    {
        var result = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var directory))
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(child);
                    if (name is ".git" or "node_modules" or "bin" or "obj" or "dist" or "test-results") continue;
                    pending.Enqueue(child);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A bounded block may ignore an inaccessible generated subtree.
            }
        }
        return result.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadToolVersionsAsync(
        ProjectExecutionDefinition definition,
        string workspace,
        string? selectedNodeBin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in definition.ToolVersions)
        {
            var executable = tool.Key.Equals("node", StringComparison.OrdinalIgnoreCase)
                && selectedNodeBin is not null
                ? Path.Combine(selectedNodeBin, OperatingSystem.IsWindows() ? "node.exe" : "node")
                : tool.Key.Equals("node", StringComparison.OrdinalIgnoreCase) ? "node"
                : tool.Key is "dotnet" or "dotnetSdk" ? "dotnet"
                : tool.Key;
            var argument = executable == "node" ? "--version" : "--version";
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = argument,
                    WorkingDirectory = workspace,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null) { versions[tool.Key] = "missing"; continue; }
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
                versions[tool.Key] = process.ExitCode == 0
                    ? (await process.StandardOutput.ReadToEndAsync()).Trim()
                    : "missing";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                versions[tool.Key] = "missing";
            }
        }
        return versions;
    }

    private static string? ResolveNvmNodeBin(ProjectExecutionDefinition definition, string workspace)
    {
        var manifestEntry = definition.ToolVersions.FirstOrDefault(tool =>
            tool.Key.Equals("node", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(manifestEntry.Value)) return null;
        var expected = File.ReadAllText(Path.Combine(
            workspace, manifestEntry.Value.Replace('/', Path.DirectorySeparatorChar)))
            .Trim().TrimStart('v');
        if (string.IsNullOrWhiteSpace(expected)) return null;
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("NVM_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm"),
        }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var bin = Path.Combine(root!, "versions", "node", "v" + expected, "bin");
            var executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(executable)) return bin;
        }
        return null;
    }

    private static (PreparationFailureKind Kind, string Signature, string Reason)? VersionFailure(
        ProjectExecutionDefinition definition,
        string workspace,
        IReadOnlyDictionary<string, string> actual)
    {
        foreach (var tool in definition.ToolVersions)
        {
            if (!actual.TryGetValue(tool.Key, out var version) || version == "missing")
                return (PreparationFailureKind.ToolMissing, $"tool:{tool.Key}:missing", $"Required tool '{tool.Key}' is missing.");
            var manifest = File.ReadAllText(Path.Combine(workspace, tool.Value.Replace('/', Path.DirectorySeparatorChar))).Trim();
            string? expected = tool.Key.Equals("node", StringComparison.OrdinalIgnoreCase)
                ? manifest.TrimStart('v').Trim()
                : tool.Key is "dotnet" or "dotnetSdk"
                    ? JsonDocument.Parse(manifest).RootElement.TryGetProperty("sdk", out var sdk)
                      && sdk.TryGetProperty("version", out var sdkVersion) ? sdkVersion.GetString() : null
                    : null;
            if (!string.IsNullOrWhiteSpace(expected)
                && !version.TrimStart('v').StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                return (PreparationFailureKind.VersionMismatch, $"tool:{tool.Key}:version-mismatch",
                    $"Tool '{tool.Key}' version {version} does not match {tool.Value} ({expected}).");
        }
        return null;
    }

    /// <summary>
    /// Publishes each missed block as a new immutable entry. The per-run working
    /// folder is copied, not moved: it remains the location the gate's or run's
    /// later commands read through <see cref="ProjectPreparationResult.Environment"/>.
    /// A block whose entry another run published in the meantime keeps this run's
    /// own copy and publishes nothing - the entry is immutable once it exists.
    /// </summary>
    private static void Publish(
        string cacheRoot,
        IReadOnlyList<CacheBinding> bindings,
        Action<string>? log)
    {
        foreach (var binding in bindings.Where(binding => !binding.Hit))
        {
            // An empty block has no reusable payload. Publishing it used to
            // create an entry that lookup rejected on the next run, alternating
            // every gate between green and a cache failure. Keep it a miss.
            if (!ContainsAnyFile(binding.WorkingPath))
            {
                log?.Invoke($"project-prepare cache block={binding.Block} key={binding.Key} state=unused");
                continue;
            }
            using (AcquireEntryLock(cacheRoot, binding.Block, binding.Key))
            {
                var existing = InspectEntry(binding.EntryPath, binding.Block, binding.Key);
                if (existing == CacheEntryInspection.Hit)
                {
                    log?.Invoke($"project-prepare cache block={binding.Block} key={binding.Key} state=already-published");
                    continue;
                }
                if (existing is CacheEntryInspection.Incomplete or CacheEntryInspection.LegacyEmpty)
                    TryQuarantine(cacheRoot, binding.Block, binding.Key, binding.EntryPath,
                        existing == CacheEntryInspection.Incomplete ? "incomplete" : "legacy-empty");

                var parent = Path.GetDirectoryName(binding.EntryPath)!;
                Directory.CreateDirectory(parent);
                var staging = binding.EntryPath + ".staging-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(staging);
                var content = Path.Combine(staging, "content");
                CopyDirectory(binding.WorkingPath, content);
                File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    block = binding.Block,
                    key = binding.Key,
                    createdAtUtc = DateTimeOffset.UtcNow,
                    inputs = binding.Inputs,
                    writeOnce = true,
                    // AGT-2858: recorded at publication so the retention sweep can
                    // apply its size bound without walking every cached file tree.
                    sizeBytes = ProjectPreparationCacheSweep.Measure(content),
                }, Json));
                try
                {
                    Directory.Move(staging, binding.EntryPath);
                    log?.Invoke($"project-prepare cache block={binding.Block} key={binding.Key} state=published");
                }
                catch (IOException) when (Directory.Exists(binding.EntryPath))
                {
                    DeleteBestEffort(staging);
                }
            }
        }
    }

    /// <summary>
    /// A prepare command that itself reports cache corruption invalidates only
    /// the published entries it copied from. Its private run root is discarded,
    /// and the integration gate may make one clean retry.
    /// </summary>
    private static void QuarantineCacheFailureEntries(
        string cacheRoot,
        IReadOnlyList<CacheBinding> bindings,
        Action<string>? log)
    {
        foreach (var binding in bindings.Where(binding => binding.Hit))
        {
            using (AcquireEntryLock(cacheRoot, binding.Block, binding.Key))
            {
                if (!TryQuarantine(cacheRoot, binding.Block, binding.Key, binding.EntryPath, "cache-failure"))
                    continue;
                log?.Invoke(
                    $"project-prepare cache block={binding.Block} key={binding.Key} state=evicted-cache-failure");
            }
        }
    }

    private static bool TryQuarantine(
        string cacheRoot,
        string block,
        string key,
        string entryPath,
        string reason)
    {
        if (!Directory.Exists(entryPath)) return false;
        var quarantine = Path.Combine(cacheRoot, ProjectPreparationCacheSweep.QuarantineDirectoryName, block);
        Directory.CreateDirectory(quarantine);
        var destination = Path.Combine(
            quarantine,
            key + "." + reason + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(entryPath, destination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another process may already have moved the same broken entry. In
            // either case this run continues from an isolated empty working copy.
            return false;
        }
    }

    /// <summary>
    /// Serializes lookup/copy, incomplete-entry quarantine, and publication for
    /// one immutable key across concurrent processes. The lock lives outside the
    /// entry, so renaming the entry cannot invalidate the lock itself.
    /// </summary>
    private static FileStream AcquireEntryLock(string cacheRoot, string block, string key)
    {
        var directory = Path.Combine(cacheRoot, ".locks", block);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".lock");
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static ProjectPreparationManifest Manifest(
        ProjectDefinitionReadResult read,
        string? subjectSha,
        DateTimeOffset started,
        Stopwatch stopwatch,
        bool succeeded,
        IReadOnlyDictionary<string, string> tools,
        IReadOnlyList<CacheBinding> bindings,
        PreparationFailureKind kind,
        string? signature,
        string? reason,
        string? outputTail = null,
        ProjectPreparationManifest? previousManifest = null)
    {
        stopwatch.Stop();
        var lockHashes = bindings.SelectMany(binding => binding.InputHashes)
            .Where(entry => entry.Key.EndsWith("lock.json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => path.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        return new(1, subjectSha, read.DefinitionSha256!, started, DateTimeOffset.UtcNow,
            stopwatch.ElapsedMilliseconds, succeeded, read.Definition!.Commands.Prepare,
            tools, lockHashes,
            bindings.Select(binding =>
            {
                var contentBytes = ProjectPreparationCacheSweep.Measure(binding.WorkingPath);
                var unused = succeeded && !binding.Hit && !ContainsAnyFile(binding.WorkingPath);
                var unusedRuns = unused
                    ? (previousManifest?.Caches.FirstOrDefault(cache =>
                           string.Equals(cache.Block, binding.Block, StringComparison.Ordinal)
                           && cache.State == "unused")?.UnusedRunCount ?? 0) + 1
                    : 0;
                var warning = unusedRuns >= UnusedCacheWarningThreshold
                    ? $"{binding.Block} block is bound but unused for {unusedRuns} consecutive preparations; "
                      + $"the prepare script may redirect {binding.EnvironmentVariable}."
                    : null;
                return new PreparationCacheManifest(
                    binding.Block,
                    binding.Key,
                    binding.Hit ? "hit" : succeeded ? unused ? "unused" : "published" : "discarded",
                    binding.EntryPath,
                    binding.Inputs,
                    contentBytes,
                    unusedRuns,
                    warning,
                    binding.Recovery);
            }).ToArray(), kind, signature, reason, outputTail);
    }

    private static ProjectPreparationManifest? ReadManifest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ProjectPreparationManifest>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteManifest(string path, ProjectPreparationManifest manifest)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static PreparationEntryPoint ScriptInvocation(
        string workspace,
        string relativeScript)
    {
        var full = Path.GetFullPath(Path.Combine(workspace, relativeScript.Replace('/', Path.DirectorySeparatorChar)));
        return PreparationEntryPointPolicy.Resolve(
            full, OperatingSystem.IsWindows(), File.Exists, FindGitBash);
    }

    /// <summary>
    /// First Git for Windows bash.exe on this host. Only paths derived from a
    /// Git installation are considered, so the WSL launcher in System32 is never
    /// picked up as a POSIX shell.
    /// </summary>
    private static string? FindGitBash() =>
        GitBashCandidates().FirstOrDefault(File.Exists);

    private static IEnumerable<string> GitBashCandidates()
    {
        foreach (var root in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var value = Environment.GetEnvironmentVariable(root);
            if (string.IsNullOrWhiteSpace(value)) continue;
            yield return Path.Combine(value, "Git", "bin", "bash.exe");
            yield return Path.Combine(value, "Programs", "Git", "bin", "bash.exe");
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? installation;
            try { installation = Path.GetDirectoryName(Path.GetDirectoryName(Path.Combine(directory, "git.exe"))); }
            catch (ArgumentException) { continue; }
            if (!string.IsNullOrWhiteSpace(installation) && File.Exists(Path.Combine(directory, "git.exe")))
                yield return Path.Combine(installation, "bin", "bash.exe");
        }
    }

    private static void CopySafeHostEnvironment(IDictionary<string, string?> target)
    {
        foreach (var key in PreparationHostEnvironment.KeysFor(OperatingSystem.IsWindows()))
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void DeleteBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool Contains(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Bound(string stdout, string stderr)
    {
        var value = $"stdout:\n{stdout}\nstderr:\n{stderr}".Trim();
        return value.Length <= 32_000 ? value : value[^32_000..];
    }

    /// <summary>
    /// Bounded tail of what the failed prepare script actually printed. stderr
    /// wins because that is where a prepare script reports why it stopped;
    /// stdout is the fallback for scripts that only write there.
    /// </summary>
    internal static string? OutputTail(string stdout, string stderr, int limit = ManifestTailLimit)
    {
        var source = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var normalized = (source ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0) return null;
        return normalized.Length <= limit ? normalized : normalized[^limit..];
    }

    /// <summary>
    /// Operator-facing reason plus a short single-line excerpt of the tail, so
    /// the integration failure detail on the card names the real error instead
    /// of an opaque exit code.
    /// </summary>
    internal static string? ReasonWithOutputTail(string? reason, string? tail)
    {
        if (string.IsNullOrWhiteSpace(tail)) return reason;
        var excerpt = Regex.Replace(tail, "\\s+", " ").Trim();
        if (excerpt.Length > ReasonTailLimit) excerpt = "..." + excerpt[^ReasonTailLimit..];
        return string.IsNullOrWhiteSpace(reason) ? excerpt : $"{reason} Output tail: {excerpt}";
    }

    private sealed record CacheBinding(
        string Block,
        string EnvironmentVariable,
        string Key,
        string EntryPath,
        string WorkingPath,
        bool Hit,
        string? Recovery,
        IReadOnlyList<string> Inputs,
        IReadOnlyDictionary<string, string> InputHashes);
}
