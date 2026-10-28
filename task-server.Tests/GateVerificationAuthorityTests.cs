using System.Security.Cryptography;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// DP-S4: merge, promotion and batch verification subjects, the immutable
/// bundle endpoint, host proof, and grant-backed gate admission.
/// </summary>
public sealed class GateVerificationAuthorityTests
{
    private static readonly DateTime Fixed = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Sha = "7a1c5e0b7a1c5e0b7a1c5e0b7a1c5e0b7a1c5e0b";
    private const string Repository = "product";

    [Fact]
    public async Task Batch_subject_with_monolith_member_ids_is_accepted_at_a_remote_task_server()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var batchId = Guid.NewGuid().ToString("N");
        var digest = new string('d', 64);
        var plan = Plan() with { SubjectBindingDigest = digest };
        // Member task and run IDs exist only in the monolith; the Task Server
        // previously rejected them as an unknown source run.
        var request = new CreateGateSubjectRequest("AGT-1", "monolith-run-1", Repository,
            "https://example.invalid/product.git", Sha,
            $"refs/agent-studio/batch-candidates/{batchId}/{digest}/1", null, null,
            GateRules.PlanHash(plan), "policy-v1", "platform-1", new string('3', 64), plan,
            clock.GetUtcNow().UtcDateTime.AddMinutes(15), 2, batchId, digest, new string('1', 40),
            ["monolith-run-1", "monolith-run-2"], GateSubjectKinds.Batch);

        var status = await store.CreateGateSubjectAsync(request, "monolith", default);

        Assert.Equal(GateSubjectKinds.Batch, status.Subject.Kind);
        Assert.Equal(GateStates.Queued, status.Phase);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CreateGateSubjectAsync(
            request with { Kind = null }, "monolith", default));
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("claimed", claim.Status);
        var passed = await ReportAsync(store, claim, Report(Proof(claim.Subject!)));
        Assert.Equal(GateStates.Passed, passed.Phase);
    }

    [Fact]
    public async Task Bundle_endpoint_refuses_a_wrong_digest_or_size_and_stores_nothing()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, Clock());
        await store.InitializeAsync();
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var sha = Hash(content);

        var wrongDigest = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.PutGateBundleAsync(
            new string('a', 64), content.Length, Repository, new MemoryStream(content), "monolith", default));
        Assert.Equal("gate-bundle-digest-mismatch", wrongDigest.Code);
        var longer = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.PutGateBundleAsync(
            sha, content.Length - 1, Repository, new MemoryStream(content), "monolith", default));
        Assert.Equal("gate-bundle-size-mismatch", longer.Code);
        var shorter = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.PutGateBundleAsync(
            sha, content.Length + 1, Repository, new MemoryStream(content), "monolith", default));
        Assert.Equal("gate-bundle-size-mismatch", shorter.Code);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutGateBundleAsync(
            sha, GateBundleLimits.MaxBytes + 1, Repository, new MemoryStream(content), "monolith", default));
        Assert.Null(await store.OpenGateBundleAsync(sha, null, default));

        var locator = await store.PutGateBundleAsync(sha, content.Length, Repository,
            new MemoryStream(content), "monolith", default);
        var replay = await store.PutGateBundleAsync(sha, content.Length, Repository,
            new MemoryStream(content), "monolith", default);
        Assert.Equal(locator, replay);
        Assert.Equal(GateBundleLimits.Locator(sha), locator.Locator);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.PutGateBundleAsync(
            sha, content.Length + 2, Repository, new MemoryStream(content), "monolith", default));
        Assert.Equal("gate-bundle-conflict", conflict.Code);
    }

    [Fact]
    public async Task Bundle_subject_names_only_a_stored_bundle_and_only_its_live_holder_reads_it()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var content = RandomNumberGenerator.GetBytes(4096);
        var sha = Hash(content);
        var locator = new GateBundleLocator(sha, content.Length, GateBundleLimits.Locator(sha));
        var request = MergeRequest(clock, locator);

        var missing = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateGateSubjectAsync(request, "monolith", default));
        Assert.Equal("gate-bundle-missing", missing.Code);
        await store.PutGateBundleAsync(sha, content.Length, Repository, new MemoryStream(content), "monolith", default);
        var mismatch = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CreateGateSubjectAsync(
            request with { RepositoryId = "other" }, "monolith", default));
        Assert.Equal("gate-bundle-mismatch", mismatch.Code);
        await store.CreateGateSubjectAsync(request, "monolith", default);

        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var denied = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.OpenGateBundleAsync(sha, "gate-a", default));
        Assert.Equal("gate-bundle-not-leased", denied.Code);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(locator, claim.Subject!.Bundle);
        var opened = (await store.OpenGateBundleAsync(sha, "gate-a", default))!.Value;
        await using (opened.Content)
        {
            using var copy = new MemoryStream();
            await opened.Content.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.OpenGateBundleAsync(sha, "gate-b", default));

        await ReportAsync(store, claim, Report(Proof(claim.Subject)));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.OpenGateBundleAsync(sha, "gate-a", default));
    }

    [Fact]
    public async Task Verification_report_without_matching_host_proof_never_passes()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (claim, _) = await ClaimMergeAsync(store, clock, "gate-a", "host-a");
        var authority = await AdvanceToCleaningAsync(store, claim);
        var proof = Proof(claim.Subject!);

        foreach (var invalid in new GateReport[]
                 {
                     Report(null),
                     Report(proof with { PolicyHash = "another-policy" }),
                     Report(proof with { PlanHash = new string('f', 64) }),
                     Report(proof with { Head = new string('e', 40) }),
                     Report(proof with { BundleSha256 = new string('c', 64) }),
                     Report(proof with { BundleBytes = proof.BundleBytes + 1 }),
                 })
        {
            var error = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ReportGateAsync(
                claim.Attempt!.AttemptId, new SubmitGateReportRequest(authority, invalid), "gate-a", default));
            Assert.Equal("gate-report-invalid", error.Code);
        }
        Assert.Equal(GateStates.Cleaning,
            (await store.GetGateStatusAsync(claim.Subject!.SubjectId, default))!.Phase);
        var passed = await store.ReportGateAsync(claim.Attempt!.AttemptId,
            new SubmitGateReportRequest(authority, Report(proof)), "gate-a", default);
        Assert.Equal(GateStates.Passed, passed.Phase);
        Assert.Equal(proof, passed.Report!.Proof);
    }

    [Fact]
    public async Task Lease_loss_fences_the_holder_and_prevents_a_passing_publication()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (claim, _) = await ClaimMergeAsync(store, clock, "gate-a", "host-a");
        var authority = await AdvanceToCleaningAsync(store, claim);

        clock.Advance(TimeSpan.FromMinutes(5));
        var expired = (await store.GetGateStatusAsync(claim.Subject!.SubjectId, default))!;

        Assert.Equal(GateStates.InfraRetry, expired.Attempts[0].State);
        var renew = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RenewGateAsync(
            claim.Attempt!.AttemptId, new GateRenewRequest(authority), default));
        Assert.Equal("stale-gate-fence", renew.Code);
        var report = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ReportGateAsync(
            claim.Attempt!.AttemptId, new SubmitGateReportRequest(authority, Report(Proof(claim.Subject))),
            "gate-a", default));
        Assert.Equal("stale-gate-fence", report.Code);
        var after = (await store.GetGateStatusAsync(claim.Subject.SubjectId, default))!;
        Assert.NotEqual(GateStates.Passed, after.Phase);
        Assert.Null(after.Report);
    }

    [Fact]
    public async Task Two_hosts_claim_distinct_gates_and_neither_can_report_the_other()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var first = await CreateMergeAsync(store, clock, 1);
        var second = await CreateMergeAsync(store, clock, 2);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        await RegisterGateAsync(store, clock, "gate-b", "instance-b", "host-b");

        var claims = await Task.WhenAll(
            store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default),
            store.ClaimGateAsync(new GateClaimRequest("gate-b", "instance-b"), "gate-b", default));

        Assert.All(claims, claim => Assert.Equal("claimed", claim.Status));
        Assert.Equal(
            new[] { first.Subject.SubjectId, second.Subject.SubjectId }.Order(),
            claims.Select(claim => claim.Subject!.SubjectId).Order());
        Assert.Equal("empty", (await store.ClaimGateAsync(
            new GateClaimRequest("gate-a", "instance-a"), "gate-a", default)).Status);
        var a = await AdvanceToCleaningAsync(store, claims[0]);
        var b = await AdvanceToCleaningAsync(store, claims[1]);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ReportGateAsync(
            claims[1].Attempt!.AttemptId, new SubmitGateReportRequest(a, Report(Proof(claims[1].Subject!))),
            "gate-a", default));
        foreach (var (claim, authority) in new[] { (claims[0], a), (claims[1], b) })
        {
            var passed = await store.ReportGateAsync(claim.Attempt!.AttemptId,
                new SubmitGateReportRequest(authority, Report(Proof(claim.Subject!))), authority.ExecutorId, default);
            Assert.Equal(GateStates.Passed, passed.Phase);
            Assert.Single(passed.Attempts);
        }
    }

    [Fact]
    public async Task Grant_mode_reserves_the_host_allocation_and_cancel_releases_it()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock, new TaskServerOptions
        {
            DataDirectory = temp.Path, GateAdmissionMode = "grant", GateGrantHalfCores = 3,
        });
        await store.InitializeAsync();
        await CreateMergeAsync(store, clock, 1);
        await CreateMergeAsync(store, clock, 2);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var noAllocation = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("empty", noAllocation.Status);
        Assert.Contains("allocation", noAllocation.Message!, StringComparison.Ordinal);

        await SetAllocationAsync(store, "host-a", "gate-a", maxParallelism: 1, allocationVersion: 4, applied: 3);
        var stale = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("empty", stale.Status);
        Assert.Contains("acknowledged allocation version 4", stale.Message!, StringComparison.Ordinal);

        await SetAllocationAsync(store, "host-a", "gate-a", maxParallelism: 1, allocationVersion: 4, applied: 4);
        var granted = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("claimed", granted.Status);
        Assert.Equal(3, granted.Lease!.GrantHalfCores);
        Assert.Equal(4, granted.Lease.AllocationVersion);
        var full = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("empty", full.Status);
        Assert.Contains("fully reserved (1/1)", full.Message!, StringComparison.Ordinal);

        var cancelled = await store.CancelGateSubjectAsync(granted.Subject!.SubjectId, "monolith", default);
        Assert.Equal(GateStates.Cancelled, cancelled.Phase);
        Assert.True(GateStates.IsTerminal(cancelled.Phase));
        var renew = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RenewGateAsync(
            granted.Attempt!.AttemptId, new GateRenewRequest(Authority(granted)), default));
        Assert.Equal("stale-gate-fence", renew.Code);
        var next = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("claimed", next.Status);
        Assert.NotEqual(granted.Subject.SubjectId, next.Subject!.SubjectId);
    }

    [Fact]
    public async Task Restarted_grant_holder_is_contained_and_its_grant_returns_to_the_host()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var options = new TaskServerOptions { DataDirectory = temp.Path, GateAdmissionMode = "grant" };
        var store = Store(temp.Path, clock, options);
        await store.InitializeAsync();
        var subject = await CreateMergeAsync(store, clock, 1, maxAttempts: 1);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        await SetAllocationAsync(store, "host-a", "gate-a", maxParallelism: 1, allocationVersion: 2, applied: 2);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("claimed", claim.Status);

        var restarted = Store(temp.Path, clock, options);
        await restarted.InitializeAsync();
        var contained = await restarted.ConfirmGateContainmentAsync(claim.Attempt!.AttemptId,
            new GateContainmentRequest("gate-a", "instance-a", "host-a", claim.Lease!.Fence,
                claim.Lease.ResourceNamespace, true, true), "gate-a", default);

        Assert.Equal(GateStates.InfraFailed, contained.Phase);
        Assert.True(GateStates.IsTerminal(contained.Phase));
        Assert.Equal(subject.Subject.SubjectId, contained.Subject.SubjectId);
        await CreateMergeAsync(restarted, clock, 2);
        Assert.Equal("claimed", (await restarted.ClaimGateAsync(
            new GateClaimRequest("gate-a", "instance-a"), "gate-a", default)).Status);
    }

    [Fact]
    public async Task Slot_mode_keeps_one_gate_per_host_and_reserves_no_grant()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await CreateMergeAsync(store, clock, 1);
        await CreateMergeAsync(store, clock, 2);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");

        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);

        Assert.Equal(0, claim.Lease!.GrantHalfCores);
        Assert.Equal("Gate host slot is occupied.", (await store.ClaimGateAsync(
            new GateClaimRequest("gate-a", "instance-a"), "gate-a", default)).Message);
    }

    [Fact]
    public async Task Unknown_gate_admission_mode_fails_closed_at_startup()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, Clock(), new TaskServerOptions { DataDirectory = temp.Path, GateAdmissionMode = "ssh" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync());
    }

    [Fact]
    public async Task Older_store_drops_gate_source_foreign_keys_and_keeps_its_subjects()
    {
        using var temp = new TempDirectory();
        var clock = Clock();
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={first.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                DROP TABLE gate_subjects;
                CREATE TABLE gate_subjects(
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL REFERENCES tasks(id),
                    source_run_id TEXT NOT NULL REFERENCES runs(id),
                    gate_id TEXT NOT NULL,
                    plan_hash TEXT NOT NULL,
                    policy_hash TEXT NOT NULL,
                    subject_json TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    dispatch_deadline TEXT NOT NULL,
                    max_attempts INTEGER NOT NULL,
                    last_fence INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(source_run_id, gate_id, plan_hash)
                );
                INSERT INTO gate_subjects(id, task_id, source_run_id, gate_id, plan_hash, policy_hash,
                    subject_json, created_at, dispatch_deadline, max_attempts, last_fence)
                VALUES ('gsub_legacy', 'task', 'run', 'post-build-test-gate', 'hash', 'policy', '{}',
                    '2026-10-01T00:00:00.0000000Z', '2026-10-01T00:15:00.0000000Z', 2, 3);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path, clock);
        await upgraded.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={upgraded.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT count(*) FROM pragma_foreign_key_list('gate_subjects')),
                       (SELECT subject_kind || ':' || last_fence FROM gate_subjects WHERE id = 'gsub_legacy'),
                       (SELECT count(*) FROM pragma_foreign_key_list('gate_attempts') WHERE "table" = 'gate_subjects');
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal("run:3", reader.GetString(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }
    }

    private static async Task<(GateClaimResponse Claim, byte[] Content)> ClaimMergeAsync(
        TaskServerStore store, ManualTimeProvider clock, string runner, string host)
    {
        var content = RandomNumberGenerator.GetBytes(256);
        var subject = await CreateMergeAsync(store, clock, 1, content);
        await RegisterGateAsync(store, clock, runner, "instance-" + runner, host);
        var claim = await store.ClaimGateAsync(new GateClaimRequest(runner, "instance-" + runner), runner, default);
        Assert.Equal(subject.Subject.SubjectId, claim.Subject!.SubjectId);
        return (claim, content);
    }

    private static async Task<GateStatus> CreateMergeAsync(TaskServerStore store, ManualTimeProvider clock,
        int invocation, byte[]? content = null, int maxAttempts = 2)
    {
        content ??= RandomNumberGenerator.GetBytes(128 + invocation);
        var sha = Hash(content);
        var locator = await store.PutGateBundleAsync(sha, content.Length, Repository,
            new MemoryStream(content), "monolith", default);
        return await store.CreateGateSubjectAsync(MergeRequest(clock, locator, invocation) with
        {
            MaxAttempts = maxAttempts,
        }, "monolith", default);
    }

    private static CreateGateSubjectRequest MergeRequest(ManualTimeProvider clock, GateBundleLocator bundle,
        int invocation = 1)
    {
        var plan = Plan();
        return new CreateGateSubjectRequest("job-7", $"merge:invocation-{invocation}", Repository, null, Sha,
            null, null, null, GateRules.PlanHash(plan), "policy-v1", "3", new string('4', 64), plan,
            clock.GetUtcNow().UtcDateTime.AddMinutes(15), Kind: GateSubjectKinds.Merge, Bundle: bundle);
    }

    private static GatePlan Plan() => new("post-build-test-gate", 1,
        [new GateCommand("build-1", "bash", ["-lc", "true"], 30)], "", 300, [], 4096, "always");

    private static GateSubjectProof Proof(GateSubject subject)
        => new(subject.PlanHash, subject.PolicyHash, subject.ExpectedSha,
            subject.Bundle?.Sha256, subject.Bundle?.SizeBytes);

    private static GateReport Report(GateSubjectProof? proof)
        => new(GateStates.Passed, null, Sha, new string('b', 40), false, false,
            [new GateCommandEvidence("build-1", 0, false, new string('c', 64), "ok",
                Fixed.AddSeconds(-1), Fixed)],
            "host", "toolchain", [new string('c', 64)], [], "clean", proof);

    private static GateAuthority Authority(GateClaimResponse claim)
        => new(claim.Lease!.ExecutorId, claim.Lease.InstanceId, claim.Lease.LeaseId,
            claim.Lease.Fence, claim.Lease.AuthorityEpoch);

    private static async Task<GateAuthority> AdvanceToCleaningAsync(TaskServerStore store, GateClaimResponse claim)
    {
        var authority = Authority(claim);
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running,
                     GateStates.Reporting, GateStates.Cleaning })
            await store.AdvanceGateAsync(claim.Attempt!.AttemptId, new GatePhaseRequest(authority, phase), default);
        return authority;
    }

    private static async Task<GateStatus> ReportAsync(TaskServerStore store, GateClaimResponse claim, GateReport report)
    {
        var authority = await AdvanceToCleaningAsync(store, claim);
        return await store.ReportGateAsync(claim.Attempt!.AttemptId,
            new SubmitGateReportRequest(authority, report), authority.ExecutorId, default);
    }

    private static async Task SetAllocationAsync(TaskServerStore store, string host, string runner,
        int maxParallelism, long allocationVersion, long applied)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runtime_capacity_settings(host_id, max_parallelism, target_load_percent, ramp_strategy,
                version, updated_at, allocation_version)
            VALUES ($host, $max, 80, 'balanced', $version, '2026-10-28T00:00:00.0000000Z', $version)
            ON CONFLICT(host_id) DO UPDATE SET max_parallelism = excluded.max_parallelism,
                version = excluded.version, allocation_version = excluded.allocation_version;
            UPDATE runners SET runtime_capacity_applied_version = $applied WHERE id = $runner;
            """;
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$max", maxParallelism);
        command.Parameters.AddWithValue("$version", allocationVersion);
        command.Parameters.AddWithValue("$applied", applied);
        command.Parameters.AddWithValue("$runner", runner);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RegisterGateAsync(TaskServerStore store, ManualTimeProvider clock,
        string id, string instance, string host)
    {
        string[] keys = [GateCapabilities.Executor, GateCapabilities.GitMaterialization,
            GateCapabilities.BundleMaterialization, CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess, CapabilityProtocol.TaskServerConnectivity,
            CapabilityProtocol.Disk];
        await store.RegisterRunnerAsync(id, new RegisterRunnerRequest(
            id, host, instance, "1.0", TaskServerProtocol.Current, keys), id, default);
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            id, instance, CapabilityProtocol.CurrentSchemaVersion,
            clock.GetUtcNow().UtcDateTime, 300, 1,
            keys.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray()), id, default);
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static ManualTimeProvider Clock() => new(DateTimeOffset.Parse("2026-10-28T12:00:00Z"));

    private static TaskServerStore Store(string path, TimeProvider clock, TaskServerOptions? options = null)
        => new(Options.Create(options ?? new TaskServerOptions { DataDirectory = path }), clock);
}
