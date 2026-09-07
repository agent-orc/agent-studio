using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

public sealed record TunnelKeeperStatus(
    bool Supported,
    string TaskName,
    string State,
    bool Enabled,
    bool Running,
    bool SshRunning,
    string? Cause,
    string? ObservedAt,
    IReadOnlyList<string> LogTail,
    string? Detail);

public sealed record TunnelKeeperReconnectResult(
    bool Succeeded,
    bool Enabled,
    bool Started,
    TunnelKeeperStatus Keeper,
    string Detail);

public interface ITunnelKeeperManager
{
    Task<TunnelKeeperStatus> ProbeAsync(CancellationToken cancellationToken);
    Task<TunnelKeeperReconnectResult> ReconnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Studio-local Windows Scheduled Task boundary. It inspects and starts only
/// the configured tunnel keeper; credentials and SSH configuration remain
/// owned by Windows and OpenSSH.
/// </summary>
public sealed class WindowsTunnelKeeperManager : ITunnelKeeperManager
{
    private static readonly Regex SafeTaskName = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private readonly IConfiguration _configuration;

    public WindowsTunnelKeeperManager(IConfiguration configuration) => _configuration = configuration;

    private string TaskName => _configuration["RemoteRunnerLink:KeeperTaskName"]
                               ?? "AgentRunner-TunnelKeeper";

    public async Task<TunnelKeeperStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        ValidateTaskName();
        var script = """
            $ErrorActionPreference = 'Stop'
            $task = Get-ScheduledTask -TaskName $args[0] -ErrorAction SilentlyContinue
            $info = if ($task) { Get-ScheduledTaskInfo -TaskName $args[0] } else { $null }
            $stateDir = if ($args[1]) { $args[1] } else { Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper' }
            $statePath = Join-Path $stateDir 'state.json'
            $logPath = if ($args[2]) { $args[2] } else { Join-Path $stateDir 'events.log' }
            if (-not $args[2] -and -not (Test-Path -LiteralPath $logPath) -and $task) {
              $actionArguments = [string]@($task.Actions)[0].Arguments
              if ($actionArguments -match '(?i)-File\s+(?:"([^"]+\.ps1)"|([^\s]+\.ps1))') {
                $keeperPath = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                $legacyLogPath = Join-Path (Split-Path -Parent $keeperPath) 'tunnel-keeper.log'
                if (Test-Path -LiteralPath $legacyLogPath) { $logPath = $legacyLogPath }
              }
            }
            $keeperState = if (Test-Path -LiteralPath $statePath) {
              Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            } else { $null }
            $ssh = @(Get-CimInstance Win32_Process -Filter "Name = 'ssh.exe'" -ErrorAction SilentlyContinue |
              Where-Object { [string]$_.CommandLine -match '(?:^|\s)-R(?:\s+|=)' })
            [ordered]@{
              found = [bool]$task
              enabled = [bool]($task -and $task.State -ne 'Disabled')
              running = [bool]($task -and $task.State -eq 'Running')
              schedulerState = if ($task) { [string]$task.State } else { 'Missing' }
              lastTaskResult = if ($info) { [int]$info.LastTaskResult } else { $null }
              sshRunning = $ssh.Count -gt 0
              probeStatus = if ($keeperState) { [string]$keeperState.status } else { $null }
              observedAt = if ($keeperState) { [string]$keeperState.observedAt } else { $null }
              message = if ($keeperState) { [string]$keeperState.message } else { $null }
              logTail = if (Test-Path -LiteralPath $logPath) { @(Get-Content -LiteralPath $logPath -Tail 12) } else { @() }
            } | ConvertTo-Json -Compress -Depth 4
            """;
        try
        {
            var result = await RunPowerShellAsync(
                script, [TaskName, ConfiguredStateDirectory(), ConfiguredLogPath()], cancellationToken);
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            var found = Bool(root, "found");
            var enabled = Bool(root, "enabled");
            var running = Bool(root, "running");
            var sshRunning = Bool(root, "sshRunning");
            var probeStatus = Text(root, "probeStatus");
            var cause = !found ? "not-running"
                : !enabled ? "task-disabled"
                : !running ? "not-running"
                : !sshRunning ? "ssh-not-running"
                : !string.Equals(probeStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                    ? "probe-failing"
                    : null;
            var state = cause is null ? "healthy" : "unhealthy";
            var tail = root.TryGetProperty("logTail", out var lines) && lines.ValueKind == JsonValueKind.Array
                ? lines.EnumerateArray().Select(item => item.GetString() ?? "").Where(line => line.Length > 0).ToArray()
                : [];
            return new TunnelKeeperStatus(
                true, TaskName, state, enabled, running, sshRunning, cause,
                Text(root, "observedAt"), tail,
                Text(root, "message") ?? $"Scheduled Task state: {Text(root, "schedulerState") ?? "unknown"}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TunnelKeeperStatus(
                true, TaskName, "unhealthy", false, false, false, "probe-failing",
                null, [], ex.Message);
        }
    }

    public async Task<TunnelKeeperReconnectResult> ReconnectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, false, Unsupported(), "Reconnect is available only on the Windows Studio host.");
        ValidateTaskName();
        var script = """
            $ErrorActionPreference = 'Stop'
            $task = Get-ScheduledTask -TaskName $args[0] -ErrorAction Stop
            if ($task.State -eq 'Disabled') { Enable-ScheduledTask -TaskName $args[0] | Out-Null }
            Start-ScheduledTask -TaskName $args[0]
            [ordered]@{ enabled = $true; started = $true } | ConvertTo-Json -Compress
            """;
        try
        {
            var output = await RunPowerShellAsync(script, [TaskName], cancellationToken);
            using var document = JsonDocument.Parse(output);
            var keeper = await ProbeAsync(cancellationToken);
            return new(true, Bool(document.RootElement, "enabled"), Bool(document.RootElement, "started"), keeper,
                $"Enabled and started {TaskName}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var keeper = await ProbeAsync(cancellationToken);
            return new(false, keeper.Enabled, false, keeper, ex.Message);
        }
    }

    private TunnelKeeperStatus Unsupported() => new(
        false, TaskName, "unsupported", false, false, false, null, null, [],
        "Tunnel keeper supervision is available only on the Windows Studio host.");

    private string ConfiguredStateDirectory()
        => _configuration["RemoteRunnerLink:KeeperStateDirectory"] ?? "";

    private string ConfiguredLogPath()
        => _configuration["RemoteRunnerLink:KeeperLogPath"] ?? "";

    private void ValidateTaskName()
    {
        if (!SafeTaskName.IsMatch(TaskName))
            throw new InvalidOperationException("RemoteRunnerLink:KeeperTaskName contains unsupported characters.");
    }

    private static async Task<string> RunPowerShellAsync(
        string script,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Length > 0 ? error : $"PowerShell exited with code {process.ExitCode}.");
        return output;
    }

    private static bool Bool(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record RemoteRunnerLinkHealth(
    string RunnerId,
    string Name,
    string LinkState,
    DateTime? LastSnapshotAt,
    DateTime StateSince,
    double? SnapshotAgeSeconds,
    bool ReadyCardsTargetHost,
    TunnelKeeperStatus? Keeper);

public sealed record RemoteRunnerReconnectResponse(
    string RunnerId,
    bool Succeeded,
    bool Enabled,
    bool Started,
    string Detail,
    string LinkState,
    double? NextSnapshotAgeSeconds,
    TunnelKeeperStatus Keeper);

/// <summary>Pure freshness policy shared by the read and reconnect response.</summary>
public static class RemoteRunnerLinkPolicy
{
    public static (string State, DateTime Since, double? AgeSeconds) Evaluate(
        DateTime now,
        DateTime registeredAt,
        DateTime? lastSnapshotAt,
        DateTime? freshUntil,
        TimeSpan downAfter)
    {
        if (lastSnapshotAt is null)
            return ("down", registeredAt, null);
        var age = Math.Max(0, (now - lastSnapshotAt.Value.ToUniversalTime()).TotalSeconds);
        if (freshUntil is { } fresh && fresh.ToUniversalTime() >= now)
            return ("connected", lastSnapshotAt.Value.ToUniversalTime(), age);
        if (age <= downAfter.TotalSeconds)
            return ("stale", freshUntil?.ToUniversalTime() ?? lastSnapshotAt.Value.ToUniversalTime(), age);
        return ("down", freshUntil?.ToUniversalTime() ?? lastSnapshotAt.Value.ToUniversalTime(), age);
    }
}

public sealed class RemoteRunnerLinkService
{
    public const int DefaultDownAfterMinutes = 5;
    private readonly ClientIdentityStore _clients;
    private readonly V1ReviewExecutorRegistry _registry;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _projectSettings;
    private readonly ITunnelKeeperManager _keeper;
    private readonly IConfiguration _configuration;

    public RemoteRunnerLinkService(
        ClientIdentityStore clients,
        V1ReviewExecutorRegistry registry,
        TaskScannerService scanner,
        ProjectSettingsService projectSettings,
        ITunnelKeeperManager keeper,
        IConfiguration configuration)
    {
        _clients = clients;
        _registry = registry;
        _scanner = scanner;
        _projectSettings = projectSettings;
        _keeper = keeper;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<RemoteRunnerLinkHealth>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var downAfter = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("RemoteRunnerLink:SnapshotDownMinutes")
            ?? DefaultDownAfterMinutes, 1, 60));
        var snapshots = _registry.ListCapabilitySnapshots();
        var snapshotByRunner = snapshots.ToDictionary(item => item.RunnerId, StringComparer.OrdinalIgnoreCase);
        var targets = ReadyTargets();
        var clients = _clients.ListAll().Where(client => IsRunner(client, snapshotByRunner)).ToArray();
        var projected = clients.Select(client =>
        {
            snapshotByRunner.TryGetValue(client.Id, out var snapshot);
            var lastSnapshotAt = snapshot?.LastSeenAt ?? client.LastSeenAt;
            var freshUntil = snapshot?.Capabilities.Count > 0
                ? snapshot.Capabilities.Max(capability => capability.FreshUntil)
                : (DateTime?)null;
            var link = RemoteRunnerLinkPolicy.Evaluate(now, client.RegisteredAt, lastSnapshotAt, freshUntil, downAfter);
            return new RemoteRunnerLinkHealth(
                client.Id, snapshot?.Name ?? client.DisplayName, link.State, lastSnapshotAt,
                link.Since, link.AgeSeconds, targets.Contains(client.Id), null);
        }).ToList();
        foreach (var orphan in snapshots.Where(snapshot => projected.All(item =>
                     !string.Equals(item.RunnerId, snapshot.RunnerId, StringComparison.OrdinalIgnoreCase))))
        {
            var freshUntil = orphan.Capabilities.Count > 0
                ? orphan.Capabilities.Max(capability => capability.FreshUntil)
                : (DateTime?)null;
            var link = RemoteRunnerLinkPolicy.Evaluate(now, orphan.RegisteredAt, orphan.LastSeenAt, freshUntil, downAfter);
            projected.Add(new(orphan.RunnerId, orphan.Name, link.State, orphan.LastSeenAt,
                link.Since, link.AgeSeconds, targets.Contains(orphan.RunnerId), null));
        }
        if (projected.Any(item => item.LinkState == "down" && item.ReadyCardsTargetHost))
        {
            var keeper = await _keeper.ProbeAsync(cancellationToken);
            projected = projected.Select(item => item.LinkState == "down" && item.ReadyCardsTargetHost
                ? item with { Keeper = keeper }
                : item).ToList();
        }
        return projected.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<RemoteRunnerReconnectResponse?> ReconnectAsync(
        string runnerId,
        CancellationToken cancellationToken)
    {
        var before = (await SnapshotAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.RunnerId, runnerId, StringComparison.OrdinalIgnoreCase));
        if (before is null) return null;
        var result = await _keeper.ReconnectAsync(cancellationToken);
        var after = (await SnapshotAsync(cancellationToken)).First(item =>
            string.Equals(item.RunnerId, runnerId, StringComparison.OrdinalIgnoreCase));
        return new(runnerId, result.Succeeded, result.Enabled, result.Started, result.Detail,
            after.LinkState, after.SnapshotAgeSeconds, result.Keeper);
    }

    private HashSet<string> ReadyTargets()
    {
        return _scanner.ScanAllAutomationJobs()
            .Where(task => task.State == TaskStates.Ready && !task.Fixture)
            .Select(task => task.ExecutionLocation?.ConfiguredRunnerId
                            ?? task.ExecutionLocation?.RunnerId
                            ?? ProjectExecutionPolicy.ResolveExecutionLocation(_projectSettings.Get(task.ProjectName)))
            .Where(target => !string.IsNullOrWhiteSpace(target)
                             && !string.Equals(target, ExecutionLocations.Local, StringComparison.OrdinalIgnoreCase))
            .Select(target => target!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRunner(
        ClientIdentity client,
        IReadOnlyDictionary<string, Contract.RunnerCapabilitySnapshotDto> snapshots)
        => snapshots.ContainsKey(client.Id)
           || client.Kind == ClientIdentityKind.Retired
           || client.RunnerDaemonState is not null
           || client.RunnerGitStatus is not null
           || client.Id.Contains("runner", StringComparison.OrdinalIgnoreCase);
}
