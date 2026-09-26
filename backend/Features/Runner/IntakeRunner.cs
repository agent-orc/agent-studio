using System.Text.Json;
using System.Text;

namespace AgentStudio.Runner;

/// <summary>
/// Typed outcome produced by <see cref="IntakeRunner"/>. Maps 1:1 to the
/// outcomes listed in the <c>ready-orchestrator-intake-lane</c> task spec.
/// The runner uses these values to decide which lifecycle phase the card
/// lands in, and the chat / bus emit a typed message of the same shape.
/// </summary>
public enum IntakeOutcome
{
    /// <summary>Card is good to go; the coding runner may pick it up.</summary>
    Pass,
    /// <summary>Prompt is too thin to execute safely; the user has to clarify.</summary>
    NeedsClarification,
    /// <summary>A near-duplicate already exists in 2-ready or recent 5-human-review / 6-completed.</summary>
    DuplicateCandidate,
    /// <summary>Prompt mixes several independent units of work; should be split first.</summary>
    NeedsSplit,
    /// <summary>Hard block (e.g. requests out-of-scope behavior). User must resolve.</summary>
    Blocked,
    /// <summary>
    /// Done-precheck: the prompt itself states the work is already implemented /
    /// merged / shipped, so executing it would be a no-op or duplicate effort.
    /// Surfaced for a human to confirm-and-complete rather than run; the pickup
    /// gate keeps the coding runner off the card (same as any non-Pass verdict).
    /// </summary>
    AlreadyDone,
    /// <summary>
    /// Consistency-check: the card's own metadata is incoherent or
    /// self-contradictory — an empty goal/title, a reference pointing at itself,
    /// or a card that declares it is <c>blockedBy</c> something while sitting in
    /// the 2-ready pickup queue. Surfaced for a human to fix before the coding
    /// runner is allowed near it.
    /// </summary>
    Inconsistent
}

/// <summary>One verdict produced by an intake check.</summary>
public sealed record IntakeVerdict
{
    public required IntakeOutcome Outcome { get; init; }
    /// <summary>Short human-readable reason. Travels into the chat note and the lifecycle sidecar.</summary>
    public required string Reason { get; init; }
    /// <summary>Optional structured detail (e.g. duplicate-of slug, missing fields list).</summary>
    public IReadOnlyList<string>? Details { get; init; }
}

/// <summary>
/// Deterministic, in-process intake check for the orchestrator-intake lane.
///
/// <para>
/// V1 is intentionally a small set of rules: a hard out-of-scope keyword
/// check (blocked), a done-precheck that catches prompts which declare the
/// work already finished (already-done), fuzzy title match against existing
/// 2-ready / recent human-review cards (duplicate), prompt length probe
/// (clarity), and a heading-based split heuristic (needs-split). Anything
/// that passes them all is promoted with <see cref="IntakeOutcome.Pass"/>.
/// The shape is the part
/// that matters: a future iteration can swap the heuristic for a model
/// call without changing the lifecycle plumbing or the public outcome
/// contract that the runner / UI / tests are pinned to.
/// </para>
///
/// <para>
/// The intake CLI is not the task execution CLI. The bus participant id
/// uses the <c>intake:&lt;project&gt;</c> shape (see
/// <see cref="ParticipantIntakeFor"/>) so the activity log and timeline
/// render intake as its own actor. The chat log line goes on the
/// <c>[orchestrator]</c> stream with the <c>[intake]</c> tag so existing
/// activity-log parsers pick it up without changes.
/// </para>
/// </summary>
public sealed class IntakeRunner
{
    public const string IntakeParticipantPrefix = "intake:";
    public const string EnrichedContextRelativePath = "intake/enriched-context.md";
    public const int MaxEnrichmentContextCharacters = 6_000;
    public const int MaxEnrichmentEstimatedTokens = 1_500;
    public const int MaxOptionalEnrichmentBlocks = 2;
    private const int MaxDetailedOmissions = 16;
    public const string EnrichmentSelector = "constraint-selector-v4-token-economy";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly OrchestratorChatLog _chatLog;
    private readonly AgentMessageBusBridge? _bus;
    private readonly ILogger<IntakeRunner> _logger;
    private readonly TimeProvider _time;
    private readonly ProjectStyleGuideService? _styleGuides;

    public IntakeRunner(
        TaskScannerService scanner,
        TaskMutationService mutations,
        OrchestratorChatLog chatLog,
        ILogger<IntakeRunner> logger,
        AgentMessageBusBridge? bus = null,
        TimeProvider? time = null,
        ProjectStyleGuideService? styleGuides = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _chatLog = chatLog;
        _bus = bus;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _styleGuides = styleGuides;
    }

    public static string ParticipantIntakeFor(string project) => $"{IntakeParticipantPrefix}{project}";

    /// <summary>
    /// Select project-owned constraints that should be foregrounded before the
    /// coding CLI sees the prompt. Production callers pass the resolved
    /// repository root; a null root is retained only for pure selector tests.
    /// </summary>
    public static IntakeEnrichmentManifest BuildEnrichmentManifest(
        TaskInfo target,
        string? promptMarkdown,
        IReadOnlyList<ProjectStyleGuide>? applicableGuides = null,
        string? styleGuideSnapshotId = null,
        string? repositoryRoot = null,
        bool agentStudioCatalogue = false,
        IReadOnlyCollection<string>? explicitlyDeclaredBlockIds = null)
    {
        var areas = DetectTaskAreas(target, promptMarkdown);
        var areaSet = new HashSet<string>(areas, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<IntakeConstraintSelection>();

        foreach (var rule in ConstraintRules.Where(rule => rule.Constraint.Mandatory))
        {
            if (rule.Applies(areaSet))
                candidates.Add(CloneConstraint(rule.Constraint));
        }

        if (string.Equals(TaskModes.Normalize(target.Mode), TaskModes.Coding, StringComparison.Ordinal)
            && applicableGuides != null)
        {
            foreach (var guide in applicableGuides
                         .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                         .ThenBy(candidate => candidate.RelPath, StringComparer.Ordinal))
            {
                var areaWildcard = guide.AppliesTo.TaskAreas.Contains("*", StringComparer.OrdinalIgnoreCase);
                var matchedAreas = areaWildcard
                    ? (areas.Count > 0 ? areas.ToList() : ["general"])
                    : guide.AppliesTo.TaskAreas.Intersect(areaSet, StringComparer.OrdinalIgnoreCase).ToList();
                if (matchedAreas.Count == 0)
                    continue;
                candidates.Add(new IntakeConstraintSelection
                {
                    Id = $"style-guide:{guide.Id}",
                    Title = guide.Title,
                    Source = $"docs/{guide.RelPath}",
                    Revision = guide.Version,
                    Tier = "optional",
                    Priority = 20,
                    Areas = matchedAreas,
                    Text = $"Style-guide version {guide.Version}; matched task area(s): {string.Join(", ", matchedAreas)}. {guide.PromptSummary}"
                });
            }
        }

        foreach (var rule in ConstraintRules.Where(rule => !rule.Constraint.Mandatory))
        {
            if (rule.Applies(areaSet))
                candidates.Add(CloneConstraint(rule.Constraint));
        }

        if (repositoryRoot is null)
            return ApplyEnrichmentBudget(areas, candidates, styleGuideSnapshotId);

        // The built-in rule catalogue belongs to Agent Studio. Other projects
        // may use their own repository guides and root instructions only.
        if (!agentStudioCatalogue)
        {
            var rootInstructions = candidates.First(rule => rule.Id == "repo-instructions-source");
            var hasIndex = !string.IsNullOrWhiteSpace(repositoryRoot)
                           && MissingSource("docs/start/README.md", repositoryRoot) is null;
            candidates.Remove(rootInstructions);
            candidates.Add(rootInstructions with
            {
                Title = hasIndex ? rootInstructions.Title : "Use repository instructions",
                Source = hasIndex ? "AGENTS.md; docs/start/README.md" : "AGENTS.md",
                Text = hasIndex
                    ? "Follow the active AGENTS.md rules for this repository. When project documentation is needed, start at docs/start/README.md."
                    : "Follow the active AGENTS.md rules for this repository."
            });
        }

        var eligible = new List<IntakeConstraintSelection>();
        var rejected = new List<IntakeConstraintOmission>();
        foreach (var candidate in candidates)
        {
            var missing = MissingSource(candidate.Source, repositoryRoot);
            var studioRule = !candidate.Id.StartsWith("style-guide:", StringComparison.Ordinal)
                             && candidate.Id != "repo-instructions-source";
            var explicitlyDeclared = explicitlyDeclaredBlockIds?.Contains(candidate.Id, StringComparer.Ordinal) == true;
            if (missing is null && (!studioRule || agentStudioCatalogue || explicitlyDeclared))
            {
                eligible.Add(explicitlyDeclared
                    ? candidate with { SourceVerification = "pipeline-explicit" }
                    : candidate);
                continue;
            }
            rejected.Add(new IntakeConstraintOmission
            {
                Id = candidate.Id,
                Title = candidate.Title,
                Source = candidate.Source,
                Reason = missing is null ? "project-catalogue-mismatch" : "source-missing",
                MissingPath = missing,
                EstimatedCharacters = RenderConstraintMarkdown(candidate).Length,
                EstimatedTokens = EstimateTokens(RenderConstraintMarkdown(candidate).Length)
            });
        }
        return ApplyEnrichmentBudget(areas, eligible, styleGuideSnapshotId, rejected)
            with { SourceRejections = rejected };
    }

    private static string? MissingSource(string source, string repositoryRoot)
    {
        foreach (var citation in source.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var relative = citation.Split('#', 2)[0].Trim().Replace('\\', '/');
            if (relative == "existing C# project conventions")
                continue;
            if (string.IsNullOrWhiteSpace(repositoryRoot) || Path.IsPathRooted(relative)
                || relative.Split('/').Any(part => part is "." or ".."))
                return relative;
            var path = Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return relative;
            var current = repositoryRoot;
            foreach (var part in relative.Split('/'))
            {
                current = Path.Combine(current, part);
                try
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return relative;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return relative;
                }
            }
        }
        return null;
    }

    private static IntakeEnrichmentManifest ApplyEnrichmentBudget(
        IReadOnlyList<string> areas,
        IReadOnlyList<IntakeConstraintSelection> candidates,
        string? styleGuideSnapshotId,
        IReadOnlyList<IntakeConstraintOmission>? rejectedSources = null)
    {
        var ordered = candidates
            .OrderBy(candidate => candidate.Mandatory ? 0 : 1)
            .ThenBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();
        var selected = ordered
            .Where(candidate => candidate.Mandatory)
            .Concat(ordered
                .Where(candidate => !candidate.Mandatory)
                .Take(MaxOptionalEnrichmentBlocks))
            .ToList();
        var omitted = rejectedSources?.ToList() ?? [];
        foreach (var candidate in ordered.Where(candidate =>
                     !candidate.Mandatory && !selected.Contains(candidate)))
        {
            var estimatedCharacters = RenderConstraintMarkdown(candidate).Length;
            omitted.Add(new IntakeConstraintOmission
            {
                Id = candidate.Id,
                Title = candidate.Title,
                Source = candidate.Source,
                Reason = "optional-block-limit",
                EstimatedCharacters = estimatedCharacters,
                EstimatedTokens = EstimateTokens(estimatedCharacters)
            });
        }
        var detailLimit = MaxDetailedOmissions;

        while (true)
        {
            var detailed = omitted.Take(detailLimit).ToList();
            var manifest = new IntakeEnrichmentManifest
            {
                ArtifactPath = EnrichedContextRelativePath,
                Selector = EnrichmentSelector,
                Areas = areas.ToList(),
                StyleGuideSnapshotId = styleGuideSnapshotId,
                Constraints = selected.ToList(),
                CharacterBudget = MaxEnrichmentContextCharacters,
                EstimatedTokenBudget = MaxEnrichmentEstimatedTokens,
                OptionalBlockLimit = MaxOptionalEnrichmentBlocks,
                Omissions = detailed,
                AdditionalOmissionCount = omitted.Count - detailed.Count
            };
            var rendered = RenderEnrichedContextMarkdown(manifest);
            if (rendered.Length <= MaxEnrichmentContextCharacters)
            {
                return manifest with
                {
                    UsedCharacters = rendered.Length,
                    EstimatedTokens = EstimateTokens(rendered.Length)
                };
            }

            if (selected.Count > 0)
            {
                var removed = selected[^1];
                selected.RemoveAt(selected.Count - 1);
                omitted.Insert(0, new IntakeConstraintOmission
                {
                    Id = removed.Id,
                    Title = removed.Title,
                    Source = removed.Source,
                    Reason = "context-character-budget",
                    EstimatedCharacters = RenderConstraintMarkdown(removed).Length,
                    EstimatedTokens = EstimateTokens(RenderConstraintMarkdown(removed).Length)
                });
                continue;
            }

            if (detailLimit > 0)
            {
                detailLimit--;
                continue;
            }

            throw new InvalidOperationException("The fixed intake-enrichment header exceeds its hard context budget.");
        }
    }

    public static IReadOnlyList<string> DetectTaskAreas(TaskInfo target, string? promptMarkdown)
    {
        var haystack = string.Join('\n',
            target.Title ?? string.Empty,
            target.TaskType ?? string.Empty,
            string.Join(" ", target.Tags ?? Enumerable.Empty<string>()),
            promptMarkdown ?? string.Empty);

        var areas = new List<string>();
        foreach (var (area, needles) in AreaNeedles)
        {
            if (needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase)))
                areas.Add(area);
        }
        return areas;
    }

    public static string RenderEnrichedContextMarkdown(IntakeEnrichmentManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Prompt enrichment");
        sb.AppendLine();
        sb.AppendLine("The task server appended these labelled project constraints before CLI spawn. They supplement the original prompt, which remains unchanged.");
        sb.AppendLine();
        sb.AppendLine($"- Selector: `{(string.IsNullOrWhiteSpace(manifest.Selector) ? EnrichmentSelector : manifest.Selector)}`");
        sb.AppendLine($"- Detected areas: `{(manifest.Areas.Count == 0 ? "general" : string.Join("`, `", manifest.Areas))}`");
        sb.AppendLine("- Audit artifact: `enrichment-report.json`");
        if (!string.IsNullOrWhiteSpace(manifest.StyleGuideSnapshotId))
            sb.AppendLine($"- Style-guide snapshot: `{manifest.StyleGuideSnapshotId}`");
        sb.AppendLine($"- Hard budget: `{(manifest.CharacterBudget > 0 ? manifest.CharacterBudget : MaxEnrichmentContextCharacters)} characters` / `~{(manifest.EstimatedTokenBudget > 0 ? manifest.EstimatedTokenBudget : MaxEnrichmentEstimatedTokens)} tokens` / `{(manifest.OptionalBlockLimit > 0 ? manifest.OptionalBlockLimit : MaxOptionalEnrichmentBlocks)} optional blocks`");
        sb.AppendLine();

        if (manifest.Constraints.Count == 0)
        {
            sb.AppendLine("No task-specific constraints were selected.");
        }
        else
        {
            sb.AppendLine("### Injected constraints");
            sb.AppendLine();
            foreach (var constraint in manifest.Constraints)
                sb.Append(RenderConstraintMarkdown(constraint));
        }

        if (manifest.Omissions.Count > 0 || manifest.AdditionalOmissionCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Omitted relevant constraints");
            sb.AppendLine();
            sb.AppendLine("These constraints matched the task but were not injected because their sources were unavailable, their catalogue did not belong to this project, or the context budget was exhausted:");
            foreach (var omission in manifest.Omissions)
            {
                sb.AppendLine($"- `{omission.Id}`: {omission.Reason}{(omission.MissingPath is null ? "" : $" ({omission.MissingPath})")} (~{omission.EstimatedCharacters} characters)");
            }
            if (manifest.AdditionalOmissionCount > 0)
                sb.AppendLine($"- `{manifest.AdditionalOmissionCount}` additional omission(s) are summarized by count.");
        }

        return sb.ToString().TrimEnd();
    }

    public static string RenderConstraintMarkdown(IntakeConstraintSelection constraint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- **{constraint.Title}** (`{constraint.Id}`)");
        sb.AppendLine($"  Source: `{constraint.Source}`");
        sb.AppendLine($"  {constraint.Text}");
        return sb.ToString();
    }

    public static int EstimateTokens(int characters) => Math.Max(0, (characters + 3) / 4);

    /// <summary>
    /// Pure-function evaluation surface. Runs every check in order and
    /// returns the first non-Pass verdict; if every check passes, returns a
    /// Pass verdict. Pure so unit tests can pin the outcome matrix without
    /// touching disk or services.
    /// </summary>
    public static IntakeVerdict Evaluate(TaskInfo target, string? promptMarkdown, IReadOnlyList<TaskInfo> existingPeers)
    {
        var prompt = promptMarkdown ?? string.Empty;

        var blocked = CheckBlocked(prompt);
        if (blocked != null) return blocked;

        // Done-precheck runs before the thinner heuristics: a card that the
        // prompt itself declares finished should be surfaced as already-done
        // even when its prompt is short (clarity) or echoes a peer (duplicate).
        var done = CheckAlreadyDone(prompt);
        if (done != null) return done;

        // Consistency-check: a card whose own metadata is incoherent (empty
        // goal, self-reference, blocked-while-ready) is surfaced before the
        // duplicate / clarity / split heuristics — those assume a coherent card.
        var inconsistent = CheckConsistency(target);
        if (inconsistent != null) return inconsistent;

        var dup = CheckDuplicate(target, existingPeers);
        if (dup != null) return dup;

        var clarity = CheckClarity(target, prompt);
        if (clarity != null) return clarity;

        var split = CheckSplit(prompt);
        if (split != null) return split;

        return new IntakeVerdict
        {
            Outcome = IntakeOutcome.Pass,
            Reason = "Prompt looks executable: scope is bounded, no duplicates detected, no out-of-scope language."
        };
    }

    /// <summary>
    /// Context-load (prompt requirement 4): determine and record the context a
    /// card carries. Resolves its cross-references against the known-task set and
    /// its prompt attachments against the files on disk, splitting each into
    /// resolved vs. missing, and captures its tags. Pure so the resolution rules
    /// are unit-testable; <see cref="RunForJob"/> supplies the disk-derived
    /// inputs (peer scan + attachment file list). The manifest is recorded in the
    /// <c>lifecycle.json</c> sidecar — informational, it does not gate pickup.
    /// </summary>
    public static ContextManifest BuildContextManifest(
        TaskInfo target,
        string? promptMarkdown,
        IReadOnlyList<TaskInfo> knownTasks,
        IReadOnlyCollection<string> attachmentFilesOnDisk)
    {
        var manifest = new ContextManifest
        {
            Tags = target.Tags is { Count: > 0 } ? new List<string>(target.Tags) : []
        };

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in knownTasks)
        {
            if (!string.IsNullOrWhiteSpace(t.TaskKey)) known.Add(t.TaskKey.Trim());
            if (!string.IsNullOrWhiteSpace(t.Id)) known.Add(t.Id.Trim());
        }

        foreach (var (kind, refTarget) in (target.References ?? new TaskReferences()).Enumerate())
        {
            var edge = $"{kind}:{refTarget}";
            if (known.Contains(refTarget.Trim())) manifest.ResolvedReferences.Add(edge);
            else manifest.MissingReferences.Add(edge);
        }

        var onDisk = new HashSet<string>(attachmentFilesOnDisk, StringComparer.OrdinalIgnoreCase);
        foreach (var att in ExtractAttachmentPaths(promptMarkdown))
        {
            var fileName = att.Length > AttachmentPrefix.Length ? att[AttachmentPrefix.Length..] : att;
            if (onDisk.Contains(att) || onDisk.Contains(fileName)) manifest.ResolvedAttachments.Add(att);
            else manifest.MissingAttachments.Add(att);
        }

        return manifest;
    }

    private const string AttachmentPrefix = "attachments/";

    private static readonly System.Text.RegularExpressions.Regex AttachmentRef =
        new(@"attachments/[A-Za-z0-9._\-/]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Pull "attachments/<path>" tokens out of the prompt (bare or inside a
    // markdown link), de-duplicated and stripped of trailing markdown
    // punctuation. Conservative on purpose: only the explicit attachments/
    // convention counts, so prose that merely says "attachments" is ignored.
    private static IEnumerable<string> ExtractAttachmentPaths(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in AttachmentRef.Matches(prompt))
        {
            var path = m.Value.TrimEnd('.', ')', ']', ',', '!', '?');
            if (path.Length > AttachmentPrefix.Length && seen.Add(path)) yield return path;
        }
    }

    private static IReadOnlyCollection<string> ReadAttachmentFileNames(string folderPath)
    {
        try
        {
            var dir = Path.Combine(folderPath, "attachments");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Run intake on a job. Reads the job from disk, evaluates, writes the
    /// resulting phase + lifecycle sidecar, emits chat + bus messages. Idempotent:
    /// re-running on the same job re-evaluates and overwrites the phase.
    /// </summary>
    public IntakeVerdict RunForJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            throw new InvalidOperationException($"Job '{jobId}' not found");
        if (!string.Equals(info.State, TaskStates.Ready, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Intake only runs on jobs in '{TaskStates.Ready}'; '{jobId}' is in '{info.State}'");

        var promptPath = Path.Combine(info.FolderPath, "prompt.md");
        var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;

        var peers = _scanner.ScanAllAutomationJobs()
            .Where(j => string.Equals(j.WatchPath, info.WatchPath, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(j.Id, info.Id, StringComparison.Ordinal)
                        && (j.State == TaskStates.Ready
                            || j.State == TaskStates.HumanReview
                            || j.State == TaskStates.Completed))
            .ToList();

        // Context-load: resolve references + prompt attachments against what is
        // actually available, so the verdict and sidecar carry the card's context.
        var context = BuildContextManifest(info, prompt, peers, ReadAttachmentFileNames(info.FolderPath));
        var styleGuideCatalogue = _styleGuides?.GetCatalogue(info.ProjectName);
        var enrichment = BuildEnrichmentManifest(
            info,
            prompt,
            styleGuideCatalogue?.Guides,
            styleGuideCatalogue?.Guides.Count > 0 ? styleGuideCatalogue.SnapshotId : null,
            _styleGuides?.GetRepositoryRoot(info.ProjectName) ?? string.Empty,
            string.Equals(styleGuideCatalogue?.ProjectKey, "PROJ-002", StringComparison.OrdinalIgnoreCase));
        WriteEnrichedContextArtifact(info, enrichment);
        // Surface the context-load result in the run log: the manifest already
        // lands in lifecycle.json, but a structured line lets operators watching
        // the Preparation step see at a glance what context a card is missing
        // without opening the sidecar.
        _logger.LogInformation(
            "Intake context-load for {JobId}: references {ResolvedRefs} resolved / {MissingRefs} missing, attachments {ResolvedAttachments} resolved / {MissingAttachments} missing, {TagCount} tag(s), complete={ContextComplete}",
            info.Id,
            context.ResolvedReferences.Count, context.MissingReferences.Count,
            context.ResolvedAttachments.Count, context.MissingAttachments.Count,
            context.Tags.Count, context.IsComplete);
        _logger.LogInformation(
            "Intake enrichment for {JobId}: selected {ConstraintCount} constraint(s) for areas [{Areas}] into {ArtifactPath}",
            info.Id,
            enrichment.Constraints.Count,
            string.Join(", ", enrichment.Areas),
            enrichment.ArtifactPath);

        // Stamp intake-running first so observers see the in-flight state. A
        // restart between this stamp and the verdict write leaves the card in
        // intake-running, which the next tick re-runs and resolves.
        WritePhase(info, LifecyclePhases.IntakeRunning);
        EmitChat(info, "running", $"Intake check started by {ParticipantIntakeFor(info.ProjectName)}.");

        var verdict = Evaluate(info, prompt, peers);
        ApplyVerdict(info, verdict, context, enrichment);
        return verdict;
    }

    /// <summary>Apply a precomputed verdict to a job (used by tests and by RunForJob).</summary>
    public void ApplyVerdict(
        TaskInfo info,
        IntakeVerdict verdict,
        ContextManifest? context = null,
        IntakeEnrichmentManifest? enrichment = null)
    {
        var phase = verdict.Outcome == IntakeOutcome.Pass
            ? LifecyclePhases.IntakePassed
            : LifecyclePhases.IntakeBlocked;
        WritePhase(info, phase);
        WriteLifecycleSidecar(info, verdict, phase, context, enrichment);
        var tag = verdict.Outcome.ToString().ToLowerInvariant();
        EmitChat(info, tag, $"Intake {tag}: {verdict.Reason}{BuildEnrichmentChatSuffix(enrichment)}");
        // Job-lifecycle bus event: the substate transition is meaningful
        // independent of the chat note (typed timeline drill-down).
        if (_bus != null)
        {
            try
            {
                _ = _bus.EmitJobLifecycleAsync(info,
                    topic: $"intake-{tag}",
                    fromState: LifecyclePhases.IntakeRunning,
                    toState: phase,
                    reason: verdict.Reason);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus emit for intake verdict failed for {JobId}", info.Id); }
        }
        _logger.LogInformation("Intake on {JobId}: {Outcome} ({Reason})", info.Id, verdict.Outcome, verdict.Reason);
    }

    private void WritePhase(TaskInfo info, string phase)
    {
        _mutations.SetJobPhase(info.FolderPath, phase);
    }

    private void WriteLifecycleSidecar(
        TaskInfo info,
        IntakeVerdict verdict,
        string phase,
        ContextManifest? context,
        IntakeEnrichmentManifest? enrichment)
    {
        try
        {
            var path = Path.Combine(info.FolderPath, "lifecycle.json");
            var now = _time.GetUtcNow().UtcDateTime;
            var snapshot = new LifecycleSnapshot
            {
                Phase = phase,
                PhaseEnteredAt = now,
                BlockingReason = verdict.Outcome == IntakeOutcome.Pass ? null : verdict.Reason,
                Context = context,
                Enrichment = enrichment,
                IntakeChecks =
                [
                    new LifecycleCheck
                    {
                        Name = "intake-v1",
                        Status = verdict.Outcome == IntakeOutcome.Pass ? "passed" : "failed",
                        StartedAt = now,
                        FinishedAt = now,
                        Detail = verdict.Reason
                    }
                ]
            };
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write lifecycle.json for {JobId}", info.Id);
        }
    }

    private void WriteEnrichedContextArtifact(TaskInfo info, IntakeEnrichmentManifest enrichment)
    {
        try
        {
            var path = Path.Combine(info.FolderPath, EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, RenderEnrichedContextMarkdown(enrichment));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write intake enrichment artifact for {JobId}", info.Id);
        }
    }

    private static string BuildEnrichmentChatSuffix(IntakeEnrichmentManifest? enrichment)
    {
        if (enrichment == null) return "";
        if (enrichment.Constraints.Count == 0)
            return $" No additional constraints selected; audit artifact: {enrichment.ArtifactPath}.";

        var ids = string.Join(", ", enrichment.Constraints.Select(c => c.Id));
        return $" Injected {enrichment.Constraints.Count} constraint(s) into {enrichment.ArtifactPath}: {ids}.";
    }

    private void EmitChat(TaskInfo info, string tag, string body)
    {
        try
        {
            // Tag every intake line with [intake] so the activity-log parser
            // can render intake as its own actor without parsing the prose.
            _chatLog.Append(info, OrchestratorMessageKind.Decision, $"[intake:{tag}] {body}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intake chat note write failed for {JobId}", info.Id);
        }
    }

    // ---- Heuristic checks ------------------------------------------------

    private sealed record ConstraintRule(
        IntakeConstraintSelection Constraint,
        Func<IReadOnlySet<string>, bool> Applies);

    private static readonly (string Area, string[] Needles)[] AreaNeedles =
    [
        ("backend", ["backend", ".cs", "c#", ".net", "dotnet", "api", "endpoint", "service", "controller"]),
        ("runner", ["runner", "pickup", "cli", "agent", "orchestrator", "intake", "pre-step", "pipeline", "watchdog", "sentinel"]),
        ("frontend", ["frontend", "angular", ".ts", ".scss", "css", "style", "styling", "component", "ui", "design", "token", "layout", "button", "lane", "card", "visual"]),
        ("git", ["git", "commit", "push", "merge", "branch", "worktree", "workspace artifact", "auto-commit", "pull request"]),
        ("filesystem", ["job folder", "task folder", "job.json", "task.json", "lane", "state", "move", "reorder", "archive", ".orchestrator", "workspace"]),
        ("refactor", ["refactor", "split", "extract", "rename", "namespace", "move file", "decompose"]),
        ("delegation", ["delegate", "delegation", "sub-agent", "subagent", "parallel agent", "spawn agent", "multi-agent"])
    ];

    private static readonly ConstraintRule[] ConstraintRules =
    [
        new(
            new IntakeConstraintSelection
            {
                Id = "repo-instructions-source",
                Title = "Use repository instructions and indexed docs",
                Source = "AGENTS.md; docs/start/README.md",
                Tier = "mandatory-project-policy",
                Mandatory = true,
                Priority = 0,
                Areas = ["general"],
                Text = "Follow the active AGENTS.md rules first. When project documentation is needed, start at docs/start/README.md instead of scanning docs/ blindly. Repository artifacts, prompts, comments, and docs written by this project stay in English."
            },
            _ => true),
        new(
            new IntakeConstraintSelection
            {
                Id = "git-handling-api-not-cli",
                Title = "Keep git/workspace artifact handling in the backend",
                Source = "AGENTS.md#stable-update-policy; docs/operations/git/commit-push-doctrine.md; docs/system/architecture/decisions/adr-archive.md#adr-0052",
                Priority = 40,
                Areas = ["git", "runner", "backend"],
                Text = "Git handling for workspace artifacts belongs in API/backend orchestration and platform-owned pre/post pipeline steps, not in the CLI/agent layer. Worker CLIs do not commit, push, merge, or manage task worktrees on their own."
            },
            areas => areas.Contains("git") || (areas.Contains("runner") && areas.Contains("backend"))),
        new(
            new IntakeConstraintSelection
            {
                Id = "task-state-api-first",
                Title = "Use API/state-machine boundaries for task state",
                Source = "AGENTS.md#task-organization-rule-api-first; docs/system/contracts/filesystem.md",
                Priority = 50,
                Areas = ["filesystem", "runner"],
                Text = "Task folders, lanes, pickup, stop, continue, and state transitions are application-owned. Code should route task mutations through the API/state-machine services instead of direct filesystem moves or job.json state edits."
            },
            areas => areas.Contains("filesystem") || areas.Contains("runner")),
        new(
            new IntakeConstraintSelection
            {
                Id = "frontend-design-tokens-components",
                Title = "Use central frontend design tokens and components",
                Source = "frontend/AGENTS.md#spacing-tokens-never-raw-px; docs/quality/design-principles.md",
                Priority = 30,
                Areas = ["frontend"],
                Text = "Frontend changes should use the central design-token scale and existing standard components. Avoid local hard-coded spacing, colors, badge geometry, or one-off UI primitives when shared tokens/components cover the case."
            },
            areas => areas.Contains("frontend")),
        new(
            new IntakeConstraintSelection
            {
                Id = "stable-namespaces-on-splits",
                Title = "Keep namespaces stable during file splits",
                Source = "AGENTS.md; existing C# project conventions",
                Priority = 60,
                Areas = ["refactor", "backend"],
                Text = "When splitting or extracting files, keep existing namespaces and public type identities stable unless the task explicitly calls for a coordinated namespace migration."
            },
            areas => areas.Contains("refactor")),
        new(
            new IntakeConstraintSelection
            {
                Id = "orchestrator-state-machine-authority",
                Title = "The orchestrator remains the state-machine authority",
                Source = "AGENTS.md#orchestration-philosophy-deterministic-over-prompt-based; docs/system/contracts/agent-task.md",
                Priority = 70,
                Areas = ["runner", "backend"],
                Text = "CLI output and sentinels are inputs, not authority. Runner and policy code own deterministic lifecycle decisions, escalation, retries, and lane movement."
            },
            areas => areas.Contains("runner")),
        new(
            new IntakeConstraintSelection
            {
                Id = "delegation-economy",
                Title = "Keep delegation bounded and evidence-driven",
                Source = "AGENTS.md#Product-Boundaries; docs/concepts/model-escalation-and-companion-routing.md",
                Priority = 10,
                Areas = ["delegation", "runner"],
                Text = "Delegate only concrete, independent subtasks with a useful concurrency benefit. Keep fan-out bounded, preserve one orchestrator as the lifecycle authority, and account for each delegated call instead of hiding nested work or duplicating context."
            },
            areas => areas.Contains("delegation"))
    ];

    public static bool IsBuiltInConstraintId(string id) =>
        ConstraintRules.Any(rule => string.Equals(rule.Constraint.Id, id, StringComparison.Ordinal));

    private static IntakeConstraintSelection CloneConstraint(IntakeConstraintSelection c)
        => c with { Areas = c.Areas.ToList() };

    private static IntakeVerdict? CheckBlocked(string prompt)
    {
        // ADR-0052 reversed the intra-project-parallelism non-goal: bounded
        // intra-project parallel execution via git worktrees + per-task branches
        // is now an opt-in, orchestrator-gated capability (configurable
        // maxParallelism per project), not a hard non-goal. The phrase blockers
        // that used to hard-stop such cards ("parallel coding", "worktree",
        // "create a new branch", "multiple agents at once") are therefore gone.
        // Re-add a blocker here only for a genuine future hard non-goal.
        _ = prompt;
        return null;
    }

    // Done-signal phrases (English + German). Deliberately strong completion
    // language only — "already exists" / "already in place" are excluded because
    // they routinely describe a *precondition* ("the endpoint already exists, so
    // wire the UI") rather than the task itself being finished.
    private static readonly string[] DoneSignals =
    [
        "already implemented", "already done", "already merged", "already shipped",
        "already landed", "already built", "already complete", "already completed",
        "has been implemented", "has been merged", "has been shipped",
        "was already implemented", "is already implemented",
        "bereits umgesetzt", "bereits erledigt", "bereits implementiert",
        "schon umgesetzt", "schon erledigt", "schon implementiert"
    ];

    // If any guard word shares the sentence with a done-signal, the signal is
    // almost certainly negated or an instruction to verify, not an assertion
    // that the work is finished. Conservative by design: a false positive here
    // would skip real work, so precision beats recall.
    private static readonly string[] DoneGuards =
    [
        "not ", "n't", "verify", "ensure", "make sure", "confirm", "check",
        "should", "unless", "if ", "but ", "however", "todo", "to do",
        "nicht", "noch nicht", "stelle sicher", "prüf", "pruef", "falls", "sofern"
    ];

    private static IntakeVerdict? CheckAlreadyDone(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;

        foreach (var rawSentence in prompt.Split(new[] { '.', '!', '?', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var sentence = rawSentence.ToLowerInvariant();
            var matched = Array.Find(DoneSignals, s => sentence.Contains(s, StringComparison.Ordinal));
            if (matched == null) continue;
            // A guard anywhere in the same sentence vetoes the signal.
            if (Array.Exists(DoneGuards, g => sentence.Contains(g, StringComparison.Ordinal))) continue;

            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.AlreadyDone,
                Reason = $"Prompt states the work is already done (\"{matched}\"); confirm and complete instead of running.",
                Details = [matched]
            };
        }
        return null;
    }

    // Whole-title placeholders that mean the goal was never actually written.
    // Matched against the entire trimmed title (case-insensitive), never as a
    // substring, so a genuine title that merely contains one of these words
    // ("Add a title bar") is not misread as a placeholder.
    private static readonly HashSet<string> PlaceholderTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "untitled", "title", "tbd", "todo", "new task", "task", "wip", "draft", "placeholder"
    };

    /// <summary>
    /// Consistency-check (prompt requirement 3): is the card coherent —
    /// goal / references / state contradiction-free? Deterministic and
    /// peer-independent so it never false-positives on an incomplete peer set:
    /// it only flags issues that are wrong regardless of which other tasks
    /// exist.
    /// <list type="bullet">
    ///   <item><b>Goal present</b>: a placeholder / empty title has no goal.</item>
    ///   <item><b>No self-reference</b>: a reference edge pointing at the card's
    ///   own key is a contradiction.</item>
    ///   <item><b>Not blocked-while-ready</b>: intake only runs on 2-ready cards,
    ///   so a non-empty <c>blockedBy</c> contradicts being queued for pickup.</item>
    /// </list>
    /// Prompt completeness is covered by <see cref="CheckClarity"/>; tag / context
    /// completeness is recorded by the context-load manifest
    /// (<see cref="BuildContextManifest"/>), so the four facets the prompt lists
    /// (goal / prompt / references / tags) are each accounted for.
    /// </summary>
    private static IntakeVerdict? CheckConsistency(TaskInfo target)
    {
        var title = (target.Title ?? string.Empty).Trim();
        if (PlaceholderTitles.Contains(title))
        {
            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.Inconsistent,
                Reason = "Card has no real title, so the goal is incomplete. Add a descriptive title before running.",
                Details = ["title"]
            };
        }

        var refs = target.References ?? new TaskReferences();

        var selfKey = TaskReferenceValidator.NormalizeKey(target.TaskKey);
        if (selfKey.Length > 0)
        {
            foreach (var (kind, refTarget) in refs.Enumerate())
            {
                if (string.Equals(TaskReferenceValidator.NormalizeKey(refTarget), selfKey, StringComparison.OrdinalIgnoreCase))
                {
                    return new IntakeVerdict
                    {
                        Outcome = IntakeOutcome.Inconsistent,
                        Reason = $"References are contradictory: the card references itself ({selfKey}) under '{kind}'. Remove the self-reference.",
                        Details = [kind, refTarget]
                    };
                }
            }
        }

        if (refs.BlockedBy.Count > 0)
        {
            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.Inconsistent,
                Reason = $"Card is queued in 2-ready but declares it is blockedBy [{string.Join(", ", refs.BlockedBy)}]; resolve or clear the block before it can run.",
                Details = refs.BlockedBy.ToArray()
            };
        }

        return null;
    }

    private static IntakeVerdict? CheckDuplicate(TaskInfo target, IReadOnlyList<TaskInfo> existing)
    {
        // Duplicate detection: titles that share a significant prefix or
        // overlap by Jaccard similarity over normalised tokens. Conservative
        // thresholds; intake is advisory in V1, so a few false negatives are
        // preferable to false positives that block real work.
        var targetTokens = TitleTokens(target.Title);
        if (targetTokens.Count == 0) return null;

        foreach (var peer in existing)
        {
            var peerTokens = TitleTokens(peer.Title);
            if (peerTokens.Count == 0) continue;

            var inter = targetTokens.Intersect(peerTokens).Count();
            var union = targetTokens.Union(peerTokens).Count();
            if (union == 0) continue;
            var jaccard = (double)inter / union;
            if (jaccard >= 0.75)
            {
                return new IntakeVerdict
                {
                    Outcome = IntakeOutcome.DuplicateCandidate,
                    Reason = $"Title is a near-duplicate of '{peer.Id}' ({peer.State}); confirm before running.",
                    Details = [peer.Id, peer.State]
                };
            }
        }
        return null;
    }

    private static IntakeVerdict? CheckClarity(TaskInfo target, string prompt)
    {
        // Cheap clarity probe: a prompt with under 20 non-whitespace
        // characters and no sentence terminator is almost always too thin
        // to execute. The point is to surface the obvious cases, not to
        // grade prose.
        var trimmed = prompt.Trim();
        if (trimmed.Length < 20)
        {
            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.NeedsClarification,
                Reason = $"Prompt is very short ({trimmed.Length} chars). Add scope, acceptance, and any constraints.",
            };
        }
        return null;
    }

    private static IntakeVerdict? CheckSplit(string prompt)
    {
        // Heading-based split heuristic: a prompt with six or more level-2
        // markdown headings often combines independent tasks. Surface as
        // advisory; the user decides whether to split.
        var headingCount = 0;
        foreach (var line in prompt.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("## ", StringComparison.Ordinal) && !t.StartsWith("### ", StringComparison.Ordinal))
                headingCount++;
        }
        if (headingCount >= 6)
        {
            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.NeedsSplit,
                Reason = $"Prompt has {headingCount} level-2 sections; consider splitting into multiple tasks before running.",
            };
        }
        return null;
    }

    private static HashSet<string> TitleTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return new HashSet<string>();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in title.Split(new[] { ' ', '-', '_', '/', '.', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.Length < 3) continue;
            // Skip generic stop words that carry no signal.
            if (t is "the" or "and" or "for" or "with" or "from" or "that" or "this" or "into") continue;
            tokens.Add(t);
        }
        return tokens;
    }
}
