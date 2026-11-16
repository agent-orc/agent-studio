using System.Diagnostics;
using AgentStudio.Projects;
using AgentStudio.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3809: a second project added by repository URL on the local profile got
/// no server checkout, because only the first-run checklist cloned and only for
/// its first project. Its first card escalated with server-checkout-missing.
/// </summary>
public sealed class ProjectServerCheckoutServiceTests : IDisposable
{
    private const string Url = "https://github.com/example/second-project.git";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "server-checkout-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly ProjectRegistry _projects;
    private readonly ProjectSettingsService _settings;

    public ProjectServerCheckoutServiceTests()
    {
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        _projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        _settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    [Fact]
    public async Task Url_project_without_checkout_is_cloned_onto_its_integration_branch_and_registered()
    {
        SeedOrigin(publishDevelop: true);
        var project = AddUrlProject("Second project");
        _settings.SetIntegrationBranch(project.DisplayName, "develop");
        var service = Service();
        Assert.Equal("missing", service.Get(project.Id)?.State);

        var started = service.Start(project.Id);
        Assert.Equal("running", started.State);
        var finished = await WaitAsync(service, project.Id);

        var expected = Path.Combine(_root, "repository-checkouts", project.Id);
        Assert.Equal("ready", finished.State);
        Assert.Equal(expected, finished.RepositoryPath);
        Assert.Equal("develop", finished.Branch);
        Assert.Equal("develop", Git(expected, "rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal("origin/develop", Git(expected, "rev-parse", "--abbrev-ref", "@{u}").Trim());
        Assert.Equal("develop line\n", File.ReadAllText(Path.Combine(expected, "DEVELOP.md")));
        Assert.Equal(expected, _projects.FindById(project.Id)!.RepositoryPath);
        Assert.Equal(expected, _projects.FindById(project.Id)!.RootPath);
        Assert.Equal("ready", service.Get(project.Id)?.State);
        Assert.Throws<InvalidOperationException>(() => service.Start(project.Id));
    }

    [Fact]
    public async Task Unpublished_integration_branch_starts_at_the_default_branch()
    {
        SeedOrigin(publishDevelop: false);
        var project = AddUrlProject("Unpublished branch");
        _settings.SetIntegrationBranch(project.DisplayName, "integration");

        var finished = await Service().RunAsync(project.Id, CancellationToken.None);

        Assert.Equal("ready", finished.State);
        var checkout = finished.RepositoryPath!;
        Assert.Equal("integration", Git(checkout, "rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal(Git(checkout, "rev-parse", "origin/main").Trim(), Git(checkout, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Failed_clone_leaves_no_checkout_and_can_be_retried()
    {
        var project = AddUrlProject("Unreachable");
        var service = Service();

        var failed = await service.RunAsync(project.Id, CancellationToken.None);

        Assert.Equal("failed", failed.State);
        Assert.Contains(Url, failed.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "repository-checkouts", project.Id)));
        Assert.Null(_projects.FindById(project.Id)!.RepositoryPath);
        Assert.Equal("failed", service.Get(project.Id)?.State);

        SeedOrigin(publishDevelop: false);
        Assert.Equal("ready", (await service.RunAsync(project.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Foreign_folder_at_the_checkout_path_is_never_overwritten()
    {
        SeedOrigin(publishDevelop: false);
        var project = AddUrlProject("Occupied");
        var occupied = Path.Combine(_root, "repository-checkouts", project.Id);
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "notes.txt"), "keep me");

        var failed = await Service().RunAsync(project.Id, CancellationToken.None);

        Assert.Equal("failed", failed.State);
        Assert.Contains("already exists", failed.Error);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(occupied, "notes.txt")));
    }

    [Fact]
    public void Project_without_url_offers_no_checkout()
    {
        var project = AddUrlProject("No url", url: null);
        var service = Service();

        Assert.Equal("unavailable", service.Get(project.Id)?.State);
        Assert.Throws<InvalidOperationException>(() => service.Start(project.Id));
        Assert.Throws<KeyNotFoundException>(() => service.Start("PROJ-404"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ProjectServerCheckoutService Service()
    {
        var credentials = new LocalFirstRunGitSetup(_projects,
            new LocalFirstRunRepositoryProbe(new FakeTimeProvider()),
            Path.Combine(_root, "first-run-git"), Path.Combine(_root, "gitconfig"));
        // The registered URL is an https GitHub address; this maps it onto the
        // local bare repository the way the e2e Git shim does.
        return new ProjectServerCheckoutService(_projects, _settings, credentials, null, null,
            new Dictionary<string, string?>
            {
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = $"url.file://{_origin}.insteadOf",
                ["GIT_CONFIG_VALUE_0"] = Url,
            });
    }

    private ProjectRecord AddUrlProject(string name, string? url = Url)
    {
        var id = _projects.AllocateNextId();
        var storage = Path.Combine(_root, "projects", id, "tasks");
        Directory.CreateDirectory(storage);
        return _projects.Append(new ProjectRecord
        {
            Id = id,
            SourceType = ProjectSourceTypes.LocalFolder,
            DisplayName = name,
            ShortCode = "P" + id[^2..].Replace("-", "X"),
            WorkspaceId = "ws-default",
            StorageLocation = storage,
            Urls = url is null ? [] : [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = url, SortOrder = 0 }],
            CreatedAt = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private void SeedOrigin(bool publishDevelop)
    {
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        Git(_root, "init", "--bare", _origin);
        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.name", "Fixture");
        Git(seed, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(seed, "README.md"), "fixture\n");
        Git(seed, "add", "README.md");
        Git(seed, "commit", "-m", "initial");
        Git(seed, "remote", "add", "origin", _origin);
        Git(seed, "push", "origin", "main");
        if (publishDevelop)
        {
            Git(seed, "checkout", "-b", "develop");
            File.WriteAllText(Path.Combine(seed, "DEVELOP.md"), "develop line\n");
            Git(seed, "add", "DEVELOP.md");
            Git(seed, "commit", "-m", "develop");
            Git(seed, "push", "origin", "develop");
        }
        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/main");
    }

    private static async Task<ProjectServerCheckoutStatus> WaitAsync(ProjectServerCheckoutService service, string id)
    {
        for (var attempt = 0; attempt < 1_200; attempt++)
        {
            var status = service.Get(id)!;
            if (status.State != "running") return status;
            await Task.Delay(50);
        }
        throw new TimeoutException("The server checkout did not finish.");
    }

    private static string Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
