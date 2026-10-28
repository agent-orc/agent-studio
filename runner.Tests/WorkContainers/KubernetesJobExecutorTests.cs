using System.Text.Json;
using AgentRunner.WorkContainers;
using Xunit;

namespace AgentRunner.Tests.WorkContainers;

public sealed class KubernetesJobExecutorTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "k8s-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly string _kubeconfig = Path.GetTempFileName();
    private static readonly HostMemory Memory = new(56L << 30, 62L << 30);

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, true);
        File.Delete(_kubeconfig);
    }

    private KubernetesJobExecutor Executor(FakeRuntime runtime, FakeCredentials credentials)
        => new(runtime, new KubernetesJobSettings(KubernetesJobMode.Canary, "approved", _kubeconfig,
            Path.Combine(_state, "jobs"), 60), WorkContainerPolicyTests.Settings() with
            { StateDirectory = Path.Combine(_state, "budget") }, credentials, () => Memory);

    private static WorkContainerAttempt Attempt(int index) => WorkContainerPolicyTests.Attempt() with
    {
        AttemptId = "run_" + index.ToString("D4"),
        RepositorySource = "https://github.com/example/one.git",
    };

    [Fact]
    public void Cluster_executor_defaults_off_and_requires_explicit_canary()
    {
        var settings = KubernetesJobSettings.FromEnvironment(_ => null, _state);
        Assert.Equal(KubernetesJobMode.Off, settings.Mode);
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
        if (OperatingSystem.IsLinux())
            (settings with { Namespace = "approved", Kubeconfig = _kubeconfig }).Validate(requireCanary: false);
    }

    [Fact]
    public async Task One_hundred_jobs_clean_up_jobs_pods_and_projected_secrets()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = new FakeRuntime();
        var credentials = new FakeCredentials();
        var executor = Executor(runtime, credentials);
        for (var index = 0; index < 100; index++)
        {
            var receipt = await executor.RunAsync(Attempt(index), WorkContainerPolicyTests.Digest,
                new WorkContainerGrant(1, 1, 3), CancellationToken.None);
            Assert.Equal("succeeded", receipt.Outcome);
            Assert.True(receipt.CleanupVerified);
        }
        Assert.Empty(runtime.Resources);
        Assert.Equal(100, runtime.JobCreates);
        Assert.Equal(100, runtime.OwnerAttaches);
        Assert.Equal(100, credentials.Acquires);
        Assert.Equal(100, credentials.Revokes);
        Assert.Empty(Directory.GetFiles(Path.Combine(_state, "jobs"), "*.issuance.json"));
    }

    [Fact]
    public async Task Pod_retry_stays_in_one_job_and_stale_fence_is_never_succeeded()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = new FakeRuntime { RetryPod = true };
        var credentials = new FakeCredentials();
        var executor = Executor(runtime, credentials);
        var receipt = await executor.RunAsync(Attempt(1), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 3), CancellationToken.None);
        Assert.Equal("succeeded", receipt.Outcome);
        Assert.Equal(1, runtime.JobCreates);
        Assert.Equal(1, credentials.Acquires);
        Assert.Equal(2, runtime.PodCreates);

        runtime.StaleFence = true;
        var fenced = await executor.RunAsync(Attempt(2), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 3), CancellationToken.None);
        Assert.Equal("fence-superseded", fenced.Outcome);
        Assert.True(fenced.CleanupVerified);
    }

    [Fact]
    public async Task Outage_denies_admission_before_credential_or_job_creation()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = new FakeRuntime { Outage = true };
        var credentials = new FakeCredentials();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(runtime, credentials)
            .RunAsync(Attempt(1), WorkContainerPolicyTests.Digest,
                new WorkContainerGrant(1, 1, 3), CancellationToken.None));
        Assert.Equal(0, credentials.Acquires);
        Assert.Equal(0, runtime.JobCreates);
    }

    [Fact]
    public async Task Incomplete_managed_job_labels_block_grant_admission()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = new FakeRuntime();
        runtime.Resources.Add(new KubernetesJobResource("unknown", "Job", "other", "", 0, "Active", 0));
        var credentials = new FakeCredentials();
        var receipt = await Executor(runtime, credentials).RunAsync(Attempt(6), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 3), CancellationToken.None);
        Assert.Equal("inventory-untrusted", receipt.Outcome);
        Assert.Equal(0, credentials.Acquires);
        Assert.Equal(0, runtime.JobCreates);
    }

    [Fact]
    public async Task Lease_loss_stops_job_and_revokes_attempt_credential()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var lease = new CancellationTokenSource();
        var runtime = new FakeRuntime { CancelOnGet = lease };
        var credentials = new FakeCredentials();
        var receipt = await Executor(runtime, credentials).RunAsync(Attempt(3), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 3), lease.Token);
        Assert.Equal("lease-lost", receipt.Outcome);
        Assert.True(receipt.CleanupVerified);
        Assert.Empty(runtime.Resources);
        Assert.Equal(1, credentials.Revokes);
    }

    [Fact]
    public async Task Reconciliation_removes_old_fence_without_claiming_another_attempt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = new FakeRuntime();
        var credentials = new FakeCredentials();
        var attempt = Attempt(4);
        runtime.AddOrphan(attempt);
        var receipts = await Executor(runtime, credentials).ReconcileAsync(attempt.HostId,
            [new WorkAttemptAuthority(attempt.AttemptId, attempt.LeaseId, attempt.Fence + 1, true)], CancellationToken.None);
        Assert.Single(receipts);
        Assert.True(receipts[0].CleanupVerified);
        Assert.Empty(runtime.Resources);
        Assert.Equal(0, credentials.Acquires);
    }

    [Fact]
    public async Task Cluster_outage_keeps_fenced_cleanup_evidence_until_recovery()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = new FakeRuntime { OutageAfterCreate = true };
        var credentials = new FakeCredentials();
        var executor = Executor(runtime, credentials);
        var attempt = Attempt(5);
        var pending = await executor.RunAsync(attempt, WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 3), CancellationToken.None);
        Assert.Equal("cluster-unavailable", pending.Outcome);
        Assert.False(pending.CleanupVerified);
        Assert.Contains(pending.Residue, item => item.StartsWith("cluster-unreachable:", StringComparison.Ordinal));
        var stillPending = await executor.ReconcileAsync(attempt.HostId, [], CancellationToken.None);
        Assert.Single(stillPending);
        Assert.False(stillPending[0].CleanupVerified);
        runtime.Outage = false;
        var cleaned = await executor.ReconcileAsync(attempt.HostId, [], CancellationToken.None);
        Assert.Single(cleaned);
        Assert.True(cleaned[0].CleanupVerified);
        Assert.Empty(runtime.Resources);
        Assert.Empty(Directory.GetFiles(Path.Combine(_state, "jobs"), "*.issuance.json"));
    }

    [Fact]
    public void Manifest_pins_limits_fence_projection_and_finished_ttl()
    {
        var attempt = Attempt(1);
        var labels = KubernetesJobPlan.Labels(attempt, 3, "issued");
        using var doc = JsonDocument.Parse(KubernetesJobPlan.Build(attempt, "agent-work-one",
            WorkContainerPolicyTests.Digest, new WorkContainerLimits(3, 6L << 30, 512), 60, labels));
        var root = doc.RootElement;
        Assert.Equal(60, root.GetProperty("spec").GetProperty("ttlSecondsAfterFinished").GetInt32());
        var pod = root.GetProperty("spec").GetProperty("template");
        Assert.Equal(attempt.AttemptId, pod.GetProperty("metadata").GetProperty("labels").GetProperty(WorkContainerLaunchPlanner.AttemptLabel).GetString());
        Assert.Equal(attempt.Fence.ToString(), pod.GetProperty("metadata").GetProperty("labels").GetProperty(WorkContainerLaunchPlanner.FenceLabel).GetString());
        var spec = pod.GetProperty("spec");
        Assert.False(spec.GetProperty("automountServiceAccountToken").GetBoolean());
        Assert.Equal("github-token", spec.GetProperty("volumes")[1].GetProperty("projected")
            .GetProperty("sources")[0].GetProperty("secret").GetProperty("items")[0].GetProperty("path").GetString());
        var resources = spec.GetProperty("containers")[0].GetProperty("resources");
        Assert.Equal("1.5", resources.GetProperty("requests").GetProperty("cpu").GetString());
        Assert.Equal(resources.GetProperty("requests").GetRawText(), resources.GetProperty("limits").GetRawText());
    }

    [Theory]
    [InlineData("Active", 7, "lease-42", true)]
    [InlineData("Complete", 7, "lease-42", false)]
    [InlineData("Failed", 7, "lease-42", false)]
    [InlineData("Active", 8, "lease-42", false)]
    [InlineData("Active", 7, "old-lease", false)]
    public void Reconciliation_keeps_only_active_job_on_current_lease_and_fence(
        string phase, long fence, string lease, bool expected)
    {
        var attempt = WorkContainerPolicyTests.Attempt();
        var job = new KubernetesJobResource("job", "Job", attempt.AttemptId, lease,
            fence, phase, 3);
        Assert.Equal(expected, KubernetesJobReconciliationPolicy.Keep(job,
            new WorkAttemptAuthority(attempt.AttemptId, attempt.LeaseId, attempt.Fence, true)));
    }

    [Fact]
    public void Admission_counts_cluster_memory_grants_under_the_host_ceiling()
    {
        var settings = WorkContainerPolicyTests.Settings(maxHalfCores: 20, maxMemoryGiB: 8);
        var grant = new WorkContainerGrant(1, 1, 3);
        var verdict = KubernetesJobAdmissionPolicy.Decide(grant, 2, Memory, settings);
        Assert.False(verdict.Admitted);
        Assert.StartsWith("host-memory-grant-exhausted", verdict.Reason);
    }

    private sealed class FakeCredentials : IWorkContainerCredentialSource
    {
        public int Acquires, Revokes;
        public Task<WorkCredential> AcquireAsync(WorkContainerAttempt attempt, CancellationToken ct)
        {
            Acquires++;
            return Task.FromResult(new WorkCredential("issued-" + attempt.AttemptId,
                "opaque-" + Guid.NewGuid().ToString("N"),
                DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
        }
        public Task<WorkCredential?> RenewAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
            => Task.FromResult<WorkCredential?>(null);
        public Task<string> RevokeAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
        { Revokes++; return Task.FromResult("revoked"); }
    }

    private sealed class FakeRuntime : IKubernetesJobRuntime
    {
        public readonly List<KubernetesJobResource> Resources = [];
        public bool Outage, OutageAfterCreate, RetryPod, StaleFence;
        public CancellationTokenSource? CancelOnGet;
        public int JobCreates, PodCreates, OwnerAttaches;

        public Task<IReadOnlyList<KubernetesJobResource>> ListAsync(string hostId, CancellationToken ct)
        {
            if (Outage) throw new InvalidOperationException("cluster unavailable");
            return Task.FromResult<IReadOnlyList<KubernetesJobResource>>(Resources.ToArray());
        }
        public Task ApplySecretAsync(string name, string token, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { Resources.Add(Resource(name, "Secret", labels)); return Task.CompletedTask; }
        public Task ApplyJobAsync(string manifest, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(manifest);
            var root = doc.RootElement;
            var name = root.GetProperty("metadata").GetProperty("name").GetString()!;
            var labels = root.GetProperty("metadata").GetProperty("labels").EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.GetString()!);
            Resources.Add(Resource(name, "Job", labels) with { Phase = "Complete" });
            Resources.Add(Resource(name + "-pod-one", "Pod", labels));
            PodCreates++;
            if (RetryPod) { Resources.Add(Resource(name + "-pod-two", "Pod", labels)); PodCreates++; }
            JobCreates++;
            return Task.CompletedTask;
        }
        public Task AttachSecretOwnerAsync(string name, CancellationToken ct)
        { OwnerAttaches++; return Task.CompletedTask; }
        public Task<KubernetesJobResource?> GetJobAsync(string name, CancellationToken ct)
        {
            if (OutageAfterCreate) { Outage = true; throw new InvalidOperationException("cluster unavailable"); }
            var job = Resources.FirstOrDefault(item => item.Kind == "Job" && item.Name == name);
            CancelOnGet?.Cancel();
            if (CancelOnGet is not null && job is not null) job = job with { Phase = "Active" };
            return Task.FromResult(job is null || !StaleFence ? job : job with { Fence = job.Fence + 1 });
        }
        public Task<int?> GetExitCodeAsync(string name, CancellationToken ct) => Task.FromResult<int?>(0);
        public Task<string> LogsAsync(string name, CancellationToken ct) => Task.FromResult("complete");
        public Task DeleteJobAsync(string name, CancellationToken ct)
        { if (Outage) throw new InvalidOperationException("cluster unavailable");
          Resources.RemoveAll(item => item.Name == name && item.Kind == "Job"); return Task.CompletedTask; }
        public Task DeletePodAsync(string name, CancellationToken ct)
        { if (Outage) throw new InvalidOperationException("cluster unavailable");
          Resources.RemoveAll(item => item.Name == name && item.Kind == "Pod"); return Task.CompletedTask; }
        public Task DeleteSecretAsync(string name, CancellationToken ct)
        { if (Outage) throw new InvalidOperationException("cluster unavailable");
          Resources.RemoveAll(item => item.Name == name && item.Kind == "Secret"); return Task.CompletedTask; }

        public void AddOrphan(WorkContainerAttempt attempt)
        {
            var labels = KubernetesJobPlan.Labels(attempt, 3, "old-issued");
            Resources.Add(Resource("agent-work-orphan", "Job", labels));
            Resources.Add(Resource("agent-work-orphan-pod", "Pod", labels));
            Resources.Add(Resource("agent-work-orphan", "Secret", labels));
        }

        private static KubernetesJobResource Resource(string name, string kind, IReadOnlyDictionary<string, string> labels)
            => new(name, kind, labels[WorkContainerLaunchPlanner.AttemptLabel], labels[WorkContainerLaunchPlanner.LeaseLabel],
                long.Parse(labels[WorkContainerLaunchPlanner.FenceLabel]), "Active",
                int.Parse(labels[WorkContainerLaunchPlanner.GrantLabel]), labels["agent-studio.issuance"]);
    }
}
