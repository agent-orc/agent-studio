using System.Security.Cryptography;

namespace AgentStudio.Tasks;

/// <summary>
/// Status codes for <see cref="MergeService"/> operations. Endpoint mappers
/// translate to HTTP shapes (404, 409, 400, 200).
/// </summary>
public enum MergeStatus
{
    Success,
    PrimaryNotFound,
    SecondaryNotFound,
    SameJob,
    DifferentProject,
    InvalidMode,
    AlreadyMerged,
    ArchiveCollision,
    Failure,
}

public record MergeOutcome(MergeStatus Status, MergeResponse? Response = null, string? Message = null);

public enum MergeUndoStatus
{
    Success,
    TokenNotFound,
    Expired,
    AlreadyRestored,
    ArchiveMissing,
    PrimaryNotFound,
    Failure,
}

public record MergeUndoOutcome(MergeUndoStatus Status, MergeUndoResponse? Response = null, string? Message = null);

/// <summary>
/// The mutation core for the consolidation/merge API. Owns the folder
/// moves, the timeline copy, the audit-log append, and the 24h undo path.
/// Endpoint mappers are thin wrappers around the methods on this class.
///
/// <para>Memory rule: every mutation goes through here, never through
/// shell <c>mv</c> / <c>rm</c> against a job folder. The audit log + the
/// timeline events depend on the side effects being centralised, so a
/// bypass produces zombie state the orchestrator cannot reason about.</para>
/// </summary>
public sealed class MergeService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TimelineLog _timeline;
    private readonly MergeAuditLog _audit;
    private readonly MergeCandidateFinder _candidates;
    private readonly ILogger<MergeService> _logger;

    public MergeService(
        TaskScannerService scanner,
        TaskStateMachine states,
        TimelineLog timeline,
        MergeAuditLog audit,
        MergeCandidateFinder candidates,
        ILogger<MergeService> logger)
    {
        _scanner = scanner;
        _states = states;
        _timeline = timeline;
        _audit = audit;
        _candidates = candidates;
        _logger = logger;
    }

    public MergeCandidatesResponse FindCandidates(string primaryId, string? watchPath)
    {
        return new MergeCandidatesResponse
        {
            PrimaryId = primaryId,
            Candidates = _candidates.Find(primaryId, watchPath),
        };
    }

    /// <summary>
    /// Dry-run. Computes the timeline events that <c>Merge</c> would
    /// append and any conflicts the operator should know about. Does not
    /// touch disk.
    /// </summary>
    public MergeOutcome Preview(string primaryId, string? primaryWatchPath, MergeRequest req)
    {
        var validation = ValidateInputs(primaryId, primaryWatchPath, req,
            out var primary, out var secondary);
        if (validation != null) return validation;

        var proposedEvents = BuildProposedTimelineEvents(primary!, secondary!, req.Mode);
        var conflicts = DetectConflicts(primary!, secondary!);

        // Preview shares the response shape with the real merge so the FE
        // can render the same summary card; the absorbed-runs count is the
        // number of agent_run_finished entries in the secondary's timeline.
        var runsCount = CountSecondaryRuns(secondary!);

        var preview = new MergePreviewResponse
        {
            PrimaryId = primary!.Id,
            SecondaryId = secondary!.Id,
            Mode = req.Mode,
            RunsToAbsorb = runsCount,
            TimelineEventsToAppend = proposedEvents.Count,
            ProposedTimelineEvents = proposedEvents,
            Conflicts = conflicts,
        };

        // The preview is returned via the outcome.Response.Primary field is
        // unused; the endpoint mapper recognises preview by route and serialises
        // MergePreviewResponse separately.
        return new MergeOutcome(MergeStatus.Success, new MergeResponse
        {
            Primary = primary,
            AbsorbedRuns = runsCount,
            TimelineEventsAppended = proposedEvents.Count,
            Mode = req.Mode,
            RestoreToken = string.Empty,
            UndoExpiresAt = DateTime.MinValue,
        }, System.Text.Json.JsonSerializer.Serialize(preview, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        }));
    }

    public MergeOutcome Merge(string primaryId, string? primaryWatchPath, MergeRequest req, string who)
    {
        var validation = ValidateInputs(primaryId, primaryWatchPath, req,
            out var primary, out var secondary);
        if (validation != null) return validation;

        return req.Mode switch
        {
            MergeModes.LinkOnly => DoLinkOnlyMerge(primary!, secondary!, req.Reason, who),
            _ => DoConsolidateMerge(primary!, secondary!, req.Mode, req.Reason, who),
        };
    }

    private MergeOutcome DoConsolidateMerge(TaskInfo primary, TaskInfo secondary, string mode, string reason, string who)
    {
        var archiveRoot = _audit.GetArchiveMergedDir();
        if (archiveRoot == null)
        {
            return new MergeOutcome(MergeStatus.Failure,
                Message: "No workspace root resolvable; cannot archive secondary folder.");
        }

        // Archive folder name: <secondaryId>__<timestamp>__<short-token>.
        // The suffix prevents collisions if the same slug is merged twice
        // and gives the operator a visual trail back to the audit row.
        var now = DateTime.UtcNow;
        var token = MintRestoreToken();
        var archiveSlug = $"{secondary.Id}__{now:yyyyMMddHHmmss}__{token[..8]}";
        var archivePath = Path.Combine(archiveRoot, archiveSlug);

        var proposedEvents = BuildProposedTimelineEvents(primary, secondary, mode);
        var runsCount = CountSecondaryRuns(secondary);

        try
        {
            // 1) Append every secondary event into primary's timeline in
            //    chronological order. The events keep their original ts
            //    so the operator sees the true wrapper history.
            foreach (var evt in proposedEvents)
            {
                _timeline.Append(primary.FolderPath, evt);
            }

            // 2) Mirror selected artefacts (prompt, status, screenshots)
            //    into primary's results/merged/<secondaryId>/ so the
            //    detail view can drill back without reading the archive.
            MirrorArtefactsIntoPrimary(primary, secondary);

            // 3) Move the secondary folder into the archive.
            Directory.CreateDirectory(archiveRoot);
            if (Directory.Exists(archivePath))
            {
                return new MergeOutcome(MergeStatus.ArchiveCollision,
                    Message: $"Archive folder '{archiveSlug}' already exists. Retry later.");
            }
            Directory.Move(secondary.FolderPath, archivePath);

            // 4) Stamp the secondary's task.json with mergedInto so the
            //    archived record stays self-describing.
            TaskJsonFile.UpdateField(archivePath, "mergedInto", primary.Id, _logger);
            TaskJsonFile.UpdateField(archivePath, "mergedAt", now.ToString("o"), _logger);
            TaskJsonFile.UpdateField(archivePath, "mergeMode", mode, _logger);

            // 5) Audit row authorises the undo.
            var record = new MergeAuditRecord
            {
                At = now,
                Who = string.IsNullOrWhiteSpace(who) ? "unknown" : who,
                Mode = mode,
                PrimaryId = primary.Id,
                PrimaryWatchPath = primary.WatchPath,
                PrimaryFolderPath = primary.FolderPath,
                SecondaryId = secondary.Id,
                SecondaryWatchPath = secondary.WatchPath,
                SecondaryOriginalState = secondary.State,
                SecondaryOriginalFolderPath = secondary.FolderPath,
                ArchivedFolderPath = archivePath,
                Reason = reason ?? string.Empty,
                RestoreToken = token,
                AbsorbedRuns = runsCount,
                TimelineEventsAppended = proposedEvents.Count,
            };
            _audit.Append(record);

            _scanner.InvalidateCache();
            _scanner.RemoveCore(secondary);
            _scanner.PublishCoreFromKnownFolder(primary.FolderPath);

            // Re-resolve the primary so the response carries the latest
            // mtime / lane / timeline counters.
            var refreshed = _scanner.FindJob(primary.Id, primary.WatchPath) ?? primary;

            return new MergeOutcome(MergeStatus.Success, new MergeResponse
            {
                Primary = refreshed,
                AbsorbedRuns = runsCount,
                TimelineEventsAppended = proposedEvents.Count,
                Mode = mode,
                RestoreToken = token,
                UndoExpiresAt = now.AddDays(MergeModes.UndoGraceDays),
                ArchivedAt = archivePath,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge consolidate failed primary={Primary} secondary={Secondary}",
                primary.Id, secondary.Id);
            return new MergeOutcome(MergeStatus.Failure, Message: ex.Message);
        }
    }

    private MergeOutcome DoLinkOnlyMerge(TaskInfo primary, TaskInfo secondary, string reason, string who)
    {
        // link-only: secondary stays in place; only the cross-reference
        // and an event on each side are recorded. No archive move; no
        // restore needed (the secondary is still on the board).
        var now = DateTime.UtcNow;
        var token = MintRestoreToken();
        try
        {
            TaskJsonFile.UpdateField(secondary.FolderPath, "mergedInto", primary.Id, _logger);
            TaskJsonFile.UpdateField(secondary.FolderPath, "mergedAt", now.ToString("o"), _logger);
            TaskJsonFile.UpdateField(secondary.FolderPath, "mergeMode", MergeModes.LinkOnly, _logger);

            var mergedInEvent = new TimelineEvent
            {
                Ts = now,
                Kind = TimelineEventKinds.MergedIn,
                Actor = TimelineActors.Human(who),
                Summary = $"Linked {secondary.Id} into this card ({MergeModes.LinkOnly}). Reason: {SafeReason(reason)}",
                Details = new()
                {
                    ["secondaryId"] = secondary.Id,
                    ["mode"] = MergeModes.LinkOnly,
                    ["reason"] = reason ?? string.Empty,
                },
            };
            _timeline.Append(primary.FolderPath, mergedInEvent);

            var record = new MergeAuditRecord
            {
                At = now,
                Who = string.IsNullOrWhiteSpace(who) ? "unknown" : who,
                Mode = MergeModes.LinkOnly,
                PrimaryId = primary.Id,
                PrimaryWatchPath = primary.WatchPath,
                PrimaryFolderPath = primary.FolderPath,
                SecondaryId = secondary.Id,
                SecondaryWatchPath = secondary.WatchPath,
                SecondaryOriginalState = secondary.State,
                SecondaryOriginalFolderPath = secondary.FolderPath,
                Reason = reason ?? string.Empty,
                RestoreToken = token,
                AbsorbedRuns = 0,
                TimelineEventsAppended = 1,
            };
            _audit.Append(record);
            _scanner.InvalidateCache();
            _scanner.PublishCoreFromKnownFolder(primary.FolderPath);
            _scanner.PublishCoreFromKnownFolder(secondary.FolderPath);

            var refreshed = _scanner.FindJob(primary.Id, primary.WatchPath) ?? primary;
            return new MergeOutcome(MergeStatus.Success, new MergeResponse
            {
                Primary = refreshed,
                AbsorbedRuns = 0,
                TimelineEventsAppended = 1,
                Mode = MergeModes.LinkOnly,
                RestoreToken = token,
                UndoExpiresAt = now.AddDays(MergeModes.UndoGraceDays),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge link-only failed primary={Primary} secondary={Secondary}",
                primary.Id, secondary.Id);
            return new MergeOutcome(MergeStatus.Failure, Message: ex.Message);
        }
    }

    public MergeUndoOutcome Undo(string primaryId, MergeUndoRequest req, string who)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.RestoreToken))
        {
            return new MergeUndoOutcome(MergeUndoStatus.TokenNotFound,
                Message: "restoreToken is required");
        }

        var now = DateTime.UtcNow;
        var record = _audit.FindRestorable(req.RestoreToken, now);
        if (record == null)
        {
            return new MergeUndoOutcome(MergeUndoStatus.TokenNotFound,
                Message: "Unknown restore token, already used, or 24h undo window has elapsed.");
        }
        if (!string.Equals(record.PrimaryId, primaryId, StringComparison.Ordinal))
        {
            return new MergeUndoOutcome(MergeUndoStatus.TokenNotFound,
                Message: "Restore token does not match this primary.");
        }
        if ((now - record.At).TotalDays > MergeModes.UndoGraceDays)
        {
            return new MergeUndoOutcome(MergeUndoStatus.Expired,
                Message: "Undo window has elapsed (24h).");
        }

        try
        {
            switch (record.Mode)
            {
                case MergeModes.LinkOnly:
                    return UndoLinkOnly(record, now, who);
                default:
                    return UndoConsolidate(record, now, who);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge undo failed primary={Primary} secondary={Secondary}",
                record.PrimaryId, record.SecondaryId);
            return new MergeUndoOutcome(MergeUndoStatus.Failure, Message: ex.Message);
        }
    }

    private MergeUndoOutcome UndoConsolidate(MergeAuditRecord record, DateTime now, string who)
    {
        if (string.IsNullOrWhiteSpace(record.ArchivedFolderPath) || !Directory.Exists(record.ArchivedFolderPath))
        {
            return new MergeUndoOutcome(MergeUndoStatus.ArchiveMissing,
                Message: "Archived folder is missing; cannot restore.");
        }
        if (Directory.Exists(record.SecondaryOriginalFolderPath))
        {
            return new MergeUndoOutcome(MergeUndoStatus.Failure,
                Message: $"Original folder still exists at {record.SecondaryOriginalFolderPath}; refusing to overwrite.");
        }

        // Move the archived folder back to its original lane path. Clear
        // the mergedInto / mergedAt / mergeMode fields so the restored
        // card is indistinguishable from a never-merged one.
        Directory.CreateDirectory(Path.GetDirectoryName(record.SecondaryOriginalFolderPath)!);
        Directory.Move(record.ArchivedFolderPath, record.SecondaryOriginalFolderPath);
        TaskJsonFile.UpdateField(record.SecondaryOriginalFolderPath, "mergedInto", "", _logger);
        TaskJsonFile.UpdateField(record.SecondaryOriginalFolderPath, "mergedAt", "", _logger);
        TaskJsonFile.UpdateField(record.SecondaryOriginalFolderPath, "mergeMode", "", _logger);

        // Drop the mirrored artefacts under primary/results/merged/<secondaryId>/
        // so the primary's detail view stops showing the absorbed history.
        var mirrorDir = Path.Combine(record.PrimaryFolderPath, "results", "merged", record.SecondaryId);
        if (Directory.Exists(mirrorDir))
        {
            try { Directory.Delete(mirrorDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete mirror {Dir} on undo", mirrorDir); }
        }

        // Append an undo event on the primary's timeline so the reopen is
        // visible in the ledger. We intentionally do NOT strip the prior
        // merged_in events: the ledger is append-only and the undo row is
        // the canonical "ignore these" marker.
        _timeline.Append(record.PrimaryFolderPath, new TimelineEvent
        {
            Ts = now,
            Kind = TimelineEventKinds.MergedIn,
            Actor = TimelineActors.Human(who),
            Summary = $"Merge UNDONE: {record.SecondaryId} restored to {record.SecondaryOriginalState}",
            Details = new()
            {
                ["secondaryId"] = record.SecondaryId,
                ["undo"] = "true",
                ["restoreToken"] = record.RestoreToken,
            },
        });

        _audit.AppendUndo(record, now, who);
        _scanner.InvalidateCache();
        var secondaryProject = _scanner.GetWatchPaths().FirstOrDefault(path =>
            WatchPathComparison.PathsEqual(path.Path, record.SecondaryWatchPath))?.Name
            ?? Path.GetFileName(record.SecondaryWatchPath);
        _scanner.PublishCoreFromFolder(record.SecondaryOriginalFolderPath,
            record.SecondaryWatchPath, secondaryProject,
            record.SecondaryOriginalState);
        _scanner.PublishCoreFromKnownFolder(record.PrimaryFolderPath);

        return new MergeUndoOutcome(MergeUndoStatus.Success, new MergeUndoResponse
        {
            Restored = true,
            PrimaryId = record.PrimaryId,
            SecondaryId = record.SecondaryId,
            Message = $"Restored {record.SecondaryId} to {record.SecondaryOriginalState}.",
        });
    }

    private MergeUndoOutcome UndoLinkOnly(MergeAuditRecord record, DateTime now, string who)
    {
        if (!Directory.Exists(record.SecondaryOriginalFolderPath))
        {
            return new MergeUndoOutcome(MergeUndoStatus.ArchiveMissing,
                Message: "Linked secondary folder no longer exists.");
        }
        TaskJsonFile.UpdateField(record.SecondaryOriginalFolderPath, "mergedInto", "", _logger);
        TaskJsonFile.UpdateField(record.SecondaryOriginalFolderPath, "mergedAt", "", _logger);
        TaskJsonFile.UpdateField(record.SecondaryOriginalFolderPath, "mergeMode", "", _logger);
        _timeline.Append(record.PrimaryFolderPath, new TimelineEvent
        {
            Ts = now,
            Kind = TimelineEventKinds.MergedIn,
            Actor = TimelineActors.Human(who),
            Summary = $"Link UNDONE: {record.SecondaryId} unlinked.",
            Details = new() { ["secondaryId"] = record.SecondaryId, ["undo"] = "true" },
        });
        _audit.AppendUndo(record, now, who);
        _scanner.InvalidateCache();
        _scanner.PublishCoreFromKnownFolder(record.PrimaryFolderPath);
        _scanner.PublishCoreFromKnownFolder(record.SecondaryOriginalFolderPath);
        return new MergeUndoOutcome(MergeUndoStatus.Success, new MergeUndoResponse
        {
            Restored = true,
            PrimaryId = record.PrimaryId,
            SecondaryId = record.SecondaryId,
            Message = $"Unlinked {record.SecondaryId} from {record.PrimaryId}.",
        });
    }

    // ---- Helpers --------------------------------------------------------

    private MergeOutcome? ValidateInputs(
        string primaryId,
        string? primaryWatchPath,
        MergeRequest req,
        out TaskInfo? primary,
        out TaskInfo? secondary)
    {
        primary = null;
        secondary = null;

        if (req == null) return new MergeOutcome(MergeStatus.Failure, Message: "Request body required");
        if (string.IsNullOrWhiteSpace(primaryId))
            return new MergeOutcome(MergeStatus.PrimaryNotFound, Message: "primaryId required");
        if (string.IsNullOrWhiteSpace(req.SecondaryId))
            return new MergeOutcome(MergeStatus.SecondaryNotFound, Message: "secondaryId required");
        if (!MergeModes.IsValid(req.Mode))
            return new MergeOutcome(MergeStatus.InvalidMode,
                Message: $"mode must be one of {string.Join(", ", MergeModes.All)}");
        if (string.Equals(primaryId, req.SecondaryId, StringComparison.Ordinal))
            return new MergeOutcome(MergeStatus.SameJob, Message: "primary and secondary must differ");

        primary = _scanner.FindJob(primaryId, primaryWatchPath);
        if (primary == null) return new MergeOutcome(MergeStatus.PrimaryNotFound);

        secondary = _scanner.FindJob(req.SecondaryId, req.SecondaryWatchPath ?? primaryWatchPath);
        if (secondary == null) return new MergeOutcome(MergeStatus.SecondaryNotFound);

        if (!string.Equals(primary.WatchPath, secondary.WatchPath, StringComparison.OrdinalIgnoreCase))
            return new MergeOutcome(MergeStatus.DifferentProject,
                Message: "primary and secondary must live in the same project (watchPath)");

        // Refuse to merge a job that is already merged into something. The
        // operator can undo first if they need to re-target.
        var primaryAlreadyMerged = HasMergedIntoField(primary.FolderPath);
        var secondaryAlreadyMerged = HasMergedIntoField(secondary.FolderPath);
        if (primaryAlreadyMerged || secondaryAlreadyMerged)
        {
            return new MergeOutcome(MergeStatus.AlreadyMerged,
                Message: "One side already carries a mergedInto pointer; undo first.");
        }

        return null;
    }

    private static bool HasMergedIntoField(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, "task.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(
                json, TaskJsonFile.ReadOpts);
            if (doc == null) return false;
            return doc.TryGetValue("mergedInto", out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrEmpty(el.GetString());
        }
        catch { return false; }
    }

    private List<TimelineEvent> BuildProposedTimelineEvents(TaskInfo primary, TaskInfo secondary, string mode)
    {
        var result = new List<TimelineEvent>();
        var now = DateTime.UtcNow;

        // Lead row: the merged_in summary. This is the row the FE
        // collapses into a single "<N> events from <secondaryId>" header.
        result.Add(new TimelineEvent
        {
            Ts = now,
            Kind = TimelineEventKinds.MergedIn,
            Actor = TimelineActors.System,
            Summary = $"Merged {secondary.Id} into {primary.Id} ({mode})",
            Details = new()
            {
                ["secondaryId"] = secondary.Id,
                ["secondaryTitle"] = secondary.Title ?? string.Empty,
                ["secondaryState"] = secondary.State,
                ["mode"] = mode,
            },
        });

        // Replay every secondary event with the original ts so the
        // operator sees the true wrapper history; we tag actor with the
        // secondary's id so it is visually distinguishable.
        foreach (var evt in _timeline.ReadAll(secondary.FolderPath))
        {
            var details = evt.Details != null
                ? new Dictionary<string, string>(evt.Details)
                : new Dictionary<string, string>();
            details["absorbedFrom"] = secondary.Id;

            result.Add(evt with
            {
                Details = details,
                Summary = $"[from {secondary.Id}] {evt.Summary}",
            });
        }

        return result;
    }

    private static int CountSecondaryRuns(TaskInfo secondary)
    {
        try
        {
            var sessionEventsPath = TaskPaths.SessionEventsLog(secondary.FolderPath);
            if (!File.Exists(sessionEventsPath)) return 0;
            var lines = File.ReadAllLines(sessionEventsPath);
            // session-events.jsonl carries one row per CLI start/finish; we
            // count distinct "kind":"started" rows as a cheap run count.
            return lines.Count(l => l.Contains("\"kind\":\"started\"", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    private static List<MergeConflict> DetectConflicts(TaskInfo primary, TaskInfo secondary)
    {
        var conflicts = new List<MergeConflict>();
        if (primary.Commits.Count > 0 && secondary.Commits.Count > 0)
        {
            var primarySha = primary.Commits.Select(c => c.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in secondary.Commits)
            {
                if (!string.IsNullOrEmpty(c.Sha) && primarySha.Contains(c.Sha))
                {
                    conflicts.Add(new MergeConflict
                    {
                        Kind = "duplicate-commit",
                        Description = $"Both jobs reference commit {c.Sha[..Math.Min(7, c.Sha.Length)]}; primary's entry wins.",
                    });
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(secondary.SessionName)
            && !string.IsNullOrWhiteSpace(primary.SessionName)
            && !string.Equals(secondary.SessionName, primary.SessionName, StringComparison.Ordinal))
        {
            conflicts.Add(new MergeConflict
            {
                Kind = "session-name",
                Description = $"Secondary has session '{secondary.SessionName}'; primary keeps its own '{primary.SessionName}'.",
            });
        }
        return conflicts;
    }

    private void MirrorArtefactsIntoPrimary(TaskInfo primary, TaskInfo secondary)
    {
        try
        {
            var mirrorDir = Path.Combine(primary.FolderPath, "results", "merged", secondary.Id);
            Directory.CreateDirectory(mirrorDir);

            // Copy prompt.md, status.md, and the entire screenshots/results/
            // tree so the primary's detail view can drill back without going
            // to the archive.
            foreach (var name in new[] { "prompt.md", "status.md" })
            {
                var src = Path.Combine(secondary.FolderPath, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(mirrorDir, name), overwrite: true);
            }
            CopyDirectoryIfExists(Path.Combine(secondary.FolderPath, "results"), Path.Combine(mirrorDir, "results"));
            CopyDirectoryIfExists(Path.Combine(secondary.FolderPath, "attachments"), Path.Combine(mirrorDir, "attachments"));
            CopyDirectoryIfExists(Path.Combine(secondary.FolderPath, "logs"), Path.Combine(mirrorDir, "logs"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MirrorArtefactsIntoPrimary partial copy failed for {Primary}<-{Secondary}",
                primary.Id, secondary.Id);
        }
    }

    private static void CopyDirectoryIfExists(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            try { File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "MergeService: best-effort"); /* best-effort */ }
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryIfExists(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    private static string SafeReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "(no reason)" : reason.Trim();

    private static string MintRestoreToken()
    {
        // 32 random bytes -> 64-char hex. Long enough that a brute-force
        // undo attempt is uneconomical even with the audit log readable.
        var buf = new byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
