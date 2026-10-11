using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// The list wire contract. Detail-only history and evidence stay on the task
/// detail route; a card carries the identity, sorting, filters, badges and
/// short status facts used by board consumers.
/// </summary>
internal static class BoardCardSummary
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] SerializeList(IReadOnlyList<TaskInfo> jobs, Func<TaskInfo, byte[]>? serializeCard = null)
    {
        serializeCard ??= Serialize;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var job in jobs)
                writer.WriteRawValue(serializeCard(job), skipInputValidation: true);
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    public static byte[] SerializeGrouped(
        IReadOnlyDictionary<string, IReadOnlyList<TaskInfo>> lanes,
        DateTimeOffset? gitStateAt,
        bool stale,
        Func<TaskInfo, byte[]>? serializeCard = null)
    {
        serializeCard ??= Serialize;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var cards = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (name, jobs) in lanes)
            {
                writer.WritePropertyName(name);
                writer.WriteStartArray();
                foreach (var job in jobs)
                {
                    if (!cards.TryGetValue(job.TaskKey, out var bytes))
                        cards[job.TaskKey] = bytes = serializeCard(job);
                    writer.WriteRawValue(bytes, skipInputValidation: true);
                }
                writer.WriteEndArray();
            }
            writer.WritePropertyName("gitStateAt");
            JsonSerializer.Serialize(writer, gitStateAt, Json);
            writer.WriteBoolean("stale", stale);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] Serialize(TaskInfo job)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = job.Id,
            ["taskKey"] = job.TaskKey,
            ["key"] = job.Key,
            ["title"] = job.Title,
            ["state"] = job.State,
            ["order"] = job.Order,
            ["agent"] = job.Agent,
            ["createdAt"] = job.CreatedAt,
            ["watchPath"] = job.WatchPath,
            ["projectName"] = job.ProjectName,
            ["lastActivity"] = job.LastActivity,
            ["enteredLaneAt"] = job.EnteredLaneAt,
            ["model"] = job.Model,
            ["modelExplicit"] = job.ModelExplicit,
            ["thinkingLevel"] = job.ThinkingLevel,
            ["thinkingLevelExplicit"] = job.ThinkingLevelExplicit,
            ["cliType"] = job.CliType,
            ["kind"] = job.Kind,
            ["epicId"] = job.EpicId,
            ["mode"] = job.Mode,
            ["ownerClientId"] = job.OwnerClientId,
            ["sessionName"] = job.SessionName,
            ["phase"] = job.Phase,
            ["phaseEnteredAt"] = job.PhaseEnteredAt,
            ["steerPendingSince"] = job.SteerPendingSince,
            ["taskType"] = job.TaskType,
            ["tags"] = job.Tags,
            ["taggingStatus"] = job.TaggingStatus,
            ["orchestratorVerdict"] = job.OrchestratorVerdict,
            ["commitCount"] = job.CommitCount,
            ["requiresIntegration"] = job.RequiresIntegration,
        };
        if (job.CodeActivityDetected) fields["codeActivityDetected"] = true;
        if (job.NoBranchExpected) fields["noBranchExpected"] = true;
        if (job.Released) fields["released"] = true;
        if (job.Commits.Count > 0)
        {
            var latest = job.Commits[^1];
            fields["commitSummary"] = new
            {
                count = job.Commits.Count,
                latestShortSha = latest.ShortSha,
                latestSubject = Short(latest.Message, 100),
                latestFilesChanged = latest.FilesChanged,
                hasFiles = latest.Files.Count > 0,
            };
        }
        if (job.Provenance is { } provenance)
            fields["gitContext"] = new
            {
                branch = provenance.Branch,
                branchTip = provenance.Transitions.LastOrDefault(t => !string.IsNullOrEmpty(t.BranchTip))?.BranchTip,
                mergeSha = provenance.Merge?.MergeCommit,
            };
        if (job.TokenSummary is { } tokens)
        {
            var tokenFields = new Dictionary<string, object?>
            {
                ["totalTokens"] = tokens.TotalTokens,
                ["allModelsPriced"] = tokens.AllModelsPriced,
            };
            if (tokens.InputTokens != 0) tokenFields["inputTokens"] = tokens.InputTokens;
            if (tokens.OutputTokens != 0) tokenFields["outputTokens"] = tokens.OutputTokens;
            if (tokens.CacheReadTokens != 0) tokenFields["cacheReadTokens"] = tokens.CacheReadTokens;
            if (tokens.CacheCreationTokens != 0) tokenFields["cacheCreationTokens"] = tokens.CacheCreationTokens;
            if (tokens.EstimatedApiCostUsd != 0) tokenFields["estimatedApiCostUsd"] = tokens.EstimatedApiCostUsd;
            if (tokens.LastModel is not null) tokenFields["lastModel"] = tokens.LastModel;
            if (tokens.LastUpdate is not null) tokenFields["lastUpdate"] = tokens.LastUpdate;
            if (tokens.HasModelMismatch) tokenFields["hasModelMismatch"] = true;
            fields["tokenSummary"] = tokenFields;
        }
        if (job.Integration is { } integration)
            fields["integration"] = new
            {
                integration.Status, integration.DeliveryRef, integration.Sha,
                integration.IntegrationBranch, integration.ReachUnavailable,
                detail = Short(integration.Detail, 120),
                repositoryLabel = Short(string.Join(" · ", integration.Repositories.Select(repository =>
                    $"{repository.Repository} {repository.Commits.Count(commit => commit.OnIntegrationBranch)}/{repository.Commits.Count} " +
                    $"{repository.IntegrationBranch}{(repository.OnReleaseBranch && repository.ReleaseBranch != repository.IntegrationBranch ? " and " + repository.ReleaseBranch : "")}")), 160),
                repositoryDetail = Short(string.Join(" · ", integration.Repositories.Select(repository =>
                    $"{repository.Repository}: {repository.Detail}")), 180),
                verification = integration.Verification is null ? null : new
                {
                    integration.Verification.State,
                    integration.Verification.Sha,
                    integration.Verification.GateFailed,
                    reason = Short(integration.Verification.Reason, 100),
                },
                failure = integration.Failure is null ? null : new
                {
                    integration.Failure.Code,
                    integration.Failure.Label,
                    reason = Short(integration.Failure.Reason, 120),
                    failureClass = integration.Failure.FailureClass.ToString().ToLowerInvariant(),
                    integration.Failure.RebaseRecoveryAvailable,
                },
            };
        if (job.TestEvidence is { } evidence)
            fields["testEvidence"] = new
            {
                evidence.EvidenceState, evidence.MatchQuality, evidence.Summary,
                evidence.RunId, evidence.RunCommit, evidence.RunState, evidence.RunResult,
                sourceCount = evidence.Sources.Count,
                sourceLabel = Short(evidence.Sources.FirstOrDefault()?.Summary, 80),
            };
        if (job.OutcomeIssue is { } issue)
            fields["outcomeIssue"] = new
            {
                issue.Kind, issue.Label, issue.Severity,
                summary = Short(issue.Summary, 120), issue.LastSeenAt,
            };
        if (job.ParkedBlocker is { } park)
            fields["parkedBlocker"] = new
            {
                park.BlockerType, park.ConditionKind,
                conditionDescription = Short(park.ConditionDescription, 120),
                park.ParkedAt, park.ParkedForSeconds,
                reason = Short(park.Reason, 120), park.RecallStatus,
                park.RequiresDecisionCard,
                decision = park.Decision is null ? null : new
                {
                    park.Decision.QuestionId,
                    question = Short(park.Decision.Question, 120),
                    park.Decision.DecisionCardKey,
                },
            };
        if (job.Decision is { } decision)
            fields["decision"] = new
            {
                question = Short(decision.Question, 120),
                decision.Status, decision.Decider, decision.DecidedBy,
                decision.DueDate,
                // Board badge tooltip and the Dossier decision inbox list the
                // cards a pending decision blocks.
                dependants = decision.Dependants.Count == 0 ? null : decision.Dependants.Take(10).ToList(),
            };
        if (job.OlderBriefDelivery is { } olderBrief)
            fields["olderBriefDelivery"] = new { olderBrief.Status };
        // Small status projections used directly by board badges and filters.
        Add(fields, "execution", job.Execution);
        Add(fields, "quotaFallback", job.QuotaFallback);
        Add(fields, "quotaWait", job.QuotaWait);
        Add(fields, "causeWait", job.CauseWait);
        Add(fields, "remoteClaimFailure", job.RemoteClaimFailure);
        Add(fields, "pendingIntent", job.PendingIntent);
        if (job.NeedsInput is { } need)
            fields["needsInput"] = new { message = Short(need.FirstLine, 120) ?? "" };
        if (job.AutoLoop is { } loop)
            fields["autoLoop"] = new
            {
                loop.Iteration, loop.MaxIterations, loop.TokensUsed, loop.MaxTokens,
                loop.StartedAt,
                lastQuestion = Short(loop.LastQuestion, 120),
                lastError = Short(loop.LastError, 100),
            };
        Add(fields, "summaryState", job.SummaryState);
        if (job.PostProcessingChecks.FirstOrDefault(check => check.Status == "running") is { } runningCheck)
            fields["postProcessingChecks"] = new[] { new { runningCheck.Name, runningCheck.Status } };
        if (!job.References.IsEmpty)
            fields["references"] = new
            {
                dependsOn = job.References.DependsOn,
                raisedFollowUps = job.References.RaisedFollowUps.Take(1).ToArray(),
            };
        if (job.WaitsOn is { } waits)
            fields["waitsOn"] = new
            {
                waits.Blocked, waits.CycleDetected, waits.UnsatisfiableGate,
                itemCount = waits.Items.Count,
                openCount = waits.Items.Count(item => !item.Fulfilled),
                items = waits.Items.OrderByDescending(item => item.Unsatisfiable)
                    .ThenBy(item => item.Fulfilled).Take(3)
                    .Select(item => new
                    {
                        item.Key, item.Resolved, item.PendingDecision, item.Fulfilled,
                        item.WaitingForRelease, item.Unsatisfiable, item.TargetJobId,
                        item.TargetState,
                        targetTitle = Short(item.TargetTitle, 40), item.TargetWatchPath,
                    }).ToArray(),
            };
        Add(fields, "blockedBy", job.BlockedBy.Count == 0 ? null : job.BlockedBy);
        Add(fields, "transitiveWaiters", job.TransitiveWaiters);
        Add(fields, "pickupHold", job.PickupHold);
        Add(fields, "runActivity", job.RunActivity);
        if (job.LiveStatus is { } live)
            fields["liveStatus"] = new
            {
                live.Attempt, live.ActiveStep,
                nextSteps = live.NextSteps.Take(3).Select(step => new { step.DisplayName }).ToArray(),
                live.Queue, live.LatestEventAt,
            };
        Add(fields, "externalCompletion", job.ExternalCompletion);
        Add(fields, "runner", job.Runner);
        Add(fields, "executionLocation", job.ExecutionLocation);
        Add(fields, "mergeSignal", job.MergeSignal);
        Add(fields, "publishSignal", job.PublishSignal);
        Add(fields, "betterCandidates", job.BetterCandidates);
        // AGT-3051: the review-queue badge and the "Review first" menu state
        // read these two fields from the board card.
        if (job.ReviewPriority == "urgent") fields["reviewPriority"] = job.ReviewPriority;
        Add(fields, "reviewQueuePosition", job.ReviewQueuePosition);
        return JsonSerializer.SerializeToUtf8Bytes(fields, Json);
    }

    public static JsonElement AsJsonElement(TaskInfo job)
        => JsonSerializer.Deserialize<JsonElement>(Serialize(job));

    private static void Add(Dictionary<string, object?> fields, string name, object? value)
    {
        if (value is not null) fields[name] = value;
    }

    private static string? Short(string? value, int max)
    {
        if (value is null) return null;
        var firstLine = value.Split('\n', 2)[0].Trim();
        return firstLine.Length <= max ? firstLine : firstLine[..max];
    }
}
