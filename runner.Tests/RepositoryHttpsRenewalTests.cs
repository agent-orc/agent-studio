using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RepositoryHttpsRenewalTests
{
    [Fact]
    public async Task Unknown_issuer_expiry_and_private_workspace_denial_keep_old_generation()
    {
        var host = new Host();
        var git = new Git { DenyWorkspace = true };
        var renewal = new RepositoryHttpsRenewal(host, git, new Journal());
        var request = Request();

        var result = await renewal.RotateAsync(request, CancellationToken.None);

        Assert.Null(request.Metadata.IssuerExpiresAt);
        Assert.Equal("unknown", request.Metadata.ExpiryKnowledge);
        Assert.Equal("repository-proof-failed", result.Status);
        Assert.False(result.Completed);
        Assert.Equal("old", host.Active);
        Assert.Equal("workspace", git.Purpose);
        Assert.Equal(request.Metadata.Origin, git.Origin);
    }

    [Fact]
    public async Task Retry_reuses_staged_token_and_only_switches_after_exact_proof()
    {
        var host = new Host();
        var git = new Git { DenyWorkspace = true };
        var renewal = new RepositoryHttpsRenewal(host, git, new Journal());
        var request = Request();

        await renewal.RotateAsync(request, CancellationToken.None);
        git.DenyWorkspace = false;
        var result = await renewal.RotateAsync(request, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, host.Stages);
        Assert.Equal("new", host.Active);
        Assert.Equal(1, host.Retired);
    }

    [Fact]
    public async Task Missing_repository_grant_is_guided_before_staging()
    {
        var host = new Host();
        var request = Request() with { Metadata = Request().Metadata with { PushGrant = false } };
        var result = await new RepositoryHttpsRenewal(host, new Git(), new Journal())
            .RotateAsync(request, CancellationToken.None);
        Assert.Equal("repository-grant-required", result.Status);
        Assert.Equal(0, host.Stages);
    }

    private static RepositoryHttpsRotationRequest Request() => new("op-https", "old",
        new("runner-token", "unknown", "workspace",
            "https://github.com/example/private-workspace.git", true, true, true, null, "unknown"), "old-token");

    private sealed class Host : IRepositoryHttpsCredentialHost
    {
        public string Active = "old";
        public int Stages;
        public int Retired;
        public Task<string> ActiveGenerationAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
            => Task.FromResult(Active);
        public Task<string?> StageAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
        { Stages++; return Task.FromResult<string?>("new"); }
        public Task<bool> DrainTransportsAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
            => Task.FromResult(true);
        public Task SwitchAsync(RepositoryHttpsRotationRequest request, string generation, CancellationToken ct)
        { Active = generation; return Task.CompletedTask; }
        public Task RetireAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
        { Retired++; return Task.CompletedTask; }
    }

    private sealed class Git : IRepositoryGitProof
    {
        public bool DenyWorkspace;
        public string? Origin;
        public string? Purpose;
        public Task<RepositoryAccessProof> ProveAsync(RepositoryAccessBinding binding, string? generation, CancellationToken ct)
        {
            Origin = binding.Origin;
            Purpose = binding.Purpose;
            var ok = !DenyWorkspace;
            return Task.FromResult(new RepositoryAccessProof(binding.Purpose, binding.Origin,
                ok, ok && binding.RequiresPush, binding.RequiresPush,
                ok ? "verified" : "fetch-denied"));
        }
    }

    private sealed class Journal : IRepositoryHttpsRotationJournal
    {
        private RepositoryHttpsRotationReceipt? _receipt;
        public Task<RepositoryHttpsRotationReceipt?> LoadAsync(string operationId, CancellationToken ct)
            => Task.FromResult(_receipt);
        public Task SaveAsync(RepositoryHttpsRotationReceipt receipt, CancellationToken ct)
        { _receipt = receipt; return Task.CompletedTask; }
    }
}
