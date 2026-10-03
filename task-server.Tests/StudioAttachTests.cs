using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Task Server side of the Studio connector attach handshake contract
/// (gate 4). The connector side of the same contract is covered by
/// backend.Tests ConnectorSecurityPolicyTests and ConnectorNegativeMatrixTests.
/// </summary>
public sealed class StudioAttachTests
{
    private const string StudioToken = "studio-attach-token-0000000000000000000000000001";
    private const string EngineToken = "engine-attach-token-0000000000000000000000000001";

    private static readonly ProtocolRangeDto Server = new(
        2, 1, 2, "9.9.9", "server", ["studio"], null, [TaskServerHubProtocol.StudioRange()]);

    private static ProtocolAttachRequest Offer(
        int minimumApi = TaskServerProtocol.MinimumSupported,
        int maximumApi = TaskServerProtocol.MaximumSupported,
        int minimumHub = TaskServerHubProtocol.MinimumSupported,
        int maximumHub = TaskServerHubProtocol.MaximumSupported,
        string clientKind = "studio")
        => new(clientKind, "1.0.0", minimumApi, maximumApi, minimumHub, maximumHub);

    public static TheoryData<string, ProtocolAttachRequest, string?, bool, string, int?, int?> PolicyMatrix => new()
    {
        { "matching studio", Offer(), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.Attached, 2, 1 },
        { "older connector", Offer(1, 1), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.Attached, 1, 1 },
        { "newer connector overlapping", Offer(2, 5, 1, 3), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.Attached, 2, 1 },
        { "api too new", Offer(3, 4), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.ApiProtocolIncompatible, null, null },
        { "hub too new", Offer(minimumHub: 2, maximumHub: 2), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.HubProtocolIncompatible, null, null },
        { "empty range", Offer(2, 1), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.InvalidRange, null, null },
        { "runner credential", Offer(), TaskServerPrincipalKinds.Runner, true, ProtocolAttachCodes.PrincipalKindMismatch, null, null },
        { "engine credential", Offer(), TaskServerPrincipalKinds.Engine, true, ProtocolAttachCodes.PrincipalKindMismatch, null, null },
        { "runner client kind", Offer(clientKind: "runner"), TaskServerPrincipalKinds.Studio, true, ProtocolAttachCodes.ClientKindUnsupported, null, null },
        { "local compatibility without auth", Offer(), null, false, ProtocolAttachCodes.Attached, 2, 1 },
    };

    [Theory]
    [MemberData(nameof(PolicyMatrix))]
    public void Attach_policy_matrix(
        string scenario,
        ProtocolAttachRequest request,
        string? principalKind,
        bool authenticationRequired,
        string expectedCode,
        int? expectedApi,
        int? expectedHub)
    {
        var decision = StudioAttachPolicy.Decide(
            request,
            principalKind,
            authenticationRequired,
            Server,
            TaskServerHubProtocol.StudioRange());

        Assert.True(expectedCode == decision.Code, $"{scenario}: {decision.Code}");
        Assert.Equal(expectedApi, decision.ApiProtocol);
        Assert.Equal(expectedHub, decision.HubProtocol);
        Assert.Equal(decision.Attached, decision.Reason is null);
    }

    [Fact]
    public async Task Studio_principal_attaches_with_the_negotiated_api_and_hub_versions()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var studio = Client(factory, StudioToken);

        var response = await studio.PostAsJsonAsync("/api/v1/protocol/attach", Offer());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attach = (await response.Content.ReadFromJsonAsync<ProtocolAttachResponse>())!;
        Assert.True(attach.Attached);
        Assert.Equal(ProtocolAttachCodes.Attached, attach.Code);
        Assert.Equal(TaskServerProtocol.Current, attach.ApiProtocol);
        Assert.Equal(TaskServerHubProtocol.Current, attach.HubProtocol);
        Assert.Equal(TaskServerHubProtocol.StudioHubPath, attach.HubPath);
        var hub = Assert.Single(attach.Server.Hubs!);
        Assert.Equal(TaskServerHubProtocol.StudioRange(), hub);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-attach-token-000000000000000000000000000")]
    public async Task Absent_or_invalid_bearer_cannot_attach(string? token)
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var client = Client(factory, token);

        var response = await client.PostAsJsonAsync("/api/v1/protocol/attach", Offer());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("authentication-required", error!.Code);
    }

    [Fact]
    public async Task Runner_credential_is_refused_as_a_studio_attach()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var studio = Client(factory, StudioToken);
        var issued = await studio.PostAsJsonAsync(
            "/api/v1/management/principals",
            new CreatePrincipalRequest("runner:attach", TaskServerPrincipalKinds.Runner, [TaskServerScopes.TasksRead], "attach"));
        issued.EnsureSuccessStatusCode();
        var runnerCredential = (await issued.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!.Credential;
        using var runner = Client(factory, runnerCredential);

        var response = await runner.PostAsJsonAsync("/api/v1/protocol/attach", Offer());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var attach = (await response.Content.ReadFromJsonAsync<ProtocolAttachResponse>())!;
        Assert.False(attach.Attached);
        Assert.Equal(ProtocolAttachCodes.PrincipalKindMismatch, attach.Code);
        Assert.Contains("'runner' credential", attach.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 4, 1, 1, ProtocolAttachCodes.ApiProtocolIncompatible, "/api/v1 protocol 3-4")]
    [InlineData(1, 2, 2, 2, ProtocolAttachCodes.HubProtocolIncompatible, "Studio hub protocol 2-2")]
    public async Task Protocol_mismatch_is_refused_with_an_operator_readable_reason(
        int minimumApi,
        int maximumApi,
        int minimumHub,
        int maximumHub,
        string expectedCode,
        string expectedReasonFragment)
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        // The attach itself is exempt from the protocol header gate, so a
        // mismatched connector learns why instead of a bare 426.
        using var studio = Client(factory, StudioToken, protocolHeader: maximumApi.ToString());

        var response = await studio.PostAsJsonAsync(
            "/api/v1/protocol/attach",
            Offer(minimumApi, maximumApi, minimumHub, maximumHub));

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var attach = (await response.Content.ReadFromJsonAsync<ProtocolAttachResponse>())!;
        Assert.False(attach.Attached);
        Assert.Equal(expectedCode, attach.Code);
        Assert.Null(attach.ApiProtocol);
        Assert.Contains(expectedReasonFragment, attach.Reason, StringComparison.Ordinal);
        Assert.Contains("Install a connector and Task Server", attach.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_status_advertises_the_studio_hub_at_its_mapped_path()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var studio = Client(factory, StudioToken);

        var protocol = await studio.GetFromJsonAsync<ProtocolRangeDto>("/api/v1/protocol");

        var hub = Assert.Single(protocol!.Hubs!);
        Assert.Equal(TaskServerHubProtocol.StudioHubPath, hub.Path);
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Contains(TaskServerHubProtocol.StudioHubPath, routes);
    }

    private static AttachFactory Factory(string path)
        => new(path, new Dictionary<string, string?>
        {
            ["AUTH"] = "bearer",
            ["STUDIO_AUTH_TOKEN"] = StudioToken,
            ["ENGINE_AUTH_TOKEN"] = EngineToken,
        });

    private static HttpClient Client(AttachFactory factory, string? credential, string? protocolHeader = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            protocolHeader ?? TaskServerProtocol.Current.ToString());
        if (credential is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private sealed class AttachFactory(
        string dataDirectory,
        IReadOnlyDictionary<string, string?> overrides)
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
                };
                foreach (var (key, value) in overrides)
                    values[key] = value;
                configuration.AddInMemoryCollection(values);
            });
        }
    }
}
