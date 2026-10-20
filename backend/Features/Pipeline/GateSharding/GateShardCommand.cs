using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentStudio.Pipeline;

/// <summary>
/// A suite command the gate can shard: one <c>dotnet test</c> invocation of one
/// test project, with its own selection filter (for example
/// <c>Category!=MachineBound</c>) separated out so each shard intersects it.
/// </summary>
/// <param name="Command">The suite command as the project defines it.</param>
/// <param name="CommandWithoutFilter">The same invocation without its <c>--filter</c>.</param>
/// <param name="BaseFilter">The suite's own filter expression, or null.</param>
/// <param name="TestProject">The test project argument, relative to the command's working directory.</param>
public sealed record GateShardableCommand(
    string Command,
    string CommandWithoutFilter,
    string? BaseFilter,
    string TestProject);

/// <summary>
/// AGT-3232: builds the per-shard invocations of a suite command. A shard's
/// filter is the suite's own filter intersected with its class partition, so
/// category filters apply exactly as in the unsharded run. The filter travels
/// in a run-settings file (<c>RunConfiguration/TestCaseFilter</c>) because the
/// complement shard names hundreds of classes, beyond what a command line
/// carries on every gate host.
/// </summary>
public static class GateShardCommand
{
    /// <summary>
    /// The shardable form of <paramref name="command"/>, or null when it is not
    /// a single <c>dotnet test</c> of one project, or when it already brings
    /// run settings or lists tests instead of running them.
    /// </summary>
    public static GateShardableCommand? TryParse(string command)
    {
        if (!GateFlakyRerunPolicy.IsTargetableDotNetTest(command)) return null;
        var filters = FilterOption.Matches(command);
        if (filters.Count > 1) return null;
        var withoutFilter = FilterOption.Replace(command, string.Empty).Trim();
        var tokens = withoutFilter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(token => token is "--settings" or "-s" or "--list-tests" or "-t"
                || token.StartsWith("--settings=", StringComparison.Ordinal)))
            return null;
        var verb = Array.FindIndex(tokens, 1, token => string.Equals(token, "test", StringComparison.OrdinalIgnoreCase));
        if (verb < 0 || verb + 1 >= tokens.Length) return null;
        var project = tokens[verb + 1].Trim('"', '\'');
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return null;
        var baseFilter = filters.Count == 1 ? filters[0].Groups["value"].Value.Trim('"', '\'').Trim() : null;
        return new GateShardableCommand(command.Trim(), withoutFilter, string.IsNullOrEmpty(baseFilter) ? null : baseFilter, project);
    }

    /// <summary>
    /// The filter of shard <paramref name="index"/>: its own classes, or for
    /// the complement shard everything outside the other shards' classes.
    /// </summary>
    public static string Filter(GateShardableCommand command, GateShardPlan plan, int index)
    {
        var selection = index == plan.ComplementIndex
            ? string.Join("&", plan.ClassesOutsideComplement.Select(name => $"FullyQualifiedName!~{ClassTerm(name)}"))
            : string.Join("|", plan.Shards[index].Classes.Select(name => $"FullyQualifiedName~{ClassTerm(name)}"));
        if (selection.Length == 0) return command.BaseFilter ?? string.Empty;
        return command.BaseFilter is null ? selection : $"({command.BaseFilter})&({selection})";
    }

    /// <summary>
    /// <c>.Name.</c> matches the class segment of a fully qualified test name
    /// (<c>Namespace.Name.Method</c>) without depending on the namespace, and
    /// never matches a class that only starts with the same name.
    /// </summary>
    internal static string ClassTerm(string className) => $".{className}.";

    public static string RunSettings(string filter)
        => new XDocument(new XElement("RunSettings",
            new XElement("RunConfiguration",
                new XElement("TestCaseFilter", filter)))).ToString();

    /// <summary>The shard invocation: the suite command without its filter, reading the shard's run settings.</summary>
    public static string ShardInvocation(GateShardableCommand command, string runSettingsPath)
        => $"{command.CommandWithoutFilter} --settings \"{runSettingsPath.Replace('\\', '/')}\"";

    private static readonly Regex FilterOption = new(
        @"\s--filter(?:\s+|=)(?<value>""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}

/// <summary>
/// The test classes a .NET test project declares, read from its sources. The
/// list only balances the shards: a class it misses still runs, in the
/// complement shard.
/// </summary>
public static class GateShardTestCatalog
{
    private static readonly string[] TestAttributes =
        ["[Fact", "[Theory", "[SkippableFact", "[SkippableTheory"];

    private static readonly Regex FileScopedNamespace = new(
        @"^namespace\s+[A-Za-z_][\w.]*\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex ClassDeclaration = new(
        @"^(?<indent>[ \t]*)(?<modifiers>(?:(?:public|internal|file|sealed|abstract|static|partial|unsafe)\s+)*)"
        + @"class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public static IReadOnlyList<string> Classes(string projectDirectory)
    {
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(projectDirectory))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "GateShardTestCatalog: unreadable test source; its classes run in the complement shard");
                continue;
            }
            foreach (var name in DeclaredTestClasses(text)) classes.Add(name);
        }
        return classes.ToArray();
    }

    /// <summary>
    /// Top-level, instantiable classes of a file that declares tests. A
    /// file-scoped namespace keeps top-level types at column zero; a block
    /// namespace indents them by one level.
    /// </summary>
    internal static IEnumerable<string> DeclaredTestClasses(string source)
    {
        if (!TestAttributes.Any(attribute => source.Contains(attribute, StringComparison.Ordinal))) yield break;
        var maxIndent = FileScopedNamespace.IsMatch(source) ? 0 : 4;
        foreach (Match match in ClassDeclaration.Matches(source))
        {
            var indent = match.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
            var modifiers = match.Groups["modifiers"].Value;
            if (indent > maxIndent || modifiers.Contains("abstract", StringComparison.Ordinal)
                || modifiers.Contains("static", StringComparison.Ordinal))
                continue;
            yield return match.Groups["name"].Value;
        }
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(current, "*.cs", SearchOption.TopDirectoryOnly).ToArray();
                children = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "GateShardTestCatalog: unreadable test directory; its classes run in the complement shard");
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name is "bin" or "obj" or "TestResults" || name.StartsWith('.')) continue;
                pending.Push(child);
            }
        }
    }
}
