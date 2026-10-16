using System.Net;
using System.Text;
using AgentStudio.Registry;
using AgentStudio.ExecutionPreparation;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectRepositoryProvisioningTests
{
    private static readonly string TemplateRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../project-templates"));

    [Theory]
    [InlineData("static-web")]
    [InlineData("container-service")]
    public async Task Template_builds_and_tests(string template)
    {
        var source = ProjectRepositoryTemplates.ReadFiles(template, TemplateRoot);
        Assert.Contains(".agent-studio/project.yml", source.Keys);
        Assert.Contains("docs/deployments/README.md", source.Keys);
        var path = Path.Combine(Path.GetTempPath(), "studio-template-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, content) in source)
            {
                var target = Path.Combine(path, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, content);
            }
            var definition = ProjectDefinitionReader.ReadWorkspace(path);
            Assert.True(definition.IsValid,
                string.Join(Environment.NewLine, definition.Issues.Select(issue => issue.Message)));
            await RunNpm(path, "ci");
            await RunNpm(path, "test");
            await RunNpm(path, "run", "build");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task Creates_repository_with_main_and_develop_from_template()
    {
        var handler = new FakeGitHubHandler();
        var checkout = new FakeCheckout();
        var client = CreateClient(handler, checkout);
        var result = await client.CreateAsync(Request(), CancellationToken.None);

        Assert.Equal("https://github.com/me/new-app", result.Url);
        Assert.Contains(handler.Routes, route => route.EndsWith("/git/trees", StringComparison.Ordinal));
        Assert.Contains(handler.Routes, route => route.EndsWith("/git/refs", StringComparison.Ordinal));
        Assert.Equal(result.CheckoutPath, checkout.Path);
        Assert.Equal(0, handler.Deletes);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "name-taken")]
    [InlineData(HttpStatusCode.Forbidden, "missing-rights")]
    public async Task Creation_failure_has_actionable_code_without_checkout(HttpStatusCode status, string code)
    {
        var handler = new FakeGitHubHandler { CreateStatus = status };
        var checkout = new FakeCheckout();
        var client = CreateClient(handler, checkout);

        var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => client.CreateAsync(Request(), CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.Null(checkout.Path);
        Assert.Equal(0, handler.Deletes);
    }

    [Fact]
    public async Task Partial_failure_deletes_new_repository()
    {
        var handler = new FakeGitHubHandler { FailTree = true };
        var client = CreateClient(handler, new FakeCheckout());

        await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => client.CreateAsync(Request(), CancellationToken.None));
        Assert.Equal(1, handler.Deletes);
    }

    [Fact]
    public void Missing_host_credential_explains_next_step()
    {
        var source = new HostProvisioningCredentialSource();
        if (Environment.GetEnvironmentVariable("AGENT_STUDIO_GITHUB_PROVISIONING_TOKEN") is not null)
            return;
        var error = Assert.Throws<RepositoryProvisioningException>(source.GetToken);
        Assert.Equal("missing-credential", error.Code);
        Assert.Contains("operator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CreateRepositoryRequest Request() => new("me", "new-app", "private", "static-web");

    private static GitHubProjectRepositoryProvisioner CreateClient(FakeGitHubHandler handler, FakeCheckout checkout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectOnboarding:TemplateRoot"] = TemplateRoot,
            ["ProjectOnboarding:RepositoryRoot"] = Path.Combine(Path.GetTempPath(),
                "studio-provision-test-" + Guid.NewGuid().ToString("N")),
        }).Build();
        return new GitHubProjectRepositoryProvisioner(new FakeClientFactory(handler), config,
            new FakeCredential(), checkout, NullLogger<GitHubProjectRepositoryProvisioner>.Instance);
    }

    private static async Task RunNpm(string path, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("npm")
        {
            WorkingDirectory = path,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
    }

    private sealed class FakeCredential : IProvisioningCredentialSource
    {
        public string GetToken() => "fixture-" + Guid.NewGuid().ToString("N");
    }

    private sealed class FakeCheckout : IProjectRepositoryCheckout
    {
        public string? Path { get; private set; }
        public Task CloneAsync(string url, string token, string path, CancellationToken cancellationToken)
        {
            Path = path;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClientFactory(FakeGitHubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        public HttpStatusCode CreateStatus { get; init; } = HttpStatusCode.Created;
        public bool FailTree { get; init; }
        public int Deletes { get; private set; }
        public List<string> Routes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Routes.Add(path);
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (path == "/user") return Json("{\"login\":\"me\"}");
            if (path == "/user/repos")
                return CreateStatus == HttpStatusCode.Created
                    ? Json("{\"html_url\":\"https://github.com/me/new-app\",\"default_branch\":\"main\"}", CreateStatus)
                    : Json("{}", CreateStatus);
            if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                return Json("{\"object\":{\"sha\":\"base\"}}");
            if (path.EndsWith("/git/commits/base", StringComparison.Ordinal))
                return Json("{\"tree\":{\"sha\":\"base-tree\"}}");
            if (path.EndsWith("/git/trees", StringComparison.Ordinal))
                return FailTree ? Json("{}", HttpStatusCode.InternalServerError) : Json("{\"sha\":\"new-tree\"}");
            if (path.EndsWith("/git/commits", StringComparison.Ordinal)) return Json("{\"sha\":\"new-commit\"}");
            if (path.EndsWith("/git/refs/heads/main", StringComparison.Ordinal)) return Json("{\"ref\":\"refs/heads/main\"}");
            if (path.EndsWith("/git/refs", StringComparison.Ordinal)) return Json("{\"ref\":\"refs/heads/develop\"}");
            throw new InvalidOperationException("Unexpected GitHub request: " + request.Method + " " + path);
        }

        private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
