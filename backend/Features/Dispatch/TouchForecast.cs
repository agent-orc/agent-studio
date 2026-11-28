using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
using System.Runtime.CompilerServices;

namespace AgentStudio.Dispatch;

public sealed record ForecastAccuracy(double? Precision, double? Recall);
public sealed record TouchDelivery(string ResultSha, string MergeBase, DateTimeOffset RecordedAt,
    IReadOnlyList<string> RealFiles, IReadOnlyList<string> Modules,
    ForecastAccuracy FileAccuracy, ForecastAccuracy ModuleAccuracy)
{
    public TouchForecastV1? Forecast { get; init; }
}
public sealed record TouchForecastV1(IReadOnlyList<string> CitedFiles, IReadOnlyList<string> ForecastFiles,
    IReadOnlyList<string> Modules, IReadOnlyList<string> HubFiles, string Confidence, string IndexRevision)
{
    public int Version { get; init; } = 1;
    public string RepositoryStatus { get; init; } = "available";
    public string IntegrationTip { get; init; } = "";
    public string PromptRevision { get; init; } = "";
    public DateTimeOffset ComputedAt { get; init; }
    public DateTime ReadyEntryAt { get; init; }
    public IReadOnlyList<TouchDelivery> Deliveries { get; init; } = [];
}

public sealed record CoChangeIndex(string Revision, DateTimeOffset AsOf, string IntegrationTip,
    IReadOnlyList<string> TrackedFiles, Dictionary<string, string[]> Expansion);

/// <summary>Model-free facts only. Admission and dispatcher decisions belong to the decision library.</summary>
public static class TouchForecast
{
    // Integration inventories are immutable snapshots. Key by the list instance so
    // a record clone with a replacement tree cannot inherit an older lookup.
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, ForecastInventory> Inventories = new();
    private sealed class ForecastInventory
    {
        public HashSet<string> Files { get; }
        public Dictionary<string, string> UniqueNames { get; }
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public ForecastInventory(IReadOnlyList<string> tracked)
        {
            Files = tracked.ToHashSet(StringComparer.Ordinal);
            UniqueNames = tracked.GroupBy(Path.GetFileName, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key!, group => group.Single(), StringComparer.Ordinal);
            foreach (var file in tracked)
            {
                for (var slash = file.LastIndexOf('/'); slash > 0; slash = file.LastIndexOf('/', slash - 1))
                    Directories.Add(file[..slash]);
            }
        }
    }

    public static readonly string[] DefaultHubFiles =
    [
        "backend/Host/Program.cs", "backend/Features/Security/Iam/RoutePermissionCatalog.cs",
        "frontend/src/app/models/task.model.ts",
    ];
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".fs", ".fsproj", ".props", ".targets", ".json", ".yaml", ".yml", ".toml", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".html", ".css", ".scss",
        ".sass", ".less", ".razor", ".cshtml", ".py", ".sh", ".ps1", ".go", ".rs", ".java",
        ".kt", ".rb", ".php", ".sql", ".vue", ".svelte", ".c", ".cpp", ".h", ".swift",
    };
    public static bool IsCode(string path) => !path.StartsWith("docs/", StringComparison.Ordinal)
        && CodeExtensions.Contains(Path.GetExtension(path));

    public static string Module(string path)
    {
        var parts = path.Split('/');
        var length = path.StartsWith("frontend/src/app/features/", StringComparison.Ordinal) ? 5
            : path.StartsWith("frontend/src/app/", StringComparison.Ordinal) ? 4
            : path.StartsWith("frontend/e2e/", StringComparison.Ordinal) ? 3
            : path.StartsWith("backend/Features/", StringComparison.Ordinal) ? 3 : 1;
        return string.Join('/', parts.Take(Math.Min(length, parts.Length)));
    }

    public static CoChangeIndex BuildIndex(string tip, DateTimeOffset asOf, IEnumerable<string> tracked,
        IEnumerable<IReadOnlyList<string>> commits)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairs = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var files = commit.Where(IsCode).Distinct(StringComparer.Ordinal).ToArray();
            if (files.Length > 40) continue;
            foreach (var file in files)
            {
                counts[file] = counts.GetValueOrDefault(file) + 1;
                if (!pairs.TryGetValue(file, out var partners)) pairs[file] = partners = new(StringComparer.Ordinal);
                foreach (var partner in files.Where(partner => partner != file))
                    partners[partner] = partners.GetValueOrDefault(partner) + 1;
            }
        }
        var inventory = tracked.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var existing = inventory.ToHashSet(StringComparer.Ordinal);
        var expansion = pairs.ToDictionary(pair => pair.Key,
            pair => pair.Value.Where(partner => partner.Value >= 3 && partner.Value * 2 >= counts[pair.Key]
                    && existing.Contains(partner.Key))
                .Select(partner => partner.Key).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return new($"co-change.v1:{asOf:yyyyMMddTHHmmssfffffffZ}:{tip}", asOf, tip, inventory, expansion);
    }

    public static TouchForecastV1 Compute(string text, CoChangeIndex index, IReadOnlyList<string> hubs,
        IReadOnlyList<string>? citedPaths = null)
    {
        var inventory = Inventories.GetValue(index.TrackedFiles, static tracked => new ForecastInventory(tracked));
        var explicitFiles = (citedPaths ?? []).Where(inventory.Files.Contains).ToHashSet(StringComparer.Ordinal);
        var tokens = explicitFiles.Count > 0 ? explicitFiles : Regex.Matches(text.Replace('\\', '/'), @"[\p{L}\p{N}_.@/-]+")
            .Select(match => match.Value.TrimEnd('.', ':'))
            .Select(token => token.StartsWith("./", StringComparison.Ordinal) ? token[2..] : token).ToHashSet(StringComparer.Ordinal);
        var cited = tokens.Where(inventory.Files.Contains).Concat(tokens.Where(inventory.UniqueNames.ContainsKey)
            .Select(token => inventory.UniqueNames[token])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var files = cited.Concat(cited.SelectMany(file => index.Expansion.GetValueOrDefault(file) ?? []))
            .Where(inventory.Files.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var modules = Modules(files, hubs);
        if (cited.Length == 0)
        {
            // Directory citations can identify a module without pretending to predict files.
            var directories = tokens.Where(token => token.Contains('/')).Select(token => token.TrimEnd('/'))
                .Where(inventory.Directories.Contains).ToArray();
            modules = directories.Length == 0 ? [] : index.TrackedFiles.Except(hubs, StringComparer.Ordinal)
                .Where(file => directories.Any(directory => file.StartsWith(directory + "/", StringComparison.Ordinal)))
                .Select(Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        return new(cited, files, modules, hubs.ToArray(), cited.Length > 0 ? "cited" : modules.Length > 0 ? "module-only" : "none", index.Revision)
        { IntegrationTip = index.IntegrationTip };
    }

    public static string[] Modules(IEnumerable<string> files, IReadOnlyList<string> hubs) => files
        .Except(hubs, StringComparer.Ordinal).Select(Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static DispatchForecast ForDecision(TouchForecastV1? forecast) => forecast is null ? DispatchForecast.Unknown
        : new(forecast.ForecastFiles, forecast.Modules, forecast.Confidence switch
        {
            "cited" => DispatchForecastConfidence.High,
            "module-only" => DispatchForecastConfidence.Low,
            _ => DispatchForecastConfidence.None,
        });

    public static ForecastAccuracy Accuracy(IEnumerable<string> predicted, IEnumerable<string> actual)
    {
        var expected = predicted.ToHashSet(StringComparer.Ordinal);
        var real = actual.ToHashSet(StringComparer.Ordinal);
        var overlap = expected.Intersect(real, StringComparer.Ordinal).Count();
        return new(expected.Count == 0 ? null : (double)overlap / expected.Count,
            real.Count == 0 ? null : (double)overlap / real.Count);
    }
}
