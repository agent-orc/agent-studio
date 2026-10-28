using AgentStudio.Pipeline;
using AgentStudio.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorCandidateVerificationTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private static readonly DateTimeOffset Now = Clock.GetUtcNow();
    private static readonly string Sha = new('a', 40);

    [Theory]
    [InlineData(StudioRoles.Owner, true)]
    [InlineData(StudioRoles.Operator, true)]
    [InlineData(StudioRoles.Contributor, false)]
    [InlineData(StudioRoles.Viewer, false)]
    public void OnlyAnAuthenticatedOwnerOrOperatorCanRecord(string role, bool allowed)
    {
        var context = new DefaultHttpContext();
        Assert.False(OperatorCandidateVerificationPolicy.HasOperatorAuthority(context));
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(
            new StudioUser
            {
                Id = "person", Username = "person", DisplayName = "Person", Role = role,
                PasswordHash = "unused", CreatedAt = Now.UtcDateTime, PasswordChangedAt = Now.UtcDateTime,
            },
            new StudioSession
            {
                Id = "session", UserId = "person", TokenHash = "unused", CsrfHash = "unused",
                CreatedAt = Now.UtcDateTime, LastSeenAt = Now.UtcDateTime,
                ExpiresAt = Now.AddHours(1).UtcDateTime, AbsoluteExpiresAt = Now.AddHours(1).UtcDateTime,
            });
        Assert.Equal(allowed, OperatorCandidateVerificationPolicy.HasOperatorAuthority(context));
        context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] = new RunnerPrincipal(
            "coding-agent", "Coding agent", "credential", new HashSet<string>());
        Assert.False(OperatorCandidateVerificationPolicy.HasOperatorAuthority(context));
    }

    [Fact]
    public void ValidationRequiresPassingTimedSuitesAndLogDigest()
    {
        var request = Request();
        Assert.Null(OperatorCandidateVerificationPolicy.Validate(request, Now));
        Assert.NotNull(OperatorCandidateVerificationPolicy.Validate(request with
        {
            Suites = [new OperatorCandidateSuite("dotnet test", 10, 1, 0)],
        }, Now));
        Assert.NotNull(OperatorCandidateVerificationPolicy.Validate(request with
        {
            EndedAtUtc = Now.AddMinutes(6),
        }, Now));
        Assert.NotNull(OperatorCandidateVerificationPolicy.Validate(request with { LogHash = "bad" }, Now));
    }

    [Fact]
    public void WorkspaceOperationRequiresAWorkspaceBinding()
    {
        var context = new DefaultHttpContext();
        Assert.False(OperatorCandidateVerificationPolicy.HasWorkspaceScope(context));
        context.Items[AccessSecurityMiddleware.RequestAccessItem] = Access(IamPrincipalKind.User, IamScope.ForProject("alpha"));
        Assert.False(OperatorCandidateVerificationPolicy.HasWorkspaceScope(context));
        context.Items[AccessSecurityMiddleware.RequestAccessItem] = Access(IamPrincipalKind.User, IamScope.Workspace);
        Assert.True(OperatorCandidateVerificationPolicy.HasWorkspaceScope(context));
    }

    [Fact]
    public void SyntheticServiceAccountCannotRecordEvenWithOperatorProjection()
    {
        var context = new DefaultHttpContext();
        var user = new StudioUser
        {
            Id = "service-account:bot", Username = "service-account:bot", DisplayName = "Bot",
            Role = StudioRoles.Operator, PasswordHash = "unused", CreatedAt = Now.UtcDateTime,
            PasswordChangedAt = Now.UtcDateTime,
        };
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(user, new StudioSession
        {
            Id = "bot", UserId = user.Id, TokenHash = "unused", CsrfHash = "unused",
            CreatedAt = Now.UtcDateTime, LastSeenAt = Now.UtcDateTime,
            ExpiresAt = Now.AddHours(1).UtcDateTime, AbsoluteExpiresAt = Now.AddHours(1).UtcDateTime,
        });
        context.Items[AccessSecurityMiddleware.RequestAccessItem] = Access(IamPrincipalKind.ServiceAccount, IamScope.Workspace);
        Assert.False(OperatorCandidateVerificationPolicy.HasOperatorAuthority(context));
    }

    [Fact]
    public void DeveloperProjectionDoesNotGainOperatorAuthorityFromItsLegacyOperatorSummary()
    {
        var context = new DefaultHttpContext();
        var user = new StudioUser
        {
            Id = "person", Username = "person", DisplayName = "Person",
            Role = StudioRoles.Operator, PasswordHash = "unused", CreatedAt = Now.UtcDateTime,
            PasswordChangedAt = Now.UtcDateTime,
        };
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(user, new StudioSession
        {
            Id = "session", UserId = user.Id, TokenHash = "unused", CsrfHash = "unused",
            CreatedAt = Now.UtcDateTime, LastSeenAt = Now.UtcDateTime,
            ExpiresAt = Now.AddHours(1).UtcDateTime, AbsoluteExpiresAt = Now.AddHours(1).UtcDateTime,
        });
        context.Items[AccessSecurityMiddleware.RequestAccessItem] = new RequestAccess(
            new IamSubject(IamPrincipalKind.User, user.Id, new HashSet<string>()), [],
            IamPermissions.WorkspaceOperate, (bound, requested) => bound == requested, IamRoles.Developer);
        Assert.False(OperatorCandidateVerificationPolicy.HasOperatorAuthority(context));
    }

    private static RecordOperatorCandidateRequest Request() => new(
        Sha, ["AGT-3166"], "gate-host", [new("dotnet test", 10, 0, 0)],
        Now.AddMinutes(-40), Now, new string('b', 64));

    private static RequestAccess Access(IamPrincipalKind kind, IamScope scope)
    {
        var id = kind == IamPrincipalKind.User ? "person" : "bot";
        var subject = new IamSubject(kind, id, new HashSet<string>());
        var binding = new RoleBinding
        {
            Id = "binding", PrincipalKind = kind, PrincipalId = id, Role = IamRoles.Admin,
            Scope = scope, Source = IamBindingSources.Manual,
        };
        return new RequestAccess(subject, [binding], IamPermissions.WorkspaceOperate,
            (bound, requested) => string.Equals(bound, requested, StringComparison.OrdinalIgnoreCase), IamRoles.Admin);
    }
}
