using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// The list wire contract. Detail-only history and evidence stay on the task
/// detail route; a card carries the identity, sorting, filters, badges and
/// short status facts used by board consumers.
///
/// <para>Sub-objects are sparse: a value that is null, false, empty or the
/// documented default is left out, and long text is cut to what a card face
/// or tooltip shows. The full values live on <c>/details/overview</c>
/// (parked blocker, waits-on, execution location, pending intent) and
/// <c>/details/git</c> (merge signal, integration, test evidence).</para>
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

    /// <summary>Short SHA length on the card; consumers display seven characters.</summary>
    private const int ShortShaLength = 9;

    /// <summary>
    /// One board card. The task key is left out on the list
    /// and grouped routes: there the key is always <c>{watchPath}::{id}</c>,
    /// which the client derives (task.service), and repeating the watch path
    /// and the often long id cost about 180 bytes per card. Single-card replies
    /// (query rows, commit owner) keep it for API callers.
    /// </summary>
    public static byte[] Serialize(TaskInfo job) => Serialize(job, includeTaskKey: false);

    /// <inheritdoc cref="Serialize(TaskInfo)"/>
    public static byte[] Serialize(TaskInfo job, bool includeTaskKey)
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
            // The card shows ages, so instants it alone uses are whole seconds.
            ["createdAt"] = WholeSeconds(job.CreatedAt),
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
        if (!includeTaskKey && job.TaskKey == TaskIdentity.CreateKey(job.WatchPath, job.Id))
            fields.Remove("taskKey");
        // An absent top-level value reads as null on every consumer.
        foreach (var name in fields.Where(field => field.Value is null or "").Select(field => field.Key).ToList())
            fields.Remove(name);
        if (job.CodeActivityDetected) fields["codeActivityDetected"] = true;
        if (job.NoBranchExpected) fields["noBranchExpected"] = true;
        if (job.Released) fields["released"] = true;
        if (job.Commits.Count > 0)
        {
            var latest = job.Commits[^1];
            fields["commitSummary"] = new
            {
                // The count is the card's commitCount.
                latestShortSha = latest.ShortSha,
                latestSubject = Short(latest.Message, 72),
                latestFilesChanged = latest.FilesChanged,
                hasFiles = latest.Files.Count > 0,
            };
        }
        if (job.Provenance is { } provenance)
            fields["gitContext"] = new
            {
                // The default task/{id} branch repeats the id; the client
                // restores it when gitContext carries no branch.
                branch = provenance.Branch == DefaultBranch(job.Id) ? null : provenance.Branch,
                branchTip = ShortSha(provenance.Transitions.LastOrDefault(t => !string.IsNullOrEmpty(t.BranchTip))?.BranchTip),
                mergeSha = ShortSha(provenance.Merge?.MergeCommit),
            };
        if (job.TokenSummary is { } tokens)
        {
            var tokenFields = new Dictionary<string, object?>();
            // The total is the sum of the four counters on every live card;
            // it is sent only when it differs. Absent allModelsPriced reads
            // as priced; only an unpriced model is named.
            if (tokens.TotalTokens != tokens.InputTokens + tokens.OutputTokens
                + tokens.CacheReadTokens + tokens.CacheCreationTokens)
                tokenFields["totalTokens"] = tokens.TotalTokens;
            if (!tokens.AllModelsPriced) tokenFields["allModelsPriced"] = false;
            if (tokens.InputTokens != 0) tokenFields["inputTokens"] = tokens.InputTokens;
            if (tokens.OutputTokens != 0) tokenFields["outputTokens"] = tokens.OutputTokens;
            if (tokens.CacheReadTokens != 0) tokenFields["cacheReadTokens"] = tokens.CacheReadTokens;
            if (tokens.CacheCreationTokens != 0) tokenFields["cacheCreationTokens"] = tokens.CacheCreationTokens;
            if (tokens.EstimatedApiCostUsd != 0) tokenFields["estimatedApiCostUsd"] = tokens.EstimatedApiCostUsd;
            if (tokens.LastModel is not null) tokenFields["lastModel"] = tokens.LastModel;
            if (tokens.LastUpdate is { } lastUpdate) tokenFields["lastUpdate"] = WholeSeconds(lastUpdate);
            if (tokens.HasModelMismatch) tokenFields["hasModelMismatch"] = true;
            fields["tokenSummary"] = tokenFields;
        }
        if (job.Integration is { } integration)
            fields["integration"] = new
            {
                integration.Status,
                // The board names the delivery ref only while it is not yet
                // integrated, or when no commit is attributed to the card.
                deliveryRef = integration.Status == IntegrationStatuses.Integrated && job.Commits.Count > 0
                    ? null : integration.DeliveryRef,
                integration.Sha,
                integrationBranch = integration.IntegrationBranch == "develop" ? null : NullIfEmpty(integration.IntegrationBranch),
                reachUnavailable = integration.ReachUnavailable ? true : (bool?)null,
                detail = Short(integration.Detail, 120),
                repositoryLabel = NullIfEmpty(Short(string.Join(" · ", integration.Repositories.Select(repository =>
                    $"{repository.Repository} {repository.Commits.Count(commit => commit.OnIntegrationBranch)}/{repository.Commits.Count} " +
                    $"{repository.IntegrationBranch}{(repository.OnReleaseBranch && repository.ReleaseBranch != repository.IntegrationBranch ? " and " + repository.ReleaseBranch : "")}")), 160)),
                repositoryDetail = NullIfEmpty(Short(string.Join(" · ", integration.Repositories.Select(repository =>
                    $"{repository.Repository}: {repository.Detail}")), 180)),
                // The badge tooltip shows a seven-character SHA and one short
                // sentence; the full reason is on /details/git.
                verification = integration.Verification is null ? null : new
                {
                    integration.Verification.State,
                    sha = ShortSha(integration.Verification.Sha),
                    gateFailed = integration.Verification.GateFailed ? true : (bool?)null,
                    reason = Short(integration.Verification.Reason, 60),
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
                // Usually the summary itself; sent only when it adds something.
                // Both sides are trimmed first lines, so trailing whitespace on
                // the summary does not make an identical label look different.
                sourceLabel = Short(evidence.Sources.FirstOrDefault()?.Summary, 80) is { } label
                    && label != Short(evidence.Summary, 80) ? label : null,
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
                // The board badge reads the type, the decision flag, the
                // question or reason and the park instant. The condition and
                // the recall verdict belong to the parked section, which reads
                // them from /details/overview.
                park.BlockerType,
                // No read-time age: the client derives it from ParkedAt, so an
                // unchanged parked card serializes to the same bytes on every read.
                parkedAt = WholeSeconds(park.ParkedAt),
                reason = Short(park.Reason, 100),
                requiresDecisionCard = park.RequiresDecisionCard ? true : (bool?)null,
                decision = park.Decision is null
                    || (string.IsNullOrEmpty(park.Decision.QuestionId) && string.IsNullOrEmpty(park.Decision.Question)
                        && park.Decision.DecisionCardKey is null) ? null : new
                {
                    questionId = NullIfEmpty(park.Decision.QuestionId),
                    question = NullIfEmpty(Short(park.Decision.Question, 120)),
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
        if (job.PendingIntent is { } intent)
        {
            // pending-intent.json is read without a non-null check, so a
            // "prompt": null file must not fail the whole board read.
            var pendingPrompt = intent.Prompt ?? string.Empty;
            fields["pendingIntent"] = new
            {
                // A steer prompt can run to pages; the Ready card shows its
                // start and the tooltip 120 characters. /details/overview
                // carries the whole intent.
                intent.RunIntent, intent.Mode, intent.SavedAt,
                prompt = NullIfEmpty(pendingPrompt.Length <= PendingPromptLength
                    ? pendingPrompt : pendingPrompt[..PendingPromptLength]),
                promptTruncated = pendingPrompt.Length > PendingPromptLength ? true : (bool?)null,
            };
        }
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
                // Every key, so the count is its length.
                dependsOnKeys = job.References.DependsOn.Select(dependency => dependency.Key).ToArray(),
                // A plain edge serializes as its key, which dependsOnKeys
                // already lists; the object form is sent only for release gates.
                dependsOn = job.References.DependsOn.Take(3).Any(dependency => dependency.ReleaseGate)
                    ? job.References.DependsOn.Take(3).ToArray() : null,
                raisedFollowUps = job.References.RaisedFollowUps.Count == 0
                    ? null : job.References.RaisedFollowUps.Take(1).ToArray(),
            };
        if (job.WaitsOn is { } waits)
            fields["waitsOn"] = new
            {
                blocked = Flag(waits.Blocked), cycleDetected = Flag(waits.CycleDetected),
                unsatisfiableGate = Flag(waits.UnsatisfiableGate),
                itemCount = waits.Items.Count,
                openCount = waits.Items.Count(item => !item.Fulfilled),
                anyWaitingForRelease = Flag(waits.Items.Any(item => item.WaitingForRelease)),
                // The "waiting for release" filter keeps these targets visible;
                // it matches them by key, so the long job id and watch path
                // stay on /details/overview.
                releaseTargetKeys = waits.Items.Any(item => item.WaitingForRelease)
                    ? waits.Items.Where(item => item.WaitingForRelease).Select(item => item.Key).ToArray()
                    : null,
                items = waits.Items.OrderByDescending(item => item.Unsatisfiable)
                    .ThenBy(item => item.Fulfilled).Take(3)
                    // A fulfilled edge no longer explains a wait: the chip
                    // tooltip lists it as "KEY (done)", so it carries its key
                    // and flags only. The title stays on open edges; the
                    // target lane, which no board consumer reads, is on
                    // /details/overview.
                    .Select(item => new
                    {
                        item.Key,
                        // An existing target is the default; only a missing one is named.
                        resolved = item.Resolved || item.Fulfilled ? (bool?)null : false, pendingDecision = Flag(item.PendingDecision),
                        fulfilled = Flag(item.Fulfilled), releaseGate = Flag(item.ReleaseGate),
                        targetReleased = Flag(item.TargetReleased && !item.Fulfilled),
                        waitingForRelease = Flag(item.WaitingForRelease),
                        unsatisfiable = Flag(item.Unsatisfiable),
                        targetTitle = item.Fulfilled ? null : Short(item.TargetTitle, 40),
                    }).ToArray(),
            };
        Add(fields, "blockedBy", job.BlockedBy.Count == 0 ? null : job.BlockedBy);
        Add(fields, "transitiveWaiters", job.TransitiveWaiters);
        if (job.PickupHold is { } hold)
            fields["pickupHold"] = new
            {
                // HeldForSeconds stays off the card for the same reason as the
                // parked age; the client derives it from SinceUtc.
                hold.Classification, hold.Mechanism, hold.Reason, hold.SinceUtc,
                hold.Unsatisfiable, hold.Resolutions, hold.AttentionTargetKey, hold.AttentionReason,
                hold.AttentionSinceUtc,
            };
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
        Add(fields, "executionLocation", ExecutionLocationOnCard(job.State, job.ExecutionLocation));
        if (job.MergeSignal is { } merge)
            fields["mergeSignal"] = new
            {
                // The branch is gitContext.branch; the d/m segments default
                // their labels to develop and main.
                inIntegration = Flag(merge.InIntegration), inRelease = Flag(merge.InRelease),
                integrationBranch = merge.IntegrationBranch == "develop" ? null : NullIfEmpty(merge.IntegrationBranch),
                releaseBranch = merge.ReleaseBranch == "main" ? null : NullIfEmpty(merge.ReleaseBranch),
                integrationSha = ShortSha(merge.IntegrationSha),
                releaseSha = ShortSha(merge.ReleaseSha),
            };
        Add(fields, "publishSignal", job.PublishSignal);
        Add(fields, "betterCandidates", job.BetterCandidates);
        // AGT-3051: the review-queue badge and the "Review first" menu state
        // read these two fields from the board card.
        if (job.ReviewPriority == "urgent") fields["reviewPriority"] = job.ReviewPriority;
        Add(fields, "reviewQueuePosition", job.ReviewQueuePosition);
        return JsonSerializer.SerializeToUtf8Bytes(fields, Json);
    }

    /// <summary>
    /// The execution location a card face can show. An idle location (no run,
    /// no rejection, not historical) renders nothing on a card; only a Ready
    /// card reads its configured runner, to pick the provider-auth check.
    /// Branch, worktree path, trust reason, session and last-activity stamp are
    /// detail facts on /details/overview. <see cref="BoardReadSignatureSource"/>
    /// folds this projection rather than the full record, so a client
    /// heartbeat the card does not show cannot invalidate the board.
    /// </summary>
    internal static BoardExecutionLocation? ExecutionLocationOnCard(string lane, TaskExecutionLocation? location)
    {
        if (location is null) return null;
        var idle = location.State == TaskExecutionStates.NoActiveExecution
            && location.LastRejection is null && !location.Historical;
        if (idle && (lane != TaskStates.Ready || location.ConfiguredRunnerId is null)) return null;
        return new BoardExecutionLocation(
            location.State, location.ExecutionKind, location.RunnerId, location.ConfiguredRunnerId,
            location.HostDisplayName, location.StartedAt, location.LastHeartbeat, location.ProcessId,
            location.ConnectionState == "none" ? null : location.ConnectionState,
            location.LeaseState == "none" ? null : location.LeaseState,
            location.Historical ? true : null, location.LastRejection, location.LastRunnerEvent);
    }

    public static JsonElement AsJsonElement(TaskInfo job)
        => JsonSerializer.Deserialize<JsonElement>(Serialize(job, includeTaskKey: true));

    private static string DefaultBranch(string id) => "task/" + id;

    private static void Add(Dictionary<string, object?> fields, string name, object? value)
    {
        if (value is not null) fields[name] = value;
    }

    private const int PendingPromptLength = 200;

    private static bool? Flag(bool value) => value ? true : null;

    private static DateTime WholeSeconds(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? ShortSha(string? sha)
        => string.IsNullOrEmpty(sha) ? null : sha.Length <= ShortShaLength ? sha : sha[..ShortShaLength];

    private static string? Short(string? value, int max)
    {
        if (value is null) return null;
        var firstLine = value.Split('\n', 2)[0].Trim();
        return firstLine.Length <= max ? firstLine : firstLine[..max];
    }
}

/// <summary>Card shape of <see cref="TaskExecutionLocation"/>; value equality makes it foldable.</summary>
internal sealed record BoardExecutionLocation(
    string State, string ExecutionKind, string? RunnerId, string? ConfiguredRunnerId,
    string? HostDisplayName, DateTime? StartedAt, DateTime? LastHeartbeat, int? ProcessId,
    string? ConnectionState, string? LeaseState, bool? Historical,
    RemoteDispatchRejection? LastRejection, string? LastRunnerEvent);
