using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>AGT-3232 compatible class partitioning for a standalone runner host.</summary>
public static partial class PostMergeShards
{
    [GeneratedRegex(@"\s--filter(?:\s+|=)(?<value>""[^""]*""|'[^']*'|\S+)")]
    private static partial Regex FilterOption();
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_][\w.+]*\.[A-Za-z_]\w*)(?:\(|\s|$)")]
    private static partial Regex TestName();

    public static int Count(ProjectTestSuite suite, int slots, int classes)
    {
        var requested = suite.Shards switch
        {
            null or 1 => 1,
            ProjectTestSuite.AutoShards => Math.Clamp((int)Math.Ceiling(
                suite.ExpectedDurationSeconds / 360d), 1, 8),
            { } fixedCount => Math.Clamp(fixedCount, 1, ProjectTestSuite.MaxShards),
        };
        return Math.Min(Math.Min(requested, Math.Max(1, slots)), Math.Max(1, classes));
    }

    public static IReadOnlyList<string> Classes(string listOutput) => listOutput.Split('\n')
        .Select(line => TestName().Match(line))
        .Where(match => match.Success)
        .Select(match => match.Groups["name"].Value)
        .Select(name => name[..name.LastIndexOf('.')])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> Commands(string command, IReadOnlyList<string> classes,
        int count, string settingsRoot)
    {
        if (count < 2) return [command];
        var match = FilterOption().Match(command);
        var baseFilter = match.Success ? match.Groups["value"].Value.Trim('"', '\'') : null;
        var bare = match.Success ? FilterOption().Replace(command, string.Empty).Trim() : command;
        var buckets = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();
        foreach (var name in classes)
        {
            var term = name + ".";
            if (classes.Any(other => other.Length > name.Length && (other + ".").Contains(term, StringComparison.Ordinal)))
            {
                buckets[count - 1].Add(name);
                continue;
            }
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
            buckets[(int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)count)].Add(name);
        }
        Directory.CreateDirectory(settingsRoot);
        var commands = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var selection = index == count - 1
                ? string.Join("&", buckets.Take(count - 1).SelectMany(bucket => bucket)
                    .Select(name => $"FullyQualifiedName!~{name}."))
                : string.Join("|", buckets[index].Select(name => $"FullyQualifiedName~{name}."));
            if (index < count - 1 && selection.Length == 0) continue;
            var filter = string.IsNullOrWhiteSpace(baseFilter) ? selection
                : selection.Length == 0 ? baseFilter : $"({baseFilter})&({selection})";
            var settingsPath = Path.Combine(settingsRoot, $"shard-{index}.runsettings");
            new XDocument(new XElement("RunSettings", new XElement("RunConfiguration",
                new XElement("TestCaseFilter", filter)))).Save(settingsPath);
            commands.Add($"{bare} --settings '{settingsPath.Replace("'", "'\\''", StringComparison.Ordinal)}'");
        }
        return commands;
    }
}
