using System.Diagnostics;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// Keeps the TaskRepository cheap to inspect and bounded between operator
/// visits. The hosted cadence is the scheduler, so no platform-specific Git
/// maintenance task is installed on the machine.
/// </summary>
public sealed class WorkspaceRepositoryMaintenanceWorker : BackgroundService
{
    private readonly WorkspaceRepositoryMaintenanceService _maintenance;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceRepositoryMaintenanceWorker> _logger;

    public WorkspaceRepositoryMaintenanceWorker(
        WorkspaceRepositoryMaintenanceService maintenance,
        IConfiguration configuration,
        ILogger<WorkspaceRepositoryMaintenanceWorker> logger)
    {
        _maintenance = maintenance;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UntrackSecurityStoreSafely(stoppingToken);
        UntrackOversizedJournalsSafely(stoppingToken);
        await RunSafelyAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("WorkspaceRepository:MaintenanceIntervalMinutes") ?? 60,
            5,
            24 * 60)));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunSafelyAsync(stoppingToken).ConfigureAwait(false);
    }

    private void UntrackOversizedJournalsSafely(CancellationToken ct)
    {
        try
        {
            var result = _maintenance.UntrackOversizedReviewJournals(ct);
            if (!result.Success)
                _logger.LogWarning("workspace-review-journal-untrack-failed phase={Phase} error={Error}", result.Phase, result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-review-journal-untrack-failed phase=exception");
        }
    }

    private void UntrackSecurityStoreSafely(CancellationToken ct)
    {
        try
        {
            var result = _maintenance.UntrackSecurityStore(ct);
            if (!result.Success)
                _logger.LogWarning("workspace-security-store-untrack-failed phase={Phase} error={Error}", result.Phase, result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-security-store-untrack-failed phase=exception");
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            var result = await _maintenance.RunOnceAsync(ct).ConfigureAwait(false);
            if (!result.Success)
                _logger.LogWarning("workspace-repository-maintenance-failed phase={Phase} error={Error}", result.Phase, result.Error);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "WorkspaceRepositoryMaintenanceWorker: graceful shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-repository-maintenance-failed phase=exception");
        }
    }
}

public sealed class WorkspaceRepositoryMaintenanceService
{
    internal const int DefaultGcAuto = 5_000;
    private static readonly string[] RuntimeIgnorePatterns =
    {
        "/logs/bus/",
        "/.metadata/attempt-authority*",
        "*.bak",
        AgentStudio.Git.SecurityStorePathPolicy.IgnorePattern,
        AgentStudio.Magazine.MagazineStorePathPolicy.IgnorePattern,
    };
    internal const long LegacyFallbackUntrackBytes = 1024 * 1024;

    private readonly IConfiguration _configuration;
    private readonly WorkspaceArtifactCommitService _commits;
    private readonly ILoadThrottleGate _loadGate;
    private readonly ILogger<WorkspaceRepositoryMaintenanceService> _logger;
    private readonly TimeSpan _timeout;

    public WorkspaceRepositoryMaintenanceService(
        IConfiguration configuration,
        WorkspaceArtifactCommitService commits,
        ILoadThrottleGate loadGate,
        ILogger<WorkspaceRepositoryMaintenanceService> logger)
    {
        _configuration = configuration;
        _commits = commits;
        _loadGate = loadGate;
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceRepository:MaintenanceTimeoutSeconds") ?? 600,
            30,
            3600));
    }

    public async Task<WorkspaceRepositoryMaintenanceResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!(_configuration.GetValue<bool?>("WorkspaceRepository:MaintenanceEnabled") ?? true))
            return WorkspaceRepositoryMaintenanceResult.Ok("disabled");

        var gitRoot = _commits.ResolveWorkspaceGitRoot(_configuration["TaskRepository"]);
        if (gitRoot == null)
            return WorkspaceRepositoryMaintenanceResult.Ok("workspace-missing");

        await _loadGate.WaitUntilReadyAsync("workspace-repository-configuration", ct).ConfigureAwait(false);
        lock (WorkspaceArtifactCommitService.RepositoryGate(gitRoot))
        {
            var configured = ConfigureRepository(gitRoot, ct);
            if (!configured.Success) return configured;
        }

        var journals = UntrackOversizedReviewJournals(ct);
        if (!journals.Success) return journals;

        var sweep = _commits.TryCommitTrackedSweep(gitRoot);
        if (!sweep.Success)
            return WorkspaceRepositoryMaintenanceResult.Failed("tracked-sweep", sweep.Error ?? "unknown failure");

        await _loadGate.WaitUntilReadyAsync("workspace-repository-maintenance", ct).ConfigureAwait(false);
        lock (WorkspaceArtifactCommitService.RepositoryGate(gitRoot))
        {
            var bootstrap = BootstrapPackWhenNeeded(gitRoot, ct);
            if (!bootstrap.Success) return bootstrap;
            var maintenance = RunGit(gitRoot,
                ["maintenance", "run", "--task=loose-objects", "--task=incremental-repack"], ct);
            if (!maintenance.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed("git-maintenance", maintenance.Error);
        }

        _logger.LogInformation(
            "workspace-repository-maintenance-succeeded repo={Repo} sweepCommitted={SweepCommitted}",
            gitRoot, sweep.DidCommit);
        return WorkspaceRepositoryMaintenanceResult.Ok("complete");
    }

    /// <summary>
    /// Startup step: removes <c>.security/</c> from the workspace index while
    /// keeping the files on disk, and adds the folder to <c>info/exclude</c>.
    /// The staged removal is recorded by the next tracked sweep. Runs whether
    /// or not periodic maintenance is enabled, and logs only when it untracked
    /// something, so a clean repository stays silent.
    /// </summary>
    public WorkspaceRepositoryMaintenanceResult UntrackSecurityStore(CancellationToken ct = default)
    {
        var gitRoot = _commits.ResolveWorkspaceGitRoot(_configuration["TaskRepository"]);
        if (gitRoot == null)
            return WorkspaceRepositoryMaintenanceResult.Ok("workspace-missing");

        lock (WorkspaceArtifactCommitService.RepositoryGate(gitRoot))
        {
            // The magazine store (MAG-1) is server state too; it is excluded
            // here but was never tracked, so nothing needs untracking.
            var excluded = EnsureExcluded(gitRoot,
                [AgentStudio.Git.SecurityStorePathPolicy.IgnorePattern, AgentStudio.Magazine.MagazineStorePathPolicy.IgnorePattern], ct);
            if (!excluded.Success) return excluded;

            var securitySpec = $":(top,icase){AgentStudio.Git.SecurityStorePathPolicy.DirectoryName}";
            var tracked = RunGit(gitRoot, ["ls-files", "-z", "--", securitySpec], ct);
            if (!tracked.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed("security-store-inventory", tracked.Error);
            var count = tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
            if (count == 0)
                return WorkspaceRepositoryMaintenanceResult.Ok("security-store-untracked");

            var untrack = RunGit(gitRoot, ["rm", "-r", "-q", "--cached", "--ignore-unmatch", "--", securitySpec], ct);
            if (!untrack.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed("security-store-untrack", untrack.Error);
            _logger.LogWarning(
                "workspace-security-store-untracked repo={Repo} files={Files}; the files stay on disk and the removal is committed by the next tracked sweep",
                gitRoot, count);
            return WorkspaceRepositoryMaintenanceResult.Ok("security-store-untracked");
        }
    }

    /// <summary>Stage index-only removal of large legacy journals. The normal tracked
    /// sweep records the deletion in a platform-owned commit; files remain readable
    /// on disk until their cards are archived or migrated.</summary>
    public WorkspaceRepositoryMaintenanceResult UntrackOversizedReviewJournals(CancellationToken ct = default)
    {
        var gitRoot = _commits.ResolveWorkspaceGitRoot(_configuration["TaskRepository"]);
        if (gitRoot == null) return WorkspaceRepositoryMaintenanceResult.Ok("workspace-missing");
        lock (WorkspaceArtifactCommitService.RepositoryGate(gitRoot))
        {
            var tracked = RunGit(gitRoot,
                ["ls-files", "-z", "--", ":(glob)projects/**/logs/remote-review-settlement-*.json"], ct);
            if (!tracked.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed("journal-inventory", tracked.Error);
            var oversized = tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(path => WorkspaceArtifactCommitService.IsSettlementJournal(path))
                .Select(path => (Path: path, Full: Path.GetFullPath(Path.Combine(gitRoot, path))))
                .Where(item => File.Exists(item.Full) && new FileInfo(item.Full).Length > 5L * 1024 * 1024)
                .ToArray();
            if (oversized.Length == 0) return WorkspaceRepositoryMaintenanceResult.Ok("journals-bounded");
            var bytes = oversized.Sum(item => new FileInfo(item.Full).Length);
            foreach (var batch in oversized.Chunk(64))
            {
                var args = new List<string> { "rm", "-q", "--cached", "--" };
                args.AddRange(batch.Select(item => $":(top,literal){item.Path}"));
                var removed = RunGit(gitRoot, args, ct);
                if (!removed.Success)
                    return WorkspaceRepositoryMaintenanceResult.Failed("journal-untrack", removed.Error);
            }
            _logger.LogWarning("workspace-review-journals-untracked repo={Repo} files={Files} bytes={Bytes} "
                + "historyBytesStillReachable={Bytes} repackReclaimableBytes=0; tracked sweep will commit index removals",
                gitRoot, oversized.Length, bytes, bytes);
            return WorkspaceRepositoryMaintenanceResult.Ok("journals-untracked");
        }
    }

    private WorkspaceRepositoryMaintenanceResult BootstrapPackWhenNeeded(string gitRoot, CancellationToken ct)
    {
        var inventory = RunGit(gitRoot, ["count-objects", "-v"], ct);
        if (!inventory.Success)
            return WorkspaceRepositoryMaintenanceResult.Failed("git-object-inventory", inventory.Error);
        var hasPacks = inventory.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.StartsWith("packs:", StringComparison.Ordinal)
                && int.TryParse(line["packs:".Length..].Trim(), out var count)
                && count > 0);
        if (hasPacks) return WorkspaceRepositoryMaintenanceResult.Ok("pack-present");

        var repack = RunGit(gitRoot, ["repack", "-d", "-l"], ct);
        return repack.Success
            ? WorkspaceRepositoryMaintenanceResult.Ok("pack-bootstrapped")
            : WorkspaceRepositoryMaintenanceResult.Failed("git-pack-bootstrap", repack.Error);
    }

    private WorkspaceRepositoryMaintenanceResult ConfigureRepository(string gitRoot, CancellationToken ct)
    {
        var gcAuto = Math.Clamp(
            _configuration.GetValue<int?>("WorkspaceRepository:GcAuto") ?? DefaultGcAuto,
            100,
            100_000);
        var settings = new List<(string Key, string Value)>
        {
            ("gc.auto", gcAuto.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("maintenance.strategy", "incremental"),
        };
        if (OperatingSystem.IsWindows())
        {
            var fsMonitor = _configuration.GetValue<bool?>("WorkspaceRepository:CoreFsMonitor") ?? true;
            settings.Add(("core.fsmonitor", fsMonitor ? "true" : "false"));
        }

        foreach (var setting in settings)
        {
            var result = RunGit(gitRoot, ["config", "--local", setting.Key, setting.Value], ct);
            if (!result.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed("git-config", result.Error);
        }

        var excluded = EnsureExcluded(gitRoot, RuntimeIgnorePatterns, ct);
        if (!excluded.Success) return excluded;

        var untrack = RunGit(gitRoot,
            ["rm", "-r", "--cached", "--ignore-unmatch", "--", "logs/bus", ".metadata/attempt-authority*"], ct);
        if (!untrack.Success)
            return WorkspaceRepositoryMaintenanceResult.Failed("runtime-untrack", untrack.Error);

        // Remove only legacy large records. New compact evidence stays tracked.
        // The following tracked sweep makes the index removal a normal workspace commit.
        var trackedFallbacks = RunGit(gitRoot,
            ["ls-files", "-z", "--", "projects/**/logs/batch-fallback-*.json*"], ct);
        if (!trackedFallbacks.Success)
            return WorkspaceRepositoryMaintenanceResult.Failed("fallback-inventory", trackedFallbacks.Error);
        var oldFiles = trackedFallbacks.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => File.Exists(Path.Combine(gitRoot, path))
                && new FileInfo(Path.Combine(gitRoot, path)).Length > LegacyFallbackUntrackBytes)
            .ToArray();
        if (oldFiles.Length > 0)
        {
            var ignored = EnsureExcluded(gitRoot,
                oldFiles.Select(path => "/" + path.Replace('\\', '/')).ToArray(), ct);
            if (!ignored.Success) return ignored;
            var args = new List<string> { "rm", "-q", "--cached", "--ignore-unmatch", "--" };
            args.AddRange(oldFiles.Select(path => $":(top,literal){path}"));
            var removed = RunGit(gitRoot, args, ct);
            if (!removed.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed("fallback-untrack", removed.Error);
            _logger.LogInformation("workspace-fallback-large-untracked repo={Repo} files={Count} bytes={Bytes}",
                gitRoot, oldFiles.Length,
                oldFiles.Sum(path => new FileInfo(Path.Combine(gitRoot, path)).Length));
        }
        return WorkspaceRepositoryMaintenanceResult.Ok("configured");
    }

    private WorkspaceRepositoryMaintenanceResult EnsureExcluded(
        string gitRoot, IReadOnlyList<string> patterns, CancellationToken ct)
    {
        var excludePathResult = RunGit(gitRoot, ["rev-parse", "--git-path", "info/exclude"], ct);
        if (!excludePathResult.Success)
            return WorkspaceRepositoryMaintenanceResult.Failed("git-exclude-path", excludePathResult.Error);
        var excludePath = excludePathResult.Output.Trim();
        if (!Path.IsPathRooted(excludePath)) excludePath = Path.GetFullPath(Path.Combine(gitRoot, excludePath));
        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        var text = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
        var existing = text.Split('\n', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var missing = patterns.Where(pattern => !existing.Contains(pattern)).ToList();
        if (missing.Count > 0)
        {
            var prefix = text.Length == 0 || text.EndsWith('\n') ? string.Empty : Environment.NewLine;
            File.AppendAllText(excludePath, prefix + string.Join(Environment.NewLine, missing) + Environment.NewLine);
        }
        return WorkspaceRepositoryMaintenanceResult.Ok("excluded");
    }

    private MaintenanceGitResult RunGit(string cwd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(psi, timeout: _timeout, cancellationToken: ct);
        return new MaintenanceGitResult(
            result.Success,
            result.StandardOutput,
            string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim());
    }

    private sealed record MaintenanceGitResult(bool Success, string Output, string Error);
}

public sealed record WorkspaceRepositoryMaintenanceResult(bool Success, string Phase, string? Error)
{
    public static WorkspaceRepositoryMaintenanceResult Ok(string phase) => new(true, phase, null);
    public static WorkspaceRepositoryMaintenanceResult Failed(string phase, string error) => new(false, phase, error);
}
