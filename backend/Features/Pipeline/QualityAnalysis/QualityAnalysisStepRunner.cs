using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public static class QualityAnalysisPolicyFiles
{
    public const string RelativePath = ".quality/agent-studio.json";
    public const string SchemaId = "https://agent.studio/schemas/quality-analysis-policy.v1.schema.json";
}

public sealed record QualityAnalysisSelection(
    IReadOnlySet<string> EnabledSteps,
    string? ConfigurationPath,
    IReadOnlyList<string> AngularPaths,
    IReadOnlyList<string> DotNetPaths);

/// <summary>
/// Pure card-class policy. Changed repository paths define the card class;
/// project policy may only come from the versioned repository file. There is
/// deliberately no card, appsettings, or environment override.
/// </summary>
public static class QualityAnalysisPolicy
{
    public static QualityAnalysisSelection Resolve(
        string repositoryPath,
        IReadOnlyList<string>? changedFiles)
    {
        var paths = (changedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var angularPaths = paths.Where(path => IsAngularPath(repositoryPath, path)).ToArray();
        var dotNetPaths = paths.Where(IsDotNetPath).ToArray();
        var enabled = new HashSet<string>(StringComparer.Ordinal)
        {
            // Every classified project receives its stack's core rules by
            // convention. Axis additions are explicit below.
        };

        if (angularPaths.Length > 0)
        {
            enabled.Add(PipelineCatalogue.QualityAngularRulesStepId);
            enabled.Add(PipelineCatalogue.QualityVisualStepId);
        }
        if (dotNetPaths.Length > 0)
        {
            enabled.Add(PipelineCatalogue.QualityDotNetRulesStepId);
            enabled.Add(PipelineCatalogue.QualitySecurityStepId);
        }

        var configurationPath = ApplyRepositoryOverrides(repositoryPath, enabled);
        return new QualityAnalysisSelection(enabled, configurationPath, angularPaths, dotNetPaths);
    }

    private static string? ApplyRepositoryOverrides(string repositoryPath, HashSet<string> enabled)
    {
        var path = Path.Combine(repositoryPath,
            QualityAnalysisPolicyFiles.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Any(property => property.Name is not ("$schema" or "schemaVersion" or "steps"))
            || !root.TryGetProperty("$schema", out var schema)
            || schema.ValueKind != JsonValueKind.String
            || !string.Equals(schema.GetString(), QualityAnalysisPolicyFiles.SchemaId, StringComparison.Ordinal)
            || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1
            || !root.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{QualityAnalysisPolicyFiles.RelativePath} must be a v1 Quality Studio pipeline policy object.");
        }

        var known = PipelineCatalogue.QualityAnalysisStepIds.ToHashSet(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateObject())
        {
            if (!known.Contains(step.Name))
                throw new InvalidDataException(
                    $"{QualityAnalysisPolicyFiles.RelativePath} references unknown analysis step '{step.Name}'.");
            if (step.Value.ValueKind != JsonValueKind.Object
                || step.Value.EnumerateObject().Any(property => property.Name != "enabled")
                || !step.Value.TryGetProperty("enabled", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException(
                    $"{QualityAnalysisPolicyFiles.RelativePath} override '{step.Name}' must contain one boolean 'enabled'.");
            }
            if (value.GetBoolean()) enabled.Add(step.Name);
            else enabled.Remove(step.Name);
        }
        return QualityAnalysisPolicyFiles.RelativePath;
    }

    private static bool IsAngularPath(string repositoryPath, string path)
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".ts" or ".html" or ".css" or ".scss")) return false;

        var repositoryRoot = Path.GetFullPath(repositoryPath);
        var candidate = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(
            repositoryRoot,
            path.Replace('/', Path.DirectorySeparatorChar))));
        while (candidate is not null && IsWithin(repositoryRoot, candidate))
        {
            if (File.Exists(Path.Combine(candidate, "angular.json"))) return true;
            if (string.Equals(candidate, repositoryRoot, StringComparison.Ordinal)) break;
            candidate = Path.GetDirectoryName(candidate);
        }
        return false;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                comparison);
    }

    private static bool IsDotNetPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".cs" or ".csproj" or ".fs" or ".fsproj" or ".sln" or ".slnx";
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');
}

public sealed record QualityStudioLocation(string Path, int? StartLine, int? StartColumn);

public sealed record QualityStudioFinding(
    string Id,
    string RuleId,
    string Aspect,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    string? Evidence,
    IReadOnlyList<QualityStudioLocation> Locations);

public sealed record QualityStudioCoreResult(
    bool Available,
    string? UnavailableReason,
    string Producer,
    string? ProducerVersion,
    IReadOnlyList<QualityStudioFinding> Findings);

public interface IQualityStudioAnalysisCore
{
    Task<QualityStudioCoreResult> RunAsync(
        string repositoryPath,
        string analysisName,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads deterministic findings from the Quality Studio HTTP sensor API.
/// The registration must point at this exact checkout; a sibling checkout is
/// not valid evidence for the current review subject.
/// </summary>
public sealed class QualityStudioAnalysisCoreAdapter : IQualityStudioAnalysisCore
{
    internal const string RulesAnalysisName = "quality-rules";
    private readonly HttpClient client;
    private readonly IConfiguration configuration;

    public QualityStudioAnalysisCoreAdapter(HttpClient client, IConfiguration configuration)
    {
        this.client = client;
        this.configuration = configuration;
    }

    public async Task<QualityStudioCoreResult> RunAsync(
        string repositoryPath,
        string analysisName,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var baseUrl = this.configuration["QualityStudio:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Unavailable("Quality Studio API URL is not configured.");
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https"))
            return Unavailable("Quality Studio API URL must be an absolute HTTP URL.");
        if (analysisName != RulesAnalysisName)
            return Unavailable($"Analysis '{analysisName}' is not supported by the HTTP adapter.");
        try
        {
            using var registrations = await SendAsync(origin, HttpMethod.Get, "api/repos", cancellationToken);
            var repositories = registrations.RootElement.GetProperty("repositories");
            var expectedRoot = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var registered = repositories.EnumerateArray().FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.GetProperty("rootPath").GetString()!)
                    .TrimEnd(Path.DirectorySeparatorChar), expectedRoot, comparison));
            if (registered.ValueKind == JsonValueKind.Undefined)
                return Unavailable("Quality Studio has no registration for this exact checkout.");
            var repositoryId = registered.GetProperty("id").GetString()!;
            var route = $"api/repos/{Uri.EscapeDataString(repositoryId)}/sensors/eslint/scan?path=frontend";
            using var scan = await SendAsync(origin, HttpMethod.Post, route, cancellationToken);
            var root = scan.RootElement;
            var available = root.GetProperty("available").GetBoolean();
            var reason = root.TryGetProperty("unavailableReason", out var unavailable) && unavailable.ValueKind == JsonValueKind.String
                ? unavailable.GetString() : null;
            var provenance = root.GetProperty("provenance");
            var version = provenance.TryGetProperty("sensorVersion", out var sensorVersion)
                ? sensorVersion.GetString() : null;
            var wanted = relativePaths.Select(path => path.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var findings = root.GetProperty("findings").EnumerateArray()
                .Select(MapFinding)
                .Where(finding => finding.Locations.Any(location => wanted.Contains(location.Path)))
                .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .ToArray();
            return new QualityStudioCoreResult(available, reason, "quality-studio:eslint", version, findings);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable($"Quality Studio HTTP analysis failed ({ex.GetBaseException().Message}).");
        }
    }

    private async Task<JsonDocument> SendAsync(Uri origin, HttpMethod method, string route, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(origin, route));
        var token = configuration["QualityStudio:ApiToken"];
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var clientId = configuration["QualityStudio:ClientId"];
        if (!string.IsNullOrWhiteSpace(clientId))
            request.Headers.Add("X-Client-Id", clientId);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static QualityStudioFinding MapFinding(JsonElement finding)
    {
        var locations = finding.GetProperty("locations").EnumerateArray().Select(location =>
        {
            var start = location.TryGetProperty("range", out var range)
                && range.ValueKind == JsonValueKind.Object
                && range.TryGetProperty("start", out var position)
                ? position : default;
            return new QualityStudioLocation(
                location.GetProperty("path").GetString()!.Replace('\\', '/'),
                start.ValueKind == JsonValueKind.Object ? start.GetProperty("line").GetInt32() : null,
                start.ValueKind == JsonValueKind.Object ? start.GetProperty("column").GetInt32() : null);
        }).ToArray();
        return new QualityStudioFinding(
            finding.GetProperty("id").GetString()!,
            finding.GetProperty("ruleId").GetString()!,
            finding.GetProperty("aspect").GetString()!,
            finding.GetProperty("severity").GetString()!.ToLowerInvariant(),
            finding.GetProperty("title").GetString()!,
            finding.GetProperty("description").GetString()!,
            finding.GetProperty("recommendation").GetString()!,
            finding.GetProperty("fingerprint").GetString()!,
            finding.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.String
                ? evidence.GetString() : null,
            locations);
    }

    private static QualityStudioCoreResult Unavailable(string reason) =>
        new(false, reason, "quality-studio:eslint", null, []);
}

public enum QualityAnalysisStepVerdict
{
    NotApplicable,
    Disabled,
    Unavailable,
    Passed,
    Findings,
}

public sealed record QualityAnalysisStepResult(
    string StepId,
    QualityAnalysisStepVerdict Verdict,
    long DurationMs,
    string Reason,
    string? EvidencePath,
    IReadOnlyList<QualityStudioFinding> Findings,
    IReadOnlyList<QualityStudioFinding> BlockingFindings);

public interface IQualityAnalysisStepRunner
{
    Task<QualityAnalysisStepResult> RunAngularRulesAsync(
        string repositoryPath,
        string taskFolderPath,
        IReadOnlyList<string>? changedFiles,
        int? runIndex,
        CancellationToken cancellationToken);
}

public sealed class QualityAnalysisStepRunner : IQualityAnalysisStepRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IQualityStudioAnalysisCore core;
    private readonly ILogger<QualityAnalysisStepRunner> logger;

    public QualityAnalysisStepRunner(
        IQualityStudioAnalysisCore core,
        ILogger<QualityAnalysisStepRunner> logger)
    {
        this.core = core;
        this.logger = logger;
    }

    public async Task<QualityAnalysisStepResult> RunAngularRulesAsync(
        string repositoryPath,
        string taskFolderPath,
        IReadOnlyList<string>? changedFiles,
        int? runIndex,
        CancellationToken cancellationToken)
    {
        const string stepId = PipelineCatalogue.QualityAngularRulesStepId;
        QualityAnalysisSelection selection;
        try
        {
            selection = QualityAnalysisPolicy.Resolve(repositoryPath, changedFiles);
        }
        catch (Exception ex)
        {
            return new QualityAnalysisStepResult(
                stepId, QualityAnalysisStepVerdict.Unavailable, 0,
                ex.Message, null, [], []);
        }

        if (!selection.EnabledSteps.Contains(stepId))
        {
            return new QualityAnalysisStepResult(
                stepId,
                selection.AngularPaths.Count > 0
                    ? QualityAnalysisStepVerdict.Disabled
                    : QualityAnalysisStepVerdict.NotApplicable,
                0,
                selection.AngularPaths.Count > 0
                    ? $"repository policy disabled {stepId}"
                    : "card did not touch Angular files",
                null,
                [],
                []);
        }
        if (selection.AngularPaths.Count == 0)
        {
            return new QualityAnalysisStepResult(
                stepId, QualityAnalysisStepVerdict.NotApplicable, 0,
                "no changed Angular files were available for file-scoped analysis", null, [], []);
        }

        var started = DateTime.UtcNow;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await core.RunAsync(
            repositoryPath,
            QualityStudioAnalysisCoreAdapter.RulesAnalysisName,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reviewKind"] = "code" },
            selection.AngularPaths,
            cancellationToken);
        timer.Stop();

        var relativeEvidencePath = $"results/quality-analysis/{stepId}.json";
        var evidencePath = Path.Combine(
            taskFolderPath,
            relativeEvidencePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var report = new
        {
            schemaVersion = 1,
            stepId,
            analysis = QualityStudioAnalysisCoreAdapter.RulesAnalysisName,
            policy = new
            {
                source = selection.ConfigurationPath ?? "convention",
                ruleConfiguration = ".quality/rules.json",
                reportOnly = true,
            },
            startedAt = started,
            completedAt = DateTime.UtcNow,
            result.Available,
            result.UnavailableReason,
            result.Producer,
            result.ProducerVersion,
            changedFiles = selection.AngularPaths,
            findings = result.Findings,
            blockingFindingIds = Array.Empty<string>(),
        };
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, JsonOptions));

        foreach (var finding in result.Findings)
        {
            ReviewEvidenceLog.Append(taskFolderPath, new ReviewEvidenceEntry
            {
                Id = $"quality-studio:{finding.Fingerprint}",
                Source = ReviewEvidenceSources.CodeReview,
                Severity = EvidenceSeverity(finding.Severity),
                Title = finding.Title,
                RuleId = finding.RuleId,
                Body = $"{finding.Description}\n\nRecommendation: {finding.Recommendation}"
                    + (string.IsNullOrWhiteSpace(finding.Evidence) ? "" : $"\n\nEvidence: {finding.Evidence}"),
                CreatedAt = DateTime.UtcNow,
                RunIndex = runIndex,
                Artifacts = [relativeEvidencePath],
                FileRefs = finding.Locations.Select(LocationReference).ToList(),
            });
        }

        logger.LogInformation(
            "Quality Studio analysis {StepId} completed available={Available} findings={Findings} blocking={Blocking} durationMs={DurationMs}",
            stepId, result.Available, result.Findings.Count, 0, timer.ElapsedMilliseconds);
        var verdict = !result.Available
            ? QualityAnalysisStepVerdict.Unavailable
            : result.Findings.Count == 0 ? QualityAnalysisStepVerdict.Passed : QualityAnalysisStepVerdict.Findings;
        var reason = !result.Available
            ? result.UnavailableReason ?? "Quality Studio analysis was unavailable"
            : $"{result.Findings.Count} finding(s) recorded as review evidence";
        return new QualityAnalysisStepResult(
            stepId, verdict, timer.ElapsedMilliseconds, reason,
            relativeEvidencePath, result.Findings, []);
    }

    private static string EvidenceSeverity(string severity) => severity switch
    {
        "critical" or "high" => ReviewEvidenceSeverities.High,
        "medium" => ReviewEvidenceSeverities.Warn,
        _ => ReviewEvidenceSeverities.Info,
    };

    private static string LocationReference(QualityStudioLocation location) =>
        location.StartLine is > 0 ? $"{location.Path}:{location.StartLine}" : location.Path;
}
