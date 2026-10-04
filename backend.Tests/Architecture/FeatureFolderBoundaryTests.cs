using System.Text.RegularExpressions;
using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Enforces docs/system/architecture/backend-structure/styleguide.md §1+§2 so the structure migration
/// cannot regress:
///
/// <list type="bullet">
///   <item>Feature folders are fractal BY SUB-DOMAIN — a technical-role folder
///   (Services/, Models/, Endpoints/, ...) must never reappear under
///   <c>backend/Features/</c> at any depth.</item>
///   <item>Namespace follows folder: every file under <c>Features/&lt;X&gt;/**</c>
///   declares <c>AgentStudio.&lt;X&gt;</c>; <c>Host/**</c> = AgentStudio.Host;
///   <c>Shared/**</c> = AgentStudio.Shared.</item>
///   <item>The legacy <c>OrchestratorApi</c> namespace is fully retired from
///   source (the ASSEMBLY name stays <c>OrchestratorApi</c> on purpose —
///   start/stop/watchdog scripts match the process name).</item>
/// </list>
/// </summary>
public class FeatureFolderBoundaryTests
{
    /// <summary>Technical-role folder names forbidden below Features/ (any depth).</summary>
    private static readonly string[] ForbiddenFolderNames =
        ["Services", "Models", "Endpoints", "Handlers", "Dtos", "Helpers", "Utils", "Infrastructure"];

    private static string RepoRoot()
        => RepositoryRoot.Find();

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Features_ContainNoTechnicalRoleFolders()
    {
        var features = Path.Combine(RepoRoot(), "backend", "Features");
        Assert.True(Directory.Exists(features), $"missing {features}");

        var offenders = Directory.EnumerateDirectories(features, "*", SearchOption.AllDirectories)
            .Where(d => ForbiddenFolderNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .Select(d => Path.GetRelativePath(features, d))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Technical-role folders are forbidden under Features/ (STYLEGUIDE §1). Split by sub-domain instead:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NamespaceFollowsFolder()
    {
        var root = RepoRoot();
        var backend = Path.Combine(root, "backend");
        var nsDecl = new Regex(@"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline);
        var violations = new List<string>();

        foreach (var f in SourceFiles(backend))
        {
            var rel = Path.GetRelativePath(backend, f).Replace('\\', '/');
            string? expected = null;
            if (rel.StartsWith("Features/")) expected = "AgentStudio." + rel.Split('/')[1];
            else if (rel.StartsWith("Host/")) expected = "AgentStudio.Host";
            else if (rel.StartsWith("Shared/")) expected = "AgentStudio.Shared";
            if (expected == null) continue;

            var m = nsDecl.Match(File.ReadAllText(f));
            if (!m.Success) continue; // top-level statements (Program.cs)
            if (m.Groups[1].Value != expected)
                violations.Add($"{rel}: namespace {m.Groups[1].Value} (expected {expected})");
        }

        Assert.True(violations.Count == 0,
            "Namespace must follow the feature folder (STYLEGUIDE §2):\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void LegacyOrchestratorApiNamespace_IsRetired()
    {
        var root = RepoRoot();
        var decl = new Regex(@"^\s*(namespace|using(\s+static)?)\s+(global::)?OrchestratorApi\b", RegexOptions.Multiline);
        var offenders = new List<string>();
        foreach (var dir in new[] { Path.Combine(root, "backend"), Path.Combine(root, "backend.Tests") })
        {
            foreach (var f in SourceFiles(dir))
            {
                if (decl.IsMatch(File.ReadAllText(f)))
                    offenders.Add(Path.GetRelativePath(root, f));
            }
        }
        Assert.True(offenders.Count == 0,
            "OrchestratorApi namespace declarations/usings are retired (assembly name only):\n  "
            + string.Join("\n  ", offenders));
    }
}
