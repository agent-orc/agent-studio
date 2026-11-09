namespace AgentStudio.Pipeline;

/// <summary>
/// Classifies an accepted delivery as documentation-only (the AGT-2417
/// research/docs rule): every changed path is documentation or task evidence
/// and none is product code. A docs-only delivery cannot change a build or
/// test signal, so it integrates through a light gate - a conflict-checked
/// merge probe - instead of the full-suite release gate and its
/// rebased-onto-main requirement. Its batch and per-task gates run the
/// build-only level (AGT-3414).
///
/// <para>
/// Fail closed: an unknown diff (null) or an empty change set is NOT
/// docs-only; those deliveries stay on the strict release path. The path
/// classes are deliberately conservative. Code, tests and their fixtures,
/// build files, templates, prompts, scripts and every dot directory such as
/// <c>.agent-studio/</c> or <c>.github/</c> count as product behaviour, not
/// documentation, even when the file is Markdown.
/// </para>
/// </summary>
public static class DocsOnlyDeliveryPolicy
{
    // A file below one of these folders feeds code, a test, a build or an
    // agent prompt, whatever its extension.
    private static readonly HashSet<string> ProductFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompts", "templates", "template", "project-templates", "scripts", "agent-rules",
        "src", "test", "tests", "testdata", "fixtures", "e2e", "__tests__",
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".csproj", ".fs", ".fsproj", ".props", ".targets", ".sln", ".slnx",
        ".razor", ".cshtml", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
        ".py", ".sh", ".bash", ".ps1", ".psm1", ".cmd", ".bat", ".go", ".rs", ".java", ".kt",
        ".rb", ".php", ".sql", ".scss", ".sass", ".less", ".yml", ".yaml", ".toml", ".xml",
        ".config", ".lock",
    };

    private static readonly HashSet<string> BuildFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "angular.json", "global.json", "nuget.config",
        "Dockerfile", "Makefile", "project.yml",
    };

    public static bool IsDocsOnly(IReadOnlyList<string>? changedPaths)
    {
        if (changedPaths is null || changedPaths.Count == 0) return false;
        foreach (var path in changedPaths)
            if (!IsDocsPath(path)) return false;
        return true;
    }

    internal static bool IsDocsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        var file = segments[^1];
        if (segments.Any(segment => segment.StartsWith('.'))) return false;
        if (segments[..^1].Any(IsProductFolder)) return false;
        if (BuildFileNames.Contains(file)
            || file.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || CodeExtensions.Contains(Path.GetExtension(file)))
            return false;
        if (segments.Length > 1 && segments[0].ToLowerInvariant() is "docs" or "results" or "attachments")
            return true;
        return file.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProductFolder(string segment)
        => ProductFolders.Contains(segment)
           || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
           || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase);
}
