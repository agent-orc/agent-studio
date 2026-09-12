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
    string? Image);

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
            "cachePaths", "capabilities", "environment", "devServer", "image",
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

        if (!int.TryParse(schemaVersion, out var version)) version = 0;
        var definition = new ProjectExecutionDefinition(
            version, stack, tools, commands, suites, caches, capabilities,
            environment, devServer, NullIfBlank(image));
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
        if (definition.SchemaVersion != 1)
            issues.Add(new("schemaVersion", "unsupported-version", "schemaVersion must be 1."));
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
        return issues;
    }

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

public sealed record PreparationCacheManifest(
    string Block,
    string Key,
    string State,
    string EntryPath,
    IReadOnlyList<string> Inputs);

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
    string? FailureReason);

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

    public static ProjectPreparationResult NotConfigured() =>
        new(false, true, null, [], string.Empty, null, PreparationFailureKind.None, null, null);
}

/// <summary>
/// Runs the repository-owned prepare script with product-owned technology
/// caches. Every cache miss writes into a private staging directory. Only a
/// green prepare atomically publishes a new immutable entry, and a failed run
/// deletes all staging content.
/// </summary>
public static partial class ProjectPreparationExecutor
{
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

        Directory.CreateDirectory(runRoot);
        var (fileName, arguments) = ScriptInvocation(workspace, read.Definition.Commands.Prepare);
        var processStart = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) processStart.ArgumentList.Add(argument);
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
        else DeleteBestEffort(runRoot);
        stopwatch.Stop();
        var manifest = Manifest(read, subjectSha, started, stopwatch, succeeded, tools,
            cacheBindings, failureKind, signature, reason);
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
        var working = Path.Combine(runRoot, block);
        var entryExists = Directory.Exists(entry);
        var hit = File.Exists(Path.Combine(entry, "manifest.json"))
                  && Directory.Exists(content)
                  && ContainsAnyFile(content);
        var invalidEntry = entryExists && !hit;
        if (hit) CopyDirectory(content, working);
        else Directory.CreateDirectory(working);
        log?.Invoke($"project-prepare cache block={block} key={key} state={(invalidEntry ? "incomplete" : hit ? "hit" : "miss")}");
        var relativeInputs = allInputs
            .Select(path => Path.GetRelativePath(workspace, path).Replace('\\', '/'))
            .ToArray();
        var inputHashes = allInputs.ToDictionary(
            path => Path.GetRelativePath(workspace, path).Replace('\\', '/'),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            StringComparer.Ordinal);
        return new(block, environmentVariable, key, entry, working, hit, invalidEntry,
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
        string? reason)
    {
        stopwatch.Stop();
        var lockHashes = bindings.SelectMany(binding => binding.InputHashes)
            .Where(entry => entry.Key.EndsWith("lock.json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => path.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        return new(1, subjectSha, read.DefinitionSha256!, started, DateTimeOffset.UtcNow,
            stopwatch.ElapsedMilliseconds, succeeded, read.Definition!.Commands.Prepare,
            tools, lockHashes,
            bindings.Select(binding => new PreparationCacheManifest(
                binding.Block, binding.Key, binding.Hit ? "hit" : succeeded ? "published" : "discarded",
                binding.EntryPath, binding.Inputs)).ToArray(), kind, signature, reason);
    }

    private static void WriteManifest(string path, ProjectPreparationManifest manifest)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static (string FileName, IReadOnlyList<string> Arguments) ScriptInvocation(
        string workspace,
        string relativeScript)
    {
        var full = Path.GetFullPath(Path.Combine(workspace, relativeScript.Replace('/', Path.DirectorySeparatorChar)));
        if (OperatingSystem.IsWindows())
        {
            var powershell = File.Exists(full + ".ps1") ? full + ".ps1" : full;
            return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", powershell]);
        }
        return ("/bin/sh", [full]);
    }

    private static void CopySafeHostEnvironment(IDictionary<string, string?> target)
    {
        foreach (var key in new[] { "PATH", "HOME", "USERPROFILE", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR" })
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

    private sealed record CacheBinding(
        string Block,
        string EnvironmentVariable,
        string Key,
        string EntryPath,
        string WorkingPath,
        bool Hit,
        bool InvalidEntry,
        IReadOnlyList<string> Inputs,
        IReadOnlyDictionary<string, string> InputHashes);
}
