using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateEconomicsAccessTests : IDisposable
{
    // Injected clock: account timestamps, lane events and the window read this instant.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-economics-access-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scoped_account_sees_only_member_projects_and_no_foreign_holder()
    {
        var registry = new ProjectRegistry(
            new ConfigurationBuilder().Build(), NullLogger<ProjectRegistry>.Instance);
        var alpha = registry.EnsureProjectForStorage(Path.Combine(_root, "alpha"), "Alpha", DefaultWorkspace.Id);
        registry.EnsureProjectForStorage(Path.Combine(_root, "beta"), "Beta", DefaultWorkspace.Id);
        var alphaJob = Path.Combine(_root, "alpha-job");
        Directory.CreateDirectory(alphaJob);
        // Alpha waits for the machine gate that Beta holds.
        IntegrationLaneEvents.Append(alphaJob, "Alpha", "A-1", "enqueued", sequence: 1,
            deliveredAtUtc: Now.AddMinutes(-10), atUtc: Now.AddMinutes(-9));
        IntegrationLaneEvents.Append(alphaJob, "Alpha", "A-1", "gate-token-waiting", sequence: 1,
            holdingProject: "Beta", atUtc: Now.AddMinutes(-5));
        var context = new DefaultHttpContext();
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = Principal(StudioRoles.Operator, [alpha.Id]);

        var allowed = GateEconomicsEndpoints.AccessFilter(context, registry);
        Assert.NotNull(allowed);
        var visible = new[] { "Alpha", "Beta" }.Where(allowed).ToArray();
        var rows = GateEconomicsEndpoints.Rows(
            visible.Select(name => (name, (IReadOnlyList<string>)(name == "Alpha" ? [alphaJob] : []),
                new GateRetestReport(0, 0, 0, 0, GateResultCache.RetestWindowSize),
                (string?)null, (string?)null)).ToArray(),
            [], since: Now.AddDays(-7), now: Now, allowed);

        Assert.Equal(["Alpha"], visible);
        Assert.Equal([GateEconomicsEndpoints.AllProjects, "Alpha"], rows.Select(row => row.Project));
        Assert.All(rows, row => Assert.Equal(GateEconomicsEndpoints.OtherProject, row.GateHoldingProject));
    }

    [Fact]
    public void Cached_holder_is_redacted_for_each_callers_actual_grants()
    {
        var row = GateEconomicsEndpoints.ProjectRow("Alpha", [],
            since: Now.AddDays(-7), now: Now, retest: null) with { GateHoldingProject = "Archived Beta" };
        var cached = new GateEconomicsResponse("7d", Now, [row]);
        var restricted = GateEconomicsProjection.ForCaller(cached, name => name == "Alpha");
        var granted = GateEconomicsProjection.ForCaller(cached, _ => true);
        Assert.Equal(GateEconomicsEndpoints.OtherProject, restricted.Rows[0].GateHoldingProject);
        Assert.Equal("Archived Beta", granted.Rows[0].GateHoldingProject);
        Assert.Equal("Archived Beta", cached.Rows[0].GateHoldingProject);
    }

    [Fact]
    public void Owner_and_all_projects_grant_see_every_project()
    {
        var registry = new ProjectRegistry(
            new ConfigurationBuilder().Build(), NullLogger<ProjectRegistry>.Instance);
        var owner = new DefaultHttpContext();
        owner.Items[AccessSecurityMiddleware.HumanPrincipalItem] = Principal(StudioRoles.Owner, []);
        var granted = new DefaultHttpContext();
        granted.Items[AccessSecurityMiddleware.HumanPrincipalItem] =
            Principal(StudioRoles.Viewer, [ProjectAccessAuthorization.AllProjectsGrant]);

        Assert.Null(GateEconomicsEndpoints.AccessFilter(owner, registry));
        Assert.Null(GateEconomicsEndpoints.AccessFilter(granted, registry));
        Assert.Null(GateEconomicsEndpoints.AccessFilter(new DefaultHttpContext(), registry));
    }

    private static HumanPrincipal Principal(string role, List<string> projects)
    {
        var at = Now.UtcDateTime;
        var user = new StudioUser
        {
            Id = "usr_scoped", Username = "scoped", DisplayName = "Scoped",
            Role = role, PasswordHash = "unused", Projects = projects,
            CreatedAt = at, PasswordChangedAt = at,
        };
        return new HumanPrincipal(user, new StudioSession
        {
            Id = "sess", UserId = user.Id, TokenHash = "h", CsrfHash = "h",
            CreatedAt = at, LastSeenAt = at, ExpiresAt = at.AddHours(1), AbsoluteExpiresAt = at.AddHours(8),
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
