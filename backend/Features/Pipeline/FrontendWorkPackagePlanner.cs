using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Derives the bounded Angular unit-test slice used when an integration merge
/// changes <c>frontend/</c>. The command is built only from an existing Angular
/// package script and repository-owned spec paths. It never accepts executable
/// text from the diff.
/// </summary>
internal static class FrontendWorkPackagePlanner
{
    private const string FrontendPrefix = "frontend/";

    private static readonly string[] CollisionHotspots =
    [
        "src/app/app.spec.ts",
        "src/app/features/studio-shell/studio-shell.component.spec.ts",
        "src/app/features/task-detail/task-detail.spec.ts",
    ];

    public static bool TouchesFrontend(IEnumerable<string>? changedFiles)
        => changedFiles?.Any(path => Normalize(path).StartsWith(
            FrontendPrefix, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool IsBroadFrontendTest(VerifyCommand command)
    {
        if (command.Kind != VerifyCommandKind.Test
            || command.Command.Contains("--include", StringComparison.OrdinalIgnoreCase))
            return false;
        if (command.Ecosystem == VerifyEcosystem.Node
            && string.Equals(command.WorkingSubdir, "frontend", StringComparison.OrdinalIgnoreCase))
            return true;

        var invokesAngular = command.Command.Contains("ng test", StringComparison.OrdinalIgnoreCase);
        var invokesFrontendNpm = command.Command.Contains("npm", StringComparison.OrdinalIgnoreCase)
            && command.Command.Contains("test", StringComparison.OrdinalIgnoreCase)
            && command.Command.Contains("frontend", StringComparison.OrdinalIgnoreCase);
        return invokesAngular || invokesFrontendNpm;
    }

    public static VerifyCommand? Plan(
        string repositoryPath,
        IReadOnlyList<string> changedFiles)
    {
        if (!TouchesFrontend(changedFiles)) return null;

        var frontendRoot = Path.Combine(repositoryPath, "frontend");
        var script = AngularTestScript(Path.Combine(frontendRoot, "package.json"));
        if (script is null) return null;

        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles.Select(Normalize).Where(path =>
                     path.StartsWith(FrontendPrefix + "src/", StringComparison.OrdinalIgnoreCase)))
        {
            var packageRelative = changedFile[FrontendPrefix.Length..];
            var directory = Normalize(Path.GetDirectoryName(packageRelative) ?? string.Empty).TrimEnd('/');
            if (directory.Length == 0 || !IsSafeIncludePath(directory)) continue;
            var absoluteDirectory = Path.GetFullPath(Path.Combine(
                frontendRoot, directory.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(frontendRoot, absoluteDirectory) || !Directory.Exists(absoluteDirectory)) continue;
            if (Directory.EnumerateFiles(absoluteDirectory, "*.spec.ts", SearchOption.TopDirectoryOnly).Any())
                includes.Add(directory + "/*.spec.ts");
        }

        foreach (var hotspot in CollisionHotspots)
        {
            if (File.Exists(Path.Combine(frontendRoot, hotspot.Replace('/', Path.DirectorySeparatorChar))))
                includes.Add(hotspot);
        }

        if (includes.Count == 0) return null;

        var includeArguments = includes
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"--include=\"{path}\"");
        var command = script == "test"
            ? $"npm test -- --watch=false --progress=false {string.Join(' ', includeArguments)}"
            : $"npm run {script} -- {string.Join(' ', includeArguments)}";

        return new VerifyCommand(
            VerifyEcosystem.Node,
            VerifyCommandKind.Test,
            "frontend",
            command)
        {
            SelectionReason =
                "frontend diff selects touched-folder specs plus the studio-shell and task-detail barrel collision set",
        };
    }

    private static string? AngularTestScript(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var name in new[] { "test:ci", "test" })
            {
                if (!scripts.TryGetProperty(name, out var value)
                    || value.ValueKind != JsonValueKind.String)
                    continue;
                var body = value.GetString();
                // The CI wrapper validates a completed JSON report before it
                // returns success. Keep that check on focused slices as well.
                if (!string.IsNullOrWhiteSpace(body)
                    && (body.Contains("ng test", StringComparison.OrdinalIgnoreCase)
                        || body.Trim() == "node scripts/run-unit-tests.mjs"))
                    return name;
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "FrontendWorkPackagePlanner: package.json parse");
        }
        return null;
    }

    private static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeIncludePath(string path)
        => path.All(character => char.IsLetterOrDigit(character)
            || character is '/' or '-' or '_' or '.');

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
