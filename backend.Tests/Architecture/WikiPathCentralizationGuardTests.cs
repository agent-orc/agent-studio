using System.Text.RegularExpressions;
using AgentStudio.Docs;
using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture guard (2026-07 docs/app/ migration). Every hardcoded
/// <c>docs/…</c> path literal in <c>backend/**</c> must either point under the
/// code-contract area <c>docs/app/</c> or be registered in
/// <see cref="WikiProducerTargets"/> (a producer write-target or a deliberate
/// reference root with a justification comment). A new stray <c>docs/</c> path
/// - a config file dropped at the docs root, a page written outside the
/// sanctioned theme folders, a typo'd top-level folder - fails the build here
/// with the exact file:line and the fix.
///
/// Mirrors the FeatureFolderBoundary / PromptCoverage precedent: a deterministic
/// scanner plus a build-breaking fact, and a fixture proving the guard fires.
/// </summary>
public class WikiPathCentralizationGuardTests
{
    private static string RepoRoot()
        => RepositoryRoot.Find();

    // A whole-string docs/ path literal: starts with docs/, path characters only
    // (letters, digits, . _ - / and {} $ for interpolation holes), no spaces - so
    // prose sentences that merely mention docs/ are not matched.
    private static readonly Regex DocsPathLiteral =
        new("\"(docs/[A-Za-z0-9._/${}-]+)\"", RegexOptions.Compiled);

    private static IEnumerable<(string File, int Line, string Path)> Scan(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                // Skip pure comment lines (doc-comment path references are not literals).
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
                    continue;
                foreach (Match m in DocsPathLiteral.Matches(lines[i]))
                {
                    var path = m.Groups[1].Value;
                    if (path.Length <= 5) continue; // bare "docs/" prefix, not a location
                    yield return (Path.GetRelativePath(root, file).Replace('\\', '/'), i + 1, path);
                }
            }
        }
    }

    [Fact]
    public void EveryBackendDocsPathLiteral_IsUnderAppOrRegistered()
    {
        var backend = Path.Combine(RepoRoot(), "backend");
        var violations = Scan(backend)
            .Where(v => !WikiProducerTargets.IsRegistered(v.Path))
            .Select(v => $"{v.File}:{v.Line}  \"{v.Path}\"")
            .ToList();

        Assert.True(violations.Count == 0,
            "Hardcoded docs/ path literals must point under docs/app/ (the code-contract area) "
            + "or be registered in WikiProducerTargets (producer write-target or a documented "
            + "reference root). Move the file under docs/app/, reuse an existing constant, or add "
            + "a registered reference root with a justification comment:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Guard_FiresOnAnUnregisteredDocsPath()
    {
        Assert.False(WikiProducerTargets.IsRegistered("docs/random-inbox/notes.md"));
        Assert.False(WikiProducerTargets.IsRegistered("docs/home.json")); // config must live under docs/app/
    }

    [Fact]
    public void Guard_AllowsAppAndRegisteredRoots()
    {
        Assert.True(WikiProducerTargets.IsRegistered("docs/app/config/home.json"));
        Assert.True(WikiProducerTargets.IsRegistered("docs/app/schemas/agent-message.schema.json"));
        Assert.True(WikiProducerTargets.IsRegistered("docs/operations/learnings/foo.md"));
        Assert.True(WikiProducerTargets.IsRegistered("docs/concepts/proposals/bar.md"));
        Assert.True(WikiProducerTargets.IsRegistered("docs/{relPath}")); // dynamic, no hardcoded location
    }
}
