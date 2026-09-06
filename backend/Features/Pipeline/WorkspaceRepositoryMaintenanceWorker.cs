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
        await RunSafelyAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("WorkspaceRepository:MaintenanceIntervalMinutes") ?? 60,
            5,
            24 * 60)));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunSafelyAsync(stoppingToken).ConfigureAwait(false);
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
    };

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

        var excludePathResult = RunGit(gitRoot, ["rev-parse", "--git-path", "info/exclude"], ct);
        if (!excludePathResult.Success)
            return WorkspaceRepositoryMaintenanceResult.Failed("git-exclude-path", excludePathResult.Error);
        var excludePath = excludePathResult.Output.Trim();
        if (!Path.IsPathRooted(excludePath)) excludePath = Path.GetFullPath(Path.Combine(gitRoot, excludePath));
        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        var existing = File.Exists(excludePath) ? File.ReadAllLines(excludePath).ToHashSet(StringComparer.Ordinal) : [];
        var missing = RuntimeIgnorePatterns.Where(pattern => !existing.Contains(pattern)).ToList();
        if (missing.Count > 0)
            File.AppendAllLines(excludePath, missing);

        var untrack = RunGit(gitRoot,
            ["rm", "-r", "--cached", "--ignore-unmatch", "--", "logs/bus", ".metadata/attempt-authority*"], ct);
        return untrack.Success
            ? WorkspaceRepositoryMaintenanceResult.Ok("configured")
            : WorkspaceRepositoryMaintenanceResult.Failed("runtime-untrack", untrack.Error);
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
        var result = GitNetworkProcessRunner.Run(psi, timeout: _timeout, cancellationToken: ct);
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
