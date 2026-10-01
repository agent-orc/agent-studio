using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// I05 acceptance (docs/deployment-story/index.html, D5 option A): first login,
/// owner bootstrap closure, role denial, stolen/expired enrolment denial,
/// rotation, interrupted setup, loss-of-host recovery, installation identity
/// isolation, and canonical project registration with host probes.
/// </summary>
public sealed class IdentityBootstrapTests
{
    private const string StudioToken = "studio-edge-token-0000000000000000000000000001";
    private const string EngineToken = "engine-token-000000000000000000000000000000001";
    private const string OwnerCode = "owner-code-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherOwnerCode = "owner-code-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OwnerPassword = "owner password long enough";

    // ---- Pure policy matrices -------------------------------------------------

    [Theory]
    [InlineData(true, null, "h1", OwnerBootstrapPolicy.ArmDecision.Closed)]
    [InlineData(true, "h1", "h2", OwnerBootstrapPolicy.ArmDecision.Closed)]
    [InlineData(false, null, null, OwnerBootstrapPolicy.ArmDecision.NoCode)]
    [InlineData(false, null, "h1", OwnerBootstrapPolicy.ArmDecision.Arm)]
    [InlineData(false, "h1", "h1", OwnerBootstrapPolicy.ArmDecision.AlreadyArmed)]
    [InlineData(false, "h1", "h2", OwnerBootstrapPolicy.ArmDecision.KeepArmedIgnoreDifferentFile)]
    public void Arm_policy_never_reopens_or_rotates(
        bool ownerExists, string? armed, string? file, OwnerBootstrapPolicy.ArmDecision expected)
        => Assert.Equal(expected, OwnerBootstrapPolicy.DecideArm(ownerExists, armed, file));

    [Theory]
    [InlineData(true, true, "h", true, true, OwnerBootstrapPolicy.AdmitDecision.AlreadyBootstrapped)]
    [InlineData(false, false, null, false, false, OwnerBootstrapPolicy.AdmitDecision.Admit)]
    [InlineData(false, false, "h", false, false, OwnerBootstrapPolicy.AdmitDecision.CodeRequired)]
    [InlineData(false, true, "h", false, false, OwnerBootstrapPolicy.AdmitDecision.CodeRequired)]
    [InlineData(false, true, null, false, true, OwnerBootstrapPolicy.AdmitDecision.CodeNotArmed)]
    [InlineData(false, true, "h", false, true, OwnerBootstrapPolicy.AdmitDecision.CodeInvalid)]
    [InlineData(false, true, "h", true, true, OwnerBootstrapPolicy.AdmitDecision.Admit)]
    public void Admit_policy_requires_the_armed_code_on_authenticated_installations(
        bool ownerExists, bool requiresCode, string? armed, bool matches, bool presented,
        OwnerBootstrapPolicy.AdmitDecision expected)
        => Assert.Equal(expected, OwnerBootstrapPolicy.DecideAdmit(ownerExists, requiresCode, armed, matches, presented));

    [Theory]
    [InlineData(false, false, false, 60, true, false, EnrolmentExchangePolicy.Decision.Unknown)]
    [InlineData(true, true, false, -60, true, false, EnrolmentExchangePolicy.Decision.AlreadyConsumed)]
    [InlineData(true, false, true, 60, true, false, EnrolmentExchangePolicy.Decision.Revoked)]
    [InlineData(true, false, false, -1, true, false, EnrolmentExchangePolicy.Decision.Expired)]
    [InlineData(true, false, false, 60, false, false, EnrolmentExchangePolicy.Decision.InstallationMismatch)]
    [InlineData(true, false, false, 60, true, true, EnrolmentExchangePolicy.Decision.PrincipalExists)]
    [InlineData(true, false, false, 60, true, false, EnrolmentExchangePolicy.Decision.Issue)]
    public void Enrolment_exchange_policy_matrix(
        bool found, bool consumed, bool revoked, int expiresInSeconds, bool installationMatches,
        bool principalExists, EnrolmentExchangePolicy.Decision expected)
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, EnrolmentExchangePolicy.Decide(
            found, consumed, revoked, now.AddSeconds(expiresInSeconds), now, installationMatches, principalExists));
    }

    [Theory]
    [InlineData("https://GitHub.com/Org/Repo.git/", "https://github.com/Org/Repo.git")]
    [InlineData("https://git.example.com:8443/org/repo", "https://git.example.com:8443/org/repo")]
    [InlineData("ssh://git@GitHub.com/org/repo.git", "ssh://git@github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "ssh://git@github.com/org/repo.git")]
    public void Canonical_repository_urls(string input, string expected)
        => Assert.Equal(expected, ProjectRepositoryPolicy.Canonicalize(input));

    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git")]
    [InlineData("https://token@github.com/org/repo.git")]
    [InlineData("ssh://deploy:pw@github.com/org/repo.git")]
    [InlineData("https://github.com/org/repo.git?token=x")]
    [InlineData("https://github.com/org/repo.git#main")]
    [InlineData("http://github.com/org/repo.git")]
    [InlineData("file:///srv/git/repo.git")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("https://github.com/")]
    [InlineData("")]
    public void Credential_bearing_or_local_repository_urls_are_rejected(string input)
        => Assert.Throws<ArgumentException>(() => ProjectRepositoryPolicy.Canonicalize(input));

    [Theory]
    [InlineData("https://github.com/org/repo.git", true, true, false, ProjectRepositoryProbeVerdicts.Admitted)]
    [InlineData("https://github.com/org/repo.git", true, false, false, ProjectRepositoryProbeVerdicts.PushFailed)]
    [InlineData("https://github.com/org/repo.git", false, true, false, ProjectRepositoryProbeVerdicts.FetchFailed)]
    [InlineData("https://github.com/org/fallback.git", true, true, false, ProjectRepositoryProbeVerdicts.OriginMismatch)]
    [InlineData("https://github.com/org/repo.git", true, true, true, ProjectRepositoryProbeVerdicts.FallbackRemoteOnly)]
    [InlineData("https://github.com/org/fallback.git", true, true, true, ProjectRepositoryProbeVerdicts.FallbackRemoteOnly)]
    public void Probe_policy_never_lets_a_fallback_admit_the_project(
        string observed, bool fetch, bool push, bool fallback, string expected)
        => Assert.Equal(expected, ProjectRepositoryPolicy.DecideProbe(
            "https://github.com/org/repo.git",
            new ProjectRepositoryProbeRequest(observed, observed, fetch, push, fallback)));

    // ---- HTTP acceptance ------------------------------------------------------

    [Fact]
    public async Task First_login_requires_the_installer_code_and_bootstrap_closes_for_good()
    {
        using var temp = new TempDirectory();
        await using (var factory = new IdentityFactory(temp.Path, OwnerCode))
        {
            using var edge = Client(factory, StudioToken);
            var installation = await edge.GetFromJsonAsync<InstallationIdentityDto>("/api/v1/installation");
            Assert.True(installation!.OwnerBootstrapArmed);
            Assert.False(installation.OwnerBootstrapped);

            Assert.Equal("owner-bootstrap-code-required", await ErrorCodeAsync(await edge.PostAsJsonAsync(
                "/api/v1/studio/auth/bootstrap", new StudioBootstrapRequest("owner", OwnerPassword)), HttpStatusCode.Unauthorized));
            Assert.Equal("owner-bootstrap-code-invalid", await ErrorCodeAsync(await edge.PostAsJsonAsync(
                "/api/v1/studio/auth/bootstrap",
                new StudioBootstrapRequest("owner", OwnerPassword, BootstrapCode: OtherOwnerCode)), HttpStatusCode.Unauthorized));

            var owner = await BootstrapOwnerAsync(edge);
            Assert.StartsWith("rcv_", owner.RecoveryCode, StringComparison.Ordinal);
            Assert.DoesNotContain("ats_", owner.SessionToken, StringComparison.Ordinal);

            Assert.Equal("studio-already-bootstrapped", await ErrorCodeAsync(await edge.PostAsJsonAsync(
                "/api/v1/studio/auth/bootstrap",
                new StudioBootstrapRequest("second", OwnerPassword, BootstrapCode: OwnerCode)), HttpStatusCode.Conflict));
        }

        // Re-running the installer with a fresh code file cannot create a new owner.
        await using (var rerun = new IdentityFactory(temp.Path, OtherOwnerCode))
        {
            using var edge = Client(rerun, StudioToken);
            var installation = await edge.GetFromJsonAsync<InstallationIdentityDto>("/api/v1/installation");
            Assert.True(installation!.OwnerBootstrapped);
            Assert.False(installation.OwnerBootstrapArmed);
            Assert.Equal("studio-already-bootstrapped", await ErrorCodeAsync(await edge.PostAsJsonAsync(
                "/api/v1/studio/auth/bootstrap",
                new StudioBootstrapRequest("intruder", OwnerPassword, BootstrapCode: OtherOwnerCode)), HttpStatusCode.Conflict));
            var login = await edge.PostAsJsonAsync("/api/v1/studio/auth/login", new StudioLoginRequest("owner", OwnerPassword));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task Interrupted_setup_keeps_the_original_armed_code_without_implicit_rotation()
    {
        using var temp = new TempDirectory();
        await using (var first = new IdentityFactory(temp.Path, OwnerCode))
        {
            using var edge = Client(first, StudioToken);
            Assert.True((await edge.GetFromJsonAsync<InstallationIdentityDto>("/api/v1/installation"))!.OwnerBootstrapArmed);
        }

        // The setup was interrupted before first login and rerun with a different code file.
        await using var rerun = new IdentityFactory(temp.Path, OtherOwnerCode);
        using var client = Client(rerun, StudioToken);
        Assert.Equal("owner-bootstrap-code-invalid", await ErrorCodeAsync(await client.PostAsJsonAsync(
            "/api/v1/studio/auth/bootstrap",
            new StudioBootstrapRequest("owner", OwnerPassword, BootstrapCode: OtherOwnerCode)), HttpStatusCode.Unauthorized));
        await BootstrapOwnerAsync(client);
    }

    [Fact]
    public async Task Operator_and_viewer_sessions_are_denied_identity_and_project_administration()
    {
        using var temp = new TempDirectory();
        await using var factory = new IdentityFactory(temp.Path, OwnerCode);
        using var edge = Client(factory, StudioToken);
        var owner = await BootstrapOwnerAsync(edge);
        using var ownerClient = Client(factory, StudioToken, owner.SessionToken);

        var created = await ownerClient.PostAsJsonAsync("/api/v1/studio/auth/users",
            new StudioCreateUserRequest("alex", "operator password 1", StudioUserRoles.Operator));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True((await created.Content.ReadFromJsonAsync<StudioAuthUserDto>())!.MustChangePassword);
        Assert.Equal("invalid-request", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync("/api/v1/studio/auth/users",
            new StudioCreateUserRequest("second-owner", "operator password 1", StudioUserRoles.Owner)), HttpStatusCode.BadRequest));

        var login = await (await edge.PostAsJsonAsync("/api/v1/studio/auth/login",
            new StudioLoginRequest("alex", "operator password 1"))).Content.ReadFromJsonAsync<StudioAuthSessionDto>();
        using var operatorClient = Client(factory, StudioToken, login!.SessionToken);

        // The operator keeps ordinary task access.
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync("/api/v1/studio/board")).StatusCode);

        Assert.Equal("owner-role-required", await ErrorCodeAsync(await operatorClient.PostAsJsonAsync(
            "/api/v1/management/enrolments",
            new CreateEnrolmentRequest("runner:x", TaskServerPrincipalKinds.Runner, "x")), HttpStatusCode.Forbidden));
        Assert.Equal("owner-role-required", await ErrorCodeAsync(await operatorClient.PostAsJsonAsync(
            "/api/v1/studio/auth/users",
            new StudioCreateUserRequest("eve", "operator password 1", StudioUserRoles.Viewer)), HttpStatusCode.Forbidden));
        Assert.Equal("owner-role-required", await ErrorCodeAsync(await operatorClient.PostAsJsonAsync(
            "/api/v1/projects/registrations", Registration("prj-a", "https://github.com/org/a.git")), HttpStatusCode.Forbidden));

        // The edge bearer alone, without a human session, administers nothing.
        using var bareEdge = Client(factory, StudioToken);
        Assert.Equal("authentication-required", await ErrorCodeAsync(await bareEdge.PostAsJsonAsync(
            "/api/v1/management/enrolments",
            new CreateEnrolmentRequest("runner:x", TaskServerPrincipalKinds.Runner, "x")), HttpStatusCode.Unauthorized));

        // X-Client-Id is attribution only and never authentication.
        using var hinted = Client(factory, credential: null);
        hinted.DefaultRequestHeaders.Add("X-Client-Id", "owner");
        hinted.DefaultRequestHeaders.Add("X-Studio-Session-Token", owner.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hinted.PostAsJsonAsync(
            "/api/v1/management/enrolments",
            new CreateEnrolmentRequest("runner:x", TaskServerPrincipalKinds.Runner, "x"))).StatusCode);
    }

    [Fact]
    public async Task Enrolment_issues_one_separately_revocable_principal_and_denies_stolen_or_expired_codes()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new IdentityFactory(temp.Path, OwnerCode, clock);
        using var edge = Client(factory, StudioToken);
        var owner = await BootstrapOwnerAsync(edge);
        using var ownerClient = Client(factory, StudioToken, owner.SessionToken);
        var installationId = (await edge.GetFromJsonAsync<InstallationIdentityDto>("/api/v1/installation"))!.InstallationId;
        using var anonymous = Client(factory, credential: null);

        var runnerA = await EnrolAsync(ownerClient, "runner:host-a", "host-a");
        Assert.Equal(installationId, runnerA.InstallationId);
        Assert.StartsWith("enr_", runnerA.EnrolmentCode, StringComparison.Ordinal);

        // The code is not a bearer.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(factory, runnerA.EnrolmentCode).GetAsync("/api/v1/workspaces")).StatusCode);

        var exchanged = await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(runnerA.EnrolmentCode, installationId));
        Assert.Equal(HttpStatusCode.Created, exchanged.StatusCode);
        var hostA = (await exchanged.Content.ReadFromJsonAsync<ExchangedEnrolment>())!.Issued;
        Assert.Equal("host-a", hostA.Principal.RunnerId);
        using var hostAClient = Client(factory, hostA.Credential);
        Assert.Equal(HttpStatusCode.OK, (await hostAClient.GetAsync("/api/v1/workspaces")).StatusCode);

        // A stolen, already-consumed code is denied and the denial is audited.
        Assert.Equal("enrolment-denied", await ErrorCodeAsync(await anonymous.PostAsJsonAsync(
            "/api/v1/enrolments/exchange", new ExchangeEnrolmentRequest(runnerA.EnrolmentCode, installationId)),
            HttpStatusCode.Unauthorized));

        // Re-enrolling an existing principal is a conflict, never an implicit rotation.
        Assert.Equal("principal-exists", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync(
            "/api/v1/management/enrolments",
            new CreateEnrolmentRequest("runner:host-a", TaskServerPrincipalKinds.Runner, "host-a")), HttpStatusCode.Conflict));

        // Expired code.
        var runnerB = await EnrolAsync(ownerClient, "runner:host-b", "host-b", ttl: 60);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("enrolment-denied", await ErrorCodeAsync(await anonymous.PostAsJsonAsync(
            "/api/v1/enrolments/exchange", new ExchangeEnrolmentRequest(runnerB.EnrolmentCode, installationId)),
            HttpStatusCode.Unauthorized));

        // Interrupted host setup: a new code withdraws the earlier unused one.
        var first = await EnrolAsync(ownerClient, "runner:host-c", "host-c");
        var second = await EnrolAsync(ownerClient, "runner:host-c", "host-c");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(first.EnrolmentCode, installationId))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(second.EnrolmentCode, installationId))).StatusCode);

        // Explicit revocation of a pending code.
        var engine = await EnrolAsync(ownerClient, "engine-1", null, TaskServerPrincipalKinds.Engine);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsync(
            $"/api/v1/management/enrolments/{engine.EnrolmentId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(engine.EnrolmentCode, installationId))).StatusCode);

        // Rotation is explicit and the old secret stops working after the overlap.
        var rotated = await (await ownerClient.PostAsJsonAsync("/api/v1/management/principals/runner:host-a/rotate",
            new RotatePrincipalRequest(0))).Content.ReadFromJsonAsync<IssuedPrincipalCredential>();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await hostAClient.GetAsync("/api/v1/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, rotated!.Credential).GetAsync("/api/v1/workspaces")).StatusCode);

        // Loss of host A: revoke only its identity; host C keeps working.
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsync(
            "/api/v1/management/principals/runner:host-a/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(factory, rotated.Credential).GetAsync("/api/v1/workspaces")).StatusCode);
        var audit = await ownerClient.GetFromJsonAsync<List<AuditRecordDto>>("/api/v1/management/audit");
        Assert.Contains(audit!, record => record.Action == "enrolment.denied");
        Assert.DoesNotContain(audit!, record => record.DetailJson.Contains("enr_", StringComparison.Ordinal)
                                                || record.DetailJson.Contains("ats_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_installations_cannot_silently_share_an_identity()
    {
        using var tempA = new TempDirectory();
        using var tempB = new TempDirectory();
        await using var a = new IdentityFactory(tempA.Path, OwnerCode);
        await using var b = new IdentityFactory(tempB.Path, OwnerCode);
        using var edgeA = Client(a, StudioToken);
        using var edgeB = Client(b, StudioToken);
        var idA = (await edgeA.GetFromJsonAsync<InstallationIdentityDto>("/api/v1/installation"))!.InstallationId;
        var idB = (await edgeB.GetFromJsonAsync<InstallationIdentityDto>("/api/v1/installation"))!.InstallationId;
        Assert.NotEqual(idA, idB);

        var ownerA = await BootstrapOwnerAsync(edgeA);
        var ownerB = await BootstrapOwnerAsync(edgeB);
        using var ownerClientA = Client(a, StudioToken, ownerA.SessionToken);
        using var ownerClientB = Client(b, StudioToken, ownerB.SessionToken);
        var enrolmentA = await EnrolAsync(ownerClientA, "runner:shared", "shared");
        var enrolmentB = await EnrolAsync(ownerClientB, "runner:shared", "shared");

        // A code from A presented to B is unknown there.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(b, null).PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(enrolmentA.EnrolmentCode, idB))).StatusCode);
        // A host that expects installation A refuses to join B even with B's valid code.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(b, null).PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(enrolmentB.EnrolmentCode, idA))).StatusCode);

        var issuedA = (await (await Client(a, null).PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(enrolmentA.EnrolmentCode, idA))).Content.ReadFromJsonAsync<ExchangedEnrolment>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(b, issuedA.Issued.Credential).GetAsync("/api/v1/workspaces")).StatusCode);
        // Owner sessions do not cross installations either.
        using var crossed = Client(b, StudioToken, ownerA.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await crossed.GetAsync("/api/v1/studio/auth/users")).StatusCode);
    }

    [Fact]
    public async Task Owner_recovery_replaces_the_password_revokes_sessions_and_rotates_the_code_once()
    {
        using var temp = new TempDirectory();
        await using var factory = new IdentityFactory(temp.Path, OwnerCode);
        using var edge = Client(factory, StudioToken);
        var owner = await BootstrapOwnerAsync(edge);

        Assert.Equal("invalid-recovery", await ErrorCodeAsync(await edge.PostAsJsonAsync("/api/v1/studio/auth/recover",
            new StudioRecoverRequest("owner", "rcv_wrong", "new owner password 1")), HttpStatusCode.Unauthorized));
        var recovered = await edge.PostAsJsonAsync("/api/v1/studio/auth/recover",
            new StudioRecoverRequest("owner", owner.RecoveryCode, "new owner password 1"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var result = (await recovered.Content.ReadFromJsonAsync<StudioRecoverResult>())!;
        Assert.NotEqual(owner.RecoveryCode, result.RecoveryCode);

        using var stale = Client(factory, StudioToken, owner.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/studio/auth/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.PostAsJsonAsync("/api/v1/studio/auth/recover",
            new StudioRecoverRequest("owner", owner.RecoveryCode, "another password 1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.PostAsJsonAsync("/api/v1/studio/auth/login",
            new StudioLoginRequest("owner", OwnerPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await edge.PostAsJsonAsync("/api/v1/studio/auth/login",
            new StudioLoginRequest("owner", "new owner password 1"))).StatusCode);
    }

    [Fact]
    public async Task Project_registration_owns_one_canonical_repository_and_a_fallback_probe_never_admits_it()
    {
        using var temp = new TempDirectory();
        await using var factory = new IdentityFactory(temp.Path, OwnerCode);
        using var edge = Client(factory, StudioToken);
        var owner = await BootstrapOwnerAsync(edge);
        using var ownerClient = Client(factory, StudioToken, owner.SessionToken);
        var workspace = await (await ownerClient.PostAsJsonAsync("/api/v1/workspaces",
            new CreateWorkspaceRequest("Main"))).Content.ReadFromJsonAsync<WorkspaceDto>();

        Assert.Equal("invalid-request", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync("/api/v1/projects/registrations",
            Registration("prj-alpha", "https://bot:ghp_secret@github.com/org/alpha.git", workspace!.WorkspaceId)),
            HttpStatusCode.BadRequest));

        var created = await ownerClient.PostAsJsonAsync("/api/v1/projects/registrations",
            Registration("prj-alpha", "https://GitHub.com/org/alpha.git/", workspace.WorkspaceId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var registration = (await created.Content.ReadFromJsonAsync<ProjectRepositoryDto>())!;
        Assert.Equal("https://github.com/org/alpha.git", registration.RepositoryUrl);
        Assert.Equal(RepositoryIdentityContract.FromUrl(registration.RepositoryUrl), registration.RepositoryId);

        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync("/api/v1/projects/registrations",
            Registration("prj-alpha", "https://github.com/org/alpha.git", workspace.WorkspaceId))).StatusCode);
        Assert.Equal("project-repository-registered", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync(
            "/api/v1/projects/registrations",
            Registration("prj-alpha", "https://github.com/org/alpha.git", workspace.WorkspaceId, integrationRef: "main")),
            HttpStatusCode.Conflict));
        Assert.Equal("repository-owned-by-other-project", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync(
            "/api/v1/projects/registrations",
            Registration("prj-beta", "git@github.com:org/alpha.git".Replace("git@github.com:", "https://github.com/"),
                workspace.WorkspaceId, prefix: "BETA")),
            HttpStatusCode.Conflict));
        Assert.Contains((await ownerClient.GetFromJsonAsync<List<ProjectDto>>("/api/v1/projects"))!,
            project => project.ProjectId == "prj-alpha");

        var enrolment = await EnrolAsync(ownerClient, "runner:probe-host", "probe-host");
        var installationId = enrolment.InstallationId;
        var runner = (await (await Client(factory, null).PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(enrolment.EnrolmentCode, installationId))).Content.ReadFromJsonAsync<ExchangedEnrolment>())!;
        using var runnerClient = Client(factory, runner.Issued.Credential);

        // The registered origin is broken; the runner's fallback remote passes. Not admitted.
        var fallback = await (await runnerClient.PostAsJsonAsync("/api/v1/runners/probe-host/project-probes/prj-alpha",
            new ProjectRepositoryProbeRequest("https://github.com/org/fallback.git", null, true, true, true,
                "fallback https://bot:tok@github.com/org/fallback.git ok")))
            .Content.ReadFromJsonAsync<ProjectRepositoryProbeDto>();
        Assert.False(fallback!.Admitted);
        Assert.Equal(ProjectRepositoryProbeVerdicts.FallbackRemoteOnly, fallback.Verdict);
        Assert.DoesNotContain("tok@", fallback.Detail, StringComparison.Ordinal);

        var pushDenied = await (await runnerClient.PostAsJsonAsync("/api/v1/runners/probe-host/project-probes/prj-alpha",
            new ProjectRepositoryProbeRequest("https://github.com/org/alpha.git", "https://github.com/org/alpha.git", true, false, false)))
            .Content.ReadFromJsonAsync<ProjectRepositoryProbeDto>();
        Assert.Equal(ProjectRepositoryProbeVerdicts.PushFailed, pushDenied!.Verdict);

        var admitted = await (await runnerClient.PostAsJsonAsync("/api/v1/runners/probe-host/project-probes/prj-alpha",
            new ProjectRepositoryProbeRequest("https://github.com/org/alpha.git", "https://github.com/org/alpha.git", true, true, false)))
            .Content.ReadFromJsonAsync<ProjectRepositoryProbeDto>();
        Assert.True(admitted!.Admitted);

        // A runner may report only for its own identity.
        Assert.Equal(HttpStatusCode.Forbidden, (await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/other-host/project-probes/prj-alpha",
            new ProjectRepositoryProbeRequest("https://github.com/org/alpha.git", null, true, true, false))).StatusCode);

        var status = await ownerClient.GetFromJsonAsync<ProjectRepositoryStatusDto>("/api/v1/projects/prj-alpha/repository");
        Assert.Single(status!.Probes);
        Assert.True(status.Probes[0].Admitted);
    }

    [Fact]
    public async Task Loopback_development_store_keeps_first_caller_bootstrap_without_a_code()
    {
        using var temp = new TempDirectory();
        await using var factory = new IdentityFactory(temp.Path, ownerCode: null, authenticated: false);
        using var client = Client(factory, credential: null);
        var response = await client.PostAsJsonAsync("/api/v1/studio/auth/bootstrap",
            new StudioBootstrapRequest("owner", OwnerPassword));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static RegisterProjectRepositoryRequest Registration(
        string projectId, string url, string workspaceId = "wsp-missing", string integrationRef = "develop",
        string prefix = "ALPHA")
        => new(projectId, workspaceId, projectId, prefix, url, integrationRef, "main");

    private static async Task<StudioOwnerBootstrapSessionDto> BootstrapOwnerAsync(HttpClient edge)
    {
        var response = await edge.PostAsJsonAsync("/api/v1/studio/auth/bootstrap",
            new StudioBootstrapRequest("owner", OwnerPassword, BootstrapCode: OwnerCode));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StudioOwnerBootstrapSessionDto>())!;
    }

    private static async Task<IssuedEnrolment> EnrolAsync(
        HttpClient owner, string principalId, string? runnerId, string kind = TaskServerPrincipalKinds.Runner, int? ttl = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/management/enrolments",
            new CreateEnrolmentRequest(principalId, kind, runnerId, ttl));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IssuedEnrolment>())!;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiError>())!.Code;
    }

    private static HttpClient Client(IdentityFactory factory, string? credential, string? session = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        if (credential is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (session is not null)
            client.DefaultRequestHeaders.Add("X-Studio-Session-Token", session);
        return client;
    }

    private sealed class IdentityFactory(
        string dataDirectory, string? ownerCode, TimeProvider? clock = null, bool authenticated = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                };
                if (authenticated)
                {
                    values["AUTH"] = "bearer";
                    values["STUDIO_AUTH_TOKEN"] = StudioToken;
                    values["ENGINE_AUTH_TOKEN"] = EngineToken;
                }
                if (ownerCode is not null) values["OWNER_BOOTSTRAP_CODE"] = ownerCode;
                configuration.AddInMemoryCollection(values);
            });
            if (clock is not null)
                builder.ConfigureTestServices(services => services.AddSingleton(clock));
        }
    }
}
