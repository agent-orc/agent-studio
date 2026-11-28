using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Derives the bounded Angular unit-test slice used when a review or integration merge
/// changes <c>frontend/</c>. The command is built only from an existing Angular
/// package script and repository-owned spec paths. It never accepts executable
/// text from the diff.
/// </summary>
internal static class FrontendWorkPackagePlanner
{
    internal const string ReviewScope = "review-angular";
    private const string FrontendPrefix = "frontend/";

    private static readonly string[] CollisionHotspots =
    [
        "src/app/app.spec.ts",
        "src/app/features/studio-shell/studio-shell.component.spec.ts",
        "src/app/features/task-detail/task-detail.spec.ts",
    ];

    public static bool TouchesFrontend(IEnumerable<string>? changedFiles)
        => changedFiles?.Any(path => Normalize(path).StartsWith(
            FrontendPrefix, StringComparison.OrdinalIgnoreCase)
            && new[] { ".ts", ".js", ".mjs", ".html", ".css", ".scss", ".sass", ".less", ".json" }
                .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) == true;

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
        IReadOnlyList<string> changedFiles,
        IReadOnlyDictionary<string, string>? sourceTree = null)
    {
        if (!TouchesFrontend(changedFiles)) return null;
        var files = sourceTree ?? ReadSourceTree(repositoryPath);
        var script = AngularTestScript(files.GetValueOrDefault("frontend/package.json"));
        if (script is null) return null;

        var sources = files.Where(pair => IsUnitSource(pair.Key))
            .ToDictionary(pair => pair.Key, pair => StripComments(pair.Value), StringComparer.Ordinal);
        var specs = sources.Keys.Where(IsSpec).ToHashSet(StringComparer.Ordinal);
        var changed = changedFiles.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var production = sources.Keys.Where(path => !IsSpec(path)).ToArray();
        var owners = production.Where(path => changed.Contains(path)
            || OwnsResource(path, sources[path], changed)).ToHashSet(StringComparer.Ordinal);
        var includes = specs.Where(changed.Contains).ToHashSet(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        void Companion(string source)
        {
            var spec = source[..^3] + ".spec.ts";
            if (specs.Contains(spec)) includes.Add(spec);
            else missing.Add(spec);
        }
        foreach (var owner in owners) Companion(owner);

        var modules = new ModuleIndex(sources, files);
        var seeds = owners.SelectMany(path => modules.Exports(path)
            .Select(name => modules.Owner(path, name)))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var source in sources.Keys)
        {
            if (!modules.ImportsChanged(source, seeds)
                && !(IsSpec(source) && modules.ReferencesChanged(source, seeds))) continue;
            if (IsSpec(source)) includes.Add(source);
            else if (!owners.Contains(source)) Companion(source);
        }

        // The cap applies to the union of all touched features, before the
        // mandatory companions, consumers and collision floor are added.
        var features = changed.Select(Feature).Where(path => path is not null).ToHashSet(StringComparer.Ordinal);
        var featureSpecs = specs.Where(spec => features.Any(feature => spec.StartsWith(feature!, StringComparison.Ordinal)))
            .ToArray();
        if (featureSpecs.Length <= 8) includes.UnionWith(featureSpecs);
        foreach (var hotspot in CollisionHotspots)
            if (specs.Contains(FrontendPrefix + hotspot)) includes.Add(FrontendPrefix + hotspot);
            else missing.Add(FrontendPrefix + hotspot);

        // Exact paths make the selected inventory auditable and avoid overlaps
        // between a feature glob, a companion and a fixed collision spec.
        if (includes.Count == 0) return null;
        var includeArguments = includes.OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"--include=\"{path[FrontendPrefix.Length..]}\"");
        var command = script == "test"
            ? $"npm test -- --watch=false --progress=false {string.Join(' ', includeArguments)}"
            : $"npm run {script} -- {string.Join(' ', includeArguments)}";
        return new VerifyCommand(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", command)
        {
            TestScope = TestExecutionLevels.WorkPackage,
            SelectionReason = "frontend: changed specs, component/resource companions, named references and immediate consumers; "
                + (featureSpecs.Length <= 8 ? $"touched-feature union {featureSpecs.Length} specs" :
                    $"touched-feature union {featureSpecs.Length} specs exceeds eight; full feature coverage remains post-merge")
                + "; shell/task-detail collision specs; missing matches: "
                + (missing.Count == 0 ? "none" : string.Join(", ", missing))
                + "; selected only, execution and zero-test evidence come from run-unit-tests",
        };
    }

    internal static bool IsSourceTreePath(string path)
        => path == "frontend/package.json"
            || (path.StartsWith(FrontendPrefix, StringComparison.Ordinal)
                && Path.GetFileName(path).StartsWith("tsconfig", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal))
            || IsUnitSource(path);

    private static IReadOnlyDictionary<string, string> ReadSourceTree(string repositoryPath)
    {
        var root = Path.Combine(repositoryPath, "frontend");
        if (!Directory.Exists(root)) return new Dictionary<string, string>();
        var paths = new List<string>();
        // Never walk node_modules or build output. Angular application and
        // library sources are the discovery roots, not Playwright's e2e tree.
        foreach (var directory in new[] { "src", "projects" })
        {
            var absolute = Path.Combine(root, directory);
            if (Directory.Exists(absolute)) paths.AddRange(Directory.EnumerateFiles(absolute, "*.ts", SearchOption.AllDirectories));
        }
        paths.AddRange(Directory.EnumerateFiles(root, "tsconfig*.json"));
        paths.Add(Path.Combine(root, "package.json"));
        return paths.Where(File.Exists).Select(path => (Path: Normalize(Path.GetRelativePath(repositoryPath, path)), Content: File.ReadAllText(path)))
            .Where(file => IsSourceTreePath(file.Path)).ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
    }

    private static bool IsUnitSource(string path)
        => (path.StartsWith("frontend/src/", StringComparison.Ordinal) || path.StartsWith("frontend/projects/", StringComparison.Ordinal))
            && path.EndsWith(".ts", StringComparison.Ordinal) && IsSafeIncludePath(path)
            && !path.Split('/').Any(part => part is "e2e" or "node_modules" or "dist" or "test-results")
            && !path.Contains(".e2e.", StringComparison.Ordinal);

    private static bool IsSpec(string path) => path.EndsWith(".spec.ts", StringComparison.Ordinal);

    private static string? Feature(string path)
    {
        const string prefix = "frontend/src/app/features/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = path.IndexOf('/', prefix.Length);
        return end < 0 ? null : path[..(end + 1)];
    }

    private static bool OwnsResource(string path, string content, HashSet<string> changed)
    {
        var stem = path[..^3];
        if (changed.Any(resource => resource.StartsWith(stem + ".", StringComparison.Ordinal)
                && new[] { ".html", ".css", ".scss", ".sass", ".less" }.Contains(Path.GetExtension(resource)))) return true;
        foreach (Match resource in Regex.Matches(content,
                     @"(?:templateUrl|styleUrl)\s*:\s*['""](?<path>[^'""]+)['""]|styleUrls\s*:\s*\[(?<styles>[^\]]*)\]"))
        {
            var urls = resource.Groups["path"].Success ? new[] { resource.Groups["path"].Value }
                : Regex.Matches(resource.Groups["styles"].Value, @"['""]([^'""]+)['""]").Select(match => match.Groups[1].Value);
            if (urls.Any(url => changed.Contains(Relative(path, url)))) return true;
        }
        return false;
    }

    private static string Relative(string source, string relative)
        => Normalize(Path.GetRelativePath("/", Path.GetFullPath(Path.Combine("/", Path.GetDirectoryName(source)!, relative))));

    private static string StripComments(string content)
        => Regex.Replace(content, @"'[^'\\]*(?:\\.[^'\\]*)*'|""[^""\\]*(?:\\.[^""\\]*)*""|`[^`]*`|(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)",
            match => match.Groups["comment"].Success ? " " : match.Value);

    // Resolve named bindings to their declaring module, not just a same-name
    // token. Re-exports can rename bindings or pass them through index barrels.
    private sealed class ModuleIndex(Dictionary<string, string> sources, IReadOnlyDictionary<string, string> files)
    {
        private readonly Dictionary<(string, string), string?> _owners = [];
        private readonly Dictionary<string, string[]> _exports = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _aliases = ReadAliases(files);
        private static readonly Regex Bindings = new(
            @"\b(?<kind>import|export)\s+(?:type\s+)?\{(?<names>[^}]+)\}\s*(?:from\s*['""](?<module>[^'""]+)['""])?", RegexOptions.CultureInvariant);
        private static readonly Regex Declarations = new(
            @"\bexport\s+(?:declare\s+)?(?:abstract\s+)?(?:class|interface|type|enum|function|const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant);

        private static IEnumerable<(string Imported, string Local)> Names(Match binding)
            => binding.Groups["names"].Value.Split(',').Select(name => Regex.Replace(name.Trim(), @"^type\s+", ""))
                .Where(name => name.Length > 0).Select(name => Regex.Split(name, @"\s+as\s+"))
                .Select(parts => (parts[0].Trim(), parts[^1].Trim()));

        public IEnumerable<string> Exports(string path) => ExportNames(path, []);

        private string[] ExportNames(string path, HashSet<string> visiting)
        {
            if (_exports.TryGetValue(path, out var cached)) return cached;
            if (!sources.TryGetValue(path, out var content) || !visiting.Add(path)) return [];
            var names = Declarations.Matches(content).Select(match => match.Groups["name"].Value)
                .Concat(Bindings.Matches(content).Where(match => match.Groups["kind"].Value == "export").SelectMany(Names).Select(name => name.Local))
                .ToHashSet(StringComparer.Ordinal);
            foreach (Match star in Regex.Matches(content, @"\bexport\s+\*\s+from\s*['""]([^'""]+)['""]"))
                if (Resolve(path, star.Groups[1].Value) is { } target) names.UnionWith(ExportNames(target, visiting));
            visiting.Remove(path);
            return _exports[path] = names.ToArray();
        }

        public string? Owner(string path, string name) => Owner(path, name, []);

        private string? Owner(string path, string name, HashSet<(string, string)> visiting)
        {
            var key = (path, name);
            if (_owners.TryGetValue(key, out var cached)) return cached;
            if (!sources.TryGetValue(path, out var content) || !visiting.Add(key)) return null;
            string? result = null;
            if (Declarations.Matches(content).Any(match => match.Groups["name"].Value == name)) result = path + "#" + name;
            foreach (Match binding in Bindings.Matches(content).Where(match => match.Groups["kind"].Value == "export"))
            foreach (var pair in Names(binding).Where(pair => pair.Local == name))
            {
                if (binding.Groups["module"].Success && Resolve(path, binding.Groups["module"].Value) is { } target)
                    result = Owner(target, pair.Imported, visiting);
                else
                {
                    var import = Imports(path).FirstOrDefault(import => import.Local == pair.Imported);
                    if (import.Target is not null) result = Owner(import.Target, import.Imported, visiting);
                    else result = path + "#" + pair.Imported;
                }
            }
            if (result is null)
                foreach (Match star in Regex.Matches(content, @"\bexport\s+\*\s+from\s*['""]([^'""]+)['""]"))
                    if (Resolve(path, star.Groups[1].Value) is { } target && Owner(target, name, visiting) is { } owner)
                    { result = owner; break; }
            visiting.Remove(key);
            // A cycle can temporarily hide an export while another branch is
            // being resolved. Cache proven ownership only.
            if (result is not null) _owners[key] = result;
            return result;
        }

        private IEnumerable<(string? Target, string Imported, string Local)> Imports(string path)
            => Bindings.Matches(sources[path]).Where(match => match.Groups["kind"].Value == "import" && match.Groups["module"].Success)
                .SelectMany(binding => Names(binding).Select(pair => (Resolve(path, binding.Groups["module"].Value), pair.Imported, pair.Local)));

        public bool ImportsChanged(string path, HashSet<string> seeds)
            => Imports(path).Any(import => import.Target is not null && Owner(import.Target, import.Imported) is { } owner && seeds.Contains(owner));

        public bool ReferencesChanged(string path, HashSet<string> seeds)
        {
            var imports = Imports(path).ToArray();
            var code = Regex.Replace(sources[path], @"'[^'\\]*(?:\\.[^'\\]*)*'|""[^""\\]*(?:\\.[^""\\]*)*""|`[^`]*`", " ");
            foreach (var name in seeds.Select(seed => seed[(seed.LastIndexOf('#') + 1)..]).Distinct(StringComparer.Ordinal))
            {
                // A same-name binding from another module is not a reference
                // to this owner. Unbound direct references still select a spec.
                if (imports.Any(import => import.Imported == name || import.Local == name)) continue;
                if (Regex.IsMatch(code, @"(?<![\w$])" + Regex.Escape(name) + @"(?![\w$])")) return true;
            }
            return false;
        }

        private string? Resolve(string source, string module)
        {
            var candidates = new List<string>();
            if (module.StartsWith('.')) candidates.Add(Relative(source, module));
            else foreach (var (alias, targets) in _aliases.OrderByDescending(pair => pair.Key.Replace("*", "").Length))
            {
                var star = alias.IndexOf('*');
                if (star < 0 ? module != alias : !module.StartsWith(alias[..star], StringComparison.Ordinal)
                    || !module.EndsWith(alias[(star + 1)..], StringComparison.Ordinal)) continue;
                var replacement = star < 0 ? "" : module[star..(module.Length - (alias.Length - star - 1))];
                candidates.AddRange(targets.Select(target => Relative("frontend/tsconfig.json", target.Replace("*", replacement))));
                break;
            }
            foreach (var candidate in candidates)
                foreach (var file in new[] { candidate, candidate + ".ts", candidate + "/index.ts" })
                    if (sources.ContainsKey(file)) return file;
            return null;
        }

        private static Dictionary<string, string[]> ReadAliases(IReadOnlyDictionary<string, string> files)
        {
            var aliases = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (!files.TryGetValue("frontend/tsconfig.json", out var config)) return aliases;
            using var document = JsonDocument.Parse(config, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.TryGetProperty("compilerOptions", out var options)
                && options.TryGetProperty("paths", out var paths))
            {
                var baseUrl = options.TryGetProperty("baseUrl", out var baseValue) ? baseValue.GetString() ?? "." : ".";
                foreach (var property in paths.EnumerateObject())
                    aliases[property.Name] = property.Value.EnumerateArray()
                        .Select(value => baseUrl.TrimEnd('/') + "/" + value.GetString()).ToArray();
            }
            return aliases;
        }
    }

    private static string? AngularTestScript(string? packageJson)
    {
        if (packageJson is null) return null;
        try
        {
            using var document = JsonDocument.Parse(packageJson);
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

    private static bool IsSafeIncludePath(string path)
        => path.All(character => char.IsLetterOrDigit(character)
            || character is '/' or '-' or '_' or '.');

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
