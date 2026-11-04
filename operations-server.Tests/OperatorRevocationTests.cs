using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;
using Xunit;

namespace OperationsServer.Tests;

/// <summary>
/// Security model SM-2: the control plane's kill switch revokes the
/// Operations Server principals of operator agents and cancels their
/// operations; only a control plane principal with <c>principals.revoke</c>
/// may ask, and the host command does the same without the server.
/// </summary>
public sealed class OperatorRevocationTests
{
    private static OperationsPrincipal Revoker => Fixture.Edge with
    {
        Id = "control-plane", TokenSha256 = OperationsProtocol.Digest("revoker-token"), Scopes = [OperatorPrincipalRevocation.Scope],
    };

    private static OperationsPrincipal Operator(string id, string token, string operatorAgentId) => Fixture.Edge with
    {
        Id = id, TokenSha256 = OperationsProtocol.Digest(token), OperatorAgentId = operatorAgentId,
        Scopes = ["host.inspect", "maintenance.execute", "operations.read", OperatorPrincipalRevocation.Scope],
    };

    [Fact]
    public async Task Revoking_one_operator_agent_refuses_its_principals_cancels_their_operations_and_keeps_the_rest()
    {
        await using var f = await Fixture.Start();
        f.SavePrincipals([.. f.Principals, Revoker, Operator("console-a", "console-a-token", "opa_a"), Operator("console-b", "console-b-token", "opa_b")]);
        using var revoker = f.Client("revoker-token");
        using var consoleA = f.Client("console-a-token");
        using var consoleB = f.Client("console-b-token");
        await f.Register();
        var queued = await (await consoleA.PostAsJsonAsync(Fixture.Api + "commands", f.Command("console-a-key"))).Content.ReadFromJsonAsync<OperationAttempt>();

        var response = await revoker.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path,
            new OperatorPrincipalRevocationRequest(["opa_a"], false, "kill_1"), OperationsProtocol.Json);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<OperatorPrincipalRevocationResponse>(OperationsProtocol.Json))!;

        Assert.Equal(["console-a"], result.RevokedPrincipalIds);
        Assert.Equal(1, result.CancelledAttempts);
        Assert.Equal(HttpStatusCode.Unauthorized, (await consoleA.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await consoleB.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Service.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Null(await f.Poll());
        Assert.NotNull(queued);

        // Idempotent: a retry names the same principals and cancels nothing more.
        var again = await (await revoker.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path,
            new OperatorPrincipalRevocationRequest(["opa_a"], false, "kill_1"), OperationsProtocol.Json))
            .Content.ReadFromJsonAsync<OperatorPrincipalRevocationResponse>(OperationsProtocol.Json);
        Assert.Equal(["console-a"], again!.RevokedPrincipalIds);
        Assert.Equal(0, again.CancelledAttempts);
    }

    [Fact]
    public async Task Revoking_all_operators_refuses_every_principal_that_names_an_operator_agent()
    {
        await using var f = await Fixture.Start();
        f.SavePrincipals([.. f.Principals, Revoker, Operator("console-a", "console-a-token", "opa_a"), Operator("console-b", "console-b-token", "opa_b")]);
        using var revoker = f.Client("revoker-token");
        var result = await (await revoker.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path,
            new OperatorPrincipalRevocationRequest([], true, "kill_2"), OperationsProtocol.Json))
            .Content.ReadFromJsonAsync<OperatorPrincipalRevocationResponse>(OperationsProtocol.Json);
        Assert.Equal(["console-a", "console-b"], result!.RevokedPrincipalIds.Order());
        using var consoleB = f.Client("console-b-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await consoleB.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Service.GetAsync(Fixture.Api + "catalogue")).StatusCode);
    }

    [Fact]
    public async Task Only_a_control_plane_principal_with_the_revocation_scope_may_revoke()
    {
        await using var f = await Fixture.Start();
        // The operator principal holds the scope too, but an operator principal never revokes.
        f.SavePrincipals([.. f.Principals, Revoker, Operator("console-a", "console-a-token", "opa_a")]);
        using var consoleA = f.Client("console-a-token");
        var request = new OperatorPrincipalRevocationRequest([], true, "kill_3");
        Assert.Equal(HttpStatusCode.Forbidden, (await consoleA.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path, request, OperationsProtocol.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Service.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path, request, OperationsProtocol.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Agent.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path, request, OperationsProtocol.Json)).StatusCode);
        using var revoker = f.Client("revoker-token");
        Assert.Equal(HttpStatusCode.BadRequest, (await revoker.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path,
            new OperatorPrincipalRevocationRequest([], false, "kill_4"), OperationsProtocol.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await consoleA.GetAsync(Fixture.Api + "catalogue")).StatusCode);
    }

    [Fact]
    public async Task The_host_command_revokes_operator_principals_and_cancels_their_operations_without_the_server()
    {
        await using var f = await Fixture.Start();
        f.SavePrincipals([.. f.Principals, Operator("console-a", "console-a-token", "opa_a")]);
        using var consoleA = f.Client("console-a-token");
        await f.Register();
        (await consoleA.PostAsJsonAsync(Fixture.Api + "commands", f.Command("console-a-key"))).EnsureSuccessStatusCode();

        var result = await OperatorRevocationCommand.RunAsync(Path.Combine(f.Directory, "principals.json"), f.Directory, [], f.Clock);

        Assert.Equal(["console-a"], result.RevokedPrincipalIds);
        Assert.Equal(1, result.CancelledAttempts);
        Assert.Equal(HttpStatusCode.Unauthorized, (await consoleA.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Null(await f.Poll());
        var stored = JsonSerializer.Deserialize<OperatorRevocationDocument>(
            File.ReadAllText(Path.Combine(f.Directory, OperationsAccess.RevocationsFileName)), OperationsProtocol.Json)!;
        Assert.Equal(["console-a"], stored.RevokedPrincipalIds);
        Assert.Equal(["opa_a"], stored.RevokedOperatorAgentIds);
    }

    [Fact]
    public async Task The_principal_file_stays_read_only_and_a_principal_added_later_for_a_revoked_agent_stays_revoked()
    {
        await using var f = await Fixture.Start();
        f.SavePrincipals([.. f.Principals, Revoker, Operator("console-a", "console-a-token", "opa_a")]);
        var principalsFile = Path.Combine(f.Directory, "principals.json");
        var before = File.ReadAllText(principalsFile);
        using var revoker = f.Client("revoker-token");
        (await revoker.PostAsJsonAsync("/" + OperatorPrincipalRevocation.Path,
            new OperatorPrincipalRevocationRequest(["opa_a"], false, "kill_5"), OperationsProtocol.Json)).EnsureSuccessStatusCode();
        Assert.Equal(before, File.ReadAllText(principalsFile));

        f.SavePrincipals([.. f.Principals, Revoker, Operator("console-a", "console-a-token", "opa_a"),
            Operator("console-a2", "console-a2-token", "opa_a"), Operator("console-c", "console-c-token", "opa_c")]);
        using var replacement = f.Client("console-a2-token");
        using var other = f.Client("console-c-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await replacement.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(Fixture.Api + "catalogue")).StatusCode);
    }
}
