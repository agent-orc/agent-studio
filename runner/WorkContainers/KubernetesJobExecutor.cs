using System.Globalization;
using System.Text.Json;

namespace AgentRunner.WorkContainers;

public enum KubernetesJobMode { Off, Shadow, Canary }

public sealed record KubernetesJobSettings(KubernetesJobMode Mode, string Namespace, string Kubeconfig,
    string StateDirectory, int FinishedTtlSeconds = 300)
{
    public const string ModeVariable = "RUNNER_KUBERNETES_JOB_MODE";

    public static KubernetesJobSettings FromEnvironment(Func<string, string?> read, string stateDirectory)
    {
        var mode = (read(ModeVariable) ?? "off").Trim().ToLowerInvariant() switch
        {
            "off" or "" => KubernetesJobMode.Off,
            "shadow" => KubernetesJobMode.Shadow,
            "canary" => KubernetesJobMode.Canary,
            _ => throw new InvalidOperationException($"{ModeVariable} must be off, shadow or canary."),
        };
        return new KubernetesJobSettings(mode, read("RUNNER_KUBERNETES_NAMESPACE") ?? "",
            read("RUNNER_KUBERNETES_KUBECONFIG") ?? "", stateDirectory);
    }

    public void Validate(bool requireCanary = true)
    {
        if (requireCanary && Mode != KubernetesJobMode.Canary)
            throw new InvalidOperationException("Kubernetes Job executor is not in canary mode.");
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Kubernetes Job execution requires a supported Linux cluster profile.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Namespace, "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("A Kubernetes namespace is required.");
        if (string.IsNullOrWhiteSpace(Kubeconfig) || !File.Exists(Kubeconfig))
            throw new InvalidOperationException("An approved cluster kubeconfig is required.");
        if (FinishedTtlSeconds is < 30 or > 86400) throw new InvalidOperationException("Finished Job TTL must be between 30 seconds and one day.");
    }
}

public sealed record KubernetesJobResource(string Name, string Kind, string AttemptId, string LeaseId,
    long Fence, string Phase, int GrantHalfCores, string? IssuanceId = null);

public sealed record KubernetesJobReceipt(string AttemptId, string LeaseId, long Fence, string JobName,
    string Outcome, int? ExitCode, bool CleanupVerified, IReadOnlyList<string> Residue,
    string CredentialRevocationStatus, DateTime FinishedAtUtc);

public static class KubernetesJobReconciliationPolicy
{
    public static bool Keep(KubernetesJobResource? job, WorkAttemptAuthority? authority)
        => job is not null && job.Kind == "Job" && job.Phase is not ("Complete" or "Failed")
            && authority is { Live: true } && authority.AttemptId == job.AttemptId
            && authority.LeaseId == job.LeaseId && authority.Fence == job.Fence;
}

public static class KubernetesJobAdmissionPolicy
{
    public static WorkContainerAdmission Decide(WorkContainerGrant grant, int reservedHalfCores,
        HostMemory memory, WorkContainerSettings settings)
    {
        var verdict = WorkContainerAdmissionPolicy.Decide(grant,
            new WorkContainerHostBudget(reservedHalfCores, memory.AvailableBytes, memory.TotalBytes), settings);
        if (!verdict.Admitted) return verdict;
        if (((decimal)reservedHalfCores + grant.HalfCores) * settings.MemoryBytesPerHalfCore > settings.ProfileMaxMemoryBytes)
            return WorkContainerAdmission.Deny("host-memory-grant-exhausted: live cluster grants exceed the host profile memory ceiling.");
        return verdict;
    }
}

public interface IKubernetesJobRuntime
{
    Task<IReadOnlyList<KubernetesJobResource>> ListAsync(string hostId, CancellationToken ct);
    Task ApplySecretAsync(string name, string token, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task ApplyJobAsync(string manifest, CancellationToken ct);
    Task AttachSecretOwnerAsync(string name, CancellationToken ct);
    Task<KubernetesJobResource?> GetJobAsync(string name, CancellationToken ct);
    Task<int?> GetExitCodeAsync(string name, CancellationToken ct);
    Task<string> LogsAsync(string name, CancellationToken ct);
    Task DeleteJobAsync(string name, CancellationToken ct);
    Task DeletePodAsync(string name, CancellationToken ct);
    Task DeleteSecretAsync(string name, CancellationToken ct);
}

/// <summary>One claimed attempt owns one deterministic Job. A Pod is never an attempt authority.</summary>
public sealed class KubernetesJobExecutor(IKubernetesJobRuntime runtime, KubernetesJobSettings settings,
    WorkContainerSettings budgetSettings, IWorkContainerCredentialSource credentials,
    Func<HostMemory>? readMemory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<HostMemory> _readMemory = readMemory ?? HostMemory.Read;

    public async Task<KubernetesJobReceipt> RunAsync(WorkContainerAttempt attempt, string image,
        WorkContainerGrant grant, CancellationToken ct)
    {
        settings.Validate();
        WorkContainerLaunchPlanner.Validate(attempt, image);
        if (!attempt.RepositorySource.StartsWith("https://github.com/", StringComparison.Ordinal)
            || !attempt.RepositorySource.EndsWith(".git", StringComparison.Ordinal))
            throw new ArgumentException("Kubernetes work needs a canonical GitHub HTTPS source.");
        var name = JobName(attempt);
        var memory = _readMemory();
        var precheck = KubernetesJobAdmissionPolicy.Decide(grant, 0, memory, budgetSettings);
        if (!precheck.Admitted) return Receipt(attempt, name, "denied", null, true, [], "not-issued");

        Directory.CreateDirectory(settings.StateDirectory);
        Directory.CreateDirectory(budgetSettings.StateDirectory);
        WorkCredential? credential = null;
        // The same host grant lock used by the local container executor serializes admissions.
        await using (var held = await HostFileOperationLock.AcquireAsync(
            Path.Combine(budgetSettings.StateDirectory, "grants.lock"), ct))
        {
            // List is a cluster connectivity gate. A failed list cannot be mistaken for an empty cluster.
            var inventory = await runtime.ListAsync(attempt.HostId, ct);
            if (inventory.Any(item => item.Kind == "Job" && item.Phase is not ("Complete" or "Failed")
                && (item.GrantHalfCores <= 0 || item.Fence <= 0 || item.AttemptId.Length == 0 || item.LeaseId.Length == 0)))
                return Receipt(attempt, name, "inventory-untrusted", null, false,
                    ["managed-job-labels-incomplete"], "not-issued");
            if (inventory.Any(item => item.AttemptId == attempt.AttemptId))
                return Receipt(attempt, name, "attempt-already-present", null, false,
                    inventory.Where(item => item.AttemptId == attempt.AttemptId).Select(item => item.Kind + ":" + item.Name).ToArray(), "unknown");
            var reserved = inventory.Where(item => item.Kind == "Job" && item.Phase is not ("Complete" or "Failed"))
                .Sum(item => item.GrantHalfCores);
            var admission = KubernetesJobAdmissionPolicy.Decide(grant, reserved, memory, budgetSettings);
            if (!admission.Admitted) return Receipt(attempt, name, "denied", null, true, [], "not-issued");

            var revocation = "not-issued";
            try
            {
                credential = await credentials.AcquireAsync(attempt, ct);
                PersistIssuance(attempt, name, credential.IssuanceId);
                var labels = KubernetesJobPlan.Labels(attempt, grant.HalfCores, credential.IssuanceId);
                await runtime.ApplySecretAsync(name, credential.Token, labels, ct);
                await runtime.ApplyJobAsync(KubernetesJobPlan.Build(attempt, name, image, admission.Limits!,
                    settings.FinishedTtlSeconds, labels), ct);
                await runtime.AttachSecretOwnerAsync(name, ct);
            }
            catch
            {
                if (credential is not null)
                {
                    try { revocation = await credentials.RevokeAsync(attempt, credential, CancellationToken.None); }
                    catch { revocation = "expiry-backstop"; }
                }
                await CleanupAsync(name, CancellationToken.None);
                throw;
            }
        }
        return await FinishAsync(attempt, name, credential!, ct);
    }

    private async Task<KubernetesJobReceipt> FinishAsync(WorkContainerAttempt attempt, string name,
        WorkCredential credential, CancellationToken ct)
    {
        var outcome = "failed";
        int? exitCode = null;
        var revocation = "not-revoked";
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var job = await runtime.GetJobAsync(name, ct);
                if (job is null) { outcome = "job-lost"; break; }
                if (job.Fence != attempt.Fence || job.LeaseId != attempt.LeaseId)
                { outcome = "fence-superseded"; break; }
                if (job.Phase is "Complete" or "Failed")
                {
                    exitCode = await runtime.GetExitCodeAsync(name, ct);
                    outcome = job.Phase == "Complete" && exitCode == 0 ? "succeeded" : "failed";
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { outcome = "lease-lost"; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        { outcome = "cluster-unavailable"; }
        finally
        {
            if (outcome is "lease-lost" or "fence-superseded")
            {
                try { await runtime.DeleteJobAsync(name, CancellationToken.None); }
                catch { /* The cleanup receipt retains the fenced resource for reconciliation. */ }
            }
            try
            {
                var log = await runtime.LogsAsync(name, CancellationToken.None);
                var safe = log.Replace(credential.Token, "[REDACTED]", StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, name + ".log"), safe, CancellationToken.None);
            }
            catch { /* A cluster outage is captured by the pending cleanup receipt. */ }
        }
        var residue = await CleanupAsync(name, CancellationToken.None);
        try { revocation = await credentials.RevokeAsync(attempt, credential, CancellationToken.None); }
        catch { revocation = "expiry-backstop"; }
        if (residue.Count == 0)
        {
            var recorded = false;
            try
            {
                await credentials.RecordDeletionAsync(attempt, credential, "kubernetes-projection-removed", CancellationToken.None);
                recorded = true;
            }
            catch { /* The local receipt remains for reconciliation. */ }
            if (recorded && revocation != "expiry-backstop") DeleteIssuance(name);
        }
        var receipt = Receipt(attempt, name, outcome, exitCode, residue.Count == 0, residue, revocation);
        await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, name + ".json"),
            JsonSerializer.Serialize(receipt, Json), CancellationToken.None);
        return receipt;
    }

    public async Task<IReadOnlyList<KubernetesJobReceipt>> ReconcileAsync(string hostId,
        IReadOnlyList<WorkAttemptAuthority> authority, CancellationToken ct)
    {
        settings.Validate(requireCanary: false);
        Directory.CreateDirectory(settings.StateDirectory);
        IReadOnlyList<KubernetesJobResource> inventory;
        try { inventory = await runtime.ListAsync(hostId, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            var pending = new List<KubernetesJobReceipt>();
            foreach (var path in Directory.GetFiles(settings.StateDirectory, "*.issuance.json"))
            {
                var persisted = JsonSerializer.Deserialize<PersistedIssuance>(await File.ReadAllTextAsync(path, ct), Json);
                if (persisted is null) continue;
                var name = JobName(persisted.Attempt);
                pending.Add(new(persisted.Attempt.AttemptId, persisted.Attempt.LeaseId, persisted.Attempt.Fence,
                    name, "cluster-unavailable", null, false, ["cluster-unreachable:" + name],
                    "revoke-pending", DateTime.UtcNow));
            }
            await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, "reconcile-latest.json"),
                JsonSerializer.Serialize(pending, Json), ct);
            return pending;
        }
        var live = authority.ToDictionary(item => item.AttemptId, StringComparer.Ordinal);
        var receipts = new List<KubernetesJobReceipt>();
        var processed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in inventory.GroupBy(item => (item.AttemptId, item.Fence)))
        {
            var job = group.FirstOrDefault(item => item.Kind == "Job");
            var resource = job ?? group.First();
            var current = live.GetValueOrDefault(resource.AttemptId);
            if (KubernetesJobReconciliationPolicy.Keep(job, current))
                continue; // Pod replacement remains inside the same Job and attempt.
            var name = job?.Name ?? JobName(resource.AttemptId, resource.Fence);
            processed.Add(name);
            var residue = await CleanupAsync(name, ct);
            var revocation = "not-issued";
            var deletionRecorded = false;
            WorkCredential? recoveredCredential = null;
            WorkContainerAttempt? recoveredAttempt = null;
            var issuancePath = Path.Combine(settings.StateDirectory, name + ".issuance.json");
            if (File.Exists(issuancePath))
            {
                var persisted = JsonSerializer.Deserialize<PersistedIssuance>(await File.ReadAllTextAsync(issuancePath, ct), Json);
                if (persisted is not null)
                {
                    recoveredCredential = new WorkCredential(persisted.IssuanceId, "", DateTime.MinValue);
                    recoveredAttempt = persisted.Attempt;
                    try { revocation = await credentials.RevokeAsync(recoveredAttempt, recoveredCredential, ct); }
                    catch { revocation = "expiry-backstop"; }
                }
            }
            if (residue.Count == 0 && recoveredAttempt is not null && recoveredCredential is not null)
            {
                try { await credentials.RecordDeletionAsync(recoveredAttempt, recoveredCredential,
                    "kubernetes-projection-removed", ct); deletionRecorded = true; }
                catch { }
            }
            if (residue.Count == 0 && revocation is not "expiry-backstop" && deletionRecorded)
                DeleteIssuance(name);
            receipts.Add(new(resource.AttemptId, resource.LeaseId, resource.Fence, name,
                "reconciled", null, residue.Count == 0, residue, revocation, DateTime.UtcNow));
        }
        // A successful TTL pass may have removed the Job before the host returns.
        // The saved issuance still needs revocation and deletion evidence.
        foreach (var path in Directory.GetFiles(settings.StateDirectory, "*.issuance.json"))
        {
            var name = Path.GetFileName(path)[..^".issuance.json".Length];
            if (processed.Contains(name)) continue;
            var persisted = JsonSerializer.Deserialize<PersistedIssuance>(await File.ReadAllTextAsync(path, ct), Json);
            if (persisted is null) continue;
            var current = live.GetValueOrDefault(persisted.Attempt.AttemptId);
            if (current is { Live: true } && current.LeaseId == persisted.Attempt.LeaseId
                && current.Fence == persisted.Attempt.Fence) continue;
            var revocation = "expiry-backstop";
            var deletionRecorded = false;
            var recovered = new WorkCredential(persisted.IssuanceId, "", DateTime.MinValue);
            try { revocation = await credentials.RevokeAsync(persisted.Attempt,
                recovered, ct); }
            catch { }
            var residue = await CleanupAsync(name, ct);
            if (residue.Count == 0)
            {
                try { await credentials.RecordDeletionAsync(persisted.Attempt, recovered,
                    "kubernetes-projection-removed", ct); deletionRecorded = true; }
                catch { }
            }
            if (residue.Count == 0 && revocation != "expiry-backstop" && deletionRecorded)
                DeleteIssuance(name);
            receipts.Add(new(persisted.Attempt.AttemptId, persisted.Attempt.LeaseId, persisted.Attempt.Fence,
                name, "reconciled", null, residue.Count == 0, residue, revocation, DateTime.UtcNow));
        }
        await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, "reconcile-latest.json"),
            JsonSerializer.Serialize(receipts, Json), ct);
        return receipts;
    }

    private async Task<IReadOnlyList<string>> CleanupAsync(string name, CancellationToken ct)
    {
        try { await runtime.DeleteJobAsync(name, ct); } catch { }
        try { await runtime.DeleteSecretAsync(name, ct); } catch { }
        try
        {
            var all = await runtime.ListAsync("", ct);
            foreach (var pod in all.Where(item => item.Kind == "Pod"
                && item.Name.StartsWith(name + "-", StringComparison.Ordinal)))
            {
                try { await runtime.DeletePodAsync(pod.Name, ct); } catch { }
            }
            all = await runtime.ListAsync("", ct);
            return all.Where(item => item.Name == name || item.Name.StartsWith(name + "-", StringComparison.Ordinal))
                .Select(item => item.Kind + ":" + item.Name).ToArray();
        }
        catch { return ["cluster-unreachable:" + name]; }
    }

    private void PersistIssuance(WorkContainerAttempt attempt, string name, string issuanceId)
        => File.WriteAllText(Path.Combine(settings.StateDirectory, name + ".issuance.json"),
            JsonSerializer.Serialize(new PersistedIssuance(attempt, issuanceId), Json));

    private sealed record PersistedIssuance(WorkContainerAttempt Attempt, string IssuanceId);

    private void DeleteIssuance(string name) => File.Delete(Path.Combine(settings.StateDirectory, name + ".issuance.json"));

    private static string JobName(WorkContainerAttempt attempt)
        => JobName(attempt.AttemptId, attempt.Fence);

    private static string JobName(string attemptId, long fence)
    {
        var source = attemptId + "-f" + fence.ToString(CultureInfo.InvariantCulture);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(source)))[..12].ToLowerInvariant();
        return "agent-work-" + digest;
    }

    private static KubernetesJobReceipt Receipt(WorkContainerAttempt attempt, string name, string outcome,
        int? exitCode, bool clean, IReadOnlyList<string> residue, string revocation)
        => new(attempt.AttemptId, attempt.LeaseId, attempt.Fence, name, outcome, exitCode,
            clean, residue, revocation, DateTime.UtcNow);
}

public static class KubernetesJobPlan
{
    public static IReadOnlyDictionary<string, string> Labels(WorkContainerAttempt attempt, int grant, string issuanceId)
        => new Dictionary<string, string>
        {
            [WorkContainerLaunchPlanner.ManagedLabel] = "1",
            [WorkContainerLaunchPlanner.HostLabel] = attempt.HostId,
            [WorkContainerLaunchPlanner.AttemptLabel] = attempt.AttemptId,
            [WorkContainerLaunchPlanner.LeaseLabel] = attempt.LeaseId,
            [WorkContainerLaunchPlanner.FenceLabel] = attempt.Fence.ToString(CultureInfo.InvariantCulture),
            [WorkContainerLaunchPlanner.EpochLabel] = attempt.AuthorityEpoch.ToString(CultureInfo.InvariantCulture),
            [WorkContainerLaunchPlanner.GrantLabel] = grant.ToString(CultureInfo.InvariantCulture),
            ["agent-studio.issuance"] = issuanceId,
        };

    public static string Build(WorkContainerAttempt attempt, string name, string image, WorkContainerLimits limits,
        int ttlSeconds, IReadOnlyDictionary<string, string> labels)
    {
        WorkContainerLaunchPlanner.Validate(attempt, image);
        foreach (var key in (attempt.Environment ?? new Dictionary<string, string>()).Keys)
            WorkContainerLaunchPlanner.ValidateEnvironment(key);
        var env = new Dictionary<string, string>(attempt.Environment ?? new Dictionary<string, string>())
        {
            ["HOME"] = "/work/home", ["AGENT_EXPECTED_SHA"] = attempt.ExactSha,
            ["AGENT_REPOSITORY_SOURCE"] = attempt.RepositorySource,
            ["npm_config_cache"] = "/work/cache/npm", ["NUGET_PACKAGES"] = "/work/cache/nuget",
            ["DOTNET_CLI_HOME"] = "/work/home",
            ["GIT_CONFIG_COUNT"] = "2", ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = "/usr/local/bin/agent-git-credential-helper",
            ["GIT_CONFIG_KEY_1"] = "credential.useHttpPath", ["GIT_CONFIG_VALUE_1"] = "true",
        };
        foreach (var (key, value) in WorkerBuildServerHygiene.Variables) env[key] = value;
        var script = "set -eu; mkdir -p /work/home /work/cache/npm /work/cache/nuget; "
            + "git clone --quiet --no-checkout \"$AGENT_REPOSITORY_SOURCE\" /work/repo; "
            + "git -C /work/repo checkout --quiet --detach \"$AGENT_EXPECTED_SHA\"; "
            + "test \"$(git -C /work/repo rev-parse HEAD)\" = \"$AGENT_EXPECTED_SHA\" || exit 86; "
            + "cd /work/repo; exec \"$@\"";
        var projected = new
        {
            defaultMode = 288,
            sources = new[] { new { secret = new { name, items = new[] { new { key = "github-token", path = "github-token" } } } } },
        };
        var manifest = new
        {
            apiVersion = "batch/v1", kind = "Job",
            metadata = new { name, labels },
            spec = new
            {
                ttlSecondsAfterFinished = ttlSeconds, backoffLimit = 2,
                template = new
                {
                    metadata = new { labels },
                    spec = new
                    {
                        restartPolicy = "Never", automountServiceAccountToken = false,
                        securityContext = new { runAsUser = 10001, runAsGroup = 10001, fsGroup = 10001 },
                        volumes = new object[]
                        {
                            new { name = "work", emptyDir = new { } },
                            new { name = "credentials", projected },
                            new { name = "tmp", emptyDir = new { medium = "Memory", sizeLimit = "1Gi" } },
                        },
                        containers = new[] { new
                        {
                            name = "work", image, imagePullPolicy = "IfNotPresent",
                            command = new[] { "/bin/sh", "-c", script, "agent-work" }, args = attempt.Command,
                            env = env.Select(item => new { name = item.Key, value = item.Value }).ToArray(),
                            resources = new { requests = new Dictionary<string, string> { ["cpu"] = limits.Cpus, ["memory"] = limits.MemoryBytes.ToString(CultureInfo.InvariantCulture) },
                                limits = new Dictionary<string, string> { ["cpu"] = limits.Cpus, ["memory"] = limits.MemoryBytes.ToString(CultureInfo.InvariantCulture) } },
                            securityContext = new { allowPrivilegeEscalation = false, readOnlyRootFilesystem = true,
                                capabilities = new { drop = new[] { "ALL" } } },
                            volumeMounts = new[] { new { name = "work", mountPath = "/work", readOnly = false },
                                new { name = "credentials", mountPath = WorkContainerLaunchPlanner.SecretDirectory, readOnly = true },
                                new { name = "tmp", mountPath = "/tmp", readOnly = false } },
                        } },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(manifest);
    }
}
