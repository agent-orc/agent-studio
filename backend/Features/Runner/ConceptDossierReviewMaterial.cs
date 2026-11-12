using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>Bounded snapshot of the published Dossier for concept-fit review.</summary>
public static partial class ConceptDossierReviewMaterial
{
    private const int MaximumFiles = 64;
    private const int MaximumFileCharacters = 256_000;
    private const int MaximumTotalCharacters = 512_000;

    public static string Render(string? repositoryPath, string taskFolder)
    {
        try { return RenderCore(repositoryPath, taskFolder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            return "Published Dossier could not be read: " + exception.Message;
        }
    }

    private static string RenderCore(string? repositoryPath, string taskFolder)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return "Published repository is unavailable; the Dossier cannot be inspected.";
        var record = ConceptWorkbenchStore.Read(taskFolder);
        var entry = record?.RepoRelativeEntrypoint
            ?? ConceptDossierContract.Read(taskFolder).RepoRelativePath;
        if (string.IsNullOrWhiteSpace(entry))
            return "Published Dossier reference is missing.";
        entry = entry.Replace('\\', '/');
        if (!entry.StartsWith("docs/", StringComparison.Ordinal) || !entry.EndsWith("/index.html", StringComparison.Ordinal))
            return "Published Dossier reference is invalid.";

        var root = Path.GetFullPath(repositoryPath);
        var directory = entry[..entry.LastIndexOf('/')];
        var pending = new Queue<string>();
        pending.Enqueue(entry);
        pending.Enqueue(directory + "/workbench.json");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new StringBuilder();
        var remaining = MaximumTotalCharacters;
        while (pending.Count > 0)
        {
            if (seen.Count >= MaximumFiles)
            {
                output.AppendLine($"File limit reached ({MaximumFiles}); remaining links were not inspected.");
                break;
            }
            var relative = pending.Dequeue();
            if (!seen.Add(relative)) continue;
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || HasLink(root, full))
            {
                output.AppendLine($"{relative}: outside the repository or a filesystem link; not inspected.");
                continue;
            }
            if (!File.Exists(full))
            {
                output.AppendLine($"{relative}: linked file is missing.");
                continue;
            }
            var file = new FileInfo(full);
            if (file.Length > MaximumFileCharacters * 4L || !IsText(relative))
            {
                output.AppendLine($"{relative}: {file.Length} bytes; content exceeds the text budget or is binary.");
                continue;
            }
            var content = File.ReadAllText(full);
            if (content.Length > MaximumFileCharacters || content.Length > remaining)
            {
                output.AppendLine($"{relative}: content exceeds the remaining {remaining} character budget; review cannot establish completeness from this excerpt.");
                continue;
            }
            remaining -= content.Length;
            output.AppendLine($"### {relative} ({content.Length} characters)");
            output.AppendLine("````text");
            output.AppendLine(content);
            output.AppendLine("````");
            foreach (Match match in LinkRegex().Matches(content))
            {
                var target = match.Groups["html"].Success
                    ? match.Groups["html"].Value : match.Groups["markdown"].Value;
                var link = Resolve(relative, target);
                if (link is not null && !seen.Contains(link)) pending.Enqueue(link);
            }
        }
        return output.Length == 0 ? "Published Dossier has no readable files." : output.ToString();
    }

    private static string? Resolve(string source, string target)
    {
        target = System.Net.WebUtility.HtmlDecode(target).Trim();
        var marker = target.IndexOfAny(['#', '?']);
        if (marker >= 0) target = target[..marker];
        if (target.Length == 0 || target.Contains(':') || target.StartsWith('/') || target.StartsWith("//"))
            return null;
        target = Uri.UnescapeDataString(target).Replace('\\', '/');
        var combined = target.StartsWith("docs/", StringComparison.Ordinal)
            ? target : source[..source.LastIndexOf('/')].TrimEnd('/') + "/" + target;
        var parts = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static bool HasLink(string root, string path)
    {
        for (var current = path; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current) && new FileInfo(current).LinkTarget is not null) return true;
            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null) return true;
        }
        return false;
    }

    private static bool IsText(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".md" or ".json"
            or ".txt" or ".css" or ".js" or ".svg" or ".csv" or ".yml" or ".yaml";

    [GeneratedRegex("(?:href|src)\\s*=\\s*[\\\"'](?<html>[^\\\"']+)[\\\"']|\\]\\((?<markdown>[^)]+)\\)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
}
