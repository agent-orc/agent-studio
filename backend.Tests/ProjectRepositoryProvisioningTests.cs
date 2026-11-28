using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Persistence;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using AgentStudio.ExecutionPreparation;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectRepositoryProvisioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-provision-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

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
    public void Container_template_declares_schema_two_telemetry_environments_runbooks_and_the_otel_sdk()
    {
        var source = ProjectRepositoryTemplates.ReadFiles("container-service", TemplateRoot);
        var yaml = source[".agent-studio/project.yml"];
        var parsed = ProjectDefinitionReader.Parse(yaml);
        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(2, parsed.Definition!.SchemaVersion);
        var deployment = parsed.Definition.Deployment!;
        Assert.Equal([("staging", true), ("production", true)], deployment.Environments.Select(item => (item.Name, item.Telemetry)));
        Assert.Equal(["restart", "disk-cleanup"], deployment.Runbooks.Select(item => item.Name));

        // The declarations Studio derives from the file give every container deploy its telemetry.
        var declarations = AgentStudio.Projects.ProjectEnvironmentService.ApplyDefinition([], yaml);
        Assert.All(declarations, file => Assert.NotNull(AgentStudio.Projects.DeployTelemetryPolicy.For(file.Declaration!)));

        Assert.Contains("@opentelemetry/sdk-node", source["package.json"]);
        Assert.Contains("--import", source["Dockerfile"]);
        Assert.Contains("docs/runbooks/README.md", source.Keys);
        Assert.DoesNotContain(source.Keys, key => key.StartsWith("node_modules/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("me", "main", false)]
    [InlineData("sandbox-org", "trunk", true)]
    [InlineData("sandbox-org", "develop", true)]
    public async Task Creates_seeds_clones_and_hands_off_only_after_App_coverage(
        string owner, string defaultBranch, bool selectedRepositories)
    {
        var handler = new FakeGitHubHandler { DefaultBranch = defaultBranch, Covered = !selectedRepositories };
        var checkout = new FakeCheckout();
        var client = CreateClient(handler, checkout);
        var result = await client.CreateAsync(Request() with { Owner = owner }, CancellationToken.None);

        Assert.Equal($"https://github.com/{owner}/new-app", result.Url);
        Assert.Equal(owner == "me" ? "/user/repos" : "/orgs/sandbox-org/repos", handler.CreateRoute);
        Assert.Equal("new-commit", handler.Branches["main"]);
        Assert.Equal("new-commit", handler.Branches["develop"]);
        Assert.Equal("main", handler.PublishedDefaultBranch);
        Assert.Equal("100755", handler.PrepareMode);
        Assert.Equal(result.Url + ".git", checkout.Url);
        Assert.Equal(handler.Token, checkout.Token);
        Assert.NotNull(checkout.Path);
        Assert.False(Directory.Exists(checkout.Path));
        Assert.True(Directory.Exists(result.CheckoutPath));
        Assert.Equal(selectedRepositories ? 1 : 0, handler.Associations);
        Assert.Equal(selectedRepositories ? 2 : 1, handler.CoverageChecks);
        Assert.Equal(0, handler.Deletes);
    }

    [Fact]
    public async Task Disallowed_owner_cannot_trigger_a_credential_read_or_a_GitHub_request()
    {
        var handler = new FakeGitHubHandler();
        var credential = new FakeCredential(handler.Token);
        var provisioner = CreateClient(handler, new FakeCheckout(), credential: credential);

        var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => provisioner.CreateAsync(Request() with { Owner = "outside-owner" }, default));

        Assert.Equal("owner-not-allowed", error.Code);
        Assert.Empty(credential.Purposes);
        Assert.Empty(handler.Routes);
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

    [Theory]
    [InlineData("me", true)]
    [InlineData("me", false)]
    [InlineData("sandbox-org", true)]
    [InlineData("sandbox-org", false)]
    public async Task Seed_or_clone_failure_cleans_only_the_new_repository_and_partial_checkout(string owner, bool failSeed)
    {
        var handler = new FakeGitHubHandler { FailTree = failSeed };
        var checkout = new FakeCheckout { Fail = !failSeed };
        var client = CreateClient(handler, checkout);

        var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => client.CreateAsync(Request() with { Owner = owner }, default));

        Assert.Equal(failSeed ? "github-error" : "setup-failed", error.Code);
        Assert.DoesNotContain(handler.Token, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Deletes);
        Assert.False(handler.Exists);
        Assert.False(Directory.Exists(checkout.Path));
        Assert.False(Directory.Exists(Path.Combine(_root, owner, "new-app")));
        Assert.Equal(0, handler.CoverageChecks);
    }

    [Fact]
    public async Task Retry_after_lost_create_response_recovers_only_the_matching_attempt()
    {
        var handler = new FakeGitHubHandler { LoseCreateResponse = true, FailDelete = true };
        var provisioner = CreateClient(handler, new FakeCheckout());
        var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(() => provisioner.CreateAsync(Request(), default));
        Assert.DoesNotContain(handler.Token, error.ToString(), StringComparison.Ordinal);
        Assert.True(handler.Exists);
        Assert.Equal(0, handler.Deletes);

        // A new service instance reads the durable receipt. Requests for the same name
        // with a different template or GitHub account cannot take over the attempt.
        provisioner = CreateClient(handler, new FakeCheckout());
        var wrongTemplate = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => provisioner.CreateAsync(Request() with { Template = "container-service" }, default));
        Assert.Equal("attempt-conflict", wrongTemplate.Code);
        handler.UserId++;
        var wrongAccount = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => provisioner.CreateAsync(Request(), default));
        Assert.Equal("attempt-conflict", wrongAccount.Code);
        Assert.Equal(0, handler.Deletes);

        handler.UserId--;
        handler.FailDelete = false;
        var originalMarker = handler.Description;
        var result = await provisioner.CreateAsync(Request(), default);
        Assert.NotEqual(originalMarker, handler.Description);
        Assert.Equal(1, handler.Deletes);
        Assert.True(handler.Exists);
        Assert.True(Directory.Exists(result.CheckoutPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_repository_replaced_at_the_same_name_is_never_deleted_on_retry(bool copyDescription)
    {
        var handler = new FakeGitHubHandler { FailTree = true, FailDelete = true };
        var provisioner = CreateClient(handler, new FakeCheckout());
        await Assert.ThrowsAsync<RepositoryProvisioningException>(() => provisioner.CreateAsync(Request(), default));
        handler.RepositoryId++;
        if (!copyDescription) handler.Description = "Unrelated repository";
        handler.FailDelete = false;

        var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => CreateClient(handler, new FakeCheckout()).CreateAsync(Request(), default));
        Assert.Equal("name-taken", error.Code);
        Assert.Equal(0, handler.Deletes);
        Assert.True(handler.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refuses_enrolment_without_verified_App_coverage(bool adopted)
    {
        var handler = new FakeGitHubHandler { Adopted = adopted, Covered = false, ApplyAssociation = false };
        var checkout = new FakeCheckout();
        var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(
            () => CreateClient(handler, checkout).CreateAsync(Request(), default));

        Assert.Equal("app-coverage-required", error.Code);
        Assert.Equal(adopted ? 1 : 0, handler.Associations);
        Assert.Equal(adopted ? 2 : 1, handler.CoverageChecks);
        Assert.Equal(1, handler.Deletes);
        Assert.False(Directory.Exists(checkout.Path));
        Assert.False(Directory.Exists(Path.Combine(_root, "me", "new-app")));
    }

    [Fact]
    public async Task Checkout_collision_during_clone_preserves_existing_local_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-provision-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "me", "new-app");
        var handler = new FakeGitHubHandler();
        var client = CreateClient(handler, new CollidingCheckout(path), root);
        try
        {
            var error = await Assert.ThrowsAsync<RepositoryProvisioningException>(
                () => client.CreateAsync(Request(), CancellationToken.None));

            Assert.Equal("checkout-exists", error.Code);
            Assert.Equal(1, handler.Deletes);
            Assert.Equal("keep this directory", await File.ReadAllTextAsync(Path.Combine(path, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_handoff_or_template_validation_rolls_back_project_storage_and_repository(bool appAdopted)
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-onboard-rollback-" + Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(root, "repositories");
        var handler = new FakeGitHubHandler { Adopted = appAdopted };
        var checkout = new FakeCheckout();
        var provisioner = CreateClient(handler, checkout, repositoryRoot);
        var runner = new FailingBuildRunner();
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = root,
                            ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        }));
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IProjectRepositoryProvisioner>();
                        services.AddSingleton<IProjectRepositoryProvisioner>(provisioner);
                        services.RemoveAll<IBuildCommandRunner>();
                        services.AddSingleton<IBuildCommandRunner>(runner);
                    });
                });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var response = await client.PostAsJsonAsync("/api/projects", new
            {
                displayName = "Rollback Project",
                workspaceId = DefaultWorkspace.Id,
                createRepository = Request(),
            });

            if (appAdopted)
            {
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.Contains("npm ci", runner.Commands);
            }
            else
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains("app-coverage-required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                Assert.Empty(runner.Commands);
            }
            Assert.Equal(1, handler.Deletes);
            Assert.Empty(await client.GetFromJsonAsync<List<ProjectSummary>>("/api/projects") ?? []);
            Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "me", "new-app")));
            var projectsRoot = Path.Combine(root, "projects");
            if (Directory.Exists(projectsRoot))
                Assert.Empty(Directory.EnumerateDirectories(projectsRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_rolls_back_onboarding_and_partially_created_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-onboard-cancel-" + Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(root, "repositories");
        var handler = new FakeGitHubHandler();
        var checkout = new FakeCheckout();
        var provisioner = new TrackingProvisioner(CreateClient(handler, checkout, repositoryRoot));
        using var requestCancellation = new CancellationTokenSource();
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = root,
                            ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        }));
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IProjectRepositoryProvisioner>();
                        services.AddSingleton<IProjectRepositoryProvisioner>(provisioner);
                        services.RemoveAll<IBuildCommandRunner>();
                        services.AddSingleton<IBuildCommandRunner>(new CancelingBuildRunner(requestCancellation));
                    });
                });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

            var post = client.PostAsJsonAsync("/api/projects", new
            {
                displayName = "Canceled Project",
                workspaceId = DefaultWorkspace.Id,
                createRepository = Request(),
            }, requestCancellation.Token);
            var first = await Task.WhenAny(provisioner.DeletionCompleted.Task, post)
                .WaitAsync(TimeSpan.FromMinutes(2));
            if (first == post && !provisioner.DeletionCompleted.Task.IsCompleted)
            {
                await post;
                throw new InvalidOperationException("Project request completed before repository rollback.");
            }
            await provisioner.DeletionCompleted.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => post);

            Assert.Equal(1, handler.Deletes);
            Assert.False(provisioner.DeleteTokenWasCanceled);
            Assert.Empty(await client.GetFromJsonAsync<List<ProjectSummary>>("/api/projects") ?? []);
            Assert.Null(factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
                .Get("Canceled Project").BuildProfile);
            Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "me", "new-app")));
            var projectsRoot = Path.Combine(root, "projects");
            if (Directory.Exists(projectsRoot))
                Assert.Empty(Directory.EnumerateDirectories(projectsRoot));

            var partialHandler = new FakeGitHubHandler();
            using var partialCancellation = new CancellationTokenSource();
            var partial = CreateClient(partialHandler, new CancelingCheckout(partialCancellation), repositoryRoot);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => partial.CreateAsync(Request(), partialCancellation.Token));
            Assert.Equal(1, partialHandler.Deletes);
            Assert.False(partialHandler.DeleteTokenWasCanceled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CreateRepositoryRequest Request() => new("me", "new-app", "private", "static-web");

    private GitHubProjectRepositoryProvisioner CreateClient(
        FakeGitHubHandler handler, IProjectRepositoryCheckout checkout, string? repositoryRoot = null,
        IProvisioningCredentialSource? credential = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectOnboarding:TemplateRoot"] = TemplateRoot,
            ["ProjectOnboarding:RepositoryRoot"] = repositoryRoot ?? _root,
            ["ProjectOnboarding:AllowedOwners:0"] = "ME",
            ["ProjectOnboarding:AllowedOwners:1"] = "sandbox-org",
        }).Build();
        return new GitHubProjectRepositoryProvisioner(new FakeClientFactory(handler), config,
            credential ?? new FakeCredential(handler.Token), checkout, handler, new AtomicJsonFileWriter(),
            NullLogger<GitHubProjectRepositoryProvisioner>.Instance);
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

    private sealed class FakeCredential(string token) : IProvisioningCredentialSource
    {
        public List<string> Purposes { get; } = [];
        public Task<string> GetTokenAsync(string purpose, CancellationToken ct)
        {
            Purposes.Add(purpose);
            return Task.FromResult(token);
        }
    }

    private sealed class FakeCheckout : IProjectRepositoryCheckout
    {
        public bool Fail { get; init; }
        public string? Path { get; private set; }
        public string? Url { get; private set; }
        public string? Token { get; private set; }

        public Task CloneAsync(string url, string token, string path, CancellationToken cancellationToken)
        {
            Path = path;
            Url = url;
            Token = token;
            Directory.CreateDirectory(System.IO.Path.Combine(path, ".git"));
            if (Fail) throw new IOException("Injected clone error containing " + token);
            return Task.CompletedTask;
        }
    }

    private sealed class CollidingCheckout(string path) : IProjectRepositoryCheckout
    {
        public Task CloneAsync(string url, string token, string checkoutPath, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.Combine(checkoutPath, ".git"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "marker.txt"), "keep this directory");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingBuildRunner : IBuildCommandRunner
    {
        public List<string> Commands { get; } = [];
        public Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new BuildCommandResult(1, "deliberate validation failure"));
        }
    }

    private sealed class CancelingBuildRunner(CancellationTokenSource cancellation) : IBuildCommandRunner
    {
        public async Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct)
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Canceled validation unexpectedly continued.");
        }
    }

    private sealed class CancelingCheckout(CancellationTokenSource cancellation) : IProjectRepositoryCheckout
    {
        public Task CloneAsync(string url, string token, string path, CancellationToken ct)
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Canceled clone unexpectedly continued.");
        }
    }

    private sealed class TrackingProvisioner(IProjectRepositoryProvisioner inner) : IProjectRepositoryProvisioner
    {
        public TaskCompletionSource DeletionCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DeleteTokenWasCanceled { get; private set; }

        public Task<ProvisionedRepository> CreateAsync(CreateRepositoryRequest request, CancellationToken ct)
            => inner.CreateAsync(request, ct);

        public async Task DeleteAsync(ProvisionedRepository repository, CancellationToken ct)
        {
            DeleteTokenWasCanceled = ct.IsCancellationRequested;
            await inner.DeleteAsync(repository, ct);
            DeletionCompleted.TrySetResult();
        }
    }

    private sealed class FakeClientFactory(FakeGitHubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler, IProjectRepositoryAppCoverage
    {
        public string Token { get; } = "fixture-" + Guid.NewGuid().ToString("N");
        public HttpStatusCode CreateStatus { get; init; } = HttpStatusCode.Created;
        public bool FailTree { get; init; }
        public bool LoseCreateResponse { get; set; }
        public bool FailDelete { get; set; }
        public bool Adopted { get; init; } = true;
        public bool Covered { get; set; } = true;
        public bool ApplyAssociation { get; init; } = true;
        public bool Exists { get; private set; }
        public long UserId { get; set; } = 1;
        public long RepositoryId { get; set; } = 101;
        public string? Description { get; set; }
        public string DefaultBranch { get; init; } = "main";
        public string? PublishedDefaultBranch { get; private set; }
        public string? CreateRoute { get; private set; }
        public string? PrepareMode { get; private set; }
        public int CoverageChecks { get; private set; }
        public int Associations { get; private set; }
        public int Deletes { get; private set; }
        public bool DeleteTokenWasCanceled { get; private set; }
        public List<string> Routes { get; } = [];
        public Dictionary<string, string> Branches { get; } = [];
        private string? _fullName;

        public Task<GitHubAppProvisioningCoverage> CheckAsync(GitHubRepositoryName repository, bool personalOwner, CancellationToken ct)
        {
            Assert.Equal(_fullName, $"{repository.Owner}/{repository.Name}");
            Assert.Equal(repository.Owner == "me", personalOwner);
            Assert.Equal("new-commit", Branches["main"]);
            Assert.Equal("new-commit", Branches["develop"]);
            CoverageChecks++;
            return Task.FromResult(new GitHubAppProvisioningCoverage(Adopted ? 11 : null, Adopted && Covered));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            var method = request.Method;
            Routes.Add(path);
            if (method == HttpMethod.Delete)
            {
                Assert.Equal("/repos/" + _fullName, path);
                DeleteTokenWasCanceled = cancellationToken.IsCancellationRequested;
                cancellationToken.ThrowIfCancellationRequested();
                if (FailDelete) return Json(new { message = Token }, HttpStatusCode.Forbidden);
                Deletes++;
                Exists = false;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/user") return Json(new { id = UserId, login = "me" });
            using var body = request.Content is null ? null
                : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var fields = body?.RootElement ?? default;
            if (path is "/user/repos" or "/orgs/sandbox-org/repos")
            {
                Assert.Equal(HttpMethod.Post, method);
                if (Exists) return Json(new { message = Token }, HttpStatusCode.UnprocessableEntity);
                if (CreateStatus != HttpStatusCode.Created) return Json(new { message = Token }, CreateStatus);
                Assert.True(fields.GetProperty("auto_init").GetBoolean());
                Assert.True(fields.GetProperty("private").GetBoolean());
                CreateRoute = path;
                var owner = path == "/user/repos" ? "me" : "sandbox-org";
                _fullName = owner + "/" + fields.GetProperty("name").GetString();
                Description = fields.GetProperty("description").GetString();
                Exists = true;
                Branches.Clear();
                Branches[DefaultBranch] = "base";
                PublishedDefaultBranch = DefaultBranch;
                if (LoseCreateResponse)
                {
                    LoseCreateResponse = false;
                    throw new HttpRequestException("Injected transport error containing " + Token);
                }
                return RepositoryResponse(CreateStatus);
            }
            if (path == $"/user/installations/11/repositories/{RepositoryId}")
            {
                Assert.Equal(HttpMethod.Put, method);
                Assert.True(Adopted);
                Associations++;
                Covered = ApplyAssociation;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/repos/" + _fullName && method == HttpMethod.Get)
                return Exists ? RepositoryResponse(HttpStatusCode.OK) : new HttpResponseMessage(HttpStatusCode.NotFound);
            Assert.True(Exists);
            var prefix = "/repos/" + _fullName;
            if (path == prefix && method == HttpMethod.Patch)
            {
                PublishedDefaultBranch = fields.GetProperty("default_branch").GetString();
                return RepositoryResponse(HttpStatusCode.OK);
            }
            if (path == prefix + "/git/ref/heads/" + DefaultBranch)
                return Json(new { @object = new { sha = "base" } });
            if (path == prefix + "/git/commits/base") return Json(new { tree = new { sha = "base-tree" } });
            if (path == prefix + "/git/trees")
            {
                Assert.Equal("base-tree", fields.GetProperty("base_tree").GetString());
                PrepareMode = fields.GetProperty("tree").EnumerateArray()
                    .Single(item => item.GetProperty("path").GetString() == ".agent-studio/prepare").GetProperty("mode").GetString();
                return FailTree ? Json(new { message = Token }, HttpStatusCode.InternalServerError) : Json(new { sha = "new-tree" });
            }
            if (path == prefix + "/git/commits") return Json(new { sha = "new-commit" });
            if (path == prefix + "/git/refs" && method == HttpMethod.Post)
            {
                var branch = fields.GetProperty("ref").GetString()!["refs/heads/".Length..];
                Branches.Add(branch, fields.GetProperty("sha").GetString()!);
                return Json(new { });
            }
            if (path.StartsWith(prefix + "/git/refs/heads/", StringComparison.Ordinal) && method == HttpMethod.Patch)
            {
                var branch = path[(prefix + "/git/refs/heads/").Length..];
                Assert.Contains(branch, Branches.Keys);
                Branches[branch] = fields.GetProperty("sha").GetString()!;
                return Json(new { });
            }
            throw new InvalidOperationException("Unexpected GitHub request: " + request.Method + " " + path);
        }

        private HttpResponseMessage RepositoryResponse(HttpStatusCode code)
            => Json(new { id = RepositoryId, full_name = _fullName, description = Description, default_branch = DefaultBranch }, code);

        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(body) };
    }
}
