using AgentStudio.Operator;
using AgentStudio.Shared;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodingCapacityServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    private static RunnerCapabilitySnapshotDto Host(int bootstrap = 5, int? central = 2, int? adopted = 2, int used = 0)
        => new("coding", "coding", "physical", "instance", "1", 1, "active", Now, Now,
            new("physical", "open", null, null, null, null),
            [new(CapabilityProtocol.CodingExecutor, "runtime", "ready", "healthy", null, Now, Now.AddMinutes(10), true,
                null, null, null, null, 0, null, null, null, [], [])],
            new(Now, 20, null, null, null, null, null, null, null, null, null, 8, used),
            RuntimeCapacity: central is { } slots ? new("physical", slots, 80, "balanced", 3, Now) : null,
            EffectiveMaxParallelism: adopted, RuntimeCapacityAppliedVersion: 3, RoleMaxParallelism: bootstrap);

    [Theory]
    [InlineData(5, 2, 2, 2, 2, 0, "adopted")]
    [InlineData(2, 5, 5, 0, 5, 5, "adopted")]
    [InlineData(2, 5, 2, 0, 2, 2, "pending adoption")]
    [InlineData(2, 5, 2, 2, 2, 0, "pending adoption")]
    [InlineData(5, 2, 5, 2, 2, 0, "pending adoption")]
    [InlineData(5, 2, 5, 4, 4, 0, "pending adoption")]
    [InlineData(2, 5, null, 0, 5, 0, "pending adoption")]
    public void Allocation_respects_current_policy_and_adoption(int bootstrap, int central, int? adopted,
        int used, int total, int admissible, string state)
    {
        var hosts = CodingCapacityService.Hosts(Now, [], [Host(bootstrap, central, adopted, used)]);
        var totals = CodingCapacityService.Totals(hosts);
        Assert.Equal((total, used, admissible), totals);
        var allocation = Assert.Single(hosts).Allocation!;
        Assert.Equal(state, allocation.State);
        Assert.Equal(bootstrap, allocation.BootstrapSlots);
        var fact = CodingCapacityProjection.Project("studio", [], TaskReferenceIndex.Build([]), totals.Total, totals.Used, _ => []);
        Assert.Equal(total, fact.UsedSlots + fact.FreeSlots);
        if (fact.FreeSlots == 0)
        {
            var tracker = new CapacityIdleTracker();
            Assert.Null(tracker.Observe(fact, [], Now));
            Assert.Null(tracker.Observe(fact, [], Now.AddHours(1)));
        }
    }

    [Fact]
    public void Adoption_version_lag_is_visible_even_when_slot_numbers_match()
    {
        var host = Assert.Single(CodingCapacityService.Hosts(Now, [],
            [Host(2, 5, 5) with { RuntimeCapacityAppliedVersion = 2 }]));
        Assert.Equal("pending adoption", host.Allocation!.State);
    }

    [Fact]
    public void Shared_occupancy_includes_published_leases_and_preserves_a_healthy_peer()
    {
        var first = Host(5, 5, 5, 1);
        var second = first with { RunnerId = "second", InstanceId = "other", Telemetry = first.Telemetry! with
            { ActiveSlots = 0, ClaimLoadGate = new("closed", 5, 4, 100, Now, 0) } };
        var hosts = CodingCapacityService.Hosts(Now, [], [first, second], id => id == "coding" ? 3 : 0);
        Assert.Equal((5, 3, 2), CodingCapacityService.Totals(hosts));
        Assert.Equal(0, hosts.Single(host => host.Id == "second").AdmissibleSlots);
    }

    [Theory]
    [InlineData(7, 7, "adopted")]
    [InlineData(7, 3, "pending adoption")]
    [InlineData(0, 3, "adopted")]
    public void Adoption_follows_the_runtime_allocation_version_before_the_settings_version(long allocationVersion,
        long appliedVersion, string expected)
    {
        var snapshot = Host(2, 5, 5);
        snapshot = snapshot with { RuntimeCapacity = snapshot.RuntimeCapacity! with { AllocationVersion = allocationVersion },
            RuntimeCapacityAppliedVersion = appliedVersion };
        var host = Assert.Single(CodingCapacityService.Hosts(Now, [], [snapshot]));
        Assert.Equal(expected, host.Allocation!.State);
    }

    [Fact]
    public void Old_client_activity_cannot_override_fresh_authoritative_slot_telemetry()
    {
        var client = new ClientIdentity { Id = "coding", RunnerActiveSlots = 5, LastSeenAt = Now.AddHours(-1) };
        Assert.Equal((2, 0, 2), CodingCapacityService.Totals(CodingCapacityService.Hosts(Now, [client], [Host()])));
    }

    [Fact]
    public void A_stale_peer_without_live_leases_does_not_consume_a_healthy_hosts_allocation()
    {
        var healthy = Host(5, 5, 5);
        var stale = healthy with { RunnerId = "stale", LastSeenAt = Now.AddMinutes(-11),
            Telemetry = healthy.Telemetry! with { ActiveSlots = 4 } };
        Assert.Equal((5, 0, 5), CodingCapacityService.Totals(CodingCapacityService.Hosts(Now, [], [healthy, stale])));
    }

    [Fact]
    public void Studio_identity_targets_join_bootstrap_only_registry_snapshots()
    {
        var client = new ClientIdentity { Id = "coding", RunnerDesiredMaxParallelism = 2,
            RunnerEffectiveMaxParallelism = 2, RunnerLastClaimAt = Now.AddMinutes(-2) };
        var hosts = CodingCapacityService.Hosts(Now, [client], [Host(5, null, null, 2)]);
        Assert.Equal((2, 2, 0), CodingCapacityService.Totals(hosts));
        Assert.Equal("adopted", Assert.Single(hosts).Allocation!.State);
    }

    [Theory]
    [InlineData("automatic-drain")]
    [InlineData("stale")]
    [InlineData("missing-telemetry")]
    [InlineData("stale-telemetry")]
    [InlineData("other-role")]
    [InlineData("healthy")]
    public void Fresh_legacy_record_cannot_override_a_capability_authority(string state)
    {
        var host = Host();
        host = state switch
        {
            "automatic-drain" => host with { HostAdmission = new("physical", "automatic-drain", "hold", Now, null, null) },
            "stale" => host with { LastSeenAt = Now.AddMinutes(-11) },
            "missing-telemetry" => host with { Telemetry = null },
            "stale-telemetry" => host with { Telemetry = host.Telemetry! with { ObservedAt = Now.AddMinutes(-11) } },
            "other-role" => host with { Capabilities = [] },
            _ => host,
        };
        var legacy = new ClientIdentity { Id = "CODING", DisplayName = "coding", RunnerDaemonState = "running",
            LastSeenAt = Now, RunnerDesiredMaxParallelism = 12, RunnerActiveSlots = 0 };
        var hosts = CodingCapacityService.Hosts(Now, [legacy], [host]);
        if (state == "healthy") Assert.Equal(2, Assert.Single(hosts).Slots);
        else
        {
            Assert.Empty(hosts);
            Assert.Equal((0, 0, 0), CodingCapacityService.Totals(hosts));
        }
    }

    [Fact]
    public void Legacy_remote_capacity_and_explicit_local_capacity_have_separate_assignments()
    {
        var legacy = new ClientIdentity { Id = "remote", DisplayName = "remote", RunnerDaemonState = "running",
            LastSeenAt = Now, RunnerDesiredMaxParallelism = 2, RunnerEffectiveMaxParallelism = 2 };
        var remote = Assert.Single(CodingCapacityService.Hosts(Now, [legacy], []));
        Assert.False(CodingCapacityService.Assigned(new() { ExecutionLocation = "local" }, remote));
        Assert.True(CodingCapacityService.Assigned(new() { ExecutionLocation = "remote" }, remote));
        var local = remote with { Id = "local", IsLocal = true };
        Assert.True(CodingCapacityService.Assigned(new() { ExecutionLocation = "local" }, local));
        Assert.False(CodingCapacityService.Assigned(new() { ExecutionLocation = "remote" }, local));
    }

    [Theory]
    [InlineData("balanced", 5)]
    [InlineData("conservative", 1)]
    public void Multiple_coding_roles_share_one_host_ceiling_and_burst(string ramp, int admissible)
    {
        var first = Host(5, 5, 5) with { RuntimeCapacity = new("physical", 5, 80, ramp, 3, Now) };
        var second = first with { RunnerId = "second", InstanceId = "second-instance" };
        var hosts = CodingCapacityService.Hosts(Now, [], [first, second]);
        Assert.Equal(2, hosts.Length);
        Assert.Equal((5, 0, admissible), CodingCapacityService.Totals(hosts));
    }

    [Fact]
    public void Draining_peer_occupancy_still_consumes_the_shared_host_ceiling()
    {
        var healthy = Host(5, 5, 5);
        var draining = healthy with { RunnerId = "draining", Telemetry = healthy.Telemetry! with { ActiveSlots = 4 },
            HostAdmission = new("physical", "automatic-drain", "held", Now, null, null) };
        var hosts = CodingCapacityService.Hosts(Now, [], [healthy, draining]);
        Assert.Equal("coding", Assert.Single(hosts).Id);
        Assert.Equal((5, 4, 1), CodingCapacityService.Totals(hosts));
    }

    [Fact]
    public void Closed_daemon_gate_holds_an_idle_host_even_when_central_first_slot_would_admit()
    {
        var snapshot = Host(5, 5, 5);
        snapshot = snapshot with { Telemetry = snapshot.Telemetry! with
            { ClaimLoadGate = new("closed", 6, 4, 100, Now.AddMinutes(-2), 0) } };
        var host = Assert.Single(CodingCapacityService.Hosts(Now, [], [snapshot]));
        Assert.Equal((5, 0, 0), CodingCapacityService.Totals([host]));
        Assert.Contains(host.AdmissionBlocks, block => block.Reason.Contains("load gate"));
    }

    [Theory]
    [InlineData(95, "balanced", 1)]
    [InlineData(20, "conservative", 1)]
    [InlineData(20, "balanced", 5)]
    public void Idle_host_burst_preserves_the_shared_first_slot_and_ramp_rules(int cpu, string ramp, int admissible)
    {
        var snapshot = Host(5, 5, 5);
        snapshot = snapshot with { Telemetry = snapshot.Telemetry! with { CpuPercent = cpu },
            RuntimeCapacity = snapshot.RuntimeCapacity! with { RampStrategy = ramp } };
        Assert.Equal((5, 0, admissible), CodingCapacityService.Totals(CodingCapacityService.Hosts(Now, [], [snapshot])));
    }

    [Theory]
    [InlineData("closed", 0, "load gate")]
    [InlineData("cpu", 0, "host load")]
    [InlineData("ramp", 0, "ramp")]
    [InlineData("healthy", 4, null)]
    public void Published_admission_holds_match_the_shared_policy_and_alert_cause(string condition, int admissible, string? cause)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        var host = Host(5, 5, 5, 1);
        var identity = new ClientIdentity { Id = "coding", RunnerLastClaimAt = Now.AddMinutes(-2) };
        if (condition == "closed") host = host with
        {
            Telemetry = host.Telemetry! with { ClaimLoadGate = new("closed", 6, 4, 100, Now.AddMinutes(-2), 1) },
        };
        if (condition == "cpu") host = host with { Telemetry = host.Telemetry! with { CpuPercent = 95 } };
        if (condition == "ramp")
        {
            host = host with { RuntimeCapacity = host.RuntimeCapacity! with { RampStrategy = "conservative" } };
            identity = identity with { RunnerLastClaimAt = Now.AddSeconds(-10) };
        }
        var facts = Assert.Single(CodingCapacityService.Hosts(clock.GetUtcNow().UtcDateTime, [identity], [host]));
        Assert.Equal(admissible, facts.AdmissibleSlots);
        var cards = Enumerable.Range(0, 5).Select(i => new TaskInfo { Id = $"C-{i}", Key = $"C-{i}", State = TaskStates.Ready }).ToArray();
        var fact = CodingCapacityProjection.Project("studio", cards, TaskReferenceIndex.Build(cards), 5, 1,
            _ => facts.AdmissionBlocks, admissibleSlots: facts.AdmissibleSlots, lastProgressAt: identity.RunnerLastClaimAt);
        Assert.Equal(4, fact.FreeSlots);
        Assert.Equal(admissible, fact.AdmissibleSlots);
        Assert.Equal(cause is null ? 5 : 0, fact.ClaimableNow);
        var tracker = new CapacityIdleTracker();
        Assert.Null(tracker.Observe(fact, [], clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(21));
        if (cause is not null) Assert.Contains(cause, tracker.Observe(fact, [], clock.GetUtcNow().UtcDateTime)!.Reason);
        else Assert.Null(tracker.Observe(fact with { LastProgressAt = clock.GetUtcNow().UtcDateTime }, [], clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Preflight_is_project_specific_fresh_and_bound_to_registration()
    {
        var repository = new RemoteProjectRepository("project", "repository", "https://example.invalid/repo.git", "develop", "registry");
        var host = Assert.Single(CodingCapacityService.Hosts(Now, [], [Host()]));
        Assert.Contains(CapacityClaimability.HostBlocks(host, repository, Now), block => block.Reason.Contains("missing"));
        var proof = new RunnerProjectPreflight { ProjectId = "project", Status = "ready", CheckedAt = Now,
            RegistrationFingerprint = ProjectDeliveryPreflightFingerprint.Create(repository) };
        host = host with { Identity = new ClientIdentity { RunnerProjectPreflights = [proof] } };
        Assert.Empty(CapacityClaimability.HostBlocks(host, repository, Now));
        Assert.Contains(CapacityClaimability.HostBlocks(host, repository, Now.AddMinutes(6)), block => block.Reason.Contains("stale"));
        Assert.Contains(CapacityClaimability.HostBlocks(host, repository with { DefaultBranch = "other" }, Now), block => block.Reason.Contains("missing"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("failed")]
    public void Preflight_hold_is_an_admission_stall_instead_of_a_supply_shortage(string condition)
    {
        var repository = new RemoteProjectRepository("project", "repository", "https://example.invalid/repo.git", "develop", "registry");
        var proof = new RunnerProjectPreflight { ProjectId = "project", Status = condition == "failed" ? "failed" : "ready",
            Detail = "repository read refused", CheckedAt = condition == "stale" ? Now.AddMinutes(-6) : Now,
            RegistrationFingerprint = ProjectDeliveryPreflightFingerprint.Create(repository) };
        var host = Assert.Single(CodingCapacityService.Hosts(Now, [], [Host()])) with
        {
            Identity = new ClientIdentity { RunnerProjectPreflights = condition == "missing" ? [] : [proof] },
        };
        var blocks = CapacityClaimability.HostBlocks(host, repository, Now);
        var cards = Enumerable.Range(0, 5).Select(i => new TaskInfo { Id = $"C-{i}", State = TaskStates.Ready }).ToArray();
        var fact = CodingCapacityProjection.Project("studio", cards, TaskReferenceIndex.Build(cards), 2, 0, _ => blocks);
        Assert.Equal((5, 0, 0), (fact.RunnableNow, fact.ClaimableNow, fact.AdmissibleSlots));
        Assert.Contains("preflight", fact.Cause);
        Assert.DoesNotContain("plan more", fact.Cause);
        var tracker = new CapacityIdleTracker();
        Assert.Null(tracker.Observe(fact, [], Now));
        Assert.Contains("preflight", tracker.Observe(fact, [], Now.AddMinutes(21))!.Reason);
    }

    [Theory]
    [InlineData("running", false)]
    [InlineData("in-flight", false)]
    [InlineData("off", true)]
    public void Non_live_overlap_uses_the_existing_policy_without_dispatch_writes(string window, bool admitted)
    {
        var candidate = new TaskInfo { Id = "candidate", Key = "C", ProjectName = "studio", State = TaskStates.Ready,
            Title = "Change backend/Features/Runner/shared.cs" };
        var other = candidate with { Id = "other", Key = "O", State = TaskStates.Progress };
        var block = CapacityClaimability.Overlap(candidate, new() { DispatchOverlapWindow = window }, [candidate, other],
            new Dictionary<string, TaskIntegrationStatus>());
        Assert.Equal(admitted, block is null);
        if (!admitted)
        {
            Assert.Contains("scope conflict", block!.Reason);
            var fact = CodingCapacityProjection.Project("studio", [candidate], TaskReferenceIndex.Build([candidate]),
                1, 0, _ => [block!]);
            Assert.Equal((1, 0, 0), (fact.RunnableNow, fact.ClaimableNow, fact.AdmissibleSlots));
            var tracker = new CapacityIdleTracker();
            Assert.Null(tracker.Observe(fact, [], Now));
            Assert.Contains("scope conflict", tracker.Observe(fact, [], Now.AddMinutes(21))!.Reason);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "watched")]
    [InlineData(true, "watched")]
    [InlineData(false, "second")]
    [InlineData(true, "second")]
    public void Fleet_counts_watched_projects_and_keeps_unwatched_dependency_heads(bool scoped, string? duplicateProject)
    {
        var root = Path.Combine(Path.GetTempPath(), "coding-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
                ["WatchPaths:0:Name"] = "watched",
                ["WatchPaths:0:Path"] = root,
                ["WatchPaths:1:Name"] = "second",
                ["WatchPaths:1:Path"] = Path.Combine(root, "second"),
            }).Build();
            TaskInfo Card(string key, string project, string state) => new()
            {
                Id = key, Key = key, ProjectName = project, State = state,
                WatchPath = Path.Combine(root, project), FolderPath = Path.Combine(root, project, state, key),
            };
            var head = Card("HEAD", "unwatched", TaskStates.Escalated);
            var waiting = Card("WAITING", "watched", TaskStates.Ready) with
            {
                References = new() { DependsOn = [new("HEAD")] },
            };
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
            var cards = new List<TaskInfo> { head, waiting, Card("OUTSIDE", "unwatched", TaskStates.Ready) };
            if (duplicateProject is not null)
                cards.Add(Card("waiting", duplicateProject, TaskStates.Ready) with { Id = "stale-copy" });
            var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config,
                () => cards, timeProvider: clock);
            scanner.SetIndexCache(cache);
            cache.GetSnapshot();
            var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
            var quota = new QuotaService(NullLogger<QuotaService>.Instance, [], config,
                new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance), clock: clock);
            var selection = new ModelSelectionService(quota,
                new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config),
                new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance),
                new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, config), settings, timeProvider: clock);
            var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
            clients.EnsureLoaded();
            var service = new CodingCapacityService(scanner, settings,
                clients, new V1ReviewExecutorRegistry(clock),
                quota, selection, new OverlapDispatchGuard(clock,
                    NullLogger<OverlapDispatchGuard>.Instance, new OverlapPredictionLedger(Path.Combine(root, "overlap"))),
                new CapacityIdleTracker(), new TaskListGitProjectionCache());

            var filesBefore = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
            var report = service.Read(clock.GetUtcNow().UtcDateTime,
                scoped ? project => project == "watched" : null, includeQuota: false)!;

            service.Read(clock.GetUtcNow().UtcDateTime, includeQuota: false);
            Assert.Equal(filesBefore.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            Assert.All(filesBefore, file => Assert.Equal(file.Value, File.ReadAllText(file.Key)));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => path.Contains("overlap")));
            Assert.Equal(1, report.Fleet.Ready);
            Assert.Equal(report.Projects.Sum(project => project.Ready), report.Fleet.Ready);
            Assert.Equal("WAITING", Assert.Single(report.Fleet.Cards).CardKey);
            Assert.Contains(Assert.Single(report.Fleet.Cards).Blocks,
                block => block.Class == "dependency" && block.Dependency == "HEAD");
            var watched = Assert.Single(report.Projects, project => project.Project == "watched");
            Assert.Equal(1, watched.Ready);
            Assert.Equal(0, watched.ClaimableNow);
            if (!scoped) Assert.Equal(0, Assert.Single(report.Projects, project => project.Project == "second").Ready);
            if (scoped) Assert.Empty(report.Fleet.Heads);
            else Assert.Equal("HEAD", Assert.Single(report.Fleet.Heads).CardKey);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static readonly RemoteProjectRepository Repository =
        new("project", "repository", "https://example.invalid/repo.git", "develop", "registry");

    private static RunnerCapabilitySnapshotDto ClassHost(RunnerCapabilitySnapshotDto host) => host with
    {
        Capabilities = [.. host.Capabilities, host.Capabilities[0] with { Key = "platform:docker" }],
    };

    private static ClientIdentity Preflight(string id, string status = "ready") => new()
    {
        Id = id, RunnerProjectPreflights = [new RunnerProjectPreflight { ProjectId = "project", Status = status,
            Detail = "repository read refused", CheckedAt = Now, RegistrationFingerprint = ProjectDeliveryPreflightFingerprint.Create(Repository) }],
    };

    [Theory]
    [InlineData(5, 4)]   // the review's case: a draining peer uses four of five shared slots
    [InlineData(12, 11)] // the installation: one shared host with twelve cores
    public void Class_routed_project_row_counts_a_draining_peers_occupancy_on_the_shared_host(int ceiling, int peerUsed)
    {
        // AGT-4348: the class branch recomputed free slots from the role's own usage only.
        var healthy = ClassHost(Host(ceiling, ceiling, ceiling));
        var draining = healthy with { RunnerId = "draining", InstanceId = "draining-instance",
            Telemetry = healthy.Telemetry! with { ActiveSlots = peerUsed },
            HostAdmission = new("physical", "automatic-drain", "held", Now, null, null) };
        var assigned = CodingCapacityService.Hosts(Now, [Preflight("coding")], [healthy, draining]);
        var config = new ProjectSettings { ExecutionLocation = "class:docker", MaxParallelism = ceiling };
        Assert.True(CodingCapacityService.Assigned(config, Assert.Single(assigned)));

        var slots = CodingCapacityService.ProjectSlots(config, assigned, 0, Repository, Now);

        Assert.Equal((ceiling, peerUsed, 1), (slots.Total, slots.Used, slots.Admissible));
        var cards = Enumerable.Range(0, 3).Select(i => new TaskInfo { Id = $"C-{i}", Key = $"C-{i}", State = TaskStates.Ready }).ToArray();
        var fact = CodingCapacityProjection.Project("studio", cards, TaskReferenceIndex.Build(cards), slots.Total, slots.Used,
            _ => [], admissibleSlots: slots.Admissible, admissionBlocks: slots.Blocks);
        Assert.Equal((1, 1), (fact.FreeSlots, fact.AdmissibleSlots));
    }

    [Theory]
    [InlineData("failed", "preflight failed: repository read refused")]
    [InlineData("missing", "preflight is missing or stale")]
    public void Host_rejected_by_the_project_preflight_adds_no_admissible_slot_and_is_named(string condition, string reason)
    {
        var ready = Host(5, 5, 5) with { HostId = "ready-host", Name = "ready-runner" };
        var rejected = Host(5, 5, 5) with { RunnerId = "rejected", Name = "rejected-runner", HostId = "rejected-host" };
        var identities = new List<ClientIdentity> { Preflight("coding") };
        if (condition == "failed") identities.Add(Preflight("rejected", "failed"));
        var assigned = CodingCapacityService.Hosts(Now, identities, [ready, rejected]);
        Assert.Equal((10, 0, 10), CodingCapacityService.Totals(assigned));

        var slots = CodingCapacityService.ProjectSlots(new ProjectSettings { ExecutionLocation = "remote" }, assigned, 0, Repository, Now);

        Assert.Equal((10, 0, 5), (slots.Total, slots.Used, slots.Admissible));
        var block = Assert.Single(slots.Blocks);
        Assert.Equal("admission", block.Class);
        Assert.StartsWith("rejected-runner: ", block.Reason);
        Assert.Contains(reason, block.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ramp_limited_capacity_names_the_ramp_and_its_limit_instead_of_advising_more_work()
    {
        var snapshot = Host(5, 5, 5);
        snapshot = snapshot with { RuntimeCapacity = snapshot.RuntimeCapacity! with { RampStrategy = "conservative" } };
        var host = Assert.Single(CodingCapacityService.Hosts(Now, [], [snapshot]));
        Assert.Equal(1, host.AdmissibleSlots);
        Assert.Contains(host.AdmissionLimits, limit => limit.Contains("conservative ramp") && limit.Contains("1 of 5"));
        var cards = Enumerable.Range(0, 2).Select(i => new TaskInfo { Id = $"C-{i}", Key = $"C-{i}", State = TaskStates.Ready }).ToArray();
        var fact = CodingCapacityProjection.Project("studio", cards, TaskReferenceIndex.Build(cards), 5, 0,
            _ => [], admissibleSlots: host.AdmissibleSlots, admissionLimits: host.AdmissionLimits);

        Assert.Equal((5, 1, 2), (fact.FreeSlots, fact.AdmissibleSlots, fact.ClaimableNow));
        Assert.Contains("conservative ramp", fact.Cause);
        Assert.Contains("1 of 5", fact.Cause);
        Assert.DoesNotContain("plan more", fact.Cause);
        Assert.DoesNotContain("plan independent", fact.Cause);
    }

}
