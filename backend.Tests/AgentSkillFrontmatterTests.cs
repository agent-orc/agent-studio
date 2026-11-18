using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Drift guard for the portable skill library under <c>.agents/skills/</c>.
///
/// <para>
/// Codex loads every <c>SKILL.md</c> in this tree on session start and
/// refuses any file that does not open with a <c>---</c>-delimited YAML
/// frontmatter block ("missing YAML frontmatter delimited by ---"). Two
/// skills (<c>task-api</c>, <c>regenerate-readme</c>) shipped without that
/// block and produced an ERROR on every Codex run. The backend skill
/// catalog (<see cref="AgentStudio.Runtime.SkillReadinessService"/>)
/// also reads <c>name</c> + <c>description</c> from the same block.
/// </para>
/// <para>
/// This test enumerates the whole tree (not a hard-coded list) so a newly
/// added skill that forgets its frontmatter is flagged in the repo before
/// it ever reaches a Codex run. It complements
/// <see cref="CliSkillFilesTests"/>, which guards the separate
/// <c>docs/system/cli/skills/</c> family.
/// </para>
/// </summary>
public class AgentSkillFrontmatterTests
{
    private static string FindRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir != null)
        {
            // The repo root contains both AGENTS.md and the .agents/ tree.
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, ".agents")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Repo root with .agents/ + AGENTS.md not found by walking up from {sourceFile}.");
    }

    private static string SkillsRoot() => Path.Combine(FindRepoRoot(), ".agents", "skills");

    public static IEnumerable<object[]> SkillFolders()
    {
        var root = SkillsRoot();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // A skill folder is one that ships a SKILL.md at its root.
            if (File.Exists(Path.Combine(dir, "SKILL.md")))
            {
                yield return new object[] { Path.GetFileName(dir) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SkillFolders))]
    public void EverySkill_OpensWithFrontmatterNameAndDescription(string slug)
    {
        var path = Path.Combine(SkillsRoot(), slug, "SKILL.md");
        var text = File.ReadAllText(path);

        // The frontmatter block must be the very first thing in the file -
        // this is the exact contract the Codex skill loader enforces.
        var frontmatter = Regex.Match(text, @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
            RegexOptions.Singleline);
        Assert.True(frontmatter.Success,
            $"Skill '{slug}' SKILL.md is missing the leading YAML frontmatter block " +
            "(must open with a `---` delimiter at line 1). Codex rejects such files " +
            "with 'missing YAML frontmatter delimited by ---'.");

        var body = frontmatter.Groups["body"].Value;

        // name must match the folder slug so the catalog id and display name agree.
        Assert.True(Regex.IsMatch(body, $@"(?m)^name:\s*{Regex.Escape(slug)}\s*$"),
            $"Skill '{slug}' frontmatter must contain 'name: {slug}' matching its folder.");

        // description feeds the project skill catalog; it must be non-empty.
        Assert.True(Regex.IsMatch(body, @"(?m)^description:\s*\S.+$"),
            $"Skill '{slug}' frontmatter must contain a non-empty 'description:'.");
    }

    [Fact]
    public void SkillsTree_HasAtLeastTheKnownSkills()
    {
        // Sanity check that the enumeration actually found the tree; guards
        // against a path regression silently turning the Theory into a no-op.
        var slugs = SkillFolders().Select(o => (string)o[0]).ToHashSet();
        Assert.Contains("task-api", slugs);
        Assert.Contains("regenerate-readme", slugs);
        Assert.Contains("runtime-log-analysis", slugs);
        Assert.Contains("visual-design", slugs);
    }

    [Theory]
    [MemberData(nameof(SkillFolders))]
    public void EverySkill_HasARowInTheSkillsReadmeTable(string slug)
    {
        // The README table is how a direct CLI session discovers a skill, so a
        // skill folder without its row is invisible to agents that start there.
        var readme = File.ReadAllText(Path.Combine(SkillsRoot(), "README.md"));
        Assert.True(
            Regex.IsMatch(readme, $@"(?m)^\|.*\]\({Regex.Escape(slug)}/SKILL\.md\)\s*\|\s*$"),
            $"Skill '{slug}' has no row in .agents/skills/README.md linking ({slug}/SKILL.md).");
    }
}
