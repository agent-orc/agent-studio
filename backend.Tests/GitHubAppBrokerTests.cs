using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The GitHub App flow against a scripted GitHub: what the browser is sent to,
/// what gets adopted, and what a token may do. Keys are generated per test run.
/// </summary>
public sealed class GitHubAppBrokerTests
{
    private const string Origin = "http://localhost:4011";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github;
    private readonly MemoryVault _vault = new();
    private readonly GitHubAppBroker _broker;

    public GitHubAppBrokerTests()
    {
        _github = new FakeGitHub(_clock);
        _broker = new GitHubAppBroker(
            new GitHubAppClient(new HttpClient(_github), _clock),
            _vault,
            new Catalog(
            [
                new("Site", "https://github.com/Owner/Site.git"),
                new("Core", "https://github.com/owner/core"),
                new("Local", null),
                new("Elsewhere", "https://gitlab.com/owner/elsewhere.git"),
                new("Workspace evidence", "https://github.com/owner/evidence", StudioOnly: true),
            ]),
            _clock);
    }

    [Fact]
    public async Task The_manifest_asks_only_for_what_the_hosts_need()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);

        Assert.StartsWith("https://github.com/settings/apps/new?state=", form.ActionUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(form.State), form.ActionUrl, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(form.Manifest);
        var json = manifest.RootElement;
        Assert.Equal(Origin + "/github-app/callback", json.GetProperty("redirect_url").GetString());
        Assert.Equal(Origin + "/github-app/callback", json.GetProperty("setup_url").GetString());
        Assert.Equal(Origin + "/github-app/callback", json.GetProperty("callback_urls")[0].GetString());
        Assert.True(json.GetProperty("public").GetBoolean());
        Assert.False(json.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
        Assert.Empty(json.GetProperty("default_events").EnumerateArray());
        Assert.Equal(
            new[] { "contents=write", "metadata=read", "workflows=write" },
            json.GetProperty("default_permissions").EnumerateObject()
                .Select(item => item.Name + "=" + item.Value.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_organization_owns_the_app_when_named()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, "agent-orc", false), default);

        Assert.StartsWith("https://github.com/organizations/agent-orc/settings/apps/new?state=", form.ActionUrl, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(form.Manifest);
        Assert.False(manifest.RootElement.GetProperty("public").GetBoolean());
        Assert.Equal("agent-studio-agent-orc", manifest.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("http://studio.example.com")]
    [InlineData("https://studio.example.com/path")]
    [InlineData("https://user:pw@studio.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public async Task GitHub_only_returns_to_a_loopback_or_https_origin(string? origin)
    {
        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(origin, null), default));
        Assert.Equal("invalid-origin", failure.Code);
    }

    [Theory]
    [InlineData("https://studio.example.com")]
    [InlineData("https://studio.example.com/")]
    [InlineData("http://127.0.0.1:4011")]
    public async Task A_loopback_or_https_origin_is_accepted(string origin)
        => await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(origin, null), default);

    [Theory]
    [InlineData("-leading")]
    [InlineData("../other")]
    [InlineData("a/b")]
    public async Task A_malformed_organization_never_reaches_a_url(string organization)
    {
        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, organization), default));
        Assert.Equal("invalid-account", failure.Code);
    }

    [Fact]
    public async Task The_browser_flow_ends_with_a_verified_installation_and_no_user_token()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);

        var registered = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, form.State, "manifest-code", null, null), "owner", default);
        Assert.Contains("/app-manifests/manifest-code/conversions", _github.Paths);
        Assert.StartsWith($"https://github.com/apps/{FakeGitHub.Slug}/installations/new?state=", registered.NextUrl, StringComparison.Ordinal);
        Assert.Equal(_github.PrivateKey, (await _vault.ReadSecretsAsync(default))!.PrivateKeyPem);

        var installed = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(
            Origin, Query(registered.NextUrl!, "state"), null, FakeGitHub.InstallationId, "install"), "owner", default);
        var authorize = installed.NextUrl!;
        Assert.StartsWith("https://github.com/login/oauth/authorize?", authorize, StringComparison.Ordinal);
        Assert.Equal(FakeGitHub.ClientId, Query(authorize, "client_id"));
        Assert.Equal(Origin + "/github-app/callback", Query(authorize, "redirect_uri"));
        Assert.Equal("S256", Query(authorize, "code_challenge_method"));
        Assert.Empty((await _vault.LoadConnectionAsync(default))!.Installations);

        var done = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, Query(authorize, "state"), "user-code", null, null), "owner", default);

        Assert.Null(done.NextUrl);
        Assert.Equal(FakeGitHub.InstallationId, Assert.Single(done.Status.Installations).Id);
        Assert.Equal("owner", done.Status.Installations[0].AdoptedBy);
        var exchange = HttpUtility.ParseQueryString(_github.UserTokenBody!);
        Assert.Equal(_github.ClientSecret, exchange["client_secret"]);
        Assert.Equal(Query(authorize, "code_challenge"),
            Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"]!))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        Assert.True(_github.UserTokenRevoked);
    }

    [Fact]
    public async Task Status_holds_no_secret_at_any_step()
    {
        await ConnectAsync();
        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default);

        var status = JsonSerializer.Serialize(await _broker.StatusAsync(false, default));

        Assert.NotEmpty(issued);
        Assert.DoesNotContain(_github.PrivateKey, status, StringComparison.Ordinal);
        Assert.DoesNotContain(_github.ClientSecret, status, StringComparison.Ordinal);
        Assert.DoesNotContain(issued[0].Token, status, StringComparison.Ordinal);
        Assert.DoesNotContain(issued[0].Token, issued[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_github.PrivateKey, new GitHubAppSecrets(_github.PrivateKey, "x").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forged_installation_number_is_not_adopted()
    {
        await RegisterAsync();
        var step = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, null, null, 999, "install"), "owner", default);

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() => _broker.AdvanceAsync(
            new AdvanceGitHubAppFlowRequest(Origin, Query(step.NextUrl!, "state"), "user-code", null, null), "owner", default));

        Assert.Equal("installation-not-reachable", failure.Code);
        Assert.Empty((await _vault.LoadConnectionAsync(default))!.Installations);
        Assert.True(_github.UserTokenRevoked);
    }

    [Fact]
    public async Task An_installation_of_another_app_is_not_adopted()
    {
        await RegisterAsync();
        _github.UserInstallations.Add((555, FakeGitHub.AppId + 1, "stranger"));
        var step = await _broker.StartAuthorizationAsync(new StartGitHubAppStepRequest(Origin), default);

        var done = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, Query(step.NextUrl!, "state"), "user-code", null, null), "owner", default);

        Assert.Equal(new[] { FakeGitHub.InstallationId }, done.Status.Installations.Select(item => item.Id));
    }

    [Fact]
    public async Task A_return_url_works_once()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);
        await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, form.State, "manifest-code", null, null), "owner", default);

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, form.State, "manifest-code", null, null), "owner", default));

        Assert.Equal("flow-expired", failure.Code);
        Assert.Single(_github.Paths, path => path.EndsWith("/conversions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_return_url_expires_after_an_hour()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);
        _clock.Advance(TimeSpan.FromMinutes(61));

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, form.State, "manifest-code", null, null), "owner", default));

        Assert.Equal("flow-expired", failure.Code);
        Assert.Empty(_github.Paths);
    }

    [Fact]
    public async Task A_stale_second_registration_cannot_replace_the_connection()
    {
        var first = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);
        var second = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);
        await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, first.State, "manifest-code", null, null), "owner", default);
        var key = (await _vault.ReadSecretsAsync(default))!.PrivateKeyPem;

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, second.State, "manifest-code", null, null), "owner", default));

        Assert.Equal("already-connected", failure.Code);
        Assert.Equal(key, (await _vault.ReadSecretsAsync(default))!.PrivateKeyPem);
    }

    [Fact]
    public async Task Another_app_cannot_be_swapped_in_while_connected()
    {
        await ConnectAsync();
        _github.AppIdOnFile = FakeGitHub.AppId + 1;

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() => _broker.ConnectExistingAsync(
            new ConnectExistingGitHubAppRequest(FakeGitHub.AppId + 1, FakeGitHub.ClientId, "x", _github.PrivateKey), "owner", default));

        Assert.Equal("already-connected", failure.Code);
        Assert.Equal(_github.ClientSecret, (await _vault.ReadSecretsAsync(default))!.ClientSecret);
    }

    [Fact]
    public async Task The_same_apps_key_can_be_replaced_and_its_installations_stay()
    {
        await ConnectAsync();

        var status = await _broker.ConnectExistingAsync(new ConnectExistingGitHubAppRequest(
            FakeGitHub.AppId, FakeGitHub.ClientId, "rotated-secret", _github.PrivateKey), "owner", default);

        Assert.Single(status.Installations);
        Assert.Equal("rotated-secret", (await _vault.ReadSecretsAsync(default))!.ClientSecret);
    }

    [Fact]
    public async Task A_token_in_a_remote_url_never_reaches_the_page()
    {
        var secret = "ghp-" + Guid.NewGuid().ToString("N");
        var broker = new GitHubAppBroker(new GitHubAppClient(new HttpClient(_github), _clock), _vault,
            new Catalog(
            [
                new("Evidence", $"https://person:{secret}@github.com/owner/evidence", StudioOnly: true),
                new("Elsewhere", $"https://person:{secret}@gitlab.com/owner/elsewhere.git"),
            ]),
            _clock);

        var disconnected = JsonSerializer.Serialize(await broker.StatusAsync(false, default));
        await RegisterAsync();
        var connected = JsonSerializer.Serialize(await broker.StatusAsync(true, default));

        Assert.DoesNotContain(secret, disconnected, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, connected, StringComparison.Ordinal);
        Assert.Contains("https://github.com/owner/evidence", connected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hung_github_costs_only_the_repository_waiting_for_it()
    {
        await ConnectAsync();
        var cached = await _broker.IssueAllAsync(RepositoryAccess.Read, null, default);
        _clock.Advance(TimeSpan.FromMinutes(11));
        _github.HangTokensFor.Add("core");
        _broker.IssueBudget = TimeSpan.FromMilliseconds(300);
        _github.TokenRequests.Clear();

        // Write tokens are new, so both repositories call GitHub; only one of them hangs.
        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, null, default);
        Assert.Equal("https://github.com/Owner/Site.git", Assert.Single(issued).RepositoryUrl);

        // A cached token does not wait for GitHub at all.
        var again = await _broker.IssueAllAsync(RepositoryAccess.Read, null, default);
        Assert.Equal(cached.Select(item => item.Token), again.Select(item => item.Token));
        Assert.False(_github.HangReleased);
    }

    [Fact]
    public async Task A_hung_github_leaves_the_status_page_answering()
    {
        await ConnectAsync();
        _clock.Advance(TimeSpan.FromMinutes(11));
        _github.HangInstallationLookups = true;
        _broker.StatusBudget = TimeSpan.FromMilliseconds(200);

        var status = await _broker.StatusAsync(false, default);

        Assert.NotNull(status.Problem);
        Assert.All(status.Repositories.Where(item => item.RepositoryUrl?.Contains("github.com") == true),
            item => Assert.Equal(GitHubAppCoverageStates.Unknown, item.State));
    }

    [Fact]
    public async Task A_host_bound_to_some_projects_receives_tokens_only_for_their_repositories()
    {
        await ConnectAsync();

        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default,
            admits: repository => repository.Project == "Site");

        // SM-9: the Core repository is reachable too, but its project is not bound to this host.
        Assert.Equal(["https://github.com/Owner/Site.git"], issued.Select(item => item.RepositoryUrl));
        Assert.Empty(await _broker.IssueAllAsync(RepositoryAccess.Read, "runner-2", default, admits: _ => false));
    }

    [Fact]
    public async Task A_token_minted_while_disconnecting_is_neither_handed_out_nor_kept()
    {
        await ConnectAsync();
        _github.BeforeTokenAnswer = () => _broker.DisconnectAsync("owner", default);

        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default);

        Assert.Empty(issued);
        Assert.NotEmpty(_github.RevokedTokens);
        Assert.Empty(await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default));
    }

    [Fact]
    public async Task A_token_minted_just_before_a_disconnect_is_not_handed_out_either()
    {
        await ConnectAsync();
        var answered = _github.TokensAnswered;
        _github.BeforeTokenAnswer = async () =>
        {
            // The other repository's token comes back first, so the disconnect finds it cached.
            for (var round = 0; round < 3000 && _github.TokensAnswered == answered; round++) await Task.Delay(10);
            await Task.Delay(100);
            await _broker.DisconnectAsync("owner", default);
        };

        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default);

        Assert.Empty(issued);
        Assert.Equal(2, _github.RevokedTokens.Count);
        Assert.Empty((await _broker.StatusAsync(false, default)).Hosts);
    }

    [Fact]
    public async Task A_refresh_cannot_be_used_to_spend_the_github_quota()
    {
        await ConnectAsync();

        await _broker.StatusAsync(true, default);
        var first = _github.Paths.Count;
        await _broker.StatusAsync(true, default);
        await _broker.StatusAsync(true, default);

        Assert.Equal(first, _github.Paths.Count);
        _clock.Advance(TimeSpan.FromSeconds(31));
        await _broker.StatusAsync(true, default);
        Assert.True(_github.Paths.Count > first);
    }

    [Fact]
    public async Task An_App_that_cannot_be_saved_is_named_and_connecting_again_works()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);
        _vault.FailSaves = true;

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, form.State, "manifest-code", null, null), "owner", default));

        Assert.Equal("registration-not-saved", failure.Code);
        Assert.Contains("The disk refused.", failure.Message);
        Assert.Contains("https://github.com/", failure.Message);
        Assert.Null(await _vault.LoadConnectionAsync(default));

        _vault.FailSaves = false;
        await RegisterAsync();
        Assert.NotNull(await _vault.LoadConnectionAsync(default));
    }

    [Fact]
    public async Task Registration_stops_before_GitHub_when_the_secret_store_cannot_write()
    {
        _vault.Unwritable = new InvalidOperationException("No key-encryption key is configured.");

        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default));

        Assert.Equal("secret-store-unavailable", failure.Code);
        Assert.Equal(503, failure.StatusCode);
        Assert.Contains("No key-encryption key is configured.", failure.Message);
        Assert.Empty(_github.Paths);
        Assert.Null(await _vault.LoadConnectionAsync(default));

        _vault.Unwritable = null;
        await RegisterAsync();
        Assert.NotNull(await _vault.LoadConnectionAsync(default));
    }

    [Fact]
    public async Task A_second_registration_is_refused_while_connected()
    {
        await RegisterAsync();
        var failure = await Assert.ThrowsAsync<GitHubAppException>(() =>
            _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default));
        Assert.Equal("already-connected", failure.Code);
        Assert.Equal(409, failure.StatusCode);
    }

    [Fact]
    public async Task A_changed_selection_on_a_known_installation_needs_no_new_consent()
    {
        await ConnectAsync();
        var step = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, null, null, FakeGitHub.InstallationId, "update"), "owner", default);
        Assert.Null(step.NextUrl);
        Assert.NotNull(step.Message);
    }

    [Fact]
    public async Task An_installation_waiting_for_an_owner_says_so()
    {
        await RegisterAsync();
        var step = await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, null, null, null, "request"), "owner", default);
        Assert.Null(step.NextUrl);
        Assert.Contains("owner", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_token_can_only_read_and_a_write_token_reaches_one_repository()
    {
        await ConnectAsync();

        var read = await _broker.IssueAllAsync(RepositoryAccess.Read, "review-1", default);
        var write = await _broker.IssueAllAsync(RepositoryAccess.Write, "coding-1", default);

        Assert.Equal(
            new[] { "https://github.com/Owner/Site.git", "https://github.com/owner/core.git" },
            read.Select(item => item.RepositoryUrl));
        Assert.All(read.Concat(write), item => Assert.Equal("x-access-token", item.Username));
        Assert.Equal(4, _github.TokenRequests.Count);
        Assert.All(_github.TokenRequests, request => Assert.Equal(FakeGitHub.InstallationId, request.InstallationId));
        Assert.All(_github.TokenRequests, request => Assert.Single(request.Repositories));
        Assert.Equal(["contents=read", "metadata=read"], _github.TokenRequests[0].Permissions);
        Assert.Equal(["contents=write", "metadata=read", "workflows=write"], _github.TokenRequests[2].Permissions);
    }

    [Fact]
    public async Task A_runner_host_never_receives_the_studio_hosts_own_repository()
    {
        _github.RepositoryInstallations["owner/evidence"] = FakeGitHub.InstallationId;
        await ConnectAsync();

        var host = await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default);
        var studio = await _broker.IssueAllAsync(RepositoryAccess.Write, "studio-host", default, includeStudioOnly: true);

        Assert.DoesNotContain(host, item => item.RepositoryUrl.Contains("evidence", StringComparison.Ordinal));
        Assert.Contains(studio, item => item.RepositoryUrl.Contains("evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_attempt_gets_a_token_of_its_own_that_nothing_else_shares()
    {
        await ConnectAsync();

        var shared = await _broker.IssueAllAsync(RepositoryAccess.Read, null, default);
        var first = await _broker.IssueDedicatedAsync("https://github.com/owner/core.git", RepositoryAccess.Read, default);
        var second = await _broker.IssueDedicatedAsync("https://github.com/owner/core.git", RepositoryAccess.Read, default);

        Assert.NotEqual(first!.Value, second!.Value);
        Assert.DoesNotContain(shared, item => item.Token == first.Value);
        Assert.Null(await _broker.IssueDedicatedAsync("https://github.com/owner/unknown.git", RepositoryAccess.Read, default));
        Assert.Null(await _broker.IssueDedicatedAsync("https://gitlab.com/owner/core.git", RepositoryAccess.Read, default));
    }

    [Fact]
    public async Task Tokens_are_reused_until_they_near_expiry()
    {
        await ConnectAsync();

        var first = await _broker.IssueAllAsync(RepositoryAccess.Read, null, default);
        _clock.Advance(TimeSpan.FromMinutes(30));
        var second = await _broker.IssueAllAsync(RepositoryAccess.Read, null, default);
        Assert.Equal(first.Select(item => item.Token), second.Select(item => item.Token));
        Assert.Equal(2, _github.TokenRequests.Count);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var third = await _broker.IssueAllAsync(RepositoryAccess.Read, null, default);
        Assert.NotEqual(first[0].Token, third[0].Token);
        Assert.Equal(4, _github.TokenRequests.Count);
        Assert.All(third, item => Assert.True(item.ExpiresAtUtc - _clock.GetUtcNow().UtcDateTime >= TimeSpan.FromMinutes(25)));
    }

    [Fact]
    public async Task No_token_for_an_installation_nobody_confirmed()
    {
        await ConnectAsync();
        _github.RepositoryInstallations["owner/core"] = 777;
        var status = await _broker.StatusAsync(true, default);

        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, null, default);

        Assert.Equal("https://github.com/Owner/Site.git", Assert.Single(issued).RepositoryUrl);
        Assert.Equal(GitHubAppCoverageStates.NotAdopted, status.Repositories.Single(item => item.Project == "Core").State);
    }

    [Fact]
    public async Task Status_names_what_each_project_lacks()
    {
        await ConnectAsync();
        _github.RepositoryInstallations.Remove("owner/core");

        var status = await _broker.StatusAsync(true, default);

        Assert.Equal(GitHubAppCoverageStates.Covered, status.Repositories.Single(item => item.Project == "Site").State);
        Assert.Equal(GitHubAppCoverageStates.NotInstalled, status.Repositories.Single(item => item.Project == "Core").State);
        Assert.Equal(GitHubAppCoverageStates.NoRepositoryUrl, status.Repositories.Single(item => item.Project == "Local").State);
        Assert.Equal(GitHubAppCoverageStates.NotGitHub, status.Repositories.Single(item => item.Project == "Elsewhere").State);
    }

    [Fact]
    public async Task One_refused_repository_costs_a_host_nothing_else()
    {
        await ConnectAsync();
        _github.RefuseTokensFor.Add("core");

        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, null, default);

        Assert.Equal("https://github.com/Owner/Site.git", Assert.Single(issued).RepositoryUrl);
    }

    [Fact]
    public async Task A_token_wider_than_requested_is_thrown_away()
    {
        await ConnectAsync();
        _github.GrantExtraPermission = true;

        Assert.Empty(await _broker.IssueAllAsync(RepositoryAccess.Read, null, default));
    }

    [Fact]
    public async Task An_app_without_workflow_rights_gets_tokens_without_them()
    {
        await ConnectAsync();
        var connection = (await _vault.LoadConnectionAsync(default))!;
        await _vault.SaveConnectionAsync(connection with { WritePermissions = ["contents"] }, default);

        await _broker.IssueAllAsync(RepositoryAccess.Write, null, default);

        Assert.Equal(["contents=write", "metadata=read"], _github.TokenRequests[0].Permissions);
    }

    [Fact]
    public async Task Without_an_app_a_host_simply_gets_nothing()
    {
        Assert.Empty(await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default));
        Assert.Empty(_github.Paths);
        var status = await _broker.StatusAsync(false, default);
        Assert.False(status.Connected);
        Assert.Equal(5, status.Repositories.Count);
    }

    [Fact]
    public async Task A_revoked_key_stops_new_tokens_and_is_named_on_the_page()
    {
        await ConnectAsync();
        _vault.Secrets = null;
        _clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Empty(await _broker.IssueAllAsync(RepositoryAccess.Write, null, default));
        Assert.NotNull((await _broker.StatusAsync(false, default)).Problem);
    }

    [Fact]
    public async Task An_existing_app_is_connected_only_after_its_key_proves_it()
    {
        var connected = await _broker.ConnectExistingAsync(new ConnectExistingGitHubAppRequest(
            FakeGitHub.AppId, FakeGitHub.ClientId, "existing-secret", _github.PrivateKey), "owner", default);

        Assert.True(connected.Connected);
        Assert.Empty(connected.Installations);
        Assert.Equal("existing-secret", (await _vault.ReadSecretsAsync(default))!.ClientSecret);
    }

    [Fact]
    public async Task A_key_of_another_app_is_refused()
    {
        var failure = await Assert.ThrowsAsync<GitHubAppException>(() => _broker.ConnectExistingAsync(
            new ConnectExistingGitHubAppRequest(FakeGitHub.AppId + 1, FakeGitHub.ClientId, "x", _github.PrivateKey), "owner", default));
        Assert.Equal("app-mismatch", failure.Code);
        Assert.Null(await _vault.ReadSecretsAsync(default));
    }

    [Fact]
    public async Task A_client_id_of_another_app_is_refused()
    {
        var failure = await Assert.ThrowsAsync<GitHubAppException>(() => _broker.ConnectExistingAsync(
            new ConnectExistingGitHubAppRequest(FakeGitHub.AppId, "Iv1.other", "x", _github.PrivateKey), "owner", default));
        Assert.Equal("app-mismatch", failure.Code);
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nbroken\n-----END RSA PRIVATE KEY-----")]
    public async Task An_unreadable_key_is_named(string pem)
    {
        var failure = await Assert.ThrowsAsync<GitHubAppException>(() => _broker.ConnectExistingAsync(
            new ConnectExistingGitHubAppRequest(FakeGitHub.AppId, FakeGitHub.ClientId, "x", pem), "owner", default));
        Assert.Equal("invalid-private-key", failure.Code);
    }

    [Fact]
    public async Task Disconnecting_revokes_the_tokens_and_forgets_everything()
    {
        await ConnectAsync();
        var issued = await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default);
        var changes = 0;
        _broker.ConnectionChanged += () => changes++;

        await _broker.DisconnectAsync("owner", default);

        Assert.Equal(issued.Count, _github.RevokedTokens.Count);
        Assert.Null(await _vault.LoadConnectionAsync(default));
        Assert.Null(await _vault.ReadSecretsAsync(default));
        Assert.Empty(await _broker.IssueAllAsync(RepositoryAccess.Write, "runner-1", default));
        Assert.Empty((await _broker.StatusAsync(false, default)).Hosts);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("https://github.com/Owner/Repo.git", "Owner", "Repo")]
    [InlineData("https://github.com/Owner/Repo", "Owner", "Repo")]
    [InlineData("https://github.com/Owner/Repo/", "Owner", "Repo")]
    [InlineData("git@github.com:Owner/Repo.git", "Owner", "Repo")]
    [InlineData("ssh://git@github.com/Owner/Repo.git", "Owner", "Repo")]
    public void A_github_remote_names_owner_and_repository(string url, string owner, string name)
    {
        var parsed = GitHubRepositoryName.TryParse(url);
        Assert.Equal(owner, parsed!.Owner);
        Assert.Equal(name, parsed.Name);
        Assert.Equal("github.com/owner/repo", parsed.Canonical);
        Assert.Equal("https://github.com/Owner/Repo.git", parsed.CloneUrl);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo.git")]
    [InlineData("https://github.com.evil.example/owner/repo.git")]
    [InlineData("http://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo/extra")]
    [InlineData("https://github.com/../repo")]
    [InlineData("https://github.com/owner/repo?x=1")]
    [InlineData("C:\\Projects\\repo")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_github_repository(string? url)
        => Assert.Null(GitHubRepositoryName.TryParse(url));

    private async Task RegisterAsync()
    {
        var form = await _broker.StartRegistrationAsync(new StartGitHubAppRegistrationRequest(Origin, null), default);
        await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, form.State, "manifest-code", null, null), "owner", default);
        _github.Paths.Clear();
    }

    private async Task ConnectAsync()
    {
        await RegisterAsync();
        var step = await _broker.StartAuthorizationAsync(new StartGitHubAppStepRequest(Origin), default);
        await _broker.AdvanceAsync(new AdvanceGitHubAppFlowRequest(Origin, Query(step.NextUrl!, "state"), "user-code", null, null), "owner", default);
        _github.Paths.Clear();
    }

    private static string Query(string url, string name) => HttpUtility.ParseQueryString(new Uri(url).Query)[name]!;

    private sealed class Catalog(IReadOnlyList<RegisteredRepository> repositories) : IRegisteredRepositoryCatalog
    {
        public Task<IReadOnlyList<RegisteredRepository>> ListAsync(CancellationToken ct) => Task.FromResult(repositories);
    }

    private sealed class MemoryVault : IGitHubAppVault
    {
        public GitHubAppSecrets? Secrets { get; set; }
        private GitHubAppConnection? _connection;
        public Task<GitHubAppConnection?> LoadConnectionAsync(CancellationToken ct) => Task.FromResult(_connection);
        public bool FailSaves { get; set; }
        public Task SaveConnectionAsync(GitHubAppConnection connection, CancellationToken ct)
        {
            if (FailSaves) throw new IOException("The disk refused.");
            _connection = connection;
            return Task.CompletedTask;
        }
        public Task DeleteConnectionAsync(CancellationToken ct) { _connection = null; return Task.CompletedTask; }
        public Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct) => Task.FromResult(Secrets);
        public Task WriteSecretsAsync(GitHubAppSecrets secrets, string actor, CancellationToken ct) { Secrets = secrets; return Task.CompletedTask; }
        public Task DeleteSecretsAsync(string actor, CancellationToken ct) { Secrets = null; return Task.CompletedTask; }
        public Exception? Unwritable { get; set; }
        public Task EnsureWritableAsync(CancellationToken ct) => Unwritable is null ? Task.CompletedTask : throw Unwritable;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public const long AppId = 7001;
        public const long InstallationId = 11;
        public const string Slug = "agent-studio-test";
        public const string ClientId = "Iv1.testclient";
        private readonly TimeProvider _clock;
        private int _serial;
        private Func<Task>? _beforeTokenAnswer;

        public FakeGitHub(TimeProvider clock)
        {
            _clock = clock;
            using var key = RSA.Create(2048);
            PrivateKey = key.ExportRSAPrivateKeyPem();
            ClientSecret = "client-" + Guid.NewGuid().ToString("N");
        }

        public string PrivateKey { get; }
        public string ClientSecret { get; }
        public List<string> Paths { get; } = [];
        public string? UserTokenBody { get; private set; }
        public bool UserTokenRevoked { get; private set; }
        public List<string> RevokedTokens { get; } = [];
        public bool GrantExtraPermission { get; set; }
        public long AppIdOnFile { get; set; } = AppId;
        public HashSet<string> HangTokensFor { get; } = new(StringComparer.Ordinal);
        public bool HangInstallationLookups { get; set; }
        public bool HangReleased { get; private set; }
        public Func<Task>? BeforeTokenAnswer { set => Volatile.Write(ref _beforeTokenAnswer, value); }
        public int TokensAnswered => Volatile.Read(ref _serial);
        public HashSet<string> RefuseTokensFor { get; } = new(StringComparer.Ordinal);
        public List<(long Id, long AppId, string Account)> UserInstallations { get; } = [(InstallationId, AppId, "owner")];
        public Dictionary<string, long> RepositoryInstallations { get; } = new(StringComparer.Ordinal)
        {
            ["owner/site"] = InstallationId,
            ["owner/core"] = InstallationId,
        };
        public List<(long InstallationId, string[] Repositories, string[] Permissions)> TokenRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var host = request.RequestUri.Host;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Paths) Paths.Add(path);
            if (host == "github.com" && path == "/login/oauth/access_token")
            {
                UserTokenBody = body;
                return Json(new { access_token = "user-" + Guid.NewGuid().ToString("N"), token_type = "bearer" });
            }
            if (path == "/app-manifests/manifest-code/conversions")
                return Json(new
                {
                    id = AppId, slug = Slug, name = Slug, client_id = ClientId, client_secret = ClientSecret, pem = PrivateKey,
                    html_url = "https://github.com/apps/" + Slug, owner = new { login = "owner" },
                    permissions = new { contents = "write", metadata = "read", workflows = "write" },
                }, HttpStatusCode.Created);
            if (path == "/app")
                return Json(new
                {
                    id = AppIdOnFile, slug = Slug, name = Slug, client_id = ClientId, html_url = "https://github.com/apps/" + Slug,
                    owner = new { login = "owner" }, permissions = new { contents = "write", metadata = "read", workflows = "write" },
                });
            if (path == "/user/installations")
                return Json(new
                {
                    installations = UserInstallations.Select(item => new { id = item.Id, app_id = item.AppId, account = new { login = item.Account, type = "User" } }),
                });
            if (path == $"/applications/{ClientId}/token") { UserTokenRevoked = true; return new HttpResponseMessage(HttpStatusCode.NoContent); }
            if (path == "/installation/token")
            {
                lock (RevokedTokens) RevokedTokens.Add(request.Headers.Authorization!.Parameter!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.StartsWith("/repos/", StringComparison.Ordinal) && path.EndsWith("/installation", StringComparison.Ordinal))
            {
                if (HangInstallationLookups) await Task.Delay(Timeout.Infinite, cancellationToken);
                var parts = path.Split('/');
                return RepositoryInstallations.TryGetValue((parts[2] + "/" + parts[3]).ToLowerInvariant(), out var id)
                    ? Json(new { id })
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") };
            }
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                using var parsed = JsonDocument.Parse(body);
                var repositories = parsed.RootElement.GetProperty("repositories").EnumerateArray().Select(item => item.GetString()!).ToArray();
                var permissions = parsed.RootElement.GetProperty("permissions").EnumerateObject()
                    .Select(item => item.Name + "=" + item.Value.GetString()).Order(StringComparer.Ordinal).ToArray();
                lock (TokenRequests) TokenRequests.Add((long.Parse(path.Split('/')[3]), repositories, permissions));
                if (HangTokensFor.Contains(repositories[0].ToLowerInvariant()))
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    HangReleased = true;
                }
                if (RefuseTokensFor.Contains(repositories[0].ToLowerInvariant()))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"Forbidden\"}") };
                if (Interlocked.Exchange(ref _beforeTokenAnswer, null) is { } before) await before();
                var granted = parsed.RootElement.GetProperty("permissions").EnumerateObject()
                    .ToDictionary(item => item.Name, item => item.Value.GetString());
                if (GrantExtraPermission) granted["administration"] = "write";
                return Json(new
                {
                    token = "token-" + Interlocked.Increment(ref _serial) + "-" + Guid.NewGuid().ToString("N"),
                    expires_at = _clock.GetUtcNow().UtcDateTime.AddHours(1).ToString("O"),
                    permissions = granted,
                    repositories = repositories.Select(name => new { name }),
                }, HttpStatusCode.Created);
            }
            throw new InvalidOperationException("Unscripted GitHub call: " + request.Method + " " + request.RequestUri);
        }

        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
