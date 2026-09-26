using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Docs;
using AgentStudio.Pipeline;
using AgentStudio.Projects;

namespace AgentStudio.Runner;

public sealed record PromptEnrichmentPreparation(
    string AuthoredPrompt,
    string LaunchPrompt,
    string ContextMarkdown,
    PromptEnrichmentReport Report);

/// <summary>
/// Deterministic prompt preprocessing boundary shared by local and remote
/// execution. It materializes the exact context and its audit report before
/// dispatch. A report persistence failure is fatal for admission.
/// </summary>
public sealed class PromptEnrichmentService
{
    public const string ReportFileName = "enrichment-report.json";
    public const string PolicyVersion = "2";
    private const string Tokenizer = "character-estimate-v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<PromptEnrichmentService> _logger;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly ProjectStyleGuideService? _styleGuides;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly TimeProvider _time;

    public PromptEnrichmentService(
        ILogger<PromptEnrichmentService> logger,
        ProjectSettingsService? projectSettings = null,
        ProjectStyleGuideService? styleGuides = null,
        PipelineExecutionLog? pipelineLog = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _projectSettings = projectSettings;
        _styleGuides = styleGuides;
        _pipelineLog = pipelineLog;
        _time = time ?? TimeProvider.System;
    }

    public PromptEnrichmentPreparation Prepare(
        TaskInfo task,
        string authoredPrompt,
        string? downstreamModel,
        bool? enabledOverride = null,
        IReadOnlyList<ProjectStyleGuide>? guidesOverride = null,
        string? styleGuideSnapshotOverride = null,
        string? repositoryRootOverride = null,
        bool? agentStudioCatalogueOverride = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(authoredPrompt);
        var stopwatch = Stopwatch.StartNew();
        var generatedAt = _time.GetUtcNow().UtcDateTime;
        var settings = _projectSettings?.Get(task.ProjectName);
        var step = PipelineCatalogue.Standard.Pre.Single(candidate =>
            string.Equals(candidate.Id, PipelineCatalogue.PromptEnrichmentStepId, StringComparison.Ordinal));
        var projectEnabled = enabledOverride
                             ?? (settings is null || PipelineStepConfigResolver.IsEnabled(
                                 PipelineTypeSettings.ForTask(settings, task), step));
        var applyEnrichment = projectEnabled;
        var warnings = new List<string>();

        IntakeEnrichmentManifest manifest;
        var repositoryRoot = string.Empty;
        try
        {
            var catalogue = guidesOverride is not null
                ? null
                : _styleGuides?.GetCatalogue(task.ProjectName);
            var guides = guidesOverride ?? catalogue?.Guides;
            var snapshot = styleGuideSnapshotOverride
                           ?? (catalogue?.Guides.Count > 0 ? catalogue.SnapshotId : null);
            repositoryRoot = repositoryRootOverride
                             ?? _styleGuides?.GetRepositoryRoot(task.ProjectName)
                             ?? string.Empty;
            if (catalogue?.Warnings.Count > 0)
            {
                warnings.AddRange(catalogue.Warnings.Select(warning =>
                    $"{warning.RelPath}: {warning.Message}"));
            }
            manifest = IntakeRunner.BuildEnrichmentManifest(
                task, authoredPrompt, guides, snapshot, repositoryRoot,
                agentStudioCatalogueOverride
                ?? string.Equals(catalogue?.ProjectKey, "PROJ-002", StringComparison.OrdinalIgnoreCase),
                PipelineTypeSettings.ForTask(settings, task)?.PipelineSteps?
                    .GetValueOrDefault(PipelineCatalogue.PromptEnrichmentStepId)?.EnrichmentBlockIds);
        }
        catch (Exception ex)
        {
            warnings.Add($"Selection failed open to the authored prompt: {ex.Message}");
            manifest = new IntakeEnrichmentManifest
            {
                ArtifactPath = IntakeRunner.EnrichedContextRelativePath,
                Selector = IntakeRunner.EnrichmentSelector,
                CharacterBudget = IntakeRunner.MaxEnrichmentContextCharacters,
                EstimatedTokenBudget = IntakeRunner.MaxEnrichmentEstimatedTokens,
                OptionalBlockLimit = IntakeRunner.MaxOptionalEnrichmentBlocks,
            };
            applyEnrichment = false;
        }

        var fallback = warnings.Any(warning =>
            warning.StartsWith("Selection failed open", StringComparison.Ordinal));
        var context = applyEnrichment && manifest.Constraints.Count > 0
            ? RenderLaunchContext(manifest)
            : string.Empty;
        var launchPrompt = AppendContext(authoredPrompt, context);
        var status = fallback
            ? PromptEnrichmentStatuses.FallbackUnenriched
            : applyEnrichment && context.Length > 0
                ? PromptEnrichmentStatuses.Enriched
                : PromptEnrichmentStatuses.Unchanged;
        var report = BuildReport(
            task,
            authoredPrompt,
            launchPrompt,
            downstreamModel,
            manifest,
            repositoryRoot,
            projectEnabled,
            applyEnrichment,
            status,
            warnings,
            [],
            generatedAt,
            stopwatch.ElapsedMilliseconds);

        var contextPath = Path.Combine(
            task.FolderPath,
            IntakeRunner.EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var reportPath = Path.Combine(task.FolderPath, ReportFileName);
        try
        {
            // The context lands first. The report is the commit marker a
            // dispatcher and the Task tab trust.
            WriteAtomic(contextPath, context);
            report = report with { TimingMs = stopwatch.ElapsedMilliseconds };
            WriteAtomic(reportPath, JsonSerializer.Serialize(report, Json));
        }
        catch (Exception ex)
        {
            var blocked = report with
            {
                Status = PromptEnrichmentStatuses.Blocked,
                TimingMs = stopwatch.ElapsedMilliseconds,
                Errors = [.. report.Errors, $"Prompt enrichment could not be persisted: {ex.Message}"],
            };
            try { WriteAtomic(reportPath, JsonSerializer.Serialize(blocked, Json)); }
            catch (Exception reportEx)
            {
                _logger.LogError(reportEx,
                    "Prompt enrichment report persistence failed for {JobId}", task.Id);
            }
            throw new InvalidOperationException(
                $"Prompt enrichment report could not be persisted for task '{task.Id}'. Dispatch is blocked.",
                ex);
        }

        RecordPipelineStep(task, report, generatedAt);
        _logger.LogInformation(
            "prompt-enrichment task={TaskId} status={Status} areas={Areas} blocks={Blocks} appendedTokens={AppendedTokens} selectorTokens=0",
            task.Id,
            report.Status,
            string.Join(",", report.DetectedAreas),
            report.AppendedBlocks.Count,
            report.Tokens.Appended);
        return new PromptEnrichmentPreparation(authoredPrompt, launchPrompt, context, report);
    }

    public static PromptEnrichmentReport? ReadReport(string jobFolder)
    {
        try
        {
            var path = Path.Combine(jobFolder, ReportFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PromptEnrichmentReport>(
                File.ReadAllText(path), ReadJson);
        }
        catch
        {
            return null;
        }
    }

    public static string AppendContext(string authoredPrompt, string? contextMarkdown)
    {
        if (string.IsNullOrWhiteSpace(contextMarkdown)) return authoredPrompt;
        var separator = authoredPrompt.EndsWith('\n') ? "\n" : "\n\n";
        return authoredPrompt + separator + "---\n\n" + contextMarkdown.TrimEnd() + "\n";
    }

    /// <summary>
    /// Adds the audited enrichment block to the existing per-mode framing seam.
    /// Local and remote runners both place that seam after the authored task, so
    /// the task remains readable while mode instructions and enrichment retain a
    /// single deterministic transport and rendering order.
    /// </summary>
    public static string ComposeModeFraming(string? modeFraming, string? enrichmentContext)
    {
        var blocks = new[] { modeFraming, enrichmentContext }
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .Select(block => block!.Trim());
        var composed = string.Join("\n\n", blocks);
        return composed.Length == 0 ? string.Empty : composed + "\n\n";
    }

    public static string RenderLaunchContext(IntakeEnrichmentManifest manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Prompt enrichment");
        builder.AppendLine();
        builder.AppendLine("> Appended by the task server before CLI spawn. The authored prompt above is unchanged.");
        builder.AppendLine();
        foreach (var constraint in manifest.Constraints)
            builder.Append(IntakeRunner.RenderConstraintMarkdown(constraint));
        return builder.ToString().TrimEnd();
    }

    private static PromptEnrichmentReport BuildReport(
        TaskInfo task,
        string authoredPrompt,
        string launchPrompt,
        string? downstreamModel,
        IntakeEnrichmentManifest manifest,
        string repositoryRoot,
        bool projectEnabled,
        bool applyEnrichment,
        string status,
        List<string> warnings,
        List<string> errors,
        DateTime generatedAt,
        long timingMs)
    {
        var appended = manifest.Constraints.Select((constraint, index) =>
        {
            var exact = IntakeRunner.RenderConstraintMarkdown(constraint).TrimEnd();
            return new PromptEnrichmentBlock
            {
                Id = constraint.Id,
                Title = constraint.Title,
                Source = constraint.Source,
                Project = task.ProjectName,
                Repository = repositoryRoot,
                SourceVerification = constraint.SourceVerification,
                Revision = constraint.Revision,
                DigestSha256 = Sha256(exact),
                Tier = constraint.Tier,
                Order = index + 1,
                EstimatedTokens = IntakeRunner.EstimateTokens(exact.Length),
                ExactContent = exact,
            };
        }).ToList();
        if (!applyEnrichment) appended = [];

        var candidates = manifest.Constraints.Select(constraint =>
            new PromptEnrichmentCandidate
            {
                Id = constraint.Id,
                Title = constraint.Title,
                Source = constraint.Source,
                Signals = constraint.Areas,
                Decision = applyEnrichment ? "appended" : "rejected-project-disabled",
                Reason = applyEnrichment
                    ? constraint.Mandatory ? "mandatory-project-policy" : "matched-task-area"
                    : projectEnabled ? "selector-fallback" : "project-step-disabled",
                EstimatedTokens = IntakeRunner.EstimateTokens(
                    IntakeRunner.RenderConstraintMarkdown(constraint).Length),
            })
            .Concat(manifest.SourceRejections.Concat(manifest.Omissions.Where(omission =>
                !manifest.SourceRejections.Any(rejection => rejection.Id == omission.Id)))
                .Select(omission =>
                new PromptEnrichmentCandidate
                {
                    Id = omission.Id,
                    Title = omission.Title,
                    Source = omission.Source,
                    Signals = manifest.Areas,
                    Decision = applyEnrichment
                        ? omission.Reason switch
                        {
                            "source-missing" => "rejected-source-missing",
                            "project-catalogue-mismatch" => "rejected-project-catalogue",
                            _ => "rejected-budget"
                        }
                        : "rejected-project-disabled",
                    Reason = applyEnrichment
                        ? omission.MissingPath ?? omission.Reason
                        : projectEnabled ? "selector-fallback" : "project-step-disabled",
                    MissingPath = omission.MissingPath,
                    EstimatedTokens = omission.EstimatedTokens,
                }))
            .ToList();

        var originalTokens = IntakeRunner.EstimateTokens(authoredPrompt.Length);
        var appendedTokens = Math.Max(0,
            IntakeRunner.EstimateTokens(launchPrompt.Length) - originalTokens);
        var finalTokens = IntakeRunner.EstimateTokens(launchPrompt.Length);
        decimal? appendedInputUsd = null;
        string? unknownReason = null;
        if (appendedTokens > 0)
        {
            var estimate = TokenPricing.Estimate(
                downstreamModel, appendedTokens, 0, 0, 0, generatedAt);
            if (estimate.ModelKnown)
                appendedInputUsd = estimate.Total;
            else
                unknownReason = string.IsNullOrWhiteSpace(downstreamModel)
                    ? "No downstream model was resolved."
                    : "The downstream model or historical price is unknown.";
        }

        var originalHash = Sha256(authoredPrompt);
        var enrichedHash = Sha256(launchPrompt);
        var enrichmentId = Sha256(string.Join("|",
            originalHash,
            PolicyVersion,
            manifest.StyleGuideSnapshotId ?? "none",
            downstreamModel ?? "none",
            string.Join(",", appended.Select(block => block.DigestSha256))))[..24];

        return new PromptEnrichmentReport
        {
            EnrichmentId = enrichmentId,
            GeneratedAtUtc = generatedAt,
            Status = status,
            OriginalPromptSha256 = originalHash,
            EnrichedPromptSha256 = enrichedHash,
            Policy = new PromptEnrichmentPolicy
            {
                Version = PolicyVersion,
                ProjectEnabled = projectEnabled,
                Selector = manifest.Selector,
                Tokenizer = Tokenizer,
                TokenBudget = IntakeRunner.MaxEnrichmentEstimatedTokens,
                OptionalBlockLimit = IntakeRunner.MaxOptionalEnrichmentBlocks,
                StyleGuideSnapshotId = manifest.StyleGuideSnapshotId,
            },
            DetectedAreas = manifest.Areas,
            Candidates = candidates,
            AppendedBlocks = appended,
            Tokens = new PromptEnrichmentTokens
            {
                Tokenizer = Tokenizer,
                Original = originalTokens,
                Appended = appendedTokens,
                Final = finalTokens,
                // V1 uses local rules. These buckets are intentionally zero,
                // not a fabricated model call.
                PreprocessingInput = 0,
                PreprocessingOutput = 0,
                PreprocessingCacheRead = 0,
                PreprocessingCacheCreation = 0,
            },
            Cost = new PromptEnrichmentCost
            {
                SelectorUsd = 0m,
                AppendedInputUsd = appendedInputUsd,
                EstimateModel = downstreamModel,
                UnknownReason = unknownReason,
            },
            TimingMs = timingMs,
            Warnings = warnings,
            Errors = errors,
        };
    }

    private void RecordPipelineStep(
        TaskInfo task,
        PromptEnrichmentReport report,
        DateTime startedAt)
    {
        if (_pipelineLog is null) return;
        try
        {
            var settings = _projectSettings?.Get(task.ProjectName);
            var pipeline = ProjectPipelineOrder.Apply(
                UiTaskPipelineRouter.Select(task, settings ?? new ProjectSettings()),
                settings);
            var record = _pipelineLog.EnsureAgentRunStart(
                task.FolderPath, pipeline, task.ProjectName, task.Id, startedAt);
            using var attempt = _pipelineLog.EnterAttempt(task.FolderPath, record.Attempt);
            var status = report.Status == PromptEnrichmentStatuses.Blocked
                ? PipelineStepStatus.Failed
                : report.Status == PromptEnrichmentStatuses.Enriched
                    ? PipelineStepStatus.Passed
                    : PipelineStepStatus.Skipped;
            _pipelineLog.RecordStep(task.FolderPath, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.PromptEnrichmentStepId,
                Kind = StepKind.Module,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = report.GeneratedAtUtc.AddMilliseconds(report.TimingMs),
                DurationMs = report.TimingMs,
                // Appended tokens are already part of the core prompt. The
                // selector itself is deterministic and consumes zero tokens.
                InputTokens = report.Tokens.PreprocessingInput,
                OutputTokens = report.Tokens.PreprocessingOutput,
                CacheReadTokens = report.Tokens.PreprocessingCacheRead,
                CacheCreationTokens = report.Tokens.PreprocessingCacheCreation,
                TokenUsageSource = "PROMPT ENRICHMENT REPORT / deterministic selector",
                Verdict = report.Status,
                VerdictSummary =
                    $"0 selector tokens; +{report.Tokens.Appended} attributed prompt tokens; {report.AppendedBlocks.Count} block(s).",
                Reason = report.Status switch
                {
                    PromptEnrichmentStatuses.Unchanged => "Disabled by the project pipeline convention.",
                    PromptEnrichmentStatuses.FallbackUnenriched =>
                        "Selector failed open; the authored prompt was dispatched unchanged.",
                    _ => null,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to record prompt-enrichment pipeline step for {JobId}", task.Id);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}
