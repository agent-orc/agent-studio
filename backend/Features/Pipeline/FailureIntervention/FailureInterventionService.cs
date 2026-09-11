using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record FailureInterventionRecord
{
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public string FollowUpTaskId { get; init; } = "";
    public string FollowUpKey { get; init; } = "";
    public string FailureDomain { get; init; } = "";
    public string FailureClass { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Signature { get; init; } = "";
    public List<string> AffectedCards { get; init; } = [];
    public List<string> EvidencePointers { get; init; } = [];
    public DateTime FirstFailureAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string Status { get; init; } = "open";
    public long TimeToCreationMs => Math.Max(0, (long)(CreatedAt - FirstFailureAt).TotalMilliseconds);
    public long? TimeToResolutionMs => ResolvedAt is { } resolved
        ? Math.Max(0, (long)(resolved - FirstFailureAt).TotalMilliseconds)
        : null;
}

public sealed record FailureInterventionResult(
    FailureInterventionRecord Intervention,
    bool Created,
    string WaitReason);

public sealed record FailureInterventionReport(
    int Count,
    int Open,
    int Closed,
    IReadOnlyList<FailureInterventionRecord> Items);

public static class FailureInterventionReporting
{
    public static FailureInterventionReport Summarize(IReadOnlyList<FailureInterventionRecord> items)
        => new(
            items.Count,
            items.Count(item => item.Status == "open"),
            items.Count(item => item.Status == "closed"),
            items);

    public static string Line(FailureInterventionRecord item)
        => $"[{item.FollowUpKey}] {item.Status}; {item.FailureClass}; affected: "
           + $"{string.Join(", ", item.AffectedCards)}; first failure to creation: "
           + $"{item.TimeToCreationMs} ms; resolution: "
           + (item.TimeToResolutionMs is { } resolution ? resolution + " ms" : "open");
}

/// <summary>
/// Failure-boundary coordinator. The project JSON ledger is the durable dedupe
/// authority; task folders remain authoritative for task state and relations.
/// </summary>
public sealed class FailureInterventionService
{
    public const string ClassifierPromptTemplate = "failure-intervention-classifier.md";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly TaskMutationService _mutations;
    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly ILogger<FailureInterventionService> _logger;
    private readonly RuntimePromptService _prompts;
    private readonly TimeProvider _time;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly PipelineStepEconomyAdvisor? _economy;
    private readonly CliOneShotRegistry? _oneShots;
    private readonly PipelineExecutionLog? _pipelineLog;

    /// <summary>LLM classification seam used only when deterministic policy returns null.</summary>
    public Func<FailureCommandEvidence, Task<FailureClassificationResult>>? AmbiguousClassifier { get; set; }

    public FailureInterventionService(
        TaskMutationService mutations,
        TaskScannerService scanner,
        TimelineLog timeline,
        OrchestratorLog orchestratorLog,
        ILogger<FailureInterventionService> logger,
        RuntimePromptService prompts,
        TimeProvider? time = null,
        ProjectSettingsService? projectSettings = null,
        PipelineStepEconomyAdvisor? economy = null,
        CliOneShotRegistry? oneShots = null,
        PipelineExecutionLog? pipelineLog = null)
    {
        _mutations = mutations;
        _scanner = scanner;
        _timeline = timeline;
        _orchestratorLog = orchestratorLog;
        _logger = logger;
        _prompts = prompts;
        _time = time ?? TimeProvider.System;
        _projectSettings = projectSettings;
        _economy = economy;
        _oneShots = oneShots;
        _pipelineLog = pipelineLog;
    }

    public async Task<FailureInterventionResult> RaiseAsync(
        TaskInfo origin,
        FailureCommandEvidence evidence,
        CancellationToken ct = default)
    {
        var classification = FailureInterventionPolicy.Classify(evidence)
            ?? await ClassifyAmbiguousAsync(origin, evidence, ct);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var records = Read(origin.WatchPath);
            var existing = records.FirstOrDefault(item =>
                string.Equals(item.Fingerprint, classification.Fingerprint, StringComparison.OrdinalIgnoreCase)
                && IsOpen(item, origin.WatchPath));
            var originKey = origin.Key ?? origin.Id;
            var created = existing is null;
            FailureInterventionRecord intervention;

            if (existing is null)
            {
                var prompt = BuildPrompt(origin, evidence, classification);
                var taskId = _mutations.CreateJob(new CreateTaskRequest
                {
                    Title = $"Intervention: {ShortTitle(classification)}",
                    WatchPath = origin.WatchPath,
                    PromptMarkdown = prompt,
                    TargetState = TaskStates.Preparation,
                    CreationSource = TimelineActors.Orchestrator,
                    CreatedBy = "Orchestrator",
                    TaskType = TaskTypes.Bug,
                }) ?? throw new InvalidOperationException("The orchestrator could not create the intervention task.");
                var followUp = _scanner.FindJob(taskId, origin.WatchPath)
                    ?? throw new InvalidOperationException("The created intervention task could not be resolved.");
                intervention = new FailureInterventionRecord
                {
                    Id = "int_" + classification.Fingerprint,
                    Project = origin.ProjectName,
                    FollowUpTaskId = taskId,
                    FollowUpKey = followUp.Key ?? taskId,
                    FailureDomain = classification.Domain,
                    FailureClass = classification.FailureClass,
                    Fingerprint = classification.Fingerprint,
                    Signature = classification.Signature,
                    AffectedCards = [originKey],
                    EvidencePointers = evidence.EvidencePointers?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
                    FirstFailureAt = evidence.OccurredAt?.ToUniversalTime() ?? now,
                    CreatedAt = now,
                };
                UpdateFollowUpReferences(followUp, intervention.AffectedCards);
                UpdateFollowUpPrompt(followUp, intervention);
                records.Add(intervention);
            }
            else
            {
                intervention = existing with
                {
                    AffectedCards = existing.AffectedCards.Append(originKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    EvidencePointers = existing.EvidencePointers
                        .Concat(evidence.EvidencePointers ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    FirstFailureAt = evidence.OccurredAt is { } observed
                        ? new[] { existing.FirstFailureAt, observed.ToUniversalTime() }.Min()
                        : existing.FirstFailureAt,
                };
                records[records.IndexOf(existing)] = intervention;
                var followUp = _scanner.FindJob(existing.FollowUpTaskId, origin.WatchPath);
                if (followUp is not null)
                {
                    UpdateFollowUpReferences(followUp, intervention.AffectedCards);
                    UpdateFollowUpPrompt(followUp, intervention);
                }
            }

            UpdateOriginReferences(origin, intervention.FollowUpKey);
            Write(origin.WatchPath, records);
            RecordSurfaces(origin, intervention, created);
            RecordPipelineStep(origin, intervention);
            return new FailureInterventionResult(intervention, created,
                $"waiting on {intervention.FollowUpKey}: {ShortTitle(classification)}");
        }
    }

    private async Task<FailureClassificationResult> ClassifyAmbiguousAsync(
        TaskInfo origin, FailureCommandEvidence evidence, CancellationToken ct)
    {
        if (AmbiguousClassifier is not null) return await AmbiguousClassifier(evidence);
        var step = PipelineCatalogue.FindStep(PipelineCatalogue.FailureInterventionStepId)!;
        var settings = _projectSettings?.Get(origin.ProjectName);
        // The fallback is deliberately a project-scoped economy call even when
        // the operator did not enable economy routing for other pipeline steps.
        var economy = _economy is null
            ? null
            : await _economy.SuggestModelAsync(settings, step.Id, ct, forceEconomy: true);
        var resolved = PipelineStepModelDefaults.Resolve(settings, step);
        var cli = economy?.CliType ?? PipelineStepModelDefaults.RuntimeDefaultCliFor(step) ?? CliTypes.Codex;
        var model = economy?.Model ?? resolved?.Model ?? PipelineStepModelDefaults.SupportModel;
        var thinking = economy?.ThinkingLevel
            ?? PipelineStepConfigResolver.ResolveThinkingLevel(settings, step, cli, model,
                PipelineStepModelDefaults.SupportThinkingLevel);
        var oneShot = _oneShots?.Get(cli);
        if (oneShot is null)
            return FailureInterventionPolicy.FromFallback(evidence, FailureDomains.Infrastructure,
                "Ambiguous failure defaulted conservatively because the economy classifier was unavailable.");

        var prompt = _prompts.Render(
            ClassifierPromptTemplate,
            new Dictionary<string, string?>
            {
                ["failure_code"] = evidence.FailureCode,
                ["outcome"] = evidence.Outcome,
                ["exit_code"] = evidence.ExitCode.ToString(),
                ["duration_ms"] = evidence.DurationMs.ToString(),
                ["command_evidence"] = FailureInterventionPolicy.NormalizeSignature(
                    (evidence.StdoutTail ?? "") + "\n" + (evidence.StderrTail ?? "")),
            },
            new PromptCallContext(
                origin.ProjectName,
                PipelineCatalogue.FailureInterventionStepId,
                model));
        var result = await oneShot.RunAsync(new CliOneShotRequest(cli, model, prompt)
        {
            ThinkingLevel = thinking,
            Timeout = TimeSpan.FromSeconds(90),
            Source = AdHocUsageSources.FailureIntervention,
            Project = origin.ProjectName,
            JobId = origin.Id,
            JobFolderPath = origin.FolderPath,
            StepId = PipelineCatalogue.FailureInterventionStepId,
        }, ct);
        var answer = (result.ParsedText ?? result.Stdout ?? "").Trim();
        var domain = answer.StartsWith("PRODUCT", StringComparison.OrdinalIgnoreCase)
            ? FailureDomains.Product
            : FailureDomains.Infrastructure;
        return FailureInterventionPolicy.FromFallback(evidence, domain,
            $"Economy-model fallback classified the ambiguous failure as {domain}.");
    }

    public IReadOnlyList<FailureInterventionRecord> List(string watchPath, bool openOnly = false)
    {
        lock (_gate)
        {
            var records = Read(watchPath);
            var changed = false;
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                var followUp = _scanner.FindJob(record.FollowUpTaskId, watchPath);
                var open = followUp is not null && !IsTerminal(followUp.State);
                if (!open && record.Status == "open")
                {
                    records[index] = record with { Status = "closed", ResolvedAt = _time.GetUtcNow().UtcDateTime };
                    changed = true;
                }
            }
            if (changed) Write(watchPath, records);
            return records.Where(record => !openOnly || record.Status == "open")
                .OrderByDescending(record => record.CreatedAt).ToArray();
        }
    }

    private bool IsOpen(FailureInterventionRecord record, string watchPath)
    {
        if (record.Status != "open") return false;
        var task = _scanner.FindJob(record.FollowUpTaskId, watchPath);
        return task is not null && !IsTerminal(task.State);
    }

    private void UpdateOriginReferences(TaskInfo origin, string followUpKey)
    {
        var refs = origin.References ?? new TaskReferences();
        _mutations.SetTaskReferences(origin.Id, refs with
        {
            BlockedBy = refs.BlockedBy.Append(followUpKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RaisedFollowUps = refs.RaisedFollowUps.Append(followUpKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }, origin.WatchPath);
    }

    private void UpdateFollowUpReferences(TaskInfo followUp, IReadOnlyList<string> originKeys)
    {
        var refs = followUp.References ?? new TaskReferences();
        _mutations.SetTaskReferences(followUp.Id, refs with
        {
            FollowUpOf = refs.FollowUpOf.Concat(originKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }, followUp.WatchPath);
    }

    private void UpdateFollowUpPrompt(TaskInfo followUp, FailureInterventionRecord intervention)
    {
        var prompt = $"""
# Orchestrator failure intervention

This task was raised automatically by the orchestrator. Diagnose and correct the shared failure. Automatic remediation at detection time is out of scope.

- Failure domain: {intervention.FailureDomain}
- Failure class: {intervention.FailureClass}
- Fingerprint: {intervention.Fingerprint}
- Affected cards: {string.Join(", ", intervention.AffectedCards)}
- Evidence signature: {intervention.Signature}
- Evidence pointers: {string.Join(", ", intervention.EvidencePointers)}

Keep every affected origin in `references.followUpOf`. Resolve the underlying toolchain, configuration, or product defect, then complete this task so waiting origins can be re-driven.
""";
        _mutations.UpdateJobFile(followUp.Id, "prompt.md", prompt, followUp.WatchPath);
    }

    private void RecordSurfaces(TaskInfo origin, FailureInterventionRecord intervention, bool created)
    {
        var summary = $"intervention raised: {intervention.FollowUpKey} ({intervention.FailureClass}, {intervention.Fingerprint})";
        _timeline.Append(origin.FolderPath, new TimelineEvent
        {
            Ts = _time.GetUtcNow().UtcDateTime,
            Kind = TimelineEventKinds.FailureInterventionRaised,
            Actor = TimelineActors.Orchestrator,
            Summary = summary,
            Details = new()
            {
                ["interventionId"] = intervention.Id,
                ["followUpKey"] = intervention.FollowUpKey,
                ["failureClass"] = intervention.FailureClass,
                ["fingerprint"] = intervention.Fingerprint,
                ["deduplicated"] = (!created).ToString().ToLowerInvariant(),
            },
        });
        _orchestratorLog.Append(origin.WatchPath, new OrchestratorLogEntry
        {
            Ts = _time.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Intervention,
            Topic = OrchestratorLogTopics.FailureIntervention,
            Summary = summary,
            Reasoning = $"{intervention.FailureDomain}: {intervention.Signature}",
            JobId = origin.Id,
        });
    }

    private void RecordPipelineStep(TaskInfo origin, FailureInterventionRecord intervention)
    {
        if (_pipelineLog is null) return;
        var run = _pipelineLog.Read(origin.FolderPath);
        if (run is null) return;
        var now = _time.GetUtcNow().UtcDateTime;
        _pipelineLog.RecordStep(origin.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.FailureInterventionStepId,
            Kind = StepKind.Orchestrator,
            Attempt = run.Attempt,
            Status = PipelineStepStatus.Passed,
            StartedAt = now,
            CompletedAt = now,
            DurationMs = 0,
            Verdict = "intervention-raised",
            VerdictSummary = $"intervention raised: {intervention.FollowUpKey} ({intervention.FailureClass}, {intervention.Fingerprint})",
        });
        _pipelineLog.RecordStep(origin.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Attempt = run.Attempt,
            Status = PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            DurationMs = 0,
            Verdict = "intervention",
            VerdictSummary = $"intervention raised: {intervention.FollowUpKey} ({intervention.FailureClass}, {intervention.Fingerprint})",
        });
    }

    private static string BuildPrompt(TaskInfo origin, FailureCommandEvidence evidence, FailureClassificationResult c)
        => $"""
# Orchestrator failure intervention

This task was raised automatically by the orchestrator. Diagnose and correct the shared failure. Automatic remediation at detection time is out of scope.

- Failure domain: {c.Domain}
- Failure class: {c.FailureClass}
- Fingerprint: {c.Fingerprint}
- First affected card: {origin.Key ?? origin.Id}
- Failed step: {evidence.StepId ?? "unknown"}
- Exit code: {evidence.ExitCode?.ToString() ?? "unknown"}
- Duration: {evidence.DurationMs?.ToString() ?? "unknown"} ms
- Evidence signature: {c.Signature}
- Evidence pointers: {string.Join(", ", evidence.EvidencePointers ?? [])}

Keep every affected origin in `references.followUpOf`. Resolve the underlying toolchain, configuration, or product defect, then complete this task so waiting origins can be re-driven.
""";

    private static string ShortTitle(FailureClassificationResult c)
        => c.FailureClass switch
        {
            "ReviewInfra/ToolUnavailable" => "review toolchain unavailable",
            "gate/MissingSource" => "gate source unavailable",
            "gate/build-gate-failed" => "build gate failed",
            "integration/configuration" => "integration unavailable",
            "run/crash-as-completion" => "run crashed at completion",
            _ => c.FailureClass,
        };

    private static bool IsTerminal(string state)
        => string.Equals(state, TaskStates.Completed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(state, TaskStates.Archive, StringComparison.OrdinalIgnoreCase);

    private static string LedgerPath(string watchPath)
        => Path.Combine(watchPath, ".orchestrator", "failure-interventions.json");

    private List<FailureInterventionRecord> Read(string watchPath)
    {
        try
        {
            var path = LedgerPath(watchPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<FailureInterventionRecord>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failure-intervention ledger read failed under {WatchPath}", watchPath);
            return [];
        }
    }

    private void Write(string watchPath, List<FailureInterventionRecord> records)
    {
        var path = LedgerPath(watchPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
        File.Move(temp, path, true);
    }
}
