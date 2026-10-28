using System.Text.Json;

namespace AgentRunner.WorkContainers;

/// <summary>Namespace-scoped kubectl transport. Secret contents travel on stdin only.</summary>
public sealed class KubectlJobRuntime(string kubeconfig, string ns) : IKubernetesJobRuntime
{
    private async Task<string> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var command = new List<string> { "--kubeconfig", kubeconfig, "--namespace", ns };
        command.AddRange(args);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(args[0] == "delete" ? TimeSpan.FromSeconds(75) : TimeSpan.FromSeconds(20));
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync("kubectl", command, stdin: stdin,
                clearEnvironment: true, ct: bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("Kubernetes command timed out."); }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException("kubectl is unavailable on this host.", ex); }
        if (!result.Success) throw new InvalidOperationException("Kubernetes command failed: " +
            (result.StdErr.Length > 300 ? result.StdErr[..300] : result.StdErr));
        return result.StdOut;
    }

    public async Task<IReadOnlyList<KubernetesJobResource>> ListAsync(string hostId, CancellationToken ct)
    {
        var selector = WorkContainerLaunchPlanner.ManagedLabel + "=1";
        if (hostId.Length > 0) selector += "," + WorkContainerLaunchPlanner.HostLabel + "=" + hostId;
        var json = await RunAsync(["get", "jobs,pods,secrets", "-l", selector, "-o", "json"], null, ct);
        using var doc = JsonDocument.Parse(json);
        var items = new List<KubernetesJobResource>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString() ?? "";
            var metadata = item.GetProperty("metadata");
            var labels = metadata.GetProperty("labels");
            string Label(string key) => labels.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
            var status = item.TryGetProperty("status", out var observedStatus) ? observedStatus : default;
            var phase = kind switch
            {
                "Job" when status.ValueKind == JsonValueKind.Object && status.TryGetProperty("conditions", out var conditions)
                    && conditions.ValueKind == JsonValueKind.Array
                    && conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Complete" && c.GetProperty("status").GetString() == "True") => "Complete",
                "Job" when status.ValueKind == JsonValueKind.Object && status.TryGetProperty("conditions", out var conditions)
                    && conditions.ValueKind == JsonValueKind.Array
                    && conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Failed" && c.GetProperty("status").GetString() == "True") => "Failed",
                "Pod" => status.ValueKind == JsonValueKind.Object && status.TryGetProperty("phase", out var podPhase)
                    ? podPhase.GetString() ?? "Pending" : "Pending",
                _ => "Active",
            };
            items.Add(new(metadata.GetProperty("name").GetString() ?? "", kind,
                Label(WorkContainerLaunchPlanner.AttemptLabel), Label(WorkContainerLaunchPlanner.LeaseLabel),
                long.TryParse(Label(WorkContainerLaunchPlanner.FenceLabel), out var fence) ? fence : 0,
                phase, int.TryParse(Label(WorkContainerLaunchPlanner.GrantLabel), out var grant) ? grant : 0,
                Label("agent-studio.issuance")));
        }
        return items;
    }

    public Task ApplySecretAsync(string name, string token, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            apiVersion = "v1", kind = "Secret", metadata = new { name, labels }, type = "Opaque",
            stringData = new Dictionary<string, string> { ["github-token"] = token },
        });
        return RunAsync(["create", "-f", "-"], payload, ct);
    }

    public Task ApplyJobAsync(string manifest, CancellationToken ct)
        => RunAsync(["create", "-f", "-"], manifest, ct);

    public async Task AttachSecretOwnerAsync(string name, CancellationToken ct)
    {
        var job = await RunAsync(["get", "job", name, "-o", "json"], null, ct);
        using var parsed = JsonDocument.Parse(job);
        var uid = parsed.RootElement.GetProperty("metadata").GetProperty("uid").GetString()
            ?? throw new InvalidDataException("Kubernetes Job has no UID.");
        var patch = JsonSerializer.Serialize(new
        {
            metadata = new { ownerReferences = new[] { new
            {
                apiVersion = "batch/v1", kind = "Job", name, uid,
                controller = false, blockOwnerDeletion = false,
            } } },
        });
        await RunAsync(["patch", "secret", name, "--type=merge", "-p", patch], null, ct);
    }

    public async Task<KubernetesJobResource?> GetJobAsync(string name, CancellationToken ct)
        => (await ListAsync("", ct)).FirstOrDefault(item => item.Kind == "Job" && item.Name == name);

    public async Task<int?> GetExitCodeAsync(string name, CancellationToken ct)
    {
        var json = await RunAsync(["get", "pods", "-l", "job-name=" + name, "-o", "json"], null, ct);
        using var doc = JsonDocument.Parse(json);
        int? failed = null;
        foreach (var pod in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (!pod.TryGetProperty("status", out var status)
                || !status.TryGetProperty("containerStatuses", out var statuses)) continue;
            foreach (var containerStatus in statuses.EnumerateArray())
                if (containerStatus.GetProperty("name").GetString() == "work"
                    && containerStatus.GetProperty("state").TryGetProperty("terminated", out var terminal))
                {
                    var code = terminal.GetProperty("exitCode").GetInt32();
                    if (code == 0) return 0;
                    failed = code;
                }
        }
        return failed;
    }

    public Task<string> LogsAsync(string name, CancellationToken ct)
        => RunAsync(["logs", "job/" + name, "--timestamps", "--all-containers"], null, ct);

    public Task DeleteJobAsync(string name, CancellationToken ct)
        => RunAsync(["delete", "job", name, "--ignore-not-found", "--cascade=foreground", "--wait=true", "--timeout=60s"], null, ct);

    public Task DeletePodAsync(string name, CancellationToken ct)
        => RunAsync(["delete", "pod", name, "--ignore-not-found", "--wait=true", "--timeout=60s"], null, ct);

    public Task DeleteSecretAsync(string name, CancellationToken ct)
        => RunAsync(["delete", "secret", name, "--ignore-not-found", "--wait=true", "--timeout=60s"], null, ct);
}
