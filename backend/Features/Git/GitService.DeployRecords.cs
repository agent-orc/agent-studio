using System.Text.RegularExpressions;

namespace AgentStudio.Git;

public partial class GitService
{
    /// <summary>Publisher subjects in the complete base..tip range. Null means Git could not read it.</summary>
    internal string[]? DeployRangeCardKeys(string repoRoot, string from, string to)
    {
        var (shallow, _, status) = RunGitArgs(repoRoot, "rev-parse", "--is-shallow-repository");
        if (status != 0 || shallow.Trim() != "false") return null;
        var (output, _, code) = RunGitArgs(repoRoot, "log", "--reverse", "--topo-order", "--format=%s",
            $"{from}..{to}", RevisionsOnly);
        if (code != 0) return null;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var subject in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ReleaseLineIntegration().Match(subject);
            var key = match.Success ? match.Groups[1].Value.ToUpperInvariant() : ParseIntegrationMergeKey(subject);
            if (key is not null && keys.Add(key)) ordered.Add(key);
        }
        return ordered.ToArray();
    }

    [GeneratedRegex(@"^Integrate ([A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*-[0-9]+):")]
    private static partial Regex ReleaseLineIntegration();
}
