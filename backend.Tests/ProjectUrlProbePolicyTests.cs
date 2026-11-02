using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3411: a networked server must not request loopback or link-local Project
/// URLs that came along from a laptop setup; it reports them as local-only.
/// </summary>
public sealed class ProjectUrlProbePolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4280/")]
    [InlineData("http://127.8.9.10/")]
    [InlineData("http://localhost:4200/")]
    [InlineData("http://LOCALHOST./")]
    [InlineData("http://app.localhost:3000/")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://0.0.0.0:5000/")]
    [InlineData("http://[::]/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://2130706433/")]
    public void Networked_LoopbackAndLinkLocal_AreLocalOnly(string url)
        => Assert.Equal(ProjectUrlProbeVerdict.LocalOnly,
            ProjectUrlProbePolicy.Decide(url, networked: true, hostOwnsActiveProcess: false));

    [Theory]
    [InlineData("https://preview.example.test/")]
    [InlineData("http://10.0.0.5:8080/")]
    [InlineData("http://192.168.1.20/")]
    [InlineData("http://169.255.0.1/")]
    [InlineData("http://localhost.example.test/")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Networked_OtherTargets_AreProbed(string? url)
        => Assert.Equal(ProjectUrlProbeVerdict.Probe,
            ProjectUrlProbePolicy.Decide(url, networked: true, hostOwnsActiveProcess: false));

    [Fact]
    public void Local_Profile_ProbesLoopback()
        => Assert.Equal(ProjectUrlProbeVerdict.Probe,
            ProjectUrlProbePolicy.Decide("http://127.0.0.1:4280/", networked: false, hostOwnsActiveProcess: false));

    [Fact]
    public void Networked_ProcessThisHostRuns_IsProbed()
        => Assert.Equal(ProjectUrlProbeVerdict.Probe,
            ProjectUrlProbePolicy.Decide("http://127.0.0.1:4280/", networked: true, hostOwnsActiveProcess: true));

    [Fact]
    public async Task Readiness_Networked_LoopbackUrl_ReportsLocalOnlyWithoutARequest()
    {
        var clients = new CountingClientFactory();
        var service = new ProjectUrlReadinessService(clients,
            NullLogger<ProjectUrlReadinessService>.Instance, Profile("networked"));

        var result = await service.ProbeAsync(Project(), Url("http://127.0.0.1:4280/"), null, CancellationToken.None);

        Assert.Equal(ProjectUrlProbePolicy.LocalOnly, result.Kind);
        Assert.Equal(ProjectUrlProbePolicy.LocalOnlyDetail, result.Detail);
        Assert.Equal(0, clients.Requests);
    }

    /// <summary>
    /// Time-compressed replay of the reported hour: the UI re-probes a visible
    /// URL every 8 seconds, so 450 probes. None of them may reach the network.
    /// </summary>
    [Fact]
    public async Task Readiness_Networked_OneHourOfPolling_SendsNoRequest()
    {
        var clients = new CountingClientFactory();
        var service = new ProjectUrlReadinessService(clients,
            NullLogger<ProjectUrlReadinessService>.Instance, Profile("networked"));

        for (var probe = 0; probe < 3600 / 8; probe++)
            Assert.Equal(ProjectUrlProbePolicy.LocalOnly, (await service.ProbeAsync(
                Project(), Url("http://127.0.0.1:4280/"), null, CancellationToken.None)).Kind);

        Assert.Equal(0, clients.Requests);
    }

    [Fact]
    public async Task Readiness_Networked_PublicUrl_IsStillProbed()
    {
        var clients = new CountingClientFactory();
        var service = new ProjectUrlReadinessService(clients,
            NullLogger<ProjectUrlReadinessService>.Instance, Profile("networked"));

        var result = await service.ProbeAsync(Project(), Url("https://preview.example.test/"), null, CancellationToken.None);

        Assert.Equal("healthy", result.Kind);
        Assert.Equal(1, clients.Requests);
    }

    [Fact]
    public async Task Readiness_Local_LoopbackUrl_IsStillProbed()
    {
        var clients = new CountingClientFactory();
        var service = new ProjectUrlReadinessService(clients,
            NullLogger<ProjectUrlReadinessService>.Instance, Profile("local"));

        var result = await service.ProbeAsync(Project(), Url("http://127.0.0.1:4280/"), null, CancellationToken.None);

        Assert.Equal("healthy", result.Kind);
        Assert.Equal(1, clients.Requests);
    }

    [Fact]
    public async Task Diagnostic_Networked_LoopbackUrl_IsLocalOnlyWithoutARequest()
    {
        var clients = new CountingClientFactory();
        var service = new ProjectUrlProcessService(NullLogger<ProjectUrlProcessService>.Instance,
            clients, configuration: Profile("networked"));

        var result = await service.ProbeAsync(Project(), Url("http://127.0.0.1:4280/"), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.LocalOnly, result.Classification);
        Assert.False(result.PortReachable);
        Assert.Equal(0, clients.Requests);
    }

    private static IConfiguration Profile(string profile) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Profile"] = profile })
        .Build();

    private static ProjectRecord Project() => new() { Id = "PROJ-001" };

    private static ProjectUrlRecord Url(string url) => new() { Id = "url-1", Label = "Preview", Url = url };

    private sealed class CountingClientFactory : IHttpClientFactory
    {
        private int _requests;
        public int Requests => _requests;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(CountingClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._requests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<main>ready</main>", System.Text.Encoding.UTF8, "text/html"),
                });
            }
        }
    }
}
