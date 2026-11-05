using System.Text;
using System.Security.Cryptography;
using AgentStudio.Security;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

/// <summary>
/// Pure builders for the two evidence sources every review / aspect prompt must
/// carry in addition to the diff, so a reviewer never false-BLOCKs a task as
/// "deliverables missing" when the work landed somewhere the git working-diff
/// does not show it (AGT-2022 / AGT-1915).
///
/// <list type="bullet">
///   <item><see cref="ResultsInventory"/> lists the job's <c>results/</c> folder
///     (file list + a short excerpt of small text artefacts). A read-only /
///     concept / forensics task legitimately produces no code diff; its
///     deliverable lives here.</item>
///   <item><see cref="ReviewCardMode"/> renders one line naming the card's
///     execution mode and whether a code diff is even expected, so the reviewer
///     reads an empty diff on a planning / research card as legitimate rather
///     than as missing work.</item>
/// </list>
///
/// Both are deliberately best-effort and never throw: a broken results/ read or
/// an unknown mode must degrade to a neutral note, never break the review pass.
/// </summary>
public static class ResultsInventory
{
    private const int ReviewMaximumFiles = 40;
    private const int ReviewMaximumExcerpts = 12;
    private const int ReviewMaximumExcerptChars = 4_000;
    private const int ReviewMaximumTextChars = 24_000;

    /// <summary>Extensions we treat as small text artefacts worth excerpting inline.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".log", ".csv", ".yaml", ".yml", ".html", ".svg", ".xml", ".diff", ".patch",
    };

    /// <summary>
    /// Bounded evidence frozen into a remote review plan. The digest includes
    /// every eligible file, including files whose names or contents exceed the
    /// prompt budget. History and links are excluded from both views.
    /// </summary>
    public static string RenderForReview(string jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath))
            return "No results/ folder (job folder path unavailable).";
        var root = Path.Combine(jobFolderPath, "results");
        if (!Directory.Exists(root)) return "No results/ folder present for this task.";
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return "results/ folder is a link; evidence withheld.";
            var files = SafeFiles(root).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) return "results/ folder present but empty.";

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var lines = new StringBuilder();
            lines.AppendLf($"results/ folder contains {files.Length} file(s):");
            for (var i = 0; i < files.Length; i++)
            {
                var relative = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
                var size = new FileInfo(files[i]).Length;
                using var stream = File.OpenRead(files[i]);
                var fileHash = SHA256.HashData(stream);
                digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + size + "\0"));
                digest.AppendData(fileHash);
                if (i < ReviewMaximumFiles)
                    lines.AppendLf($"- results/{CredentialRedactor.Redact(relative)} ({size} bytes)");
            }
            if (files.Length > ReviewMaximumFiles)
                lines.AppendLf($"- ... and {files.Length - ReviewMaximumFiles} more file(s).");
            lines.AppendLf($"results/ content SHA-256: {Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()}");

            var remaining = ReviewMaximumTextChars;
            var excerpted = 0;
            foreach (var file in files)
            {
                if (excerpted == ReviewMaximumExcerpts || remaining == 0) break;
                var extension = Path.GetExtension(file);
                if (!TextExtensions.Contains(extension) || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)) continue;
                var limit = Math.Min(ReviewMaximumExcerptChars, remaining);
                var raw = extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                    ? BoundedFileRead.ReadTailText(file, (limit + 1) * 4, out _)
                    : BoundedFileRead.ReadHeadText(file, (limit + 1) * 4);
                var truncated = raw.Length > limit || new FileInfo(file).Length > (limit + 1) * 4;
                var selected = raw.Length > limit
                    ? extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ? raw[^limit..] : raw[..limit]
                    : raw;
                if (string.IsNullOrWhiteSpace(selected)) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                lines.AppendLf();
                lines.AppendLf($"{(extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ? "Tail" : "Head")} of results/{CredentialRedactor.Redact(relative)}:");
                lines.AppendLf(CredentialRedactor.Redact(selected.Trim()));
                if (truncated) lines.AppendLf("... (truncated; inspect the named artifact for more)");
                remaining -= selected.Length;
                excerpted++;
            }
            if (excerpted == ReviewMaximumExcerpts || remaining == 0)
                lines.AppendLf("Results text excerpt budget reached; other listed artifacts were not opened.");
            return lines.ToString().TrimEnd();
        }
        catch (Exception)
        {
            return "results/ folder present but could not be read.";
        }
    }

    private static IEnumerable<string> SafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(entry))
                {
                    if (directory == root && Path.GetFileName(entry).Equals("history", StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push(entry);
                }
                else if (File.Exists(entry)) yield return entry;
            }
        }
    }

    /// <summary>
    /// Render the <c>results/</c> folder of a job into a compact inventory string
    /// for a review prompt: every file (relative path + size), plus a short
    /// leading excerpt of the first few small text files so the reviewer can see
    /// what the deliverable actually is. Directories are walked recursively.
    /// Returns a stable "no artefacts" line when the folder is missing or empty so
    /// the prompt always has an unambiguous statement rather than a blank slot.
    /// </summary>
    public static string Render(
        string jobFolderPath,
        int maxFiles = 40,
        int maxExcerptFiles = 6,
        int maxExcerptChars = 600)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath))
            return "No results/ folder (job folder path unavailable).";

        var resultsDir = Path.Combine(jobFolderPath, "results");
        if (!Directory.Exists(resultsDir))
            return "No results/ folder present for this task.";

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories)
                // Requeue history is audit evidence, not an active deliverable.
                .Where(path => !AgentStudio.Tasks.TaskPaths.IsResultsHistoryPath(resultsDir, path))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return "results/ folder present but could not be read.";
        }

        if (files.Count == 0)
            return "results/ folder present but empty.";

        var sb = new StringBuilder();
        sb.AppendLf($"results/ folder contains {files.Count} file(s):");
        var shown = Math.Min(files.Count, maxFiles);
        for (var i = 0; i < shown; i++)
        {
            var rel = RelativePath(resultsDir, files[i]);
            long size;
            try { size = new FileInfo(files[i]).Length; }
            catch { size = -1; }
            sb.AppendLf(size >= 0 ? $"- {rel} ({size} bytes)" : $"- {rel}");
        }
        if (files.Count > shown)
            sb.AppendLf($"- ... and {files.Count - shown} more file(s).");

        // Short excerpts of the first few small text artefacts so the reviewer
        // sees the actual deliverable, not just names. Non-text / oversized files
        // are listed above but never excerpted here.
        var excerpted = 0;
        foreach (var file in files)
        {
            if (excerpted >= maxExcerptFiles) break;
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;

            string excerpt;
            try
            {
                // Read just past the excerpt (at most four UTF-8 bytes per
                // character) so an agent-written file of any size costs the
                // same as a small one (AGT-2991).
                var text = BoundedFileRead.ReadHeadText(file, (maxExcerptChars + 1) * 4);
                excerpt = text.Length > maxExcerptChars ? text[..maxExcerptChars] + "\n... (truncated)" : text;
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(excerpt)) continue;

            sb.AppendLf();
            sb.AppendLf($"Excerpt of {RelativePath(resultsDir, file)}:");
            sb.AppendLf(excerpt.Trim());
            excerpted++;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// True when <c>results/</c> contains current deliverables. Rotated review
    /// history is deliberately excluded because it predates the active
    /// operator-owned assessment epoch.
    /// </summary>
    public static bool HasActiveArtifacts(string jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return false;
        try
        {
            var resultsDir = Path.Combine(jobFolderPath, "results");
            return Directory.Exists(resultsDir)
                && Directory.EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories)
                    .Any(path => !AgentStudio.Tasks.TaskPaths.IsResultsHistoryPath(resultsDir, path));
        }
        catch
        {
            return false;
        }
    }

    private static string RelativePath(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        return rel.Replace('\\', '/');
    }
}

/// <summary>
/// Renders the one-line card-mode framing every review prompt carries so an
/// empty code diff is read correctly. Read-only modes (planning / research)
/// legitimately produce no diff; their deliverable is a report under
/// <c>results/</c> or a doc commit. See <see cref="AgentStudio.Shared.TaskModes"/>.
/// </summary>
public static class ReviewCardMode
{
    public static string Describe(string? mode)
    {
        var normalized = TaskModes.Normalize(mode);
        return normalized switch
        {
            TaskModes.Planning =>
                "Card mode: planning (read-only). This task analyses the codebase and produces a written plan. "
                + "It legitimately ships NO code diff - its deliverable is the report under results/ or a docs/ commit. "
                + "Do NOT treat an empty or tiny diff as missing work.",
            TaskModes.Research =>
                "Card mode: research (read-only). This task is fact-finding and produces a written report. "
                + "It legitimately ships NO code diff - its deliverable is the report under results/ or a docs/ commit. "
                + "Do NOT treat an empty or tiny diff as missing work.",
            TaskModes.Concept =>
                "Card mode: concept (product-source-read-only). This task delivers one Dossier under "
                + "docs/<slug>/ with workbench.json and index.html. Only that dossier diff is legitimate. "
                + "Review completeness, alternatives, recommendation, evidence, and open decisions; do not require build/test evidence.",
            _ =>
                "Card mode: coding. A code change set is expected; the deliverables are the committed diff plus any "
                + "artefacts under results/. An empty branch diff with no results/ artefacts and no documented external "
                + "deliverable is a genuine gap.",
        };
    }
}
