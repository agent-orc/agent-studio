using AgentStudio.Git;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3747: one-time, idempotent repair of <see cref="PublishedGateEvidenceStore"/>.
/// A per-task publication that ran before its push recorded the gate leaves a
/// first-parent commit without an entry, although the card's own pre-develop
/// build gate passed on exactly that commit. The repair adds an entry only
/// from such a receipt: verdict <c>Ok</c>, executed (not reused), the full
/// suite ran, expected and tested SHA are the commit, and the commit is on the
/// first-parent line of the published integration branch. Containment alone
/// never creates evidence.
/// </summary>
public sealed class PublishedGateEvidenceBackfill(
    TaskScannerService scanner, GitService git, IHostApplicationLifetime lifetime,
    ILogger<PublishedGateEvidenceBackfill> logger) : IHostedService
{
    private const string GatePrefix = IntegrationGateJournal.PreDevelopBuildGateStep;
    private CancellationTokenSource? _stop;
    private Task? _repair;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        var token = _stop.Token;
        lifetime.ApplicationStarted.Register(() => _repair = Task.Run(async () =>
        {
            try
            {
                await scanner.WaitForFirstPublicationAsync(token).ConfigureAwait(false);
                RepairAll(token);
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                SilentCatch.Note(ex, "PublishedGateEvidenceBackfill: stopped before the first index publication");
            }
            catch (Exception ex) { logger.LogWarning(ex, "Published gate evidence backfill failed"); }
        }, token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is null) return;
        await stop.CancelAsync();
        if (_repair is not null)
            try { await _repair.WaitAsync(cancellationToken); }
            catch (OperationCanceledException ex)
            {
                SilentCatch.Note(ex, "PublishedGateEvidenceBackfill: stop cancellation");
            }
        stop.Dispose();
    }

    /// <summary>Backfills every card's qualifying receipts; returns the number of entries added.</summary>
    public int RepairAll(CancellationToken cancellationToken = default)
    {
        var added = 0;
        foreach (var job in scanner.ScanAllJobsWithArchive())
        {
            if (cancellationToken.IsCancellationRequested) break;
            try { added += Repair(job.ProjectName, job.Id, job.FolderPath, job.WatchPath); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Published gate evidence backfill skipped {Folder}", job.FolderPath);
            }
        }
        return added;
    }

    /// <summary>Backfills one card's qualifying receipts; an entry already recorded for the tree is kept.</summary>
    public int Repair(string project, string jobId, string jobFolderPath, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return 0;
        var receipts = PassedFullSuiteReceipts(jobFolderPath);
        if (receipts.Count == 0) return 0;
        var repoRoot = git.ResolveRepoRootForWatchPath(watchPath);
        if (repoRoot is null
            || string.Equals(Path.GetFullPath(watchPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return 0;
        var branch = TaskIntegrationBranch.Name(ReviewSubjectStore.Read(jobFolderPath)?.IntegrationBranch,
            fallback: "develop");
        // The pre-develop gate never proves a release-branch tree.
        if (!string.Equals(branch, "develop", StringComparison.OrdinalIgnoreCase)) return 0;
        var tip = git.GetBranchTip(repoRoot, git.HasOriginRemote(repoRoot)
            ? $"refs/remotes/origin/{branch}" : $"refs/heads/{branch}");
        if (!ReviewSubjectStore.IsValidResultSha(tip)) return 0;
        var recorded = PublishedGateEvidenceStore.Read(watchPath);
        var added = 0;
        foreach (var receipt in receipts)
        {
            if (recorded.Any(entry => string.Equals(entry.TreeSha, receipt.Sha, StringComparison.OrdinalIgnoreCase)
                    && IntegrationVerificationProjection.SameBranch(entry.IntegrationBranch, branch))
                || !OnFirstParentLine(repoRoot, receipt.Sha, tip!)) continue;
            var gateLog = Path.Combine("post-steps", Path.GetFileName(receipt.Path));
            if (!PublishedGateEvidenceStore.Append(watchPath, new PublishedGateEvidence(
                    project, branch, receipt.Sha, GatePrefix, jobId,
                    nameof(BuildTestGateVerdict.Ok), receipt.Reason, receipt.Sha,
                    DateTimeOffset.UtcNow, Backfilled: true, GateLog: $"{jobId}/{gateLog.Replace('\\', '/')}")))
                continue;
            added++;
            logger.LogInformation(
                "published-gate-evidence backfilled project={Project} job={JobId} branch={Branch} tree={Tree} log={Log}",
                project, jobId, branch, receipt.Sha, gateLog);
        }
        return added;
    }

    private bool OnFirstParentLine(string repoRoot, string sha, string tip)
    {
        if (string.Equals(sha, tip, StringComparison.OrdinalIgnoreCase)) return true;
        var parent = git.GetFirstParent(repoRoot, sha);
        if (!ReviewSubjectStore.IsValidResultSha(parent)) return false;
        return git.GetFirstParentRange(repoRoot, parent!, tip)?
            .Any(commit => string.Equals(commit.Sha, sha, StringComparison.OrdinalIgnoreCase)) == true;
    }

    internal sealed record PassedReceipt(string Path, string Sha, string Reason);

    /// <summary>
    /// The card's executed, green, full-suite pre-develop gate receipts whose
    /// expected and tested SHA agree. Red, cancelled, reused, narrow and
    /// malformed receipts are skipped.
    /// </summary>
    internal static IReadOnlyList<PassedReceipt> PassedFullSuiteReceipts(string jobFolderPath)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        if (!Directory.Exists(dir)) return [];
        var receipts = new List<PassedReceipt>();
        foreach (var path in Directory.GetFiles(dir, $"{GatePrefix}-*.log").Order(StringComparer.Ordinal))
        {
            try
            {
                using var reader = new StreamReader(path);
                var verdictLine = reader.ReadLine() ?? string.Empty;
                var shaLine = reader.ReadLine() ?? string.Empty;
                var reasonLine = reader.ReadLine() ?? string.Empty;
                var provenanceLine = reader.ReadLine() ?? string.Empty;
                var expected = HeaderValue(shaLine, "expectedSha=");
                var tested = HeaderValue(shaLine, "testedSha=");
                if (HeaderValue(verdictLine, "verdict=") != nameof(BuildTestGateVerdict.Ok)
                    || HeaderValue(verdictLine, "failureKind=") != nameof(BuildTestGateFailureKind.None)
                    || HeaderValue(provenanceLine, "verdictSource=") != nameof(GateVerdictSource.Executed)
                    || !ReviewSubjectStore.IsValidResultSha(tested)
                    || !string.Equals(expected, tested, StringComparison.OrdinalIgnoreCase)
                    || !reasonLine.StartsWith("reason=", StringComparison.Ordinal)
                    || !RanFullSuite(reasonLine)) continue;
                receipts.Add(new(path, tested!.ToLowerInvariant(), reasonLine["reason=".Length..]));
            }
            catch (IOException ex)
            {
                SilentCatch.Note(ex, "PublishedGateEvidenceBackfill: receipt unavailable");
            }
        }
        return receipts;
    }

    private static bool RanFullSuite(string reasonLine)
    {
        var suite = HeaderValue(reasonLine, "full-suite=")?.TrimEnd(';');
        return HeaderValue(reasonLine, "test-level=")?.TrimEnd(';') == "full"
               && suite is "map-fallback-and-run" or "required-and-run" or "run-conservatively";
    }

    private static string? HeaderValue(string line, string key)
    {
        var start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }
}
