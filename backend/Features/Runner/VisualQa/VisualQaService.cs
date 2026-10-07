using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Prompts;
using AgentStudio.Registry;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

public sealed record VisualQaRequest(
    TaskInfo Task,
    string RepositoryRoot,
    IReadOnlyCollection<string>? ChangedFiles,
    int Iteration);

public sealed record VisualQaResult(
    bool Applicable,
    VisualQaVerdict Verdict,
    VisualQaDecision Decision,
    IReadOnlyList<string> ScreenshotPaths,
    IReadOnlyList<string> EvidenceScreenshotPaths,
    string? CaptureManifestPath,
    string? VerdictPath,
    string Model,
    string ThinkingLevel,
    int Round,
    int PriorAutomaticRetries,
    // The reviewer call's receipt, resolution level and timing, so the
    // verdict step row records what the decision cost. Null usage means the
    // reviewer was never called (capture failed or CLI unavailable).
    OrchestratorTokenUsage? Usage = null,
    string? ModelSource = null,
    DateTime? ReviewStartedAt = null,
    long ReviewDurationMs = 0)
{
    public static VisualQaResult NotApplicable { get; } = new(
        false,
        new VisualQaVerdict(VisualQaVerdictStatus.Acceptable, "Visual QA is not applicable.", []),
        new VisualQaDecision(VisualQaAction.ProceedToHumanReview, "Visual QA is not applicable."),
        [], [], null, null,
        PipelineStepModelDefaults.SupportModel,
        PipelineStepModelDefaults.SupportThinkingLevel,
        0, 0);
}

internal sealed record VisualQaCaptureManifest(
    bool Ok,
    string? BaseUrl,
    IReadOnlyList<VisualQaCaptureEntry> Captures,
    IReadOnlyList<string> Errors);

internal sealed record VisualQaCaptureEntry(
    string Label,
    string Route,
    string File,
    string? Title = null);

/// <summary>
/// Runs the first AGT visual-QA slice after deterministic UI iteration gates:
/// boot the exact task worktree's Angular app against the current authority
/// backend, capture affected routes with Playwright, attach those images to a
/// bounded multimodal model verdict, and persist the action receipt.
/// </summary>
public sealed class VisualQaService
{
    public const string PromptTemplate = "visual-qa-review.md";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IConfiguration _configuration;
    private readonly CliOneShotRegistry _oneShots;
    private readonly RuntimePromptService _prompts;
    private readonly ProjectSettingsService _settings;
    private readonly ProjectRegistry _projects;
    private readonly ILogger<VisualQaService> _logger;

    public VisualQaService(
        IConfiguration configuration,
        CliOneShotRegistry oneShots,
        RuntimePromptService prompts,
        ProjectSettingsService settings,
        ProjectRegistry projects,
        ILogger<VisualQaService> logger)
    {
        _configuration = configuration;
        _oneShots = oneShots;
        _prompts = prompts;
        _settings = settings;
        _projects = projects;
        _logger = logger;
    }

    public async Task<VisualQaResult> RunAsync(VisualQaRequest request, CancellationToken ct)
    {
        var taskKey = string.IsNullOrWhiteSpace(request.Task.Key)
            ? request.Task.Id
            : request.Task.Key!.Trim();
        if (_configuration.GetValue<bool?>("VisualQa:Enabled") == false
            || !VisualQaPolicy.IsApplicable(taskKey, request.ChangedFiles))
            return VisualQaResult.NotApplicable;

        var project = _projects.FindByStorageLocation(request.Task.WatchPath)
                      ?? _projects.FindByIdOrDisplayName(request.Task.ProjectName);
        var projectId = project?.Id ?? "PROJ-002";
        var promptPath = Path.Combine(request.Task.FolderPath, "prompt.md");
        var cardPrompt = SafeReadText(promptPath);
        var routeMetadata = string.Join(
            Environment.NewLine,
            new[] { request.Task.Title, cardPrompt }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var routes = VisualQaPolicy.ResolveRoutes(
            taskKey,
            projectId,
            routeMetadata,
            request.ChangedFiles);

        var visualRoot = Path.Combine(
            UiIterationGate.IterationDirectory(request.Task.FolderPath, request.Iteration),
            "visual-qa");
        Directory.CreateDirectory(visualRoot);
        var round = NextRound(visualRoot);
        var roundDirectory = Path.Combine(visualRoot, $"round-{round:D3}");
        Directory.CreateDirectory(roundDirectory);
        var relativeRound = Path.GetRelativePath(
                TaskPaths.ResultsDir(request.Task.FolderPath), roundDirectory)
            .Replace('\\', '/');

        var manifestPath = Path.Combine(roundDirectory, "capture.json");
        var capture = await CaptureAsync(
            request.RepositoryRoot,
            roundDirectory,
            manifestPath,
            routes,
            ct);
        var screenshots = capture.Captures
            .Where(item => !string.IsNullOrWhiteSpace(item.File))
            .Select(item => $"{relativeRound}/{Path.GetFileName(item.File)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projectSettings = _settings.Get(request.Task.ProjectName);
        var modelResolution = PipelineStepConfigResolver.ResolveModelWithSource(
            projectSettings,
            PipelineCatalogue.UiVisualVerdictStepId,
            PipelineStepModelDefaults.SupportModel);
        var model = modelResolution.Model;
        OrchestratorTokenUsage? reviewUsage = null;
        DateTime? reviewStartedAt = null;
        long reviewDurationMs = 0;
        var cli = PipelineStepConfigResolver.ResolveCliType(
                      projectSettings,
                      PipelineCatalogue.UiVisualVerdictStepId)
                  ?? PipelineStepModelDefaults.DefaultCli;
        var thinking = PipelineStepConfigResolver.ResolveThinkingLevel(
                           projectSettings,
                           PipelineCatalogue.UiVisualVerdictStepId,
                           cli,
                           model)
                       ?? PipelineStepModelDefaults.SupportThinkingLevel;
        var priorRetries = CountPriorAutomaticRetries(visualRoot);

        VisualQaVerdict verdict;
        string rawResponse;
        if (!capture.Ok || screenshots.Length == 0)
        {
            rawResponse = string.Empty;
            var detail = capture.Errors.Count == 0
                ? "No screenshots were produced."
                : string.Join(" ", capture.Errors);
            verdict = new VisualQaVerdict(
                VisualQaVerdictStatus.Unavailable,
                $"Stable-equivalent visual capture failed. {detail}",
                []);
        }
        else
        {
            var renderedPrompt = BuildPrompt(
                request,
                taskKey,
                routes,
                screenshots,
                cardPrompt,
                model,
                projectSettings);
            var images = ReadImages(roundDirectory, capture.Captures);
            var oneShot = _oneShots.Get(cli);
            if (oneShot is null)
            {
                rawResponse = string.Empty;
                verdict = new VisualQaVerdict(
                    VisualQaVerdictStatus.Unavailable,
                    $"The configured visual reviewer CLI '{cli}' is unavailable.",
                    []);
            }
            else
            {
                reviewStartedAt = DateTime.UtcNow;
                var result = await oneShot.RunAsync(new CliOneShotRequest(cli, model, renderedPrompt)
                {
                    ThinkingLevel = thinking,
                    WorkingDirectory = request.RepositoryRoot,
                    Timeout = TimeSpan.FromMinutes(5),
                    Source = AdHocUsageSources.ReviewDecision,
                    Project = request.Task.ProjectName,
                    JobId = taskKey,
                    InlineImages = images,
                    JobFolderPath = request.Task.FolderPath,
                    StepId = PipelineCatalogue.UiVisualVerdictStepId,
                    TemplateRef = PromptTemplate,
                }, ct).ConfigureAwait(false);
                reviewDurationMs = Math.Max(0L, (long)(DateTime.UtcNow - reviewStartedAt.Value).TotalMilliseconds);
                reviewUsage = (result.Usage ?? new OrchestratorTokenUsage()) with
                {
                    Model = result.EffectiveModel ?? result.Usage?.Model ?? model,
                    ThinkingLevel = result.EffectiveThinkingLevel ?? result.Usage?.ThinkingLevel ?? thinking,
                };
                rawResponse = result.ParsedText;
                verdict = result.Ok
                    ? VisualQaPolicy.ParseVerdict(rawResponse)
                    : new VisualQaVerdict(
                        VisualQaVerdictStatus.Unavailable,
                        $"The visual reviewer failed: {Compact(result.Error ?? result.Stderr)}",
                        []);
            }
        }

        var decision = VisualQaPolicy.Decide(verdict, priorRetries);
        var verdictPath = Path.Combine(roundDirectory, "verdict.json");
        await File.WriteAllTextAsync(
            verdictPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                taskKey,
                iteration = request.Iteration,
                round,
                model,
                thinkingLevel = thinking,
                status = StatusToken(verdict.Status),
                verdict.Summary,
                verdict.Defects,
                action = ActionToken(decision.Action),
                decision.Reason,
                screenshots,
                captureManifest = $"{relativeRound}/capture.json",
                reviewedAt = DateTimeOffset.UtcNow,
            }, Json),
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(roundDirectory, "verdict.md"),
            RenderMarkdown(verdict, decision, screenshots, model, thinking),
            ct);
        if (!string.IsNullOrWhiteSpace(rawResponse))
            await File.WriteAllTextAsync(Path.Combine(roundDirectory, "model-response.txt"), rawResponse, ct);

        _logger.LogInformation(
            "visual_qa_completed project={Project} job={JobId} iteration={Iteration} round={Round} status={Status} action={Action} screenshots={ScreenshotCount} model={Model} thinking={Thinking}",
            request.Task.ProjectName,
            taskKey,
            request.Iteration,
            round,
            StatusToken(verdict.Status),
            ActionToken(decision.Action),
            screenshots.Length,
            model,
            thinking);

        return new VisualQaResult(
            true,
            verdict,
            decision,
            screenshots,
            AllScreenshotPaths(visualRoot, TaskPaths.ResultsDir(request.Task.FolderPath)),
            $"{relativeRound}/capture.json",
            $"{relativeRound}/verdict.json",
            model,
            thinking,
            round,
            priorRetries,
            reviewUsage,
            modelResolution.Source,
            reviewStartedAt,
            reviewDurationMs);
    }

    private string BuildPrompt(
        VisualQaRequest request,
        string taskKey,
        IReadOnlyList<VisualQaRoute> routes,
        IReadOnlyList<string> screenshots,
        string cardPrompt,
        string model,
        ProjectSettings projectSettings)
    {
        var values = new Dictionary<string, string?>
        {
            ["task_key"] = taskKey,
            ["task_title"] = request.Task.Title,
            ["task_prompt"] = cardPrompt,
            ["changed_files"] = request.ChangedFiles is { Count: > 0 }
                ? string.Join('\n', request.ChangedFiles.Select(file => $"- {file}"))
                : "- Changed-file provenance unavailable; this card was admitted by the UI pipeline.",
            ["routes"] = string.Join('\n', routes.Select(route => $"- {route.Label}: {route.Path}")),
            ["screenshots"] = string.Join('\n', screenshots.Select(path => $"- {path}")),
        };
        var promptOverride = PipelineStepConfigResolver.ResolvePrompt(
            projectSettings,
            PipelineCatalogue.UiVisualVerdictStepId);
        return string.IsNullOrWhiteSpace(promptOverride)
            ? _prompts.Render(
                PromptTemplate,
                values,
                new PromptCallContext(
                    request.Task.ProjectName,
                    PipelineCatalogue.UiVisualVerdictStepId,
                    model))
            : _prompts.UseProjectOverride(
                PromptTemplate,
                RuntimePromptService.RenderContent(promptOverride, values),
                new PromptCallContext(
                    request.Task.ProjectName,
                    PipelineCatalogue.UiVisualVerdictStepId,
                    model));
    }

    private async Task<VisualQaCaptureManifest> CaptureAsync(
        string repositoryRoot,
        string outputDirectory,
        string manifestPath,
        IReadOnlyList<VisualQaRoute> routes,
        CancellationToken ct)
    {
        var script = _configuration["VisualQa:CaptureScript"];
        if (string.IsNullOrWhiteSpace(script))
            script = Path.Combine(repositoryRoot, "scripts", "visual-qa-capture.mjs");
        if (!File.Exists(script))
            return CaptureFailure($"Visual capture script was not found: {script}");

        var start = new ProcessStartInfo
        {
            FileName = _configuration["VisualQa:NodePath"] ?? "node",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--repository");
        start.ArgumentList.Add(repositoryRoot);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputDirectory);
        start.ArgumentList.Add("--manifest");
        start.ArgumentList.Add(manifestPath);
        start.ArgumentList.Add("--backend-url");
        start.ArgumentList.Add(BackendBaseUrl());
        foreach (var route in routes)
        {
            start.ArgumentList.Add("--route");
            start.ArgumentList.Add($"{route.Label}::{route.Path}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return CaptureFailure("Visual capture process could not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "capture.log"),
                $"stdout:\n{stdout}\n\nstderr:\n{stderr}",
                ct);
            if (!File.Exists(manifestPath))
                return CaptureFailure($"Visual capture exited {process.ExitCode} without a manifest. {Compact(stderr)}");
            var manifest = JsonSerializer.Deserialize<VisualQaCaptureManifest>(
                await File.ReadAllTextAsync(manifestPath, ct),
                Json);
            return manifest ?? CaptureFailure("Visual capture manifest was empty.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return CaptureFailure("Visual capture exceeded its six-minute budget.");
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or JsonException)
        {
            TryKill(process);
            return CaptureFailure($"Visual capture failed: {exception.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private string BackendBaseUrl()
    {
        var configured = _configuration["VisualQa:BackendBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        var urls = _configuration["ASPNETCORE_URLS"];
        var firstHttp = urls?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(firstHttp))
        {
            return firstHttp.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)
                .Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                .TrimEnd('/');
        }

        return _configuration.GetValue<bool>("Environment:IsDev")
            ? "http://127.0.0.1:5030"
            : "http://127.0.0.1:5031";
    }

    private static IReadOnlyList<CliOneShotImage> ReadImages(
        string roundDirectory,
        IReadOnlyList<VisualQaCaptureEntry> captures)
    {
        var result = new List<CliOneShotImage>();
        foreach (var capture in captures.Take(VisualQaPolicy.MaxRoutes))
        {
            var path = Path.GetFullPath(Path.Combine(roundDirectory, Path.GetFileName(capture.File)));
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024) continue;
            result.Add(new CliOneShotImage(Convert.ToBase64String(bytes), MediaType(path)));
        }
        return result;
    }

    private static int NextRound(string visualRoot)
    {
        try
        {
            var existing = Directory.EnumerateDirectories(visualRoot, "round-*")
                .Select(Path.GetFileName)
                .Select(name => int.TryParse(name?["round-".Length..], out var value) ? value : 0)
                .DefaultIfEmpty(0)
                .Max();
            return existing + 1;
        }
        catch
        {
            return 1;
        }
    }

    private static IReadOnlyList<string> AllScreenshotPaths(string visualRoot, string resultsRoot)
    {
        try
        {
            return Directory.EnumerateFiles(visualRoot, "*--real.*", SearchOption.AllDirectories)
                .Where(path => new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }
                    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetRelativePath(resultsRoot, path).Replace('\\', '/'))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    internal static int CountPriorAutomaticRetries(string visualRoot)
    {
        try
        {
            return Directory.EnumerateFiles(visualRoot, "verdict.json", SearchOption.AllDirectories)
                .Count(path => SafeReadText(path).Contains(
                    "\"action\": \"retry-with-steer\"",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    private static string RenderMarkdown(
        VisualQaVerdict verdict,
        VisualQaDecision decision,
        IReadOnlyList<string> screenshots,
        string model,
        string thinking)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Visual QA verdict");
        builder.AppendLine();
        builder.AppendLine($"- Status: `{StatusToken(verdict.Status)}`");
        builder.AppendLine($"- Action: `{ActionToken(decision.Action)}`");
        builder.AppendLine($"- Model: `{model}` / `{thinking}`");
        builder.AppendLine($"- Summary: {verdict.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Screenshots");
        builder.AppendLine();
        foreach (var path in screenshots) builder.AppendLine($"- `{path}`");
        builder.AppendLine();
        builder.AppendLine("## Named defects");
        builder.AppendLine();
        if (verdict.Defects.Count == 0) builder.AppendLine("- None.");
        foreach (var defect in verdict.Defects)
            builder.AppendLine($"- **{defect.Category}:** {defect.Description}");
        return builder.ToString();
    }

    private static string StatusToken(VisualQaVerdictStatus status) => status switch
    {
        VisualQaVerdictStatus.Acceptable => "acceptable",
        VisualQaVerdictStatus.ClearDefect => "clear-defect",
        _ => "unavailable",
    };

    private static string ActionToken(VisualQaAction action) => action switch
    {
        VisualQaAction.ProceedToHumanReview => "proceed-human-review",
        VisualQaAction.RetryWithSteer => "retry-with-steer",
        _ => "escalate-human-review",
    };

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };

    private static string SafeReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { return string.Empty; }
    }

    private static string Compact(string? value)
    {
        var compact = (value ?? "unknown failure").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }

    private static VisualQaCaptureManifest CaptureFailure(string error)
        => new(false, null, [], [error]);

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "VisualQaService: best-effort capture process cleanup");
        }
    }
}
