using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>
/// Interim concept-dossier reference contract. The durable reference lives in
/// the two task result documents until <c>references.workbenches</c> replaces
/// this detector.
/// </summary>
public static partial class ConceptDossierContract
{
    private const string StartMarker = "<!-- agent-studio:concept-dossier -->";
    private const string EndMarker = "<!-- /agent-studio:concept-dossier -->";

    public static ConceptDossierSummary Read(string taskFolder)
    {
        var deliverables = ReadText(Path.Combine(TaskPaths.ResultsDir(taskFolder), "deliverables.md"));
        var status = ReadText(Path.Combine(taskFolder, "status.md"));
        var path = FindPath(deliverables);
        var source = path is null ? null : "results/deliverables.md";
        if (path is null)
        {
            path = FindPath(status);
            source = path is null ? null : "status.md";
        }

        var closure = ConceptDossierClosureStore.Read(taskFolder);
        return new ConceptDossierSummary
        {
            RepoRelativePath = closure?.NoDossierNeeded == true ? null : path,
            ReferenceSource = closure?.NoDossierNeeded == true ? null : source,
            NoDossierNeeded = closure?.NoDossierNeeded ?? false,
            NoDossierReason = closure?.Reason,
            DeclaredAt = closure?.DeclaredAt,
        };
    }

    public static string? FindPath(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var match = DossierPathRegex().Match(markdown.Replace('\\', '/'));
        return match.Success ? NormalizePath(match.Groups["path"].Value) : null;
    }

    public static bool IsDossierPath(string? path)
    {
        var normalized = NormalizePath(path);
        return normalized is not null
            && string.Equals(FindPath($"`{normalized}`"), normalized, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ReviewAgentReferences(
        string taskFolder,
        string expectedRepoRelativePath)
    {
        var findings = new List<string>();
        var expected = NormalizePath(expectedRepoRelativePath);
        if (!ReferencesPath(
                ReadText(Path.Combine(TaskPaths.ResultsDir(taskFolder), "deliverables.md")),
                expected))
        {
            findings.Add("results/deliverables.md must name the dossier path.");
        }
        if (!ReferencesPath(ReadText(Path.Combine(taskFolder, "status.md")), expected))
            findings.Add("status.md must name the dossier path.");
        return findings;
    }

    public static void WriteReference(string taskFolder, string repoRelativePath)
    {
        var normalized = NormalizePath(repoRelativePath)
            ?? throw new ArgumentException("Dossier path is required.", nameof(repoRelativePath));
        var results = TaskPaths.ResultsDir(taskFolder);
        Directory.CreateDirectory(results);
        WriteDocument(
            Path.Combine(results, "deliverables.md"),
            "# Deliverables",
            normalized);
        WriteDocument(
            Path.Combine(taskFolder, "status.md"),
            "# Status",
            normalized);
    }

    public static string PreserveReferenceInStatus(string summary, string repoRelativePath)
    {
        var normalized = NormalizePath(repoRelativePath);
        if (normalized is null || ReferencesPath(summary, normalized)) return summary;
        return AppendReferenceBlock(summary, normalized);
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized))
        {
            return null;
        }
        return normalized;
    }

    private static bool ReferencesPath(string? markdown, string? expected)
    {
        if (expected is null) return false;
        return DossierPathRegex().Matches((markdown ?? string.Empty).Replace('\\', '/'))
            .Select(match => NormalizePath(match.Groups["path"].Value))
            .Any(path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteDocument(string path, string initialHeading, string dossierPath)
    {
        var current = ReadText(path);
        if (ReferencesPath(current, dossierPath)) return;
        if (string.IsNullOrWhiteSpace(current)) current = initialHeading + Environment.NewLine;
        File.WriteAllText(path, AppendReferenceBlock(current, dossierPath), Encoding.UTF8);
    }

    private static string AppendReferenceBlock(string markdown, string dossierPath)
    {
        var nl = Environment.NewLine;
        return markdown.TrimEnd() + nl + nl
            + StartMarker + nl
            + "## Dossier" + nl + nl
            + $"- Path: `{dossierPath}`" + nl
            + EndMarker + nl;
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_./-])(?<path>docs/(?:[A-Za-z0-9._-]+/)+index\.html)(?![A-Za-z0-9_./-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DossierPathRegex();
}
