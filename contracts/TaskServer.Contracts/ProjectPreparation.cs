using System.Collections.Concurrent;
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
}

/// <summary>
/// One product cache block of a preparation run. <paramref name="ContentPath"/>
/// is the resolved package location the prepare process actually used and the
/// location every later command of the same gate or run must be pointed at
/// through <paramref name="EnvironmentVariable"/>; both stay null for a
/// discarded block, which has no location to hand on.
/// </summary>
public sealed record PreparationCacheManifest(
    string Block,
    string Key,
    string State,
    string EntryPath,
    IReadOnlyList<string> Inputs,
    string? EnvironmentVariable = null,
    string? ContentPath = null);

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

/// <summary>
/// Reads a persisted <c>preparation-manifest.json</c>. The manifest is the
/// durable record of one preparation, so a consumer that did not run the
/// preparation itself (a resumed attempt, a reattaching daemon, the Execution
/// settings view) derives the same evidence and the same
/// <see cref="PreparationCommandEnvironment"/> from it.
/// </summary>
public static class ProjectPreparationManifestFile
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>The manifest at <paramref name="path"/>, or null when it is missing or unreadable.</summary>
    public static ProjectPreparationManifest? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectPreparationManifest>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Pure resolution of the environment every command after a green preparation
/// has to run with. The prepare process restores its packages into product
/// cache locations that no toolchain can rediscover on its own: the assets file
/// a later <c>dotnet build --no-restore</c> reads points at whatever
/// <c>NUGET_PACKAGES</c> said during restore, so a gate or run that starts its
/// build without the same variable fails with NETSDK1064 "package ... was not
/// found. It might have been deleted since NuGet restore" (Windows merge gate,
/// 15.09.2026). Playwright browsers and the npm cache break the same way.
///
/// <para>The contract is therefore: preparation publishes its resolved cache
/// locations, and build, test, lint, e2e and the coding-run agent of the same
/// gate or run receive exactly those locations. Resolution is a projection of
/// the manifest, so a consumer that only has <c>preparation-manifest.json</c>
/// derives the identical environment.</para>
/// </summary>
public static class PreparationCommandEnvironment
{
    /// <summary>Nothing to hand on: not configured, failed, or no cache block.</summary>
    public static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    /// <summary>Cache states whose content path is a usable package location.</summary>
    private static readonly string[] ResolvedStates = ["hit", "published"];

    public static IReadOnlyDictionary<string, string> Resolve(ProjectPreparationManifest? manifest)
        => manifest is null || !manifest.Succeeded ? None : Resolve(manifest.Caches);

    public static IReadOnlyDictionary<string, string> Resolve(
        IEnumerable<PreparationCacheManifest>? caches)
    {
        if (caches is null) return None;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cache in caches)
        {
            if (string.IsNullOrWhiteSpace(cache.EnvironmentVariable)
                || string.IsNullOrWhiteSpace(cache.ContentPath)
                || !ResolvedStates.Contains(cache.State, StringComparer.Ordinal))
                continue;
            resolved[cache.EnvironmentVariable] = cache.ContentPath;
        }
        return resolved.Count == 0 ? None : resolved;
    }

    /// <summary>
    /// Layers the resolved locations onto a child process environment. Applied
    /// last on purpose: a gate default such as its own <c>NPM_CONFIG_CACHE</c>
    /// must not win over the location the prepare process restored into.
    /// </summary>
    public static void ApplyTo(
        IDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null) return;
        foreach (var variable in variables) environment[variable.Key] = variable.Value;
    }

    /// <inheritdoc cref="ApplyTo(IDictionary{string,string?},IReadOnlyDictionary{string,string})"/>
    public static void ApplyTo(
        Dictionary<string, string> environment,
        IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null) return;
        foreach (var variable in variables) environment[variable.Key] = variable.Value;
    }
}

/// <summary>
/// Process-local binding table from a prepared workspace to its preparation
/// environment. The coding-run agent is started through the CLI execution
/// surface that four CLI backends and their test doubles share, so the prepared
/// locations travel next to that surface instead of through its signature. A
/// run binds its workspace after a green preparation and releases it when the
/// run ends; a lookup also answers for a working directory inside the prepared
/// workspace, because a run may execute in a component subdirectory.
/// </summary>
public static class PreparedWorkspaceEnvironment
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Bound =
        new(PathComparer);

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Bind(string? workspace, IReadOnlyDictionary<string, string>? variables)
    {
        if (Key(workspace) is not { } key) return;
        if (variables is null || variables.Count == 0) Bound.TryRemove(key, out _);
        else Bound[key] = variables;
    }

    public static void Release(string? workspace)
    {
        if (Key(workspace) is { } key) Bound.TryRemove(key, out _);
    }

    /// <summary>
    /// The environment bound for <paramref name="workingDirectory"/> or for the
    /// nearest prepared ancestor of it; <see cref="PreparationCommandEnvironment.None"/>
    /// when the directory belongs to no prepared workspace.
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(string? workingDirectory)
    {
        if (Bound.IsEmpty || Key(workingDirectory) is not { } key)
            return PreparationCommandEnvironment.None;
        for (var directory = key; !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            if (Bound.TryGetValue(directory, out var variables)) return variables;
        }
        return PreparationCommandEnvironment.None;
    }

    private static string? Key(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

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
    public bool CacheHit => Manifest?.Caches.Count > 0
                            && Manifest.Caches.All(cache => cache.State == "hit");

    /// <summary>
    /// The cache locations this preparation resolved, keyed by the environment
    /// variable that points a later command at them. Every build, test, lint,
    /// e2e or coding-run command of the same gate or run must be started with
    /// these variables; see <see cref="PreparationCommandEnvironment"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> CommandEnvironment
        => Succeeded ? PreparationCommandEnvironment.Resolve(Manifest) : PreparationCommandEnvironment.None;

    public static ProjectPreparationResult NotConfigured() =>
        new(false, true, null, [], string.Empty, null, PreparationFailureKind.None, null, null);
}

/// <summary>
/// Runs the repository-owned prepare script with product-owned technology
/// caches. Every cache miss writes into a private staging directory. Only a
/// green prepare atomically publishes a new immutable entry, and a failed run
/// deletes all staging content. A hit binds the prepare process to the
/// published entry directly, so the location it restored into is the same one
/// <see cref="ProjectPreparationResult.CommandEnvironment"/> hands to every
/// later command of the gate or run.
/// </summary>
public static partial class ProjectPreparationExecutor
{
    /// <summary>Characters of the failed prepare output kept in the manifest.</summary>
    public const int ManifestTailLimit = 4_000;

    /// <summary>Characters of that tail repeated in the operator-facing reason.</summary>
    public const int ReasonTailLimit = 600;

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
        var runRoot = Path.Combine(productCacheRoot, ".runs", Guid.NewGuid().ToString("N"));
        var cacheBindings = BuildCacheBindings(workspace, productCacheRoot, runRoot, read.Definition, log);
        var selectedNodeBin = ResolveNvmNodeBin(read.Definition, workspace);
        var tools = await ReadToolVersionsAsync(
            read.Definition, workspace, selectedNodeBin, timeout, cancellationToken).ConfigureAwait(false);
        var incompleteCache = cacheBindings.FirstOrDefault(binding => binding.InvalidEntry);
        if (incompleteCache is not null)
        {
            var incompleteReason = $"Immutable {incompleteCache.Block} cache entry {incompleteCache.Key} is incomplete and requires orchestrator eviction.";
            var failedManifest = Manifest(read, subjectSha, started, stopwatch, false, tools,
                cacheBindings, PreparationFailureKind.Cache, "cache:incomplete", incompleteReason);
            WriteManifest(manifestPath, failedManifest);
            DeleteBestEffort(runRoot);
            return new(true, false, failedManifest, [], string.Empty, null,
                PreparationFailureKind.Cache, "cache:incomplete", incompleteReason);
        }
        var versionFailure = VersionFailure(read.Definition, workspace, tools);
        if (versionFailure is not null)
        {
            var failedManifest = Manifest(read, subjectSha, started, stopwatch, false, tools,
                cacheBindings, versionFailure.Value.Kind, versionFailure.Value.Signature, versionFailure.Value.Reason);
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
                cacheBindings, PreparationFailureKind.ScriptMissing, entryPoint.FailureSignature, entryReason);
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
        if (succeeded) Publish(cacheBindings, log);
        // Either way the per-run root has served its purpose: a green run moved
        // every staged block into its immutable entry, a red one discards them.
        DeleteBestEffort(runRoot);
        // A failed prepare is unreadable while only its exit code survives. The
        // bounded tail goes into the manifest and, shortened, into the reason
        // that the gate hands to the card's integration failure detail.
        var outputTail = succeeded ? null : OutputTail(stdout, stderr);
        if (!succeeded) reason = ReasonWithOutputTail(reason, outputTail);
        stopwatch.Stop();
        var manifest = Manifest(read, subjectSha, started, stopwatch, succeeded, tools,
            cacheBindings, failureKind, signature, reason, outputTail);
        WriteManifest(manifestPath, manifest);
        log?.Invoke($"project-prepare completed succeeded={succeeded} durationMs={stopwatch.ElapsedMilliseconds} cacheHit={manifest.Caches.All(cache => cache.State == "hit")}");
        return new(true, succeeded, manifest, [], Bound(stdout, stderr), exitCode,
            failureKind, signature, reason);
    }

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
        var entryExists = Directory.Exists(entry);
        var hit = File.Exists(Path.Combine(entry, "manifest.json"))
                  && Directory.Exists(content)
                  && ContainsAnyFile(content);
        var invalidEntry = entryExists && !hit;
        // A hit binds the prepare process to the published entry itself. The
        // former private copy was both a full duplication of the package folder
        // per run and a source of drift: whatever the prepare script added to
        // the copy was thrown away, while the build that followed had to read
        // some other location. A miss keeps its private staging folder, so a
        // failed first prepare still publishes nothing.
        var working = hit ? content : Path.Combine(runRoot, block);
        if (!hit) Directory.CreateDirectory(working);
        log?.Invoke($"project-prepare cache block={block} key={key} state={(invalidEntry ? "incomplete" : hit ? "hit" : "miss")}");
        var relativeInputs = allInputs
            .Select(path => Path.GetRelativePath(workspace, path).Replace('\\', '/'))
            .ToArray();
        var inputHashes = allInputs.ToDictionary(
            path => Path.GetRelativePath(workspace, path).Replace('\\', '/'),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            StringComparer.Ordinal);
        return new(block, environmentVariable, key, entry, content, working, hit, invalidEntry,
            relativeInputs, inputHashes);
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

    private static void Publish(IReadOnlyList<CacheBinding> bindings, Action<string>? log)
    {
        foreach (var binding in bindings.Where(binding => !binding.Hit))
        {
            if (Directory.Exists(binding.EntryPath))
            {
                DeleteBestEffort(binding.WorkingPath);
                continue;
            }
            var parent = Path.GetDirectoryName(binding.EntryPath)!;
            Directory.CreateDirectory(parent);
            var staging = binding.EntryPath + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            Directory.Move(binding.WorkingPath, Path.Combine(staging, "content"));
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                block = binding.Block,
                key = binding.Key,
                createdAtUtc = DateTimeOffset.UtcNow,
                inputs = binding.Inputs,
                writeOnce = true,
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
        string? outputTail = null)
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
                var state = binding.Hit ? "hit" : succeeded ? "published" : "discarded";
                return new PreparationCacheManifest(
                    binding.Block, binding.Key, state, binding.EntryPath, binding.Inputs,
                    binding.EnvironmentVariable,
                    state == "discarded" ? null : binding.ContentPath);
            }).ToArray(), kind, signature, reason, outputTail);
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

    /// <summary>
    /// One bound cache block. <see cref="WorkingPath"/> is where the prepare
    /// process writes (the immutable entry itself on a hit, private staging on a
    /// miss); <see cref="ContentPath"/> is where that content lives once the run
    /// succeeded and is what later commands are pointed at.
    /// </summary>
    private sealed record CacheBinding(
        string Block,
        string EnvironmentVariable,
        string Key,
        string EntryPath,
        string ContentPath,
        string WorkingPath,
        bool Hit,
        bool InvalidEntry,
        IReadOnlyList<string> Inputs,
        IReadOnlyDictionary<string, string> InputHashes);
}
