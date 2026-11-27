using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReadOnlyPostAdmissionTests : IDisposable
{
    private readonly RouteAdmissionFixture _fixture = new();

    public static TheoryData<string, string, string> Helpers => new()
    {
        { "/api/projects/{projectName}/execution-readiness-probe", "/api/projects/alpha/execution-readiness-probe", IamPermissions.ProjectRead },
        { "/api/projects/{projectName}/deployment/compile", "/api/projects/alpha/deployment/compile", IamPermissions.ProjectRead },
        { "/api/token-pricing/calculate", "/api/token-pricing/calculate", IamPermissions.WorkspaceRead },
        { "/api/token-pricing/what-if", "/api/token-pricing/what-if", IamPermissions.WorkspaceRead },
    };

    [Theory]
    [MemberData(nameof(Helpers))]
    public async Task Viewer_browser_service_account_and_api_token_may_read(string pattern, string path, string permission)
    {
        var project = path.StartsWith("/api/projects/", StringComparison.Ordinal) ? "alpha" : "*";
        var session = _fixture.Viewer(project);
        Assert.True((await _fixture.Run("POST", pattern, path, session, permission: permission)).Called);
        var account = _fixture.Store.CreateServiceAccount(new CreateServiceAccountRequest("reader", null,
            [new IamGrantRequest(IamRoles.Viewer, project == "*" ? IamScope.Workspace : IamScope.ForProject(project))]), _fixture.OwnerId);
        var service = _fixture.Store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null)).Secret;
        Assert.True((await _fixture.Run("POST", pattern, path, service, bearer: true, permission: permission)).Called);
        var token = _fixture.Store.CreateApiToken(new CreateApiTokenRequest("reader", StudioRoles.Viewer,
            [project], null), _fixture.OwnerId).Secret;
        Assert.True((await _fixture.Run("POST", pattern, path, token, bearer: true, permission: permission)).Called);
        Assert.Equal("csrf-invalid", (await _fixture.Run("POST", pattern, path, session, csrf: false, permission: permission)).Error);
        Assert.False((await _fixture.Run("POST", pattern, path, session, permission: permission, origin: "https://foreign.test")).Called);
    }

    [Theory]
    [InlineData("execution-readiness-probe")]
    [InlineData("deployment/compile")]
    public async Task Foreign_projects_stay_refused(string suffix)
    {
        var viewer = _fixture.Viewer("alpha");
        Assert.False((await _fixture.Run("POST", "/api/projects/{projectName}/" + suffix,
            "/api/projects/beta/" + suffix, viewer, permission: IamPermissions.ProjectRead)).Called);
    }

    [Fact]
    public async Task Read_permission_alone_does_not_admit_a_mutation_and_acceptance_readiness_is_unchanged()
    {
        var viewer = _fixture.Viewer("alpha");
        Assert.Equal("role-denied", (await _fixture.Run("POST", "/api/projects/{projectName}/mutate",
            "/api/projects/alpha/mutate", viewer, permission: IamPermissions.ProjectRead)).Error);
        Assert.True((await _fixture.Run("POST", "/api/tasks/acceptance-readiness", "/api/tasks/acceptance-readiness", viewer)).Called);
    }

    public void Dispose() => _fixture.Dispose();
}
