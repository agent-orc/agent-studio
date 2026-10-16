using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class CredentialInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Private_review_requires_a_fresh_read_capability_on_each_review_host()
    {
        var capability = ReviewRepositoryAccessPolicy.ReadCapabilityPrefix + "github.com/team/private";
        var reviews = new[] { new CredentialInventoryPolicy.PrivateReview("github.com/team/private", "AGT-1") };
        var hosts = new[]
        {
            new CredentialInventoryPolicy.Host("review-a", new HashSet<string> { ReviewCapabilities.ReviewExecutor },
                new HashSet<string>()),
            new CredentialInventoryPolicy.Host("review-b", new HashSet<string> { ReviewCapabilities.ReviewExecutor },
                new HashSet<string> { capability }),
            new CredentialInventoryPolicy.Host("gate-a", new HashSet<string> { GateCapabilities.Executor },
                new HashSet<string>()),
        };
        var items = CredentialInventoryPolicy.Derive([], hosts, reviews, [], Now.UtcDateTime);
        var missing = Assert.Single(items, item => item.State == "missing");
        Assert.Equal("review-a", missing.HostId);
        Assert.Equal(["AGT-1"], missing.AffectedCards);
        Assert.Equal("read", missing.Permission);
        Assert.DoesNotContain(items, item => item.HostId == "gate-a");
        Assert.Contains(items, item => item.HostId == "review-b" && item.State == "ok");
    }

    [Fact]
    public void Integrator_write_is_required_per_registered_repository_and_review_write_is_flagged()
    {
        var observed = new CredentialInventoryItemDto("key", "repository", "review-a", "github_deploy_key",
            "review key", [], "github.com/team/private", "write", null, null, "ok", false, "verified", []);
        var items = CredentialInventoryPolicy.Derive([observed],
            [new("review-a", new HashSet<string> { ReviewCapabilities.ReviewExecutor }, new HashSet<string>())],
            [], [new("github.com/team/private", "develop", "main")], Now.UtcDateTime);
        Assert.Contains(items, item => item.Id == "key" && item.State == "unexpected");
        var required = Assert.Single(items, item => item.Kind == "integrator_write");
        Assert.Equal(["develop", "main"], required.Scopes);
        Assert.Equal("control", required.HostId);
    }

    [Fact]
    public void Mixed_host_flags_only_a_write_grant_bound_to_review_or_gate()
    {
        var host = new CredentialInventoryPolicy.Host("shared",
            new HashSet<string> { ReviewCapabilities.CodingExecutor, ReviewCapabilities.ReviewExecutor },
            new HashSet<string>());
        var coding = new CredentialInventoryItemDto("coding", "repository", "shared", "github_deploy_key",
            "Coding key", ["coding:execution"], "github.com/team/product", "write", null, null,
            "ok", false, "Verified", []);
        var review = coding with { Id = "review", Scopes = ["review:materialization"] };
        var items = CredentialInventoryPolicy.Derive([coding, review], [host], [], [], Now.UtcDateTime);
        Assert.Contains(items, item => item.Id == "coding" && item.State == "ok");
        Assert.Contains(items, item => item.Id == "review" && item.State == "unexpected");
    }

    [Fact]
    public void Expired_integrator_write_credential_leaves_a_required_gap()
    {
        var repository = new CredentialInventoryPolicy.Repository("github.com/team/product", "develop", "main");
        var credential = new CredentialInventoryItemDto("token", "repository", "control", "github_https_token",
            "Integrator PAT", [], repository.Canonical, "write", Now.UtcDateTime.AddMinutes(-1),
            Now.UtcDateTime.AddDays(-1), "expiring", false, "Expired", []);
        var items = CredentialInventoryPolicy.Derive([credential], [], [], [repository], Now.UtcDateTime);
        Assert.Contains(items, item => item.Kind == "integrator_write" && item.State == "missing");

        var renewed = credential with { ExpiresAt = Now.UtcDateTime.AddDays(1) };
        Assert.DoesNotContain(CredentialInventoryPolicy.Derive([renewed], [], [], [repository], Now.UtcDateTime),
            item => item.Kind == "integrator_write");
    }

    [Fact]
    public async Task Registered_private_repository_requires_review_read_before_an_attempt_exists()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Now);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Inventory"), "owner", default);
        await store.RegisterProjectRepositoryAsync(new RegisterProjectRepositoryRequest(
            "prj-inventory", workspace.WorkspaceId, "Inventory", "INV", "https://github.com/team/private.git",
            "develop", "main", RepositoryVisibility: "private"), "owner", default);
        await store.RegisterRunnerAsync("review-a", new RegisterRunnerRequest("review-a", "host-review-a",
            "review-a:1", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.ReviewExecutor, ReviewCapabilities.GitMaterialization,
                ReviewCapabilities.SemanticReview]), "review-a", default);

        var inventory = await store.GetCredentialInventoryAsync(StudioUserRoles.Owner, default);
        var gap = Assert.Single(inventory.Items, item => item.Id ==
            "required:review:host-review-a:github.com/team/private");
        Assert.Equal("missing", gap.State);
        Assert.Empty(gap.AffectedCards);
    }

    [Fact]
    public async Task Inventory_response_omits_the_principal_secret_and_verifier_columns()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Now);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var issued = await store.EnsureBootstrapPrincipalAsync("engine-inventory", TaskServerPrincipalKinds.Engine,
            null, null, default);
        Assert.NotNull(issued?.Credential);
        var response = await store.GetCredentialInventoryAsync(StudioUserRoles.Owner, default);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("engine-inventory", json);
        Assert.DoesNotContain(issued.Credential!, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token_hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("code_hash", json, StringComparison.Ordinal);
    }
}
