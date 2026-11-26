using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Collects evidence on the runner checkout. The server decides how to repair it.</summary>
public static partial class PostMergeAttributor
{
    [GeneratedRegex(@"^\s*Failed\s+(?<name>[^\s\[(]+)", RegexOptions.Multiline)]
    private static partial Regex FailedDotNetTest();
    [GeneratedRegex(@"^\s*FAIL\s+(?<name>[^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex FailedOtherTest();
    [GeneratedRegex(@"\bIntegrate\s+(?<id>[A-Za-z0-9_-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntegrationTaskId();

    public static IReadOnlyList<string> FailedTests(string output)
    {
        var clean = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        return FailedDotNetTest().Matches(clean).Concat(FailedOtherTest().Matches(clean))
        .Select(match => match.Groups["name"].Value.Trim()).Where(name => name.Length <= 256)
        .Distinct(StringComparer.Ordinal).Take(64).ToArray();
    }

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

    public static bool SameTest(string reported, string test) => string.Equals(reported, test, StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<file>[A-Za-z0-9_./@-]+\.(?:spec|test)\.(?:ts|tsx|js|mjs|cjs))(?:\s+>|$)")]
    private static partial Regex VitestTestFile();
    [GeneratedRegex(@"\bdotnet\s+test\s+(?<project>['""]?[^\s'""]+\.csproj['""]?)(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetTestProject();

    /// <summary>The spec file of a Vitest failure name such as <c>src/a.spec.ts &gt; Suite &gt; case</c>.</summary>
    public static string? VitestFile(string test)
    {
        var match = VitestTestFile().Match(test);
        return match.Success && !match.Groups["file"].Value.Contains("..", StringComparison.Ordinal)
            ? match.Groups["file"].Value : null;
    }

    /// <summary>
    /// RT-3: a Vitest suite run through an npm script, narrowed to the failing
    /// spec files with the Angular unit-test builder's <c>--include</c> option
    /// (the form the base suite already uses). Null for any other command or name.
    /// </summary>
    public static string? FocusedVitestCommand(string command, IReadOnlyList<string> tests)
    {
        if (tests.Count == 0 || command.Contains("dotnet ", StringComparison.Ordinal)
            || !Regex.IsMatch(command, @"\b(?:npm|npx|pnpm|yarn|ng)\b|npm-[A-Za-z0-9_-]+\.sh")) return null;
        var files = tests.Select(VitestFile).ToArray();
        if (files.Any(file => file is null)) return null;
        var includes = string.Join(" ", files.Distinct(StringComparer.Ordinal).Select(file => "--include " + file));
        return command.Contains(" -- ", StringComparison.Ordinal) ? command + " " + includes : command + " -- " + includes;
    }

    /// <summary>The <c>.csproj</c> path of a <c>dotnet test</c> command, without quotes.</summary>
    public static string? TestProject(string command)
    {
        var match = DotnetTestProject().Match(command);
        return match.Success ? match.Groups["project"].Value.Trim('\'', '"') : null;
    }

    /// <summary>
    /// RT-3 (RT-D9 B): the build a control or bisect point needs. A .NET suite
    /// builds only its test project (<c>dotnet build &lt;project&gt; --no-restore</c>,
    /// keeping any wrapper before <c>dotnet</c>); a Vitest suite uses the build
    /// command that shares its launcher (the frontend build). Prepare, the
    /// solution build and the other build commands never run at a point. Null when
    /// no partial build is known; then no point is tested.
    /// </summary>
    public static string? PartialBuildCommand(ProjectExecutionDefinition definition, string failedCommand,
        IReadOnlyList<string> tests)
    {
        var match = DotnetTestProject().Match(failedCommand);
        if (match.Success)
            return failedCommand[..match.Index] + "dotnet build " + match.Groups["project"].Value + " --no-restore";
        if (tests.Count == 0 || tests.Any(test => VitestFile(test) is null)) return null;
        var launcher = Launcher(failedCommand);
        return launcher is null ? null : definition.Commands.Build.FirstOrDefault(command =>
            !string.IsNullOrWhiteSpace(command) && Launcher(command) == launcher);
    }

    private static string? Launcher(string command)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length < 2 ? null : tokens[0] + " " + tokens[1];
    }

    public static IReadOnlyList<string> BlockingTests(string checkout, IReadOnlyList<string> failed,
        DateTimeOffset now)
        => TestQuarantinePolicy.Partition(failed, TestQuarantinePolicy.Read(checkout).Entries,
            DateOnly.FromDateTime(now.UtcDateTime)).Blocking;

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
        // Documentation cannot change a test project, even when the project graph is incomplete.
        changedPaths = changedPaths.Where(path => !IsDocumentation(path)).ToArray();
        if (changedPaths.Count == 0) return false;
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

    private static bool IsDocumentation(string path)
    {
        var normalized = Normalize(path);
        return normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
            || !normalized.Contains('/') && normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
    private static string DirectoryOf(string project) => Path.GetDirectoryName(project)?.Replace('\\', '/') ?? "";
}
