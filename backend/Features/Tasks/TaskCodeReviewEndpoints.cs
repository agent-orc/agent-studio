using Microsoft.Extensions.Configuration;

using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// User-triggered code-review step. Posts a single review pass against
/// the task's full change set - the aggregate diff of every commit the
/// task owns - using the chosen model (or the configured default), writes
/// <c>code-review-{utc-ts}.md</c> into the job folder, and merges a
/// <c>code-review:&lt;verdict&gt;</c> tag onto the job. An explicit
/// <c>body.Commit</c> pins the review to that single commit instead.
///
/// Sibling to the auto-review pipeline that runs in 4-auto-review:
/// auto-review is policy-driven and aggregates four narrow aspects;
/// this endpoint is the user's escape hatch to run one focused review
/// with a model they pick. Lane state is never touched - verdicts
/// surface as tags only so the user keeps the final say.
/// </summary>
public static class TaskCodeReviewEndpoints
{
    /// <summary>Configuration key for the default LLM model.</summary>
    public const string DefaultModelConfigKey = "CodeReviewStep:DefaultModel";

    /// <summary>Configuration key for the default CLI type.</summary>
    public const string DefaultCliConfigKey = "CodeReviewStep:DefaultCli";

    /// <summary>Configuration key for the per-run wall-clock cap.</summary>
    public const string TimeoutSecondsConfigKey = "CodeReviewStep:PerRunTimeoutSeconds";

    /// <summary>Hard fallback when neither config nor request specifies a CLI.</summary>
    public const string DefaultCliFallback = CliTypes.Codex;

    /// <summary>Hard fallback when neither config nor request specifies a model.</summary>
    public static string DefaultModelFallback =>
        ModelMetadataRegistry.DefaultForCli(DefaultCliFallback) ?? ModelIds.Gpt55;

    /// <summary>Default per-run wall-clock cap when configuration omits it.</summary>
    public const int DefaultTimeoutSecondsFallback = 600;

    /// <summary>
    /// Resolve the effective code-review default CLI + model from
    /// configuration, falling back to the hard-coded defaults. Pure so the
    /// resolution can be unit-tested without spinning an HTTP host.
    /// </summary>
    public static (string CliType, string Model) ResolveDefaults(IConfiguration configuration)
    {
        var cli = configuration[DefaultCliConfigKey];
        var model = configuration[DefaultModelConfigKey];
        return (
            string.IsNullOrWhiteSpace(cli) ? DefaultCliFallback : cli!,
            string.IsNullOrWhiteSpace(model) ? DefaultModelFallback : model!);
    }

    public static void MapTaskCodeReviewEndpoints(this RouteGroupBuilder group)
    {
        var pricing = ((IEndpointRouteBuilder)group).ServiceProvider.GetRequiredService<ITokenPricing>();
        // GET /api/tasks/code-review/defaults
        // The configured default CLI + model for the user-triggered review
        // step. The panel seeds its picker from this when the operator has
        // no remembered last-used pair, so a deployment-level
        // CodeReviewStep:DefaultModel actually shows up in the UI instead of
        // a hard-coded guess. Both literal segments, so it never collides
        // with the parameterised /{jobId}/code-review/... routes.
        group.MapGet("/code-review/defaults", (IConfiguration configuration) =>
        {
            var (cli, model) = ResolveDefaults(configuration);
            return Results.Ok(new CodeReviewDefaultsResponse { CliType = cli, Model = model });
        });

        // GET /api/tasks/{jobId}/code-review/list
        // Returns the list of code-review-*.md artifacts in the job folder,
        // newest-first. Each entry carries the parsed frontmatter so the
        // frontend can render verdict + summary without fetching each file.
        group.MapGet("/{jobId}/code-review/list",
            (string jobId, string? project, string? watchPath, TaskScannerService scanner, FileGenerationIndex generationIndex, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });

            var entries = new List<CodeReviewListEntry>();
            var generated = generationIndex.ReadForJob(info.FolderPath);
            try
            {
                if (Directory.Exists(info.FolderPath))
                {
                    foreach (var path in Directory.EnumerateFiles(info.FolderPath, "code-review-*.md"))
                    {
                        var fileName = Path.GetFileName(path);
                        try
                        {
                            var content = File.ReadAllText(path);
                            var fm = AgentStudio.Cli.FrontmatterParser.Parse(content);
                            var fields = fm.Ok ? fm.Fields : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            var generation = generated.GetValueOrDefault(fileName);
                            var usage = ResolveReviewUsage(pricing, generation, fields);
                            entries.Add(new CodeReviewListEntry
                            {
                                FileName = fileName,
                                Verdict = fields.GetValueOrDefault("verdict") ?? "unknown",
                                Grade = fields.GetValueOrDefault("grade"),
                                Summary = fields.GetValueOrDefault("summary") ?? string.Empty,
                                Model = fields.GetValueOrDefault("model") ?? string.Empty,
                                CliType = fields.GetValueOrDefault("cliType") ?? string.Empty,
                                ThinkingLevel = fields.GetValueOrDefault("thinkingLevel"),
                                Commit = fields.GetValueOrDefault("commit"),
                                RunAt = fields.GetValueOrDefault("runAt") ?? string.Empty,
                                InputTokens = usage.InputTokens,
                                OutputTokens = usage.OutputTokens,
                                CacheReadTokens = usage.CacheReadTokens,
                                CacheCreationTokens = usage.CacheCreationTokens,
                                TotalTokens = usage.TotalTokens,
                                EstimatedApiCostUsd = usage.Cost.Total,
                                PriceKnown = usage.PriceKnown,
                                Generation = generation,
                                CouncilReaction = AgentStudio.Review.CouncilReviewReactionStore.Read(info.FolderPath, fileName),
                            });
                        }
                        catch (Exception __ex)
                        {
                            SilentCatch.Note(__ex, "TaskCodeReviewEndpoints: Skip unreadable files; they should not break the list.");
                            // Skip unreadable files; they should not break the list.
                        }
                    }
                }
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "TaskCodeReviewEndpoints: Folder enumeration failure: return empty list rather than 500.");
                // Folder enumeration failure: return empty list rather than 500.
            }

            entries.Sort((a, b) => string.CompareOrdinal(b.RunAt, a.RunAt));
            return Results.Ok(new CodeReviewListResponse { Entries = entries });
        });

        // GET /api/tasks/{jobId}/code-review/{fileName}
        // Returns the raw MD body for one review. The caller passes a file
        // name that came from the list endpoint; we never accept arbitrary
        // paths.
        group.MapGet("/{jobId}/code-review/{fileName}",
            (string jobId, string fileName, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });

            // Path-traversal guard: only accept files whose name matches the
            // expected pattern and that resolve inside the job folder.
            if (string.IsNullOrWhiteSpace(fileName)
                || !fileName.StartsWith("code-review-", StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            {
                return Results.BadRequest(new { error = "Invalid review file name." });
            }

            var path = Path.Combine(info.FolderPath, fileName);
            if (!File.Exists(path)) return Results.NotFound(new { error = "Review not found." });

            try
            {
                return Results.Ok(new { fileName, content = File.ReadAllText(path) });
            }
            catch
            {
                return Results.NotFound(new { error = "Review unreadable." });
            }
        });

        // POST /api/tasks/{jobId}/code-review
        // Body: { "watchPath"?: string, "model"?: string, "cliType"?: string, "commit"?: string }
        // Synchronous: returns the report once the review finishes.
        group.MapPost("/{jobId}/code-review",
            async (string jobId,
                   string? project,
                   string? watchPath,
                   CodeReviewStepEndpointRequest? body,
                   TaskScannerService scanner,
                   TaskSessionLog sessions,
                   GitService git,
                   CodeReviewStepService service,
                   OnDemandPostStepService onDemand,
                   IConfiguration configuration,
                   AgentStudio.Registry.ProjectRegistry projects,
                   CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var resolvedWatchPath = body?.WatchPath ?? watchPath;
            var info = scanner.FindJob(jobId, resolvedWatchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });

            var gradeMode = string.Equals(body?.Mode, "grade", StringComparison.OrdinalIgnoreCase);
            var projectContext = gradeMode
                ? ResolveProjectContext(projects, scanner, info)
                : null;
            if (gradeMode && projectContext == null)
            {
                return Results.BadRequest(new
                {
                    error = "The task is not associated with a canonical project registry identity.",
                });
            }

            var detail = scanner.GetJobDetail(jobId, resolvedWatchPath);
            var taskBody = detail?.PromptMarkdown ?? string.Empty;

            // Default scope = the aggregate diff of every commit the task owns,
            // not just HEAD. An explicit body.Commit pins the review to one
            // commit (override); when the task has no attributed commits yet we
            // fall back to HEAD, then the working tree. See ASS-794 / the
            // regression-radar per-task scoping.
            var taskShas = ResolveTaskCommitShas(info, resolvedWatchPath, sessions, git);
            var scope = CodeReviewScopeResolver.Resolve(
                body?.Commit, taskShas, git.GetHeadSha(jobId, resolvedWatchPath));

            string diff = scope.Mode switch
            {
                CodeReviewScopeMode.WorkingTree =>
                    "(no commit resolved; reviewing working-tree diff)\n\n" +
                    git.GetDiff(jobId, resolvedWatchPath, path: null),
                CodeReviewScopeMode.AggregateCommits =>
                    git.GetAggregateCommitDiff(jobId, resolvedWatchPath, scope.Shas, path: null),
                _ => git.GetCommitDiff(jobId, resolvedWatchPath, scope.Shas[0], path: null),
            };

            var cli = !string.IsNullOrWhiteSpace(body?.CliType)
                ? body!.CliType!
                : configuration[DefaultCliConfigKey] ?? DefaultCliFallback;
            var model = !string.IsNullOrWhiteSpace(body?.Model)
                ? body!.Model!
                : configuration[DefaultModelConfigKey] ?? DefaultModelFallback;
            var thinkingLevel = ModelMetadataRegistry.ResolveThinkingLevel(cli, model, body?.ThinkingLevel);
            var timeoutSeconds = configuration.GetValue<int?>(TimeoutSecondsConfigKey)
                ?? DefaultTimeoutSecondsFallback;

            var request = new CodeReviewStepRequest(
                Project: info.ProjectName ?? string.Empty,
                JobId: info.Id,
                JobTitle: info.Title ?? string.Empty,
                JobFolderPath: info.FolderPath,
                TaskBody: taskBody,
                Diff: diff,
                CliType: cli,
                Model: model)
            {
                Mode = gradeMode
                    ? CodeReviewMode.Grade
                    : CodeReviewMode.Verdict,
                ThinkingLevel = thinkingLevel,
                Commit = scope.Label,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                ResultsInventory = ResultsInventory.Render(info.FolderPath),
                CardMode = ReviewCardMode.Describe(info.Mode),
            };

            var report = await service.RunAsync(request, ct);

            if (request.Mode == CodeReviewMode.Grade)
            {
                var stepId = PipelineCatalogue.CodeReviewGradeStepId;
                var grade = report.Grade is null ? "?" : CodeReviewGradeParsing.GradeToken(report.Grade.Value);
                await onDemand.AppendAttemptAsync(
                    info,
                    projectContext!.Project.Id,
                    stepId,
                    "Code-review quality grade",
                    "Llm",
                    "Review",
                    report.Grade == CodeReviewGrade.D ? "Failed" : "Ok",
                    report.StartedAt,
                    report.StartedAt.AddMilliseconds(report.DurationMs),
                    report.DurationMs,
                    $"Quality grade {grade}: {report.Summary}",
                    report.FileName,
                    ct);
            }

            return Results.Ok(new CodeReviewStepEndpointResponse
            {
                FileName = report.FileName,
                Verdict = AspectVerdictParsing.StatusToken(report.Status),
                Summary = report.Summary,
                Model = report.Model,
                CliType = report.CliType,
                ThinkingLevel = report.ThinkingLevel,
                Commit = report.Commit,
                ConcernTagId = report.ConcernTagId,
                DurationMs = report.DurationMs,
                StartedAt = report.StartedAt,
                Grade = report.Grade is null ? null : CodeReviewGradeParsing.GradeToken(report.Grade.Value),
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);
    }

    internal static ReviewUsage ResolveReviewUsage(ITokenPricing pricing,
        FileGenerationMeta? generation,
        IReadOnlyDictionary<string, string> fields)
    {
        if (generation == null)
            return ReviewUsage.Empty(pricing);

        var input = generation.TokensIn;
        var output = generation.TokensOut;
        var cacheRead = generation.CacheReadTokens;
        var cacheCreation = generation.CacheCreationTokens;
        var reportedTotal = generation.TokensTotal;

        // Legacy provenance stored cache tokens only inside TokensTotal. Keep
        // those tokens visible, but mark the estimate partial instead of
        // guessing whether the remainder was cache read or cache creation.
        // New records carry both cache fields explicitly.
        var knownTotal = input + output + cacheRead + cacheCreation;
        var hasLegacyCacheRemainder = reportedTotal > knownTotal
            && cacheRead == 0 && cacheCreation == 0;

        var total = Math.Max(reportedTotal, input + output + cacheRead + cacheCreation);
        var model = generation.Model ?? fields.GetValueOrDefault("model");
        var recordedAt = generation.StartedAt;
        if (recordedAt == null
            && DateTime.TryParse(fields.GetValueOrDefault("runAt"), out var parsedRunAt))
        {
            recordedAt = parsedRunAt;
        }
        var cost = pricing.Estimate(model, input, output, cacheRead, cacheCreation, recordedAt);
        return new ReviewUsage(input, output, cacheRead, cacheCreation, total, cost,
            PriceKnown: cost.ModelKnown && !hasLegacyCacheRemainder);
    }

    internal sealed record ReviewUsage(
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens,
        long TotalTokens,
        TokenCostEstimate Cost,
        bool PriceKnown)
    {
        public static ReviewUsage Empty(ITokenPricing pricing) => new(
            0, 0, 0, 0, 0,
            pricing.Estimate(null, 0, 0, 0, 0),
            PriceKnown: false);
    }

    /// <summary>
    /// Every commit SHA the task owns, newest-first, deduped. Built from the
    /// same run-range + persisted-chain aggregation the protocol-pane change
    /// set uses (<see cref="TaskCommitsAggregator"/>), so an in-progress task
    /// whose commits[] chain has not been stamped yet still surfaces the
    /// commits its runs produced. Returns an empty list when the task has no
    /// resolvable commits; the resolver then falls back to HEAD.
    /// </summary>
    private static IReadOnlyList<string> ResolveTaskCommitShas(
        TaskInfo info, string? watchPath, TaskSessionLog sessions, GitService git)
    {
        try
        {
            var events = sessions.ReadSessionEvents(info.Id, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
            var aggregate = TaskCommitsAggregator.Aggregate(info, timeline.Runs,
                (before, after) => git.GetCommitsInShaRange(info.Id, watchPath, before, after));

            return aggregate.Commits
                .Select(c => c.Sha)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Aggregation is best-effort: any failure degrades to HEAD scope.
            return Array.Empty<string>();
        }
    }
}

/// <summary>Response for <c>GET /api/tasks/code-review/defaults</c>.</summary>
public sealed record CodeReviewDefaultsResponse
{
    public required string CliType { get; init; }
    public required string Model { get; init; }
}

/// <summary>One row in the per-job code-review listing.</summary>
public sealed record CodeReviewListEntry
{
    public required string FileName { get; init; }
    public required string Verdict { get; init; }

    /// <summary>
    /// Quality grade A/B/C/D for the automatic grade pass (ASS-1657); null
    /// for the older user-triggered verdict reviews that carry no grade.
    /// </summary>
    public string? Grade { get; init; }
    public required string Summary { get; init; }
    public required string Model { get; init; }
    public required string CliType { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Commit { get; init; }
    public required string RunAt { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long TotalTokens { get; init; }
    public decimal EstimatedApiCostUsd { get; init; }
    public bool PriceKnown { get; init; }
    public FileGenerationMeta? Generation { get; init; }
    /// <summary>Explicit orchestrator ruling attached to this review, if the pipeline has processed it.</summary>
    public AgentStudio.Review.CouncilReviewReaction? CouncilReaction { get; init; }
}

/// <summary>Response for <c>GET /api/tasks/{jobId}/code-review/list</c>.</summary>
public sealed record CodeReviewListResponse
{
    public required IReadOnlyList<CodeReviewListEntry> Entries { get; init; }
}

/// <summary>Body for <c>POST /api/tasks/{jobId}/code-review</c>. All fields optional.</summary>
public sealed record CodeReviewStepEndpointRequest
{
    public string? WatchPath { get; init; }
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Commit { get; init; }
    public string? Mode { get; init; }
}

/// <summary>Response shape for the code-review endpoint.</summary>
public sealed record CodeReviewStepEndpointResponse
{
    public required string FileName { get; init; }
    public required string Verdict { get; init; }
    public required string Summary { get; init; }
    public required string Model { get; init; }
    public required string CliType { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Commit { get; init; }
    public string? ConcernTagId { get; init; }
    public required long DurationMs { get; init; }
    public required DateTime StartedAt { get; init; }
    public string? Grade { get; init; }
}
