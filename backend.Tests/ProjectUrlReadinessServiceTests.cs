using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlReadinessServiceTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");
    private const string RegisteredUrl = "http://preview.example.test/app/start";

    [Theory]
    [InlineData("http://127.0.0.1:4200/health")]
    [InlineData("http://169.254.1.10/health")]
    [InlineData("http://other.example.test/health")]
    [InlineData("https://preview.example.test/health")]
    [InlineData("ftp://preview.example.test/health")]
    [InlineData("http://name@preview.example.test/health")]
    public async Task Readiness_refuses_redirects_outside_the_registered_origin_or_with_credentials(string location)
    {
        using var fixture = new ProbeFixture(RegisteredUrl, _ => [PublicAddress],
            $"302 Found\r\nLocation: {location}");

        var result = await fixture.ProbeAsync();

        Assert.Equal("http-error", result.Kind);
        Assert.Equal(302, result.StatusCode);
        Assert.Equal("unknown", result.FramePolicy);
        Assert.Equal("The readiness probe refused the redirect destination.", result.Detail);
        Assert.Equal([new IPEndPoint(PublicAddress, 80)], fixture.Connections);
        Assert.Equal(["preview.example.test"], fixture.Lookups);
    }

    [Theory]
    [InlineData("http://private.example.test/health", "127.0.0.1", 1)]
    [InlineData("http://private.example.test/health", "10.0.0.5", 1)]
    [InlineData("http://10.0.0.5/health", "10.0.0.5", 0)]
    [InlineData("http://127.0.0.1/health", "127.0.0.1", 0)]
    public async Task Unapproved_local_and_private_addresses_never_reach_the_connector_socket(
        string url, string address, int lookups)
    {
        using var fixture = new ProbeFixture(url, _ => [IPAddress.Parse(address)]);

        var result = await fixture.ProbeAsync();

        Assert.Equal("offline", result.Kind);
        Assert.Null(result.StatusCode);
        Assert.Empty(fixture.Connections);
        Assert.Equal(lookups, fixture.Lookups.Count);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.10")]
    [InlineData("10.0.0.5")]
    public async Task Readiness_checks_the_DNS_answer_again_on_the_same_origin_redirect(string address)
    {
        var answers = new Queue<IPAddress>([PublicAddress, IPAddress.Parse(address)]);
        using var fixture = new ProbeFixture(RegisteredUrl, _ => [answers.Dequeue()],
            "307 Temporary Redirect\r\nLocation: /app/health");

        var result = await fixture.ProbeAsync();

        Assert.Equal("offline", result.Kind);
        Assert.Null(result.StatusCode);
        Assert.Equal(["preview.example.test", "preview.example.test"], fixture.Lookups);
        Assert.Equal([new IPEndPoint(PublicAddress, 80)], fixture.Connections);
    }

    [Theory]
    [InlineData("200 OK\r\nX-Build-Commit: ABCDEF123", "healthy", 200, "allowed", "abcdef123")]
    [InlineData("200 OK\r\nX-Frame-Options: DENY", "frame-blocked", 200, "blocked", null)]
    [InlineData("503 Unavailable", "http-error", 503, "allowed", null)]
    public async Task Readiness_reports_the_final_response_of_an_allowed_redirect_chain(
        string finalResponse, string kind, int status, string framePolicy, string? commit)
    {
        using var fixture = new ProbeFixture(RegisteredUrl, _ => [PublicAddress],
            "301 Moved Permanently\r\nLocation: stage",
            "308 Permanent Redirect\r\nLocation: http://preview.example.test/app/health",
            finalResponse);

        var result = await fixture.ProbeAsync(inspectBuildStamp: true);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(framePolicy, result.FramePolicy);
        Assert.Equal(commit, result.Commit);
        Assert.True(result.DurationMs >= 0);
        Assert.Equal(["/app/start", "/app/stage", "/app/health"], fixture.Paths);
        Assert.Equal(3, fixture.Connections.Count);
    }

    [Fact]
    public async Task Readiness_caps_redirects_before_sending_another_request()
    {
        using var fixture = new ProbeFixture(RegisteredUrl, _ => [PublicAddress],
            Enumerable.Repeat("302 Found\r\nLocation: /app/start", 6).ToArray());

        var result = await fixture.ProbeAsync();

        Assert.Equal("http-error", result.Kind);
        Assert.Equal(302, result.StatusCode);
        Assert.Equal("The readiness probe reached its redirect limit.", result.Detail);
        Assert.Equal(6, fixture.Connections.Count);
    }

    [Fact]
    public async Task Local_process_permission_is_rechecked_on_each_redirect_connection()
    {
        using var fixture = new ProbeFixture("http://localhost:4200/app/start", _ => [IPAddress.Loopback],
            "303 See Other\r\nLocation: /app/health");
        fixture.Process = new(fixture.Project.Id, fixture.Url.Id, fixture.Url.StartRule!.Command,
            Path.GetTempPath(), ProjectUrlProcessStates.Running, 1, DateTimeOffset.UnixEpoch, null, null, []);
        fixture.OnConnect = () => fixture.Process = fixture.Process! with { State = ProjectUrlProcessStates.Stopped };

        var result = await fixture.ProbeAsync();

        Assert.Equal("offline", result.Kind);
        Assert.Equal(["/app/start", "/app/health"], fixture.Paths);
        Assert.Equal([new IPEndPoint(IPAddress.Loopback, 4200)], fixture.Connections);
    }

    private sealed class ProbeFixture : IDisposable, IHttpClientFactory
    {
        private readonly HttpClient _client;
        public ProjectUrlRecord Url { get; }
        public ProjectRecord Project { get; }
        public ProjectUrlProcessSnapshot? Process { get; set; }
        public Action? OnConnect { get; set; }
        public List<IPEndPoint> Connections { get; } = [];
        public List<string> Lookups { get; } = [];
        public List<string> Paths { get; } = [];

        public ProbeFixture(string url, Func<string, IPAddress[]> resolve, params string[] responses)
        {
            Url = new() { Id = "site", Url = url, StartRule = new() { Command = "fixture-server" } };
            Project = new() { Id = "PROJ-001", Urls = [Url] };
            var replies = new Queue<string>(responses);
            var connector = new ProjectUrlDestinationConnector((host, _) =>
            {
                Lookups.Add(host);
                return Task.FromResult(resolve(host));
            }, (endpoint, _) =>
            {
                Connections.Add(endpoint);
                OnConnect?.Invoke();
                return Task.FromResult<Stream>(new ResponseStream(replies.Dequeue()));
            });
            _client = new HttpClient(connector.CreateHandler(request =>
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.True(request.Options.TryGetValue(ProjectUrlDestinationConnector.ProjectUrlOption, out var owner));
                Assert.Equal((Project.Id, Url.Id), owner);
                return ProjectUrlDestinationConnector.ResolvePermission(request,
                    id => id == Project.Id ? Project : null, (_, _) => Process);
            }));
        }

        public Task<ProjectUrlReadinessResult> ProbeAsync(bool inspectBuildStamp = false)
            => new ProjectUrlReadinessService(this, NullLogger<ProjectUrlReadinessService>.Instance)
                .ProbeAsync(Project, Url, null, CancellationToken.None, inspectBuildStamp);

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("project-url-readiness", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    // Serve HTTP bytes over the checked connector without opening a socket.
    private sealed class ResponseStream(string reply) : MemoryStream(
        Encoding.ASCII.GetBytes($"HTTP/1.1 {reply}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
    {
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
