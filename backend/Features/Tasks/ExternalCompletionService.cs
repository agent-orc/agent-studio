using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Tasks;

/// <summary>
/// Reconciles a task that was completed outside the runner (operator chat, an
/// external agent, a remote host) in one atomic call, implementing §3 of
/// <c>docs/concepts/out-of-band-task-completion.md</c>.
///
/// <para>
/// The out-of-band completion of AGT-1917 left the card looking abandoned: the
/// work was done and committed, but <c>status.md</c> still said "escalated / no
/// summary", <c>lifecycle.json</c> was stuck in <c>post-processing-running</c>
/// (spamming scanner warnings), and the run history ended in a corpse. This
/// service is the product fix: it writes <c>status.md</c> +
/// <c>results/deliverables.md</c>, terminalizes <c>lifecycle.json</c>, records
/// the external provenance on <c>task.json</c>, appends an
/// <c>external_completion</c> row to the timeline, moves the lane, and commits
/// the workspace evidence - so the card's story is owned, not just its lane.
/// </para>
/// </summary>
public sealed class ExternalCompletionService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly WorkspaceArtifactCommitService _artifactCommits;
    private readonly HumanReviewEscalation _escalation;
    private readonly GitService _git;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalCompletionService> _logger;

    public ExternalCompletionService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskTransitionService transitions,
        TimelineLog timeline,
        WorkspaceArtifactCommitService artifactCommits,
        HumanReviewEscalation escalation,
        GitService git,
        IConfiguration configuration,
        ILogger<ExternalCompletionService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _timeline = timeline;
        _artifactCommits = artifactCommits;
        _escalation = escalation;
        _git = git;
        _configuration = configuration;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions LifecycleJsonWriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ExternalCompletionOutcome> CompleteAsync(
        string jobId,
        string? watchPath,
        ExternalCompletionRequest request,
        string actor,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Summary))
            return new ExternalCompletionOutcome(ExternalCompletionStatus.InvalidRequest, "summary is required.");

        var targetState = string.IsNullOrWhiteSpace(request.TargetState)
            ? TaskStates.HumanReview
            : request.TargetState!.Trim();
        if (!TaskStates.All.Contains(targetState))
            return new ExternalCompletionOutcome(
                ExternalCompletionStatus.InvalidRequest,
                $"Invalid targetState. Allowed: {string.Join(", ", TaskStates.All)}");

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            return new ExternalCompletionOutcome(ExternalCompletionStatus.NotFound);

        var source = string.IsNullOrWhiteSpace(request.Source) ? "external" : request.Source!.Trim();
        var summary = request.Summary!.Trim();
        var now = DateTime.UtcNow;
        var beforeFolder = info.FolderPath;

        // AGT-2220 - the invariant gate. Before ANY evidence is written, the
        // claimed delivery is re-verified against the target repository. The
        // caller's prose ("the repository was verified at <ref>") is not proof:
        // that exact sentence is what stamped the 11.07. phantom wave. A claim
        // that cannot be proven never becomes a completion stamp.
        DeliveryVerificationResult? verification = null;
        if (!string.IsNullOrWhiteSpace(request.ResultSha))
        {
            verification = _git.VerifyDeliveredCommit(
                _git.ResolveRepoRootForWatchPath(info.WatchPath),
                request.ResultRef,
                request.ResultSha);
        }

        var (decision, decisionReason) =
            OutOfBandStampPolicy.Decide(info.Mode, targetState, verification);
        if (decision == OutOfBandStampDecision.RefuseUnverified)
        {
            return await RefuseUnverifiedAsync(
                jobId, watchPath, info, request, source, summary, decisionReason, verification, now, ct);
        }

        // §3 order: write the reconciled evidence into the current folder, then
        // move the lane LAST, then commit the workspace snapshot. Every write
        // is best-effort-logged but the endpoint treats a hard failure of the
        // canonical writes (status / deliverables / task.json) as fatal so the
        // caller is not told a half-reconciled card is done.
        WriteDeliverables(beforeFolder, summary, source, now, request.Deliverables, decisionReason);
        WriteStatus(beforeFolder, summary, source, now, decisionReason);
        WriteGateItems(beforeFolder, request.GateItems);
        _mutations.SetExternalCompletionOnFolder(beforeFolder, new ExternalCompletionInfo
        {
            Source = source,
            Summary = summary,
            CompletedAt = now,
        });

        // A proven delivery also earns its commits[]: the attribution runs
        // through the same primitive as the live runner completion path
        // (InspectRemoteDeliveryCommitRange + RemoteCommitAttributionGuard), so
        // an out-of-band stamp and an in-process one record identical evidence.
        if (decision == OutOfBandStampDecision.Stamp && verification?.IsVerified == true)
        {
            AttributeVerifiedDelivery(info, beforeFolder, request, verification, source);
            _mutations.AddJobTag(jobId, OutOfBandStampPolicy.VerifiedDeliveryTag, watchPath);
        }
        else if (decision == OutOfBandStampDecision.StampUnproven)
        {
            // Reconciled, not delivered: the card is cared for, but nothing here
            // may read as proven work. No commits[], and the board says so.
            _mutations.AddJobTag(jobId, OutOfBandStampPolicy.UnverifiedDeliveryTag, watchPath);
        }
        TerminalizeLifecycle(beforeFolder, targetState, source, now);

        // Append the external ingest entry to the unified timeline BEFORE the
        // move so it lands in the same folder as the rest of the evidence; the
        // subsequent lane_changed row is emitted by the state machine.
        _timeline.Append(
            beforeFolder,
            TimelineEventKinds.ExternalCompletion,
            TimelineActors.External,
            summary: $"Completed externally by {source}",
            payloadRef: "results/deliverables.md",
            details: new Dictionary<string, string>
            {
                ["source"] = source,
                ["targetState"] = targetState,
                ["verification"] = (verification?.Status ?? DeliveryVerificationStatus.NotVerifiable)
                    .ToString(),
                ["verificationNote"] = decisionReason,
                ["verifiedSha"] = verification?.ClaimedSha ?? string.Empty,
                ["verifiedRef"] = verification?.GitRef ?? string.Empty,
            });

        var moveCause = string.IsNullOrWhiteSpace(actor) ? TimelineActors.External : actor;
        var move = targetState == TaskStates.Escalated
            ? await _escalation.EscalateAsync(
                jobId,
                watchPath ?? info.WatchPath,
                info.ProjectName,
                ExternalEscalationCategory(request.GateItems),
                summary,
                ct)
            : await _transitions.MoveAsync(
                jobId, targetState, watchPath, ct, cause: moveCause,
                transitionCause: LaneChangeCauses.ExternalCompletion, transitionDetail: source);
        var afterFolder = beforeFolder;
        switch (move.Status)
        {
            case MoveJobStatus.Success:
                afterFolder = move.NewFolderPath ?? beforeFolder;
                // Re-assert the terminal lifecycle into the moved folder: a move
                // out of 3-progress runs EnterPostProcessingPhase, which would
                // otherwise reset lifecycle.json to post-processing-running - the
                // exact stuck state this endpoint exists to retire. Idempotent for
                // every other source lane.
                TerminalizeLifecycle(afterFolder, targetState, source, now);
                break;
            case MoveJobStatus.NotFound:
                // Raced away between find and move; the evidence is already on
                // disk, so surface it as a move failure rather than a 404.
                return new ExternalCompletionOutcome(
                    ExternalCompletionStatus.MoveFailed, "Task disappeared before the lane move.", jobId);
            case MoveJobStatus.TargetFolderExists:
            case MoveJobStatus.DirectoryLocked:
                return new ExternalCompletionOutcome(ExternalCompletionStatus.MoveConflict, move.Message, jobId);
            default:
                return new ExternalCompletionOutcome(ExternalCompletionStatus.MoveFailed, move.Message, jobId);
        }

        var commit = _artifactCommits.TryCommitExternalCompletion(
            _configuration["TaskRepository"], jobId, beforeFolder, afterFolder, source);
        if (!commit.Success)
        {
            _logger.LogWarning(
                "external-completion-evidence-commit-failed project={Project} job={JobId} error={Error}",
                info.ProjectName, jobId, commit.Error);
        }

        _logger.LogInformation(
            "external-completion project={Project} job={JobId} source={Source} targetState={TargetState} evidenceSha={Sha}",
            info.ProjectName, jobId, source, targetState, commit.Sha ?? "");

        return new ExternalCompletionOutcome(
            ExternalCompletionStatus.Success,
            JobId: jobId,
            TargetState: targetState,
            EvidenceCommitSha: commit.DidCommit ? commit.Sha : null);
    }

    /// <summary>
    /// AGT-2220 - the honest state. A completion claim without repository proof
    /// does NOT become "Completed out-of-band". Instead the card keeps an
    /// explicit, board-visible <c>unverified-delivery</c> record: what was
    /// claimed, what the repository actually holds, and why the stamp was
    /// refused. Nothing here writes <c>externalCompletion</c>, terminalizes the
    /// lifecycle, or touches <c>commits[]</c> - an unproven delivery leaves no
    /// trace that could later be mistaken for evidence.
    /// </summary>
    private async Task<ExternalCompletionOutcome> RefuseUnverifiedAsync(
        string jobId,
        string? watchPath,
        TaskInfo info,
        ExternalCompletionRequest request,
        string source,
        string summary,
        string reason,
        DeliveryVerificationResult? verification,
        DateTime now,
        CancellationToken ct)
    {
        var folder = info.FolderPath;
        WriteUnverifiedDeliveryReport(folder, summary, source, reason, request, verification, now);
        WriteGateItems(folder, request.GateItems);
        _mutations.AddJobTag(jobId, OutOfBandStampPolicy.UnverifiedDeliveryTag, watchPath);

        _timeline.Append(
            folder,
            TimelineEventKinds.DeliveryUnverified,
            TimelineActors.External,
            summary: $"Completion stamp refused: delivery from {source} is unverified",
            payloadRef: "results/unverified-delivery.md",
            details: new Dictionary<string, string>
            {
                ["source"] = source,
                ["reason"] = reason,
                ["verification"] = (verification?.Status ?? DeliveryVerificationStatus.NotVerifiable)
                    .ToString(),
                ["claimedSha"] = verification?.ClaimedSha ?? request.ResultSha ?? string.Empty,
                ["claimedRef"] = verification?.GitRef ?? request.ResultRef ?? string.Empty,
                ["repositorySha"] = verification?.ResolvedRefSha ?? string.Empty,
            });

        _logger.LogWarning(
            "external-completion-refused-unverified project={Project} job={JobId} source={Source} "
            + "verification={Verification} claimedSha={ClaimedSha} claimedRef={ClaimedRef} repositorySha={RepositorySha}",
            info.ProjectName, jobId, source,
            verification?.Status ?? DeliveryVerificationStatus.NotVerifiable,
            verification?.ClaimedSha ?? request.ResultSha ?? "",
            verification?.GitRef ?? request.ResultRef ?? "",
            verification?.ResolvedRefSha ?? "");

        var escalated = await _escalation.EscalateAsync(
            jobId,
            watchPath ?? info.WatchPath,
            info.ProjectName,
            HumanReviewEscalationCategories.UnverifiedDelivery,
            reason,
            ct);
        if (escalated.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "external-completion-refused-unverified-move-failed job={JobId} status={Status} message={Message}",
                jobId, escalated.Status, escalated.Message);
        }

        return new ExternalCompletionOutcome(
            ExternalCompletionStatus.UnverifiedDelivery,
            reason,
            jobId,
            TargetState: escalated.Status == MoveJobStatus.Success ? TaskStates.Escalated : info.State);
    }

    /// <summary>
    /// Records the proven delivery as <c>commits[]</c> using the same range
    /// inspection and attribution guard the in-process runner completion uses,
    /// so a verified out-of-band stamp is evidence-equivalent to a normal one.
    /// Best-effort: the stamp itself already rests on
    /// <see cref="GitService.VerifyDeliveredCommit"/>.
    /// </summary>
    private void AttributeVerifiedDelivery(
        TaskInfo info,
        string folderPath,
        ExternalCompletionRequest request,
        DeliveryVerificationResult verification,
        string source)
    {
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath);
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(verification.GitRef))
                return;

            var range = _git.InspectRemoteDeliveryCommitRange(
                repoRoot, verification.GitRef!, verification.ClaimedSha!, info.IntegrationBranch);
            if (!range.Success) return;

            var attribution = RemoteCommitAttributionGuard.Attribute(
                info.Key ?? info.Id,
                verification.GitRef!,
                range.Commits,
                ReviewSubjectStore.Read(folderPath)?.Repository);
            _mutations.SetRunIntegrationBranchOnFolder(folderPath, range.IntegrationBranch!);
            _mutations.SetRemoteCommitAttributionOnFolder(
                folderPath,
                runAttemptId: $"external:{source}",
                runnerId: source,
                resultSha: verification.ClaimedSha!,
                attributed: attribution.Commits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "external-completion: verified delivery could not be attributed for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Writes <c>results/unverified-delivery.md</c> plus a matching
    /// <c>status.md</c>: the refusal is documented as a first-class result, not
    /// as an absence.
    /// </summary>
    private void WriteUnverifiedDeliveryReport(
        string folderPath,
        string summary,
        string source,
        string reason,
        ExternalCompletionRequest request,
        DeliveryVerificationResult? verification,
        DateTime now)
    {
        try
        {
            var resultsDir = TaskPaths.ResultsDir(folderPath);
            Directory.CreateDirectory(resultsDir);

            var sb = new StringBuilder();
            sb.Append("# Unverified delivery - completion stamp refused\n\n");
            sb.Append("- Result: **Unverified delivery** (kein Completed-Stempel)\n");
            sb.Append("- Gemeldet von: ").Append(source).Append('\n');
            sb.Append("- Geprueft am: ")
              .Append(now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
              .Append("\n\n");
            sb.Append("## Warum kein Stempel\n\n").Append(reason).Append("\n\n");
            sb.Append("## Anspruch\n\n");
            sb.Append("- Behaupteter Commit: `")
              .Append(string.IsNullOrWhiteSpace(request.ResultSha) ? "(keiner)" : request.ResultSha)
              .Append("`\n");
            sb.Append("- Behaupteter Ref: `")
              .Append(string.IsNullOrWhiteSpace(request.ResultRef) ? "(keiner)" : request.ResultRef)
              .Append("`\n");
            sb.Append("- Zielrepo haelt dort: `")
              .Append(string.IsNullOrWhiteSpace(verification?.ResolvedRefSha)
                  ? "(nicht aufloesbar)"
                  : verification!.ResolvedRefSha)
              .Append("`\n");
            sb.Append("- Verifikationsverdikt: `")
              .Append(verification?.Status ?? DeliveryVerificationStatus.NotVerifiable)
              .Append("`\n\n");
            sb.Append("## Gemeldete Zusammenfassung (unbestaetigt)\n\n").Append(summary).Append("\n\n");
            sb.Append("## Naechster Schritt\n\n");
            sb.Append("Entweder die Arbeit wirklich ins Zielrepo pushen und die Completion mit dem ")
              .Append("tatsaechlichen SHA erneut melden, oder die Karte bewusst neu schneiden. ")
              .Append("Ein Stempel ohne Repo-Nachweis ist ausgeschlossen (AGT-2220).\n");

            File.WriteAllText(
                Path.Combine(resultsDir, "unverified-delivery.md"), sb.ToString(), Encoding.UTF8);

            var status = new StringBuilder();
            status.Append("# Status\n\n");
            status.Append("- Result: Unverified delivery - completion stamp refused (")
                  .Append(source).Append(")\n\n");
            status.Append(reason).Append("\n\n");
            status.Append("- Details in `results/unverified-delivery.md`.\n");
            File.WriteAllText(Path.Combine(folderPath, "status.md"), status.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "external-completion: failed to write unverified-delivery report for {Folder}", folderPath);
        }
    }

    private static string ExternalEscalationCategory(IReadOnlyList<string>? gateItems)
    {
        if ((gateItems ?? []).Any(item =>
                item.TrimStart().StartsWith(
                    HumanReviewEscalationCategories.WorktreeBlocked + ":",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return HumanReviewEscalationCategories.WorktreeBlocked;
        }

        return HumanReviewEscalationCategories.ExternalCompletionBlocked;
    }

    private void WriteGateItems(string folderPath, IReadOnlyList<string>? gateItems)
    {
        var items = (gateItems ?? Array.Empty<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Replace('\r', ' ').Replace('\n', ' ').Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0) return;

        var path = Path.Combine(folderPath, "orchestrator-follow-up.md");
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var sb = new StringBuilder(existing);
            if (sb.Length == 0)
                sb.Append("# Orchestrator follow-up\n\n");
            else if (!existing.EndsWith("\n\n", StringComparison.Ordinal))
                sb.Append(existing.EndsWith('\n') ? "\n" : "\n\n");

            foreach (var item in items)
            {
                var row = $"- [ ] {item}";
                if (!existing.Contains(row, StringComparison.Ordinal))
                    sb.Append(row).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "external-completion: failed to write gate items for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Writes <c>results/deliverables.md</c>: what was delivered and where, by
    /// whom / which channel. This is the canonical narrative the timeline entry
    /// points at.
    /// </summary>
    private void WriteDeliverables(
        string folderPath,
        string summary,
        string source,
        DateTime now,
        IReadOnlyList<ExternalDeliverable>? deliverables,
        string verificationNote)
    {
        try
        {
            var resultsDir = TaskPaths.ResultsDir(folderPath);
            Directory.CreateDirectory(resultsDir);

            var sb = new StringBuilder();
            sb.Append("# Deliverables\n\n");
            sb.Append("Executed out-of-band on ")
              .Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" by ").Append(source).Append(".\n\n");
            sb.Append(summary).Append("\n\n");

            var items = (deliverables ?? Array.Empty<ExternalDeliverable>())
                .Where(d => d != null && (!string.IsNullOrWhiteSpace(d.Path) || !string.IsNullOrWhiteSpace(d.Url)))
                .ToList();
            if (items.Count > 0)
            {
                sb.Append("## What was delivered\n\n");
                foreach (var d in items)
                {
                    var hasPath = !string.IsNullOrWhiteSpace(d.Path);
                    var target = hasPath ? d.Path!.Trim() : d.Url!.Trim();
                    if (hasPath)
                        sb.Append("- `").Append(target).Append('`');
                    else
                        sb.Append("- [").Append(target).Append("](").Append(target).Append(')');
                    if (!string.IsNullOrWhiteSpace(d.Note))
                        sb.Append(" - ").Append(d.Note!.Trim());
                    sb.Append('\n');
                }
                sb.Append('\n');
            }

            sb.Append("## Provenance\n\n");
            sb.Append("- Completed externally by ").Append(source).Append('\n');
            sb.Append("- Recorded at ")
              .Append(now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
              .Append('\n');
            // AGT-2220: every stamp names how it was proven, not just that it happened.
            sb.Append("- ").Append(verificationNote).Append('\n');
            sb.Append("- This reconciliation was written by the external-completion endpoint; ")
              .Append("files under `results/` that predate it are the dead run's drafts.\n");

            File.WriteAllText(Path.Combine(resultsDir, "deliverables.md"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "external-completion: failed to write deliverables.md for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Replaces the stale <c>status.md</c> with a result summary that states
    /// explicitly the task was executed out-of-band plus the date. Unlike the
    /// escalation stub, this deliberately overwrites any existing text: the
    /// whole point of the endpoint is to retire the "escalated / no summary"
    /// corpse.
    /// </summary>
    private void WriteStatus(
        string folderPath, string summary, string source, DateTime now, string verificationNote)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("# Status\n\n");
            sb.Append("- Result: Completed out-of-band (").Append(source).Append(")\n\n");
            sb.Append(summary).Append("\n\n");
            sb.Append("Executed out-of-band on ")
              .Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" by ").Append(source).Append(".\n\n");
            sb.Append("- ").Append(verificationNote).Append('\n');
            sb.Append("- See `results/deliverables.md` for what was delivered and where.\n");
            File.WriteAllText(Path.Combine(folderPath, "status.md"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "external-completion: failed to write status.md for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Terminalizes <c>lifecycle.json</c>: sets the phase to
    /// <see cref="LifecyclePhases.AwaitingReview"/> and flips every still-running
    /// Post Processing check to <c>failed</c> with a note, so a card left stuck in
    /// <c>post-processing-running</c> stops spamming the scanner warning that
    /// motivated this fix. Rewrites the sidecar even when none exists so the
    /// terminal state is explicit on disk.
    /// </summary>
    private void TerminalizeLifecycle(string folderPath, string targetState, string source, DateTime now)
    {
        try
        {
            var snapshot = ReadLifecycleSnapshot(folderPath) ?? new LifecycleSnapshot();
            var note = $"Superseded by out-of-band completion ({source}).";

            var updated = snapshot with
            {
                Phase = LifecyclePhases.AwaitingReview,
                PhaseEnteredAt = now,
                BlockingReason = null,
                IntakeChecks = TerminalizeChecks(snapshot.IntakeChecks, now, note),
                PostProcessingChecks = TerminalizePostProcessingChecks(snapshot.PostProcessingChecks, now, note),
            };
            File.WriteAllText(
                Path.Combine(folderPath, "lifecycle.json"),
                JsonSerializer.Serialize(updated, LifecycleJsonWriteOpts),
                Encoding.UTF8);

            // Clear the mirrored task.json phase: the target lane
            // (5-human-review by default) carries no phase, so leaving a stale
            // running phase on the card would contradict the terminalized
            // sidecar. The lane move re-clears incompatible phases too; this
            // makes the terminal state correct even when target == source lane.
            _mutations.SetJobPhase(folderPath, LifecyclePhases.IsAllowed(targetState, LifecyclePhases.AwaitingReview)
                ? LifecyclePhases.AwaitingReview
                : "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "external-completion: failed to terminalize lifecycle.json for {Folder}", folderPath);
        }
    }

    private static List<LifecycleCheck> TerminalizeChecks(List<LifecycleCheck> checks, DateTime now, string note)
        => checks
            .Select(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Status, "pending", StringComparison.OrdinalIgnoreCase)
                ? c with { Status = "skipped", FinishedAt = now, Detail = note }
                : c)
            .ToList();

    private static List<LifecycleCheck> TerminalizePostProcessingChecks(
        List<LifecycleCheck> checks,
        DateTime now,
        string note)
        => checks
            .Select(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Status, "pending", StringComparison.OrdinalIgnoreCase)
                ? c with { Status = "failed", StartedAt = c.StartedAt ?? now, FinishedAt = now, Detail = note }
                : c)
            .ToList();

    private static LifecycleSnapshot? ReadLifecycleSnapshot(string folderPath)
    {
        var path = Path.Combine(folderPath, "lifecycle.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LifecycleSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
