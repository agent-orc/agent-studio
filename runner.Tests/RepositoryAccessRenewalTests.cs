using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RepositoryAccessRenewalTests
{
    [Fact]
    public void Candidate_key_rewrites_only_the_exact_registered_https_origin()
    {
        const string origin = "https://github.com/example/private-workspace.git";
        var environment = PlatformRepositoryGitProof.CandidateGitEnvironment(
            origin, "/protected/fixture-key");

        Assert.Equal(origin, environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("url.git@github.com:example/private-workspace.git.insteadOf",
            environment["GIT_CONFIG_KEY_0"]);
        Assert.Contains("IdentitiesOnly=yes", environment["GIT_SSH_COMMAND"]);
        Assert.Contains("IdentityAgent=none", environment["GIT_SSH_COMMAND"]);
    }

    [Fact]
    public async Task Product_success_does_not_mask_private_workspace_failure()
    {
        var git = new FakeGit { FailingOrigin = "https://github.com/example/private-workspace.git" };
        var bindings = new[]
        {
            new RepositoryAccessBinding("product", "https://github.com/example/product.git", true),
            new RepositoryAccessBinding("workspace", "https://github.com/example/private-workspace.git", false)
        };

        var result = await RepositoryAccessRenewal.ProbeBindingsAsync(bindings, git, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.True(result.Proofs[0].FetchVerified && result.Proofs[0].PushVerified);
        Assert.False(result.Proofs[1].FetchVerified);
        Assert.Equal("workspace", result.Proofs[1].Purpose);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("push")]
    [InlineData("drain")]
    public async Task Failure_preserves_old_generation_and_retry_reuses_candidate(string failAt)
    {
        var host = new FakeHost { FailDrain = failAt == "drain" };
        var admin = new FakeAdmin { FailRegister = failAt == "register" };
        var git = new FakeGit { FailPush = failAt == "push" };
        var journal = new FakeJournal();
        var request = Request();
        var renewal = new RepositoryAccessRenewal(host, admin, git, journal);

        var first = await renewal.RotateAsync(request, CancellationToken.None);
        var second = await renewal.RotateAsync(request, CancellationToken.None);

        Assert.False(first.Completed);
        Assert.False(second.Completed);
        Assert.Equal(1, host.Generated);
        Assert.Equal(failAt == "register" ? 0 : 1, admin.Created);
        Assert.Equal("old", host.ActiveGeneration);
        Assert.Equal(0, admin.Deleted);
    }

    [Fact]
    public async Task Overlap_verifies_exact_origin_then_retires_old_key()
    {
        var host = new FakeHost();
        var admin = new FakeAdmin();
        var git = new FakeGit();
        var renewal = new RepositoryAccessRenewal(host, admin, git, new FakeJournal());

        var result = await renewal.RotateAsync(Request(), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("new", host.ActiveGeneration);
        Assert.Equal(1, admin.Created);
        Assert.Equal(1, admin.Deleted);
        Assert.Equal("https://github.com/example/private-workspace.git", git.LastOrigin);
        Assert.Equal("new", git.LastGeneration);
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Lost_receipt_after_registration_finds_existing_key_on_retry()
    {
        var host = new FakeHost();
        var admin = new FakeAdmin();
        var journal = new FakeJournal { FailRegisteredSaveOnce = true };
        var renewal = new RepositoryAccessRenewal(host, admin, new FakeGit(), journal);

        await Assert.ThrowsAsync<IOException>(() => renewal.RotateAsync(Request(), CancellationToken.None));
        var retry = await renewal.RotateAsync(Request(), CancellationToken.None);

        Assert.True(retry.Completed);
        Assert.Equal(1, admin.Created);
        Assert.Equal(1, host.Generated);
        Assert.Equal(99, retry.NewGitHubKeyId);
        Assert.Equal("SHA256:public", retry.Candidate!.Fingerprint);
        Assert.Equal("provisioning-record-1", retry.ProvisioningCredentialId);
    }

    [Fact]
    public async Task Missing_retirement_authority_remains_guided_until_retry()
    {
        var host = new FakeHost();
        var admin = new FakeAdmin { FailDelete = true };
        var renewal = new RepositoryAccessRenewal(host, admin, new FakeGit(), new FakeJournal());

        var first = await renewal.RotateAsync(Request(), CancellationToken.None);
        Assert.Equal("administrator-action-required", first.Status);
        Assert.True(first.Switched);
        Assert.False(first.OldRetired);
        admin.FailDelete = false;

        var retry = await renewal.RotateAsync(Request(), CancellationToken.None);
        Assert.True(retry.Completed);
        Assert.Equal(1, admin.Created);
        Assert.Equal(1, admin.Deleted);
    }

    [Fact]
    public async Task Cancellation_during_proof_keeps_old_generation_and_resumes()
    {
        var host = new FakeHost();
        var git = new FakeGit { CancelOnce = true };
        var renewal = new RepositoryAccessRenewal(host, new FakeAdmin(), git, new FakeJournal());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renewal.RotateAsync(Request(), CancellationToken.None));
        Assert.Equal("old", host.ActiveGeneration);
        var result = await renewal.RotateAsync(Request(), CancellationToken.None);
        Assert.True(result.Completed);
    }

    private static RepositoryRotationRequest Request() => new(
        "op-1", "host-1", "old", "example", "private-workspace", "workspace",
        "https://github.com/example/private-workspace.git", true, 12, "provisioning-record-1");

    private sealed class FakeHost : IRepositoryKeyHost
    {
        public int Generated { get; private set; }
        public bool FailDrain { get; set; }
        public string ActiveGeneration { get; private set; } = "old";
        public Task<string> ActiveGenerationAsync(RepositoryRotationRequest request, CancellationToken ct)
            => Task.FromResult(ActiveGeneration);
        public Task<RepositoryKeyCandidate> GenerateOrLoadAsync(RepositoryRotationRequest request, CancellationToken ct)
        {
            Generated++;
            return Task.FromResult(new RepositoryKeyCandidate("new", "ssh-ed25519 PUBLIC", "SHA256:public", "host-key-ref"));
        }
        public Task<bool> DrainTransportsAsync(RepositoryRotationRequest request, CancellationToken ct) => Task.FromResult(!FailDrain);
        public Task SwitchAsync(RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct)
        { ActiveGeneration = candidate.Generation; return Task.CompletedTask; }
        public Task RetireAsync(RepositoryRotationRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAdmin : IRepositoryKeyAdministration
    {
        public int Created { get; private set; }
        public int Deleted { get; private set; }
        public bool FailRegister { get; set; }
        public bool FailDelete { get; set; }
        public Task<RegisteredRepositoryKey?> FindByFingerprintAsync(RepositoryRotationRequest request, string fingerprint, CancellationToken ct)
            => Task.FromResult<RegisteredRepositoryKey?>(Created > 0 ? new(99, fingerprint, false) : null);
        public Task<RegisteredRepositoryKey?> RegisterAsync(RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct)
        {
            if (FailRegister) return Task.FromResult<RegisteredRepositoryKey?>(null);
            Created++;
            return Task.FromResult<RegisteredRepositoryKey?>(new(99, candidate.Fingerprint, false));
        }
        public Task DeleteAsync(RepositoryRotationRequest request, long keyId, CancellationToken ct)
        {
            if (FailDelete) throw new RepositoryAdminAuthorityException();
            Deleted++; return Task.CompletedTask;
        }
    }

    private sealed class FakeGit : IRepositoryGitProof
    {
        public string? FailingOrigin { get; set; }
        public bool FailPush { get; set; }
        public bool CancelOnce { get; set; }
        public string? LastOrigin { get; private set; }
        public string? LastGeneration { get; private set; }
        public Task<RepositoryAccessProof> ProveAsync(RepositoryAccessBinding binding, string? generation, CancellationToken ct)
        {
            if (CancelOnce) { CancelOnce = false; throw new OperationCanceledException(); }
            LastOrigin = binding.Origin;
            LastGeneration = generation;
            var ok = binding.Origin != FailingOrigin;
            return Task.FromResult(new RepositoryAccessProof(binding.Purpose, binding.Origin, ok,
                ok && binding.RequiresPush && !FailPush, binding.RequiresPush, ok ? "verified" : "fetch-failed"));
        }
    }

    private sealed class FakeJournal : IRepositoryRotationJournal
    {
        private RepositoryRotationReceipt? _receipt;
        public bool FailRegisteredSaveOnce;
        public Task<RepositoryRotationReceipt?> LoadAsync(string operationId, CancellationToken ct) => Task.FromResult(_receipt);
        public Task SaveAsync(RepositoryRotationReceipt receipt, CancellationToken ct)
        {
            if (FailRegisteredSaveOnce && receipt.NewGitHubKeyId is not null)
            { FailRegisteredSaveOnce = false; throw new IOException("lost receipt"); }
            _receipt = receipt;
            return Task.CompletedTask;
        }
    }
}
