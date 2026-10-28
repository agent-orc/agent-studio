using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentRunner;

public sealed record HostAgentRoleConfiguration(
    string EnvironmentFile,
    string HomeDirectory,
    IReadOnlyList<string>? AdditionalEnvironmentFiles = null);
public sealed record HostAgentConfiguration(
    string StateDirectory,
    HostAgentRoleConfiguration Coding,
    HostAgentRoleConfiguration Review);
public sealed record HostAgentRoleState(
    bool Enabled,
    string RunnerId,
    string StateDirectory,
    string? LegacyDrainId,
    int? LegacyProcessId,
    DateTime ChangedAtUtc);
public sealed record HostAgentState(Dictionary<string, HostAgentRoleState> Roles);
public sealed record HostAttemptInventoryEntry(
    string Role,
    string AttemptId,
    string LeaseId,
    long Fence,
    long AuthorityEpoch,
    string Phase,
    int? ProcessId);
public sealed record HostAttemptInventory(DateTime ObservedAtUtc, IReadOnlyList<HostAttemptInventoryEntry> Attempts);

/// <summary>
/// One OS-supervised host service owns the coding and review adapters. Each
/// adapter runs in a child process to preserve its own CLI home and existing
/// Task Server registration, renewal, report, and crash recovery semantics.
/// The host inventory is a projection of those durable role records, never a
/// second lease store.
/// </summary>
public static class HostAgentSupervisor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Roles = ["coding", "review"];
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendUnixSignal(int pid, int signal);

    public static async Task<int> ExecuteAsync(string[] args, Action<string> log, CancellationToken ct)
    {
        if (args.Length < 2 || args[0] is not ("run" or "migrate-role" or "drain-role" or "enable-role" or "rollback-role" or "inventory"))
            throw new ArgumentException("Usage: agent-host host-agent <run|migrate-role|drain-role|enable-role|rollback-role|inventory> <config.json> [coding|review]");
        var operation = args[0];
        var config = JsonSerializer.Deserialize<HostAgentConfiguration>(File.ReadAllText(args[1]), Json)
                     ?? throw new InvalidDataException("Host agent configuration is empty.");
        var role = args.Length > 2 ? args[2] : null;
        if (operation is not ("run" or "inventory") && !Roles.Contains(role, StringComparer.Ordinal))
            throw new ArgumentException("A coding or review role is required.");
        Directory.CreateDirectory(config.StateDirectory);
        var options = Roles.ToDictionary(item => item, item => ParseRole(config, item), StringComparer.Ordinal);
        if (options["coding"].RunnerId == options["review"].RunnerId
            || Path.GetFullPath(options["coding"].StateDir) == Path.GetFullPath(options["review"].StateDir))
            throw new InvalidOperationException("Coding and review must have separate runner identities and state directories.");
        if (Path.GetFullPath(config.Coding.HomeDirectory) == Path.GetFullPath(config.Review.HomeDirectory))
            throw new InvalidOperationException("Coding and review must have separate provider CLI homes.");
        if (!string.Equals(options["coding"].Hostname, options["review"].Hostname, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(options["coding"].ServerUrl, options["review"].ServerUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host adapters must use the same physical host and Task Server.");

        if (operation == "inventory")
        {
            log(JsonSerializer.Serialize(CaptureInventory(options), Json));
            return 0;
        }
        if (operation == "run") return await RunAsync(config, options, log, ct);
        using var roleControlLock = new FileStream(
            Path.Combine(config.StateDirectory, $"{role}.control.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var chosen = options[role!];
        if (operation == "migrate-role")
            return await MigrateRoleAsync(config, role!, chosen, log, ct);
        if (operation == "drain-role" || operation == "rollback-role")
        {
            var drained = await DrainRoleAsync(config, role!, chosen, log, ct);
            if (drained != 0 || operation == "drain-role") return drained;
            // Only after the host adapter is gone may the legacy service claim again.
            if (role == "review") ReviewDrainGuard.ClearDrainState(chosen.StateDir);
            else CodingDrainGuard.Clear(chosen.StateDir);
            log($"role={role} rollback ready; start the old role service explicitly");
            return 0;
        }
        return EnableRole(config, role!, chosen, log);
    }

    private static RunnerOptions ParseRole(HostAgentConfiguration config, string role)
    {
        var values = ReadRoleEnvironment(config, role);
        values["RUNNER_ROLE"] = role;
        var parsed = RunnerOptions.Parse(["--poll"], name => values.GetValueOrDefault(name)).Options;
        if (parsed.Role != role) throw new InvalidOperationException($"Role configuration mismatch: {role}");
        return parsed;
    }

    private static Dictionary<string, string> ReadRoleEnvironment(HostAgentConfiguration config, string role)
    {
        var roleConfig = role == "coding" ? config.Coding : config.Review;
        if (!Path.IsPathFullyQualified(roleConfig.HomeDirectory))
            throw new InvalidDataException($"{role} home directory must be absolute.");
        var values = ReadEnvironmentFile(roleConfig.EnvironmentFile);
        foreach (var file in roleConfig.AdditionalEnvironmentFiles ?? [])
            foreach (var (key, value) in ReadEnvironmentFile(file)) values[key] = value;
        values["HOME"] = roleConfig.HomeDirectory;
        values["CODEX_HOME"] = Path.Combine(roleConfig.HomeDirectory, ".codex");
        values["CLAUDE_CONFIG_DIR"] = Path.Combine(roleConfig.HomeDirectory, ".claude");
        return values;
    }

    internal static Dictionary<string, string> ReadEnvironmentFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var equal = line.IndexOf('=');
            if (equal <= 0) throw new InvalidDataException($"Invalid environment assignment in {path}.");
            var key = line[..equal].Trim();
            if (key.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_')))
                throw new InvalidDataException($"Invalid environment name in {path}.");
            var value = line[(equal + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
                value = value[1..^1];
            values[key] = value;
        }
        return values;
    }

    private static async Task<int> RunAsync(
        HostAgentConfiguration config, Dictionary<string, RunnerOptions> options,
        Action<string> log, CancellationToken ct)
    {
        using var singleton = new FileStream(Path.Combine(config.StateDirectory, "host-agent.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var children = new Dictionary<string, Process>(StringComparer.Ordinal);
        var retryAfter = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var releaseHeld = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var state = ReadState(config);
                foreach (var role in Roles)
                {
                    var enabled = state.Roles.GetValueOrDefault(role);
                    if (enabled is null || !enabled.Enabled) continue;
                    ValidateRoleState(role, enabled, options[role]);
                    if (HostRoleDrainControl.ReadRequest(options[role].StateDir, role) is not null
                        || releaseHeld.Contains(role)
                        || retryAfter.GetValueOrDefault(role) > DateTime.UtcNow)
                        continue;
                    if (children.TryGetValue(role, out var existing) && !existing.HasExited) continue;
                    if (existing is not null)
                    {
                        log($"role={role} adapter exited code={existing.ExitCode}; reconciling durable attempts before restart");
                        if (existing.ExitCode == 4)
                        {
                            releaseHeld.Add(role);
                            log($"role={role} release or Task Server compatibility held until host-agent restart");
                        }
                        existing.Dispose();
                        children.Remove(role);
                        retryAfter[role] = DateTime.UtcNow.AddSeconds(5);
                        continue;
                    }
                    // A malformed or unreadable slot aborts startup. The role
                    // daemon then re-registers and reconciles each exact lease.
                    _ = CaptureRoleInventory(role, options[role]);
                    children[role] = StartAdapter(config, role);
                    log($"role={role} adapter started pid={children[role].Id}");
                }
                WriteAtomically(InventoryPath(config), CaptureInventory(options));
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            foreach (var (role, child) in children)
            {
                if (!child.HasExited)
                {
                    log($"role={role} host stopping; adapter will flush durable state");
                    try
                    {
                        if (OperatingSystem.IsWindows()) child.CloseMainWindow();
                        else _ = SendUnixSignal(child.Id, 15);
                    }
                    catch (InvalidOperationException) { }
                    try { await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (TimeoutException)
                    {
                        log($"role={role} adapter stop timed out; durable recovery remains required");
                        try { child.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
                    }
                }
                child.Dispose();
            }
            WriteAtomically(InventoryPath(config), CaptureInventory(options));
        }
        return 0;
    }

    private static Process StartAdapter(HostAgentConfiguration config, string role)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Host executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
            start.ArgumentList.Add(typeof(HostAgentSupervisor).Assembly.Location);
        start.ArgumentList.Add("--poll");
        start.ArgumentList.Add("--role");
        start.ArgumentList.Add(role);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RUNNER_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        foreach (var (key, value) in ReadRoleEnvironment(config, role)) start.Environment[key] = value;
        start.Environment["RUNNER_ROLE"] = role;
        start.Environment["RUNNER_HOST_ADAPTER"] = "1";
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {role} adapter.");
    }

    private static async Task<int> MigrateRoleAsync(
        HostAgentConfiguration config, string role, RunnerOptions options,
        Action<string> log, CancellationToken ct)
    {
        if (ReadState(config).Roles.TryGetValue(role, out var current) && current.Enabled)
            throw new InvalidOperationException($"{role} is already enabled in the host agent.");
        var requestId = role == "review"
            ? (ReviewDrainGuard.ReadDrainRequest(options.StateDir)
               ?? ReviewDrainGuard.RequestDrain(options.StateDir, "host agent migration")).RequestId
            : CodingDrainGuard.RequestDrain(options.StateDir).Id;
        log($"role={role} old daemon drain requested id={requestId}");
        var deadline = DateTime.UtcNow.AddSeconds(options.DrainTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (id, pid, active) = LegacyAcknowledgement(role, options.StateDir);
            if (HostAgentMigrationPolicy.CanEnable(requestId, id, active, pid,
                    pid is > 0 && ProcessAlive(pid.Value), CaptureRoleInventory(role, options).Count))
            {
                HostRoleDrainControl.Clear(options.StateDir, role);
                UpdateState(config, state => state.Roles[role] = new HostAgentRoleState(
                    true, options.RunnerId, options.StateDir, requestId, pid, DateTime.UtcNow));
                log($"role={role} old daemon exited with zero attempts; host adapter enabled");
                return 0;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        log($"role={role} migration held: old admission or attempt reconciliation did not reach zero; drain marker remains");
        return 3;
    }

    private static async Task<int> DrainRoleAsync(
        HostAgentConfiguration config, string role, RunnerOptions options,
        Action<string> log, CancellationToken ct)
    {
        var state = ReadState(config);
        if (!state.Roles.TryGetValue(role, out var enabled) || !enabled.Enabled) return 0;
        ValidateRoleState(role, enabled, options);
        var request = HostRoleDrainControl.RequestDrain(options.StateDir, role);
        var deadline = DateTime.UtcNow.AddSeconds(options.DrainTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var acknowledgement = HostRoleDrainControl.ReadAcknowledgement(options.StateDir, role);
            if (acknowledgement?.Id == request.Id && acknowledgement.ActiveSlots == 0
                && !ProcessAlive(acknowledgement.ProcessId)
                && CaptureRoleInventory(role, options).Count == 0)
            {
                UpdateState(config, current => current.Roles[role] = enabled with
                {
                    Enabled = false, ChangedAtUtc = DateTime.UtcNow,
                });
                log($"role={role} host adapter drained with zero attempts; role disabled");
                return 0;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        log($"role={role} drain held: adapter or persisted attempts remain; claim barrier remains");
        return 3;
    }

    private static int EnableRole(HostAgentConfiguration config, string role, RunnerOptions options, Action<string> log)
    {
        var state = ReadState(config);
        if (!state.Roles.TryGetValue(role, out var disabled))
            throw new InvalidOperationException($"{role} has not completed old-daemon migration.");
        ValidateRoleState(role, disabled, options);
        if (CaptureRoleInventory(role, options).Count != 0)
            throw new InvalidOperationException($"{role} still has persisted attempts; reconcile before enabling.");
        HostRoleDrainControl.Clear(options.StateDir, role);
        UpdateState(config, current => current.Roles[role] = disabled with
        {
            Enabled = true, ChangedAtUtc = DateTime.UtcNow,
        });
        log($"role={role} host adapter enabled");
        return 0;
    }

    private static (string? Id, int? Pid, int Active) LegacyAcknowledgement(string role, string stateDir)
    {
        if (role == "review")
        {
            var ack = ReviewDrainGuard.ReadDrainAcknowledgement(stateDir);
            return (ack?.RequestId, ack?.DaemonProcessId, ack?.ActiveSlots ?? -1);
        }
        var coding = CodingDrainGuard.ReadAcknowledgement(stateDir);
        return (coding?.Id, coding?.ProcessId, coding?.ActiveSlots ?? -1);
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static HostAttemptInventory CaptureInventory(Dictionary<string, RunnerOptions> options)
        => new(DateTime.UtcNow, Roles.SelectMany(role => CaptureRoleInventory(role, options[role])).ToArray());

    private static IReadOnlyList<HostAttemptInventoryEntry> CaptureRoleInventory(string role, RunnerOptions options)
    {
        if (role == "coding")
        {
            var slots = new RunnerStateStore(options.StateDir).LoadAll()
                .Select(slot => new HostAttemptInventoryEntry(role, slot.AttemptId,
                    slot.Lease.LeaseId, slot.Lease.FencingToken, slot.Lease.AuthorityEpoch,
                    slot.Phase, slot.ProcessId)).ToArray();
            var activeIds = slots.Select(slot => slot.AttemptId).ToHashSet(StringComparer.Ordinal);
            var accepted = new HostOrchestratorJournal(
                    Path.Combine(options.StateDir, "host-orchestrator.json"))
                .RecoverAcceptedWork()
                .Where(work => !activeIds.Contains(work.Run.RunId))
                .Select(work => new HostAttemptInventoryEntry(role, work.Run.RunId,
                    work.Lease.LeaseId, work.Lease.Fence, 0, "accepted-permit", null));
            return slots.Concat(accepted).ToArray();
        }
        return new ReviewStateStore(options.StateDir).LoadAll()
            .Select(slot => new HostAttemptInventoryEntry(role, slot.AttemptId,
                slot.Claim.Lease!.LeaseId, slot.Claim.Lease.Fence, slot.Claim.Lease.AuthorityEpoch,
                slot.Phase, slot.ProcessId)).ToArray();
    }

    private static void ValidateRoleState(string role, HostAgentRoleState state, RunnerOptions options)
    {
        if (state.RunnerId != options.RunnerId || Path.GetFullPath(state.StateDirectory) != Path.GetFullPath(options.StateDir))
            throw new InvalidOperationException($"{role} identity or state path changed after migration; role held.");
        var (id, _, _) = LegacyAcknowledgement(role, options.StateDir);
        if (id != state.LegacyDrainId)
            throw new InvalidOperationException($"{role} old-daemon drain barrier changed; role held.");
    }

    private static string StatePath(HostAgentConfiguration config) => Path.Combine(config.StateDirectory, "roles.json");
    private static string InventoryPath(HostAgentConfiguration config) => Path.Combine(config.StateDirectory, "attempt-inventory.json");

    private static HostAgentState ReadState(HostAgentConfiguration config)
        => File.Exists(StatePath(config))
            ? JsonSerializer.Deserialize<HostAgentState>(File.ReadAllText(StatePath(config)), Json)
              ?? throw new InvalidDataException("Host agent role state is empty.")
            : new HostAgentState(new Dictionary<string, HostAgentRoleState>(StringComparer.Ordinal));

    private static void UpdateState(HostAgentConfiguration config, Action<HostAgentState> update)
    {
        using var control = new FileStream(Path.Combine(config.StateDirectory, "roles.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var state = ReadState(config);
        update(state);
        WriteAtomically(StatePath(config), state);
    }

    private static void WriteAtomically<T>(string path, T value)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}

/// <summary>Pure migration gate for the old daemon to host adapter handoff.</summary>
public static class HostAgentMigrationPolicy
{
    public static bool CanEnable(string requestId, string? acknowledgementId,
        int acknowledgedActive, int? oldProcessId, bool oldProcessAlive, int persistedAttempts)
        => requestId == acknowledgementId
           && acknowledgedActive == 0
           && oldProcessId is > 0
           && !oldProcessAlive
           && persistedAttempts == 0;
}
