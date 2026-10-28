using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentRunner.WorkContainers;

/// <summary>Runtime decorator that counts mutating calls, so a proof can show what did not happen.</summary>
public sealed class CountingWorkContainerRuntime(IWorkContainerRuntime inner) : IWorkContainerRuntime
{
    private int _mutations;
    private int _starts;
    public int Mutations => Volatile.Read(ref _mutations);
    public int Starts => Volatile.Read(ref _starts);

    public Task<bool> IsAvailableAsync(CancellationToken ct) => inner.IsAvailableAsync(ct);
    public Task<bool> ImagePresentAsync(string image, CancellationToken ct) => inner.ImagePresentAsync(image, ct);
    public Task<WorkRuntimeResult> PullAsync(string image, CancellationToken ct) => inner.PullAsync(image, ct);
    public Task<WorkRuntimeResult> CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    { Interlocked.Increment(ref _mutations); return inner.CreateVolumeAsync(name, labels, ct); }
    public Task<WorkRuntimeResult> CreateContainerAsync(IReadOnlyList<string> createArguments, CancellationToken ct)
    { Interlocked.Increment(ref _mutations); return inner.CreateContainerAsync(createArguments, ct); }
    public Task<WorkRuntimeResult> StartAsync(string container, CancellationToken ct)
    { Interlocked.Increment(ref _mutations); Interlocked.Increment(ref _starts); return inner.StartAsync(container, ct); }
    public Task<int?> WaitAsync(string container, CancellationToken ct) => inner.WaitAsync(container, ct);
    public Task<WorkRuntimeResult> LogsAsync(string container, CancellationToken ct) => inner.LogsAsync(container, ct);
    public Task<WorkRuntimeResult> RemoveContainerAsync(string container, CancellationToken ct) => inner.RemoveContainerAsync(container, ct);
    public Task<WorkRuntimeResult> RemoveVolumeAsync(string volume, CancellationToken ct) => inner.RemoveVolumeAsync(volume, ct);
    public Task<ManagedWorkInventory> ListManagedAsync(string hostId, string? attemptId, CancellationToken ct)
        => inner.ListManagedAsync(hostId, attemptId, ct);
}

public sealed record WorkContainerProofOptions(
    string HostId,
    string Image,
    string Source,
    string Sha,
    string RepositoryKey,
    int Attempts,
    int Parallel,
    int GrantHalfCores,
    string ReportDirectory,
    string GateScript,
    bool ImageLayersLocal);

/// <summary>
/// DP-S3 acceptance proof on a real Docker or Podman host: over-budget denial,
/// SHA mismatch before work, restart reconciliation without resurrection, N
/// container attempts with verified cleanup, and the same N attempts through
/// the process executor as the baseline. Writes proof.json and proof.md.
/// </summary>
public static class WorkContainerProof
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record ProcessAttempt(int Index, double GrantToFirstCommandMs, double MaterializeMs, double CommandMs,
        double WallMs, bool ColdRepository, IReadOnlyDictionary<string, string> Cache, int ExitCode, string Bound);

    public static async Task<int> RunAsync(WorkContainerProofOptions options, WorkContainerSettings settings,
        TextWriter output, CancellationToken ct)
    {
        Directory.CreateDirectory(options.ReportDirectory);
        var counting = new CountingWorkContainerRuntime(new CliWorkContainerRuntime(settings.Runtime));
        var executor = new WorkContainerExecutor(counting, settings, log: output.WriteLine);
        var command = (IReadOnlyList<string>)["/bin/sh", "-c", options.GateScript];
        // Pre-checks use their own mirror key so the measured attempts start with a cold repository cache.
        WorkContainerAttempt Attempt(string id, long fence, string? sha = null, IReadOnlyList<string>? work = null) => new(
            options.HostId, "gate", id, $"lease-{id}", fence, 1,
            id.StartsWith("proof-", StringComparison.Ordinal) ? options.RepositoryKey : options.RepositoryKey + "-prechecks",
            options.Source, sha ?? options.Sha, work ?? command);
        var grant = new WorkContainerGrant(1, 1, options.GrantHalfCores);
        var notes = new List<string>();

        // 1. Image pull, cold for this digest on this host.
        double? pullMs = null;
        if (!await counting.ImagePresentAsync(options.Image, ct))
        {
            var watch = Stopwatch.StartNew();
            var pulled = await counting.PullAsync(options.Image, ct);
            pullMs = watch.Elapsed.TotalMilliseconds;
            if (!pulled.Ok) throw new InvalidOperationException($"Image pull failed: {pulled.Output}");
        }
        else notes.Add("image already present before the proof; pull time not measured.");

        // 2. Over-budget launches: above the profile ceiling, stale allocation,
        //    and a grant the live reservations no longer leave room for.
        var overBudget = 0;
        var deniedBeforeRuntime = 0;
        async Task Deny(WorkContainerAttempt attempt, WorkContainerGrant denied)
        {
            overBudget++;
            var before = counting.Mutations;
            var receipt = await executor.RunAsync(attempt, options.Image, denied, ct);
            var inventory = await counting.ListManagedAsync(options.HostId, attempt.AttemptId, ct);
            if (receipt.Outcome == "denied" && counting.Mutations == before
                && inventory.Containers.Count == 0 && inventory.Volumes.Count == 0)
                deniedBeforeRuntime++;
            else notes.Add($"over-budget attempt {attempt.AttemptId} was not denied cleanly: {receipt.Outcome} {receipt.Reason}");
        }
        await Deny(Attempt("over-profile", 1), grant with { HalfCores = settings.ProfileMaxHalfCores + 1 });
        await Deny(Attempt("stale-allocation", 1), new WorkContainerGrant(2, 1, options.GrantHalfCores));
        using (var holder = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var holding = executor.RunAsync(Attempt("grant-holder", 1, work: ["/bin/sh", "-c", "sleep 600"]),
                options.Image, grant with { HalfCores = settings.ProfileMaxHalfCores - 1 }, holder.Token);
            await WaitForAsync(async () => holding.IsCompleted || (await counting.ListManagedAsync(options.HostId, "grant-holder", ct))
                .Containers.Any(item => item.State == "running"), ct);
            if (holding.IsCompleted) notes.Add($"grant holder did not start: {(await holding).Reason}");
            await Deny(Attempt("host-grant-exhausted", 1), grant with { HalfCores = 2 });
            holder.Cancel();
            var held = await holding;
            if (!held.CleanupVerified) notes.Add("grant holder cleanup was not verified.");
        }

        // 3. Exact-SHA failure stops before the work command.
        var mismatch = await executor.RunAsync(Attempt("sha-mismatch", 1, sha: new string('0', 40)), options.Image, grant, ct);
        var mismatchLog = mismatch.LogPath is { } path && File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        var shaGuard = mismatch.Outcome == "sha-mismatch" && !mismatchLog.Contains("AGENT_PHASE first-command", StringComparison.Ordinal)
            && mismatch.CleanupVerified;

        // 4. Restart reconciliation: build the state a crashed host leaves behind.
        var resurrections = await ProveReconciliationAsync(counting, executor, settings, options, Attempt, notes, ct);

        // 5. N container attempts with verified cleanup.
        var receipts = new ConcurrentBag<(int Index, WorkContainerReceipt Receipt)>();
        await Parallel.ForEachAsync(Enumerable.Range(1, options.Attempts),
            new ParallelOptions { MaxDegreeOfParallelism = options.Parallel, CancellationToken = ct },
            async (index, token) => receipts.Add((index, await executor.RunAsync(
                Attempt($"proof-{index:D3}", 1), options.Image, grant, token))));
        var ordered = receipts.OrderBy(item => item.Index).Select(item => item.Receipt).ToArray();
        var finalInventory = await counting.ListManagedAsync(options.HostId, null, ct);
        var residue = finalInventory.Containers.Count + finalInventory.Volumes.Count + SecretMountsOnHost();

        // 6. Process baseline with the same gate, cache layout and parallelism.
        var baseline = await RunProcessBaselineAsync(options, ct);

        var containerMeasurement = Measure(ordered.Select(r => (r.Timings.GrantToFirstCommandMs, r.Timings.MaterializeMs,
            r.Timings.CommandMs, (double?)r.Timings.WallMs, r.Cache)).ToArray(), options.Parallel);
        var processMeasurement = Measure(baseline.Select(b => ((double?)b.GrantToFirstCommandMs, (double?)b.MaterializeMs,
            (double?)b.CommandMs, (double?)b.WallMs, b.Cache)).ToArray(), options.Parallel);
        if (options.ImageLayersLocal)
            notes.Add("role image layers were already in this host's content store, so the measured pull is not a cold pull; record a cold pull on a fresh canary host.");
        var evidence = new RolloutEvidence(containerMeasurement, processMeasurement, pullMs, pullMs is not null && !options.ImageLayersLocal, ordered.Length,
            ordered.Count(r => r.CleanupVerified && r.Outcome == "succeeded"), residue, overBudget, deniedBeforeRuntime, resurrections);
        var verdict = WorkContainerRolloutThreshold.Evaluate(evidence);
        var report = new
        {
            options.HostId, options.Image, options.Sha, options.Attempts, options.Parallel, options.GrantHalfCores,
            Settings = new { settings.Runtime, settings.ProfileMaxHalfCores, ProfileMaxMemoryMiB = settings.ProfileMaxMemoryBytes >> 20, settings.CacheVersion, settings.Network },
            ShaGuard = new { Passed = shaGuard, mismatch.Outcome, mismatch.ExitCode },
            Evidence = evidence,
            Verdict = verdict,
            ContainerOutcomes = ordered.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            ProcessBound = baseline.FirstOrDefault()?.Bound,
            Notes = notes,
            Containers = ordered,
            Process = baseline,
        };
        await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, "proof.json"), JsonSerializer.Serialize(report, Json), ct);
        await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, "proof.md"),
            Markdown(options, evidence, verdict, shaGuard, baseline.FirstOrDefault()?.Bound, notes), ct);
        output.WriteLine($"work-container proof go={verdict.Go} attempts={ordered.Length} residue={residue} shaGuard={shaGuard}");
        // Acceptance gates the exit code; the performance threshold is a recorded verdict.
        var accepted = evidence.CleanupVerified == evidence.Attempts && residue == 0 && resurrections == 0
                       && deniedBeforeRuntime == overBudget && shaGuard;
        return accepted ? 0 : 1;
    }

    private static async Task<int> ProveReconciliationAsync(
        CountingWorkContainerRuntime runtime, WorkContainerExecutor executor, WorkContainerSettings settings,
        WorkContainerProofOptions options, Func<string, long, string?, IReadOnlyList<string>?, WorkContainerAttempt> attempt,
        List<string> notes, CancellationToken ct)
    {
        var limits = WorkContainerAdmissionPolicy.Decide(new WorkContainerGrant(1, 1, 1),
            new WorkContainerHostBudget(0, long.MaxValue / 2, 0), settings).Limits!;
        async Task<string> Leave(WorkContainerAttempt item, bool start)
        {
            var plan = WorkContainerLaunchPlanner.Plan(item, options.Image, limits, settings);
            Require(await runtime.CreateVolumeAsync(plan.AttemptVolume, plan.Labels, ct));
            Require(await runtime.CreateContainerAsync(plan.CreateArguments, ct));
            if (start) Require(await runtime.StartAsync(plan.ContainerName, ct));
            return plan.ContainerName;
        }
        var sleep = (IReadOnlyList<string>)["/bin/sh", "-c", "sleep 600"];
        var fenced = await Leave(attempt("restart-fenced", 3, null, sleep), true);
        var exited = await Leave(attempt("restart-exited", 1, null, ["/bin/sh", "-c", "exit 0"]), true);
        var live = await Leave(attempt("restart-live", 1, null, sleep), true);
        await WaitForAsync(async () => (await runtime.ListManagedAsync(options.HostId, "restart-exited", ct)).Containers
            .All(item => item.State == "exited"), ct);

        var startsBefore = runtime.Starts;
        // After restart authority says: fenced attempt moved to fence 4, exited
        // attempt is still live at fence 1, live attempt is current.
        var actions = await executor.ReconcileAsync(options.HostId,
        [
            new WorkAttemptAuthority("restart-fenced", "lease-restart-fenced", 4, true),
            new WorkAttemptAuthority("restart-exited", "lease-restart-exited", 1, true),
            new WorkAttemptAuthority("restart-live", "lease-restart-live", 1, true),
        ], ct);
        var after = await runtime.ListManagedAsync(options.HostId, null, ct);
        var resurrections = runtime.Starts - startsBefore;
        resurrections += after.Containers.Count(item => item.Resource.Name == fenced || item.Resource.Name == exited);
        if (!after.Containers.Any(item => item.Resource.Name == live && item.State == "running"))
            notes.Add("reconciliation removed the live current-fence container.");
        // The live attempt then loses its lease: a second pass removes it too.
        await executor.ReconcileAsync(options.HostId, [], ct);
        var cleared = await runtime.ListManagedAsync(options.HostId, null, ct);
        resurrections += cleared.Containers.Count + cleared.Volumes.Count;
        await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, "reconcile.json"), JsonSerializer.Serialize(actions, Json), ct);
        return resurrections;
    }

    private static async Task<IReadOnlyList<ProcessAttempt>> RunProcessBaselineAsync(WorkContainerProofOptions options, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), $"work-process-baseline-{Guid.NewGuid():N}");
        var cache = Path.Combine(root, "cache");
        foreach (var name in new[] { "npm", "nuget", "repo" }) Directory.CreateDirectory(Path.Combine(cache, name));
        var mirror = Path.Combine(cache, "repo", options.RepositoryKey + ".git");
        var mirrorGate = new SemaphoreSlim(1, 1);
        var results = new ConcurrentBag<ProcessAttempt>();
        var limits = new WorkContainerLimits(options.GrantHalfCores, 0, WorkerResourceEnvelope.MinimumTasksMax);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(1, options.Attempts),
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallel, CancellationToken = ct },
                async (index, token) =>
                {
                    var granted = Stopwatch.StartNew();
                    var attempt = Path.Combine(root, $"attempt-{index:D3}");
                    var repo = Path.Combine(attempt, "repo");
                    Directory.CreateDirectory(Path.Combine(attempt, "home"));
                    bool cold;
                    await mirrorGate.WaitAsync(token);
                    try
                    {
                        cold = !(Directory.Exists(mirror)
                                 && (await Git(null, ["-C", mirror, "cat-file", "-e", options.Sha + "^{commit}"], token)).Success);
                        if (cold) Check(await Git(null, ["clone", "--quiet", "--mirror", options.Source, mirror], token));
                    }
                    finally { mirrorGate.Release(); }
                    Check(await Git(null, ["clone", "--quiet", "--shared", "--no-checkout", mirror, repo], token));
                    Check(await Git(null, ["-C", repo, "-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", options.Sha], token));
                    var head = (await Git(null, ["-C", repo, "rev-parse", "HEAD"], token)).StdOut.Trim();
                    if (head != options.Sha) throw new InvalidOperationException($"baseline HEAD {head} is not {options.Sha}");
                    var cacheState = new Dictionary<string, string>(StringComparer.Ordinal) { ["repo"] = cold ? "miss" : "hit" };
                    foreach (var name in new[] { "npm", "nuget" })
                        cacheState[name] = File.Exists(Path.Combine(cache, name, ".agent-cache")) ? "hit" : "miss";
                    var materialize = granted.Elapsed.TotalMilliseconds;
                    var firstCommand = granted.Elapsed.TotalMilliseconds;
                    var run = await WorkProcessExecutor.RunAsync(repo, "/bin/sh", ["-c", options.GateScript], limits,
                        new Dictionary<string, string?>
                        {
                            ["HOME"] = Path.Combine(attempt, "home"),
                            ["npm_config_cache"] = Path.Combine(cache, "npm"),
                            ["NUGET_PACKAGES"] = Path.Combine(cache, "nuget"),
                        }, token);
                    if (run.ExitCode == 0)
                        foreach (var name in new[] { "npm", "nuget" })
                            if (Directory.EnumerateFileSystemEntries(Path.Combine(cache, name)).Any())
                                await File.WriteAllTextAsync(Path.Combine(cache, name, ".agent-cache"), "", token);
                    Directory.Delete(attempt, true);
                    results.Add(new ProcessAttempt(index, firstCommand, materialize, run.WallMs, granted.Elapsed.TotalMilliseconds,
                        cold, cacheState, run.ExitCode, run.Bound));
                });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return results.OrderBy(item => item.Index).ToArray();
    }

    private static ExecutorMeasurement Measure(
        IReadOnlyList<(double? FirstCommand, double? Materialize, double? Command, double? Wall, IReadOnlyDictionary<string, string> Cache)> runs,
        int parallel)
    {
        bool Cold(IReadOnlyDictionary<string, string> cache) => cache.GetValueOrDefault("repo") is "miss" or "quarantined";
        // The first wave of parallel attempts races the cold caches; warm statistics start after it.
        var warm = runs.Skip(parallel).Where(run => !Cold(run.Cache)).ToArray();
        var lookups = warm.SelectMany(run => run.Cache.Values).ToArray();
        return new ExecutorMeasurement(
            LatencySummary.Of(warm.Select(run => run.FirstCommand)),
            LatencySummary.Of(runs.Where(run => Cold(run.Cache)).Select(run => run.Materialize)),
            LatencySummary.Of(runs.Select(run => run.Command)),
            LatencySummary.Of(runs.Select(run => run.Wall)),
            lookups.Length == 0 ? 0 : lookups.Count(value => value is "hit" or "refresh") / (double)lookups.Length);
    }

    private static int SecretMountsOnHost()
        => File.Exists("/proc/mounts")
            ? File.ReadLines("/proc/mounts").Count(line => line.Contains(WorkContainerLaunchPlanner.SecretDirectory, StringComparison.Ordinal)
                                                          || line.Contains("agent-work-", StringComparison.Ordinal))
            : 0;

    private static Task<ProcessResult> Git(string? directory, IReadOnlyList<string> arguments, CancellationToken ct)
        => ProcessRunner.RunAsync("git", arguments, directory, ct: ct);

    private static void Check(ProcessResult result)
    {
        if (!result.Success) throw new InvalidOperationException($"git exited {result.ExitCode}: {result.StdErr.Trim()}");
    }

    private static void Require(WorkRuntimeResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Output);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Proof condition did not hold within two minutes.");
            await Task.Delay(200, ct);
        }
    }

    private static string Markdown(WorkContainerProofOptions options, RolloutEvidence evidence, RolloutVerdict verdict,
        bool shaGuard, string? processBound, IReadOnlyList<string> notes)
    {
        string F(double? value) => value is { } ms ? ms.ToString("0", CultureInfo.InvariantCulture) : "n/a";
        var text = new StringBuilder();
        text.AppendLine("# DP-S3 work container proof");
        text.AppendLine();
        text.AppendLine($"Host `{options.HostId}`, image `{options.Image}`, exact SHA `{options.Sha}`.");
        text.AppendLine($"{options.Attempts} attempts per executor, parallelism {options.Parallel}, grant {options.GrantHalfCores} half-cores.");
        text.AppendLine($"Process baseline bound: `{processBound ?? "n/a"}`.");
        text.AppendLine();
        text.AppendLine("| Measure (ms) | Container p50 | Container p90 | Process p50 | Process p90 |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        void Row(string name, LatencySummary c, LatencySummary p)
            => text.AppendLine($"| {name} (n={c.Count}/{p.Count}) | {F(c.P50)} | {F(c.P90)} | {F(p.P50)} | {F(p.P90)} |");
        Row("Warm grant to first command", evidence.Container.GrantToFirstCommandWarm, evidence.Process.GrantToFirstCommandWarm);
        Row("Cold repository materialization", evidence.Container.ColdMaterialization, evidence.Process.ColdMaterialization);
        Row("Gate wall (work command)", evidence.Container.GateWall, evidence.Process.GateWall);
        Row("Attempt wall, grant to verified cleanup (informational)", evidence.Container.AttemptWall, evidence.Process.AttemptWall);
        text.AppendLine();
        text.AppendLine($"Image pull: {F(evidence.ImagePullMs)} ms. Warm cache hit rate: container {evidence.Container.WarmCacheHitRate:P1}, process {evidence.Process.WarmCacheHitRate:P1}.");
        text.AppendLine($"Exact-SHA guard stops before the work command: {(shaGuard ? "yes" : "NO")}.");
        text.AppendLine();
        text.AppendLine($"## Go/no-go: {(verdict.Go ? "GO" : "NO-GO")}");
        text.AppendLine();
        text.AppendLine("| Check | Threshold | Measured | Result |");
        text.AppendLine("|---|---|---|---|");
        foreach (var check in verdict.Checks)
            text.AppendLine($"| {check.Name} | {check.Threshold} | {check.Measured} | {(check.Passed ? "pass" : "FAIL")} |");
        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Notes");
            foreach (var note in notes) text.AppendLine($"- {note}");
        }
        return text.ToString();
    }
}
