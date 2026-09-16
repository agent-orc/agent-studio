using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Areas;

/// <summary>One entry of an area glossary: the term, its single definition, and its accepted synonyms.</summary>
public sealed record GlossaryTerm
{
    public string Term { get; init; } = "";
    public string Definition { get; init; } = "";
    public List<string> Synonyms { get; init; } = [];
}

/// <summary>
/// The glossary of an area, stored as a wiki page under that area
/// (<c>docs/areas/&lt;area-id&gt;/glossary.md</c>). This type owns the page
/// format and nothing else: rendering and parsing are pure string functions so
/// the round trip can be tested without a repository, and an operator can edit
/// the page in the wiki without breaking the reader.
/// </summary>
public static partial class AreaGlossaryDocument
{
    /// <summary>The registered producer write-target that owns the area pages.</summary>
    public const string AreasFolder = WikiProducerTargets.AreasFolder;
    public const string FileName = "glossary.md";
    public const int MaxTerms = 500;

    [GeneratedRegex(@"\A---\r?\n(?<body>.*?)\r?\n---\r?\n?", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>Repository-relative path of the area's glossary page.</summary>
    public static string RepoRelativePath(string areaId) => $"{AreasFolder}/{areaId}/{FileName}";

    /// <summary>
    /// Boundary rule for <c>PUT .../glossary</c>: returns null when the terms
    /// can be stored, otherwise the single reason to refuse them.
    /// </summary>
    public static string? ValidateTerms(IReadOnlyList<GlossaryTerm>? terms)
    {
        var entries = terms ?? [];
        if (entries.Count > MaxTerms)
            return $"A glossary holds at most {MaxTerms} terms.";
        foreach (var entry in entries)
        {
            var term = Collapse(entry.Term);
            if (term.Length == 0) return "Every glossary entry needs a term.";
            if (term.Length > 120) return $"Term '{term[..40]}...' must be at most 120 characters.";
            var definition = Collapse(entry.Definition);
            if (definition.Length == 0) return $"Term '{term}' needs a definition.";
            if (definition.Length > 1000) return $"The definition of '{term}' must be at most 1000 characters.";
            if (entry.Synonyms.Count > 20) return $"Term '{term}' may list at most 20 synonyms.";
            if (entry.Synonyms.Any(synonym => Collapse(synonym).Length is 0 or > 120))
                return $"Every synonym of '{term}' must be 1 to 120 characters.";
        }
        var duplicate = entries
            .GroupBy(entry => Collapse(entry.Term), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate == null ? null : $"Term '{duplicate.Key}' is declared twice.";
    }

    /// <summary>
    /// Renders the wiki page. The front matter carries the area id and the
    /// area tag so the page is discoverable through the same tag filters as
    /// every other article; the body is one <c>##</c> section per term.
    /// </summary>
    public static string Render(AreaDefinition area, IReadOnlyList<GlossaryTerm> terms)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("area: ").Append(area.Id).Append('\n');
        sb.Append("tags: [").Append(area.Id).Append("]\n");
        sb.Append("---\n\n");
        sb.Append("# ").Append(Collapse(area.Label)).Append(" glossary\n\n");
        sb.Append("The ubiquitous language of the ").Append(Collapse(area.Label).ToLowerInvariant())
          .Append(" area. Every term an agent uses for this area is defined here once.\n");
        if (!string.IsNullOrWhiteSpace(area.Description))
            sb.Append('\n').Append(Collapse(area.Description)).Append('\n');
        if (terms.Count == 0)
        {
            sb.Append("\nNo terms are defined yet.\n");
            return sb.ToString();
        }
        foreach (var entry in terms)
        {
            sb.Append("\n## ").Append(Collapse(entry.Term)).Append("\n\n");
            sb.Append(Collapse(entry.Definition)).Append('\n');
            var synonyms = entry.Synonyms.Select(Collapse).Where(s => s.Length > 0).ToList();
            if (synonyms.Count > 0)
                sb.Append("\nSynonyms: ").Append(string.Join(", ", synonyms)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads the terms back out of the page. Anything that is not a term
    /// section is ignored, so hand-written prose above the first <c>##</c>
    /// heading survives a read without becoming a term.
    /// </summary>
    public static List<GlossaryTerm> Parse(string? markdown)
    {
        var terms = new List<GlossaryTerm>();
        if (string.IsNullOrWhiteSpace(markdown)) return terms;
        var body = Frontmatter().Replace(markdown, "");

        string? term = null;
        var definition = new List<string>();
        var synonyms = new List<string>();

        void Flush()
        {
            if (term == null) return;
            var text = Collapse(string.Join(' ', definition));
            if (text.Length > 0)
                terms.Add(new GlossaryTerm { Term = term, Definition = text, Synonyms = [.. synonyms] });
            term = null;
            definition.Clear();
            synonyms.Clear();
        }

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                term = Collapse(line[3..]);
                if (term.Length == 0) term = null;
                continue;
            }
            if (term == null) continue;
            if (line.StartsWith('#')) { Flush(); continue; }
            if (line.StartsWith("Synonyms:", StringComparison.OrdinalIgnoreCase))
            {
                synonyms.AddRange(line["Synonyms:".Length..]
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(Collapse)
                    .Where(value => value.Length > 0));
                continue;
            }
            if (line.Length > 0) definition.Add(line);
        }
        Flush();
        return terms;
    }

    private static string Collapse(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : Whitespace().Replace(value.Trim(), " ");
}
