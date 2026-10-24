using AgentStudio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ContributorAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-product-role-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, string, string?, bool, bool> RouteMatrix => new()
    {
        { "GET", "/api/tasks", null, false, true },
        { "GET", "/api/projects/PROJ-1/chat/scroll", null, false, true },
        { "GET", "/api/projects/PROJ-1/visual-evidence", null, false, true },
        { "POST", "/api/tasks", null, false, true },
        { "PUT", "/api/tasks/card/title", TaskStates.Backlog, false, true },
        { "PUT", "/api/tasks/card/files/prompt.md", TaskStates.Ready, false, true },
        { "POST", "/api/tasks/card/preview-comments", TaskStates.HumanReview, false, true },
        { "POST", "/api/runner/PROJ-1/orchestrator-chat", null, false, true },
        { "PUT", "/api/tasks/card/release", TaskStates.HumanReview, true, true },
        { "PUT", "/api/tasks/card/release", TaskStates.HumanReview, false, false },
        { "PUT", "/api/tasks/card/title", TaskStates.Progress, false, false },
        { "POST", "/api/tasks/card/git/commit", TaskStates.Ready, false, false },
        { "GET", "/api/runner/PROJ-1/orchestrator-log", null, false, false },
        { "GET", "/api/tasks/card/logs/raw", null, false, false },
        { "GET", "/api/tasks/card/LOGS/raw", null, false, false },
        { "GET", "/api/tasks/card/Raw-Logs", null, false, false },
        { "HEAD", "/api/tasks/card/LoGs/raw", null, false, false },
        { "OPTIONS", "/api/tasks/card/RAW-LOGS", null, false, false },
        { "GET", "/api/projects/PROJ-1/SeCrEtS", null, false, false },
        { "GET", "/api/projects/PROJ-1/settings", null, false, false },
        { "GET", "/api/projects/PROJ-1/autonomy", null, false, false },
        { "GET", "/api/v1/management/credential-view", null, false, false },
        { "POST", "/api/auth/invitations", null, false, false },
    };

    [Theory]
    [MemberData(nameof(RouteMatrix))]
    public void Contributor_route_matrix(string method, string path, string? state, bool productionApproval, bool allowed)
        => Assert.Equal(allowed, ContributorAccessPolicy.Allows(method, path, state, productionApproval));

    [Theory]
    [InlineData(TaskStates.Backlog, TaskStates.Ready, false, false, true)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed, false, false, true)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed, true, false, false)]
    [InlineData(TaskStates.Ready, TaskStates.Progress, false, false, false)]
    [InlineData(TaskStates.Progress, TaskStates.Completed, false, false, false)]
    public void Contributor_lane_matrix(string current, string target, bool operatorOverride, bool archiveOverride, bool allowed)
        => Assert.Equal(allowed, ContributorAccessPolicy.AllowsTransition(current, target, operatorOverride, archiveOverride));

    [Fact]
    public void Invitation_is_hashed_single_use_and_expires_with_injected_clock()
    {
        var store = NewStore();
        store.Bootstrap(new BootstrapRequest("owner.account", "owner password phrase!", null));
        var invite = store.InviteUser(new InviteUserRequest("product.user", "Product User", StudioRoles.Contributor, ["PROJ-1"]));
        var persisted = File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.json"));
        Assert.DoesNotContain(invite.Token, persisted);
        var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "new product password!"));
        Assert.Equal(StudioRoles.Contributor, accepted.User.Role);
        Assert.NotNull(store.AuthenticateSession(accepted.SessionToken));
        Assert.Throws<SecurityOperationException>(() => store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "another password phrase!")));

        var expired = store.InviteUser(new InviteUserRequest("expired.user", "Expired", StudioRoles.Contributor, ["PROJ-1"]));
        _clock.Advance(TimeSpan.FromHours(25));
        Assert.Throws<SecurityOperationException>(() => store.AcceptInvitation(new AcceptInvitationRequest(expired.Token, "new expired password!")));
    }

    [Fact]
    public async Task Authenticated_contributor_identity_overrides_attribution_header()
    {
        var store = NewStore();
        store.Bootstrap(new BootstrapRequest("owner.account", "owner password phrase!", null));
        var invite = store.InviteUser(new InviteUserRequest("product.user", "Product User", StudioRoles.Contributor, ["PROJ-1"]));
        var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "new product password!"));
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Profile"] = "networked" }).Build(), store);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = "POST";
        context.Request.Path = "/api/tasks";
        context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={accepted.SessionToken}");
        context.Request.Headers["X-CSRF-Token"] = accepted.CsrfToken;
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        context.Request.Headers["X-Client-Id"] = "forged-operator";
        await middleware.InvokeAsync(context);
        Assert.True(called);
        Assert.Equal(accepted.User.Id, context.Items["ClientId"]);
    }

    private AccessSecurityStore NewStore() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked"
        }).Build(), NullLogger<AccessSecurityStore>.Instance, _clock);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
