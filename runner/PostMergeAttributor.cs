using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Collects evidence on the runner checkout. The server decides how to repair it.</summary>
public static partial class PostMergeAttributor
{
    [GeneratedRegex(@"^\s*Failed\s+(?<name>[^\s\[]+)", RegexOptions.Multiline)]
    private static partial Regex FailedDotNetTest();
    [GeneratedRegex(@"\bIntegrate\s+(?<id>[A-Za-z0-9_-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntegrationTaskId();

    public static IReadOnlyList<string> FailedTests(string output) => FailedDotNetTest().Matches(output)
        .Select(match => match.Groups["name"].Value).Where(name => name.Length <= 256)
        .Distinct(StringComparer.Ordinal).Take(64).ToArray();

    public static string? TaskIdFromMergeMessage(string message)
    {
        var match = IntegrationTaskId().Match(message);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public static string? FocusedCommand(string command, IReadOnlyList<string> tests)
    {
        if (tests.Count == 0 || !command.Contains("dotnet test ", StringComparison.Ordinal)) return null;
        if (tests.Any(test => !Regex.IsMatch(test, @"^[A-Za-z_][A-Za-z0-9_.+`]*$"))) return null;
        var clauses = tests.Select(test => "FullyQualifiedName~" + test);
        var filter = string.Join("|", clauses);
        var existing = Regex.Match(command, "--filter\\s+(?:\"(?<filter>[^\"]+)\"|'(?<filter>[^']+)'|(?<filter>[^\\s]+))");
        return existing.Success
            ? command.Remove(existing.Index, existing.Length).Insert(existing.Index,
                "--filter \"(" + existing.Groups["filter"].Value + ")&(" + filter + ")\"")
            : command + " --filter \"" + filter + "\"";
    }

    public static IReadOnlyList<string> BlockingTests(string checkout, IReadOnlyList<string> failed,
        DateTimeOffset now)
    {
        var path = Path.Combine(checkout, ".agent-studio", "test-quarantine.json");
        if (!File.Exists(path)) return failed;
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return failed; }
        using (document)
        {
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("tests", out var rows)
            || rows.ValueKind != JsonValueKind.Array) return failed;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in rows.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("test", out var test) || test.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(card.GetString())
                || !entry.TryGetProperty("expiresOn", out var expires)
                || expires.ValueKind != JsonValueKind.String
                || !DateOnly.TryParse(expires.GetString(), out var expiry)
                || expiry < DateOnly.FromDateTime(now.UtcDateTime)) continue;
            if (entry.TryGetProperty("resolvedOn", out var resolved)
                && resolved.ValueKind == JsonValueKind.String
                && DateOnly.TryParse(resolved.GetString(), out var resolvedAt)
                && resolvedAt <= DateOnly.FromDateTime(now.UtcDateTime)) continue;
            active.Add(test.GetString()!);
        }
        return failed.Where(test => !active.Contains(test)).ToArray();
        }
    }

    public static string Fingerprint(IReadOnlyList<string> failedTests, string command, string excerpt)
    {
        var signature = failedTests.Count > 0 ? string.Join("\n", failedTests.Order(StringComparer.Ordinal))
            : command + "\n" + Regex.Replace(excerpt, @"\b[0-9a-f]{7,64}\b", "<sha>", RegexOptions.IgnoreCase);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
    }

    internal static bool SuiteCouldAffect(ProjectExecutionDefinition definition, IReadOnlyList<string> changedPaths,
        string failedCommand) => PostMergeRunPlan.SelectAffected(definition, changedPaths)
            .Any(command => failedCommand == command || failedCommand.StartsWith(command + " ", StringComparison.Ordinal)
                || command.Contains("dotnet test ", StringComparison.Ordinal)
                && failedCommand.Contains(command.Split(" --filter", StringSplitOptions.None)[0], StringComparison.Ordinal));

    public static string Excerpt(string text) => text.Length <= 2000 ? text : text[^2000..];
}

/// <summary>Checkout-local test project graph, including process-level references.</summary>
public sealed class PostMergeProjectMap
{
    private readonly Dictionary<string, IReadOnlyList<string>> _references = new(StringComparer.OrdinalIgnoreCase);

    public PostMergeProjectMap(string checkout, IReadOnlyList<string> projectFiles)
    {
        foreach (var relativePath in projectFiles.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var file = Path.Combine(checkout, relativePath);
            var relative = Normalize(Path.GetRelativePath(checkout, file));
            try
            {
                _references[relative] = XDocument.Load(file).Descendants("ProjectReference")
                    .Select(node => node.Attribute("Include")?.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => Normalize(Path.GetRelativePath(checkout,
                        Path.GetFullPath(value!.Replace('\\', Path.DirectorySeparatorChar),
                            Path.GetDirectoryName(file)!))))
                    .ToArray();
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or IOException or ArgumentException)
            {
                // An incomplete graph cannot safely exclude a suspect.
                _references.Clear();
                return;
            }
        }
        if (_references.Values.SelectMany(references => references)
            .Any(reference => !_references.ContainsKey(reference)))
            _references.Clear();
    }

    public bool CouldAffect(ProjectExecutionDefinition definition, IReadOnlyList<string> changedPaths,
        string failedCommand)
    {
        var match = Regex.Match(failedCommand,
            @"\bdotnet\s+test\s+['"" ]?(?<project>[^\s'""]+\.csproj)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return PostMergeAttributor.SuiteCouldAffect(definition, changedPaths, failedCommand);
        if (_references.Count == 0) return true;
        var testProject = Normalize(match.Groups["project"].Value);
        if (!_references.ContainsKey(testProject)) return true;
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string project)
        {
            if (!closure.Add(project) || !_references.TryGetValue(project, out var references)) return;
            foreach (var reference in references) Visit(reference);
        }
        Visit(testProject);
        if (closure.Any(project => DirectoryOf(project).Length == 0)) return true;
        foreach (var changed in changedPaths)
        {
            var path = Normalize(changed);
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return true;
            var owner = _references.Keys.Where(project =>
                    path.StartsWith(DirectoryOf(project) + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(project => DirectoryOf(project).Length).FirstOrDefault();
            if (owner is null)
            {
                if (PostMergeAttributor.SuiteCouldAffect(definition, [path], failedCommand)) return true;
            }
            else if (closure.Contains(owner)) return true;
        }
        return false;
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
    private static string DirectoryOf(string project) => Path.GetDirectoryName(project)?.Replace('\\', '/') ?? "";
}
