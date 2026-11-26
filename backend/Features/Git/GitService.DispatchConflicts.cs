using AgentStudio.Dispatch;
using AgentStudio.Pipeline;
using System.Text.RegularExpressions;

namespace AgentStudio.Git;

public partial class GitService
{
    private readonly DispatchStore _dispatchConflicts;
    private DispatchStore DispatchConflicts => _dispatchConflicts;

    /// <summary>Collects attribution facts without changing the integration outcome or the compact report.</summary>
    public DispatchConflictRecordV1 RecordDispatchConflict(string repo, string project, string card,
        string kind, string baseSha, string tip, IReadOnlyList<string> files,
        string evidenceId, DateTimeOffset at, bool backfilled = false)
        => RecordDispatchConflict(repo, project, card, kind, baseSha, tip, files, evidenceId, at,
            backfilled, ReadDispatchConflictAttribution());

    internal DispatchConflictAttribution ReadDispatchConflictAttribution()
        => _scanner.ReadPerGeneration("dispatch-conflict-attribution", 1,
            publication => DispatchConflictAttribution.Build(publication.Live.Concat(publication.Archive)));

    private DispatchConflictRecordV1 RecordDispatchConflict(string repo, string project, string card,
        string kind, string baseSha, string tip, IReadOnlyList<string> files,
        string evidenceId, DateTimeOffset at, bool backfilled, DispatchConflictAttribution attribution)
    {
        var partners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        card = attribution.Card(project, card);
        if (files.Count > 0 && baseSha.Length > 0 && tip.Length > 0)
        {
            var (log, error, code) = RunGitArgs(repo, ["log", "--full-history", "--format=%H%x00%s%x00",
                $"{baseSha}..{tip}", "--", .. files]);
            if (code != 0) throw new IOException($"Dispatch conflict attribution failed: {error}");
            var fields = log.Split('\0');
            for (var index = 0; index + 1 < fields.Length; index += 2)
            {
                if (attribution.Owners(project, fields[index].Trim()) is { } owners) partners.UnionWith(owners);
                else if (PrimaryConflictCard(fields[index + 1]) is { } owner) partners.Add(owner);
            }
        }
        partners.Remove(card);
        var ordered = partners.Order(StringComparer.Ordinal).ToArray();
        var id = DispatchStore.ConflictId(project, card, kind, evidenceId);
        var record = new DispatchConflictRecordV1(id, project, card, kind, baseSha, tip,
            files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), ordered, at)
        {
            EvidenceId = evidenceId, Backfilled = backfilled,
            DispatchDecisionIds = ordered.Prepend(card).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(key => key, _ => (string?)null),
        };
        DispatchConflicts.AppendConflict(record);
        return record;
    }

    public void TryRecordDispatchRecovery(TaskInfo card, ReviewSubjectRecord subject,
        IntegrationConflictReport? report, string kind, string evidenceId)
    {
        try { RecordDispatchRecovery(card, subject, report, kind, evidenceId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _logger.LogWarning(ex, "Dispatch conflict evidence failed for {Card} ({Kind})", card.Id, kind); }
    }

    private void RecordDispatchRecovery(TaskInfo card, ReviewSubjectRecord subject,
        IntegrationConflictReport? report, string kind, string evidenceId)
    {
        var project = _scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name == card.ProjectName);
        var repo = project is null ? null : ResolveRepositoryRoot(project);
        if (repo is null) throw new IOException("Dispatch conflict repository is unavailable.");
        var tip = report?.IntegrationTipSha;
        if (string.IsNullOrWhiteSpace(tip))
        {
            var branch = TaskIntegrationBranch.Name(_projectSettings?.Get(card.ProjectName).IntegrationBranch);
            var (value, error, code) = RunGitArgs(repo, "rev-parse", "--verify", $"refs/remotes/origin/{branch}^{{commit}}");
            if (code != 0) (value, error, code) = RunGitArgs(repo, "rev-parse", "--verify", $"refs/heads/{branch}^{{commit}}");
            if (code != 0) throw new IOException($"Dispatch integration tip unavailable: {error}");
            tip = value.Trim();
        }
        var (mergeBase, baseError, baseCode) = RunGitArgs(repo, "merge-base", subject.ResultSha, tip);
        if (baseCode != 0) throw new IOException($"Dispatch delivery base unavailable: {baseError}");
        var prior = DispatchConflicts.ReadConflict(card.ProjectName, DispatchStore.ConflictId(
            card.ProjectName, card.Key ?? card.Id, "integration-conflict", subject.ResultSha + ":" + tip));
        var branchName = report?.IntegrationBranch
            ?? TaskIntegrationBranch.Name(_projectSettings?.Get(card.ProjectName).IntegrationBranch);
        var captured = prior is not null ? [] : IntegrationConflictLedger.Default.ReadCollision(
                card.ProjectName, string.IsNullOrWhiteSpace(subject.TaskKey) ? card.TaskKey : subject.TaskKey,
                branchName, subject.ResultSha, tip)?.Files.Select(file => file.Path).ToArray() ?? [];
        RecordDispatchConflict(repo, card.ProjectName, card.Key ?? card.Id, kind, subject.BaseSha ?? mergeBase.Trim(), tip,
            prior?.ConflictedFiles ?? (captured.Length > 0 ? captured : report?.ConflictedFiles ?? []), evidenceId, DateTimeOffset.UtcNow);
    }

    /// <summary>Ledger collection must not prevent the forecast collector from saving delivery facts.</summary>
    public void TryBackfillDispatchConflicts(string repo, string project, DateTimeOffset now,
        string? deliveryBase, string deliveryTip, string ownCard)
    {
        try { BackfillDispatchConflicts(repo, project, now, deliveryBase, deliveryTip, ownCard, backfilled: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { _logger.LogWarning(ex, "Dispatch conflict collection failed for {Card}; delivery forecast collection continues", ownCard); }
    }

    /// <summary>The Dossier wave method: card-owned sync and conflict-resolution commits on all refs.</summary>
    public int BackfillDispatchConflicts(string repo, string project, DateTimeOffset now,
        string? deliveryBase = null, string? deliveryTip = null, string? ownCard = null, bool backfilled = true)
    {
        var historyArgs = new List<string> { "log", $"--since={now.AddDays(-30):O}",
            $"--until={now:O}", "--format=%H%x00%P%x00%cI%x00%s%x00" };
        if (deliveryTip is null) historyArgs.Add("--all");
        else historyArgs.Add(deliveryBase is null ? deliveryTip : $"{deliveryBase}..{deliveryTip}");
        var (log, error, code) = RunGitArgs(repo, historyArgs.ToArray());
        if (code != 0) throw new IOException($"Dispatch backfill history unavailable: {error}");
        var fields = log.Split('\0');
        var count = 0;
        DispatchConflictAttribution? attribution = null;
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            var sha = fields[index].Trim();
            var subject = fields[index + 3];
            var card = PrimaryConflictCard(subject);
            if (card is null || (ownCard is not null && !card.Equals(ownCard, StringComparison.OrdinalIgnoreCase))) continue;
            if (!Regex.IsMatch(subject, @"(?:merge|sync|include|latest).*develop|develop.*(?:merge|sync)|resolv.*conflict", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var parents = fields[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length == 0) continue;
            // A linear resolution follows the last first-parent sync merge when that
            // graph survives. Rewritten histories may have no such evidence.
            var syncParents = parents;
            if (parents.Length == 1)
            {
                var (previousMerge, _, previousCode) = RunGitArgs(repo, "log", "--first-parent", "--merges", "-1", "--format=%P", parents[0]);
                if (previousCode == 0 && previousMerge.Trim().Split(' ').Length == 2)
                    syncParents = previousMerge.Trim().Split(' ');
            }
            var tip = syncParents.Length > 1 ? syncParents[1] : parents[0];
            var (baseValue, _, baseCode) = RunGitArgs(repo, "merge-base", syncParents[0], tip);
            if (baseCode != 0) continue;
            var baseSha = syncParents.Length > 1 ? baseValue.Trim() : deliveryBase ?? parents[0];
            var (paths, pathError, pathCode) = RunGitArgs(repo, "diff-tree", "--no-commit-id", "--name-only", "-r", "-z", parents[0], sha, "--");
            if (pathCode != 0) throw new IOException($"Dispatch backfill paths unavailable: {pathError}");
            var id = DispatchStore.ConflictId(project, card, "develop-sync", sha);
            if (DispatchConflicts.HasConflict(project, id)) continue;
            RecordDispatchConflict(repo, project, card, "develop-sync", baseSha, tip,
                paths.Split('\0', StringSplitOptions.RemoveEmptyEntries), sha,
                DateTimeOffset.Parse(fields[index + 2], System.Globalization.CultureInfo.InvariantCulture), backfilled,
                attribution ??= ReadDispatchConflictAttribution());
            count++;
        }
        return count;
    }
}
