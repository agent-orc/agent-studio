using AgentStudio.Registry;
using AgentStudio.Shared;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests;

public sealed class ProjectPathImportTests
{
    [Fact]
    public void Maps_windows_checkout_and_storage_and_reports_missing_checkout()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-import-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(root, "Repos", "App");
        var storage = Path.Combine(root, "Jobs", "App");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        Directory.CreateDirectory(storage);
        try
        {
            var mapping = new[] { new ProjectPathMapping(@"C:\Workspace", root) };
            var project = new ProjectRecord
            {
                Id = "PROJ-001", DisplayName = "App",
                RepositoryPath = @"C:\Workspace\Repos\App",
                RootPath = @"C:\Workspace\Repos\App",
                StorageLocation = @"C:\Workspace\Jobs\App",
            };
            var ready = ProjectPathImport.Plan(project, mapping);
            Assert.Equal("ready", ready.Status);
            Assert.Equal(checkout, ready.RepositoryPath);
            Assert.Equal(storage, ready.StorageLocation);

            Directory.Delete(checkout, true);
            var missing = ProjectPathImport.Plan(project, mapping);
            Assert.Equal("missing-checkout", missing.Status);
            Assert.Contains(checkout, missing.Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unmapped_and_prefix_collision_do_not_escape_the_import_root()
    {
        var mapping = new[] { new ProjectPathMapping(@"C:\Workspace", "/srv/studio") };
        Assert.Null(ProjectPathImport.Map(@"C:\WorkspaceOld\App", mapping));
        Assert.Null(ProjectPathImport.Map(@"C:\Workspace\..\secret", mapping));
        var row = ProjectPathImport.Plan(new ProjectRecord
        {
            Id = "PROJ-002", DisplayName = "Missing", StorageLocation = @"D:\Jobs\Missing",
            RepositoryPath = @"C:\WorkspaceOld\App",
        }, mapping);
        Assert.Equal("unmapped", row.Status);
    }

    [Fact]
    public void Root_only_project_requires_a_git_checkout_and_imports_it_as_repository_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-root-only-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(root, "App");
        Directory.CreateDirectory(checkout);
        try
        {
            var mapping = new[] { new ProjectPathMapping(@"C:\Workspace", root) };
            var project = new ProjectRecord
            {
                Id = "PROJ-003", DisplayName = "Root only", RootPath = @"C:\Workspace\App",
            };
            var missing = ProjectPathImport.Plan(project, mapping);
            Assert.Equal("missing-checkout", missing.Status);
            Assert.Contains(checkout, missing.Reason);

            Directory.CreateDirectory(Path.Combine(checkout, ".git"));
            var ready = ProjectPathImport.Plan(project, mapping);
            Assert.Equal("ready", ready.Status);
            Assert.Equal(checkout, ready.RepositoryPath);
            Assert.Equal(checkout, ready.RootPath);

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
            }).Build();
            var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            var stored = registry.EnsureProjectForStorage(root, "Root only", "ws-1", new FakeTimeProvider());
            registry.SetRootPath(stored.Id, checkout);
            var storedPlan = ProjectPathImport.Plan(registry.FindById(stored.Id)!, mapping);
            registry.ApplyPathImport([storedPlan]);
            Assert.Equal(checkout, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance)
                .FindById(stored.Id)!.RepositoryPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Apply_persists_ready_paths_and_keeps_unmapped_project_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-import-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var checkout = Path.Combine(root, "App");
            var storage = Path.Combine(root, "Jobs");
            Directory.CreateDirectory(Path.Combine(checkout, ".git"));
            Directory.CreateDirectory(storage);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
            }).Build();
            var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            var clock = new FakeTimeProvider();
            var ready = registry.EnsureProjectForStorage(@"C:\Workspace\Jobs", "Ready", "ws-1", clock);
            registry.SetRepositoryPath(ready.Id, checkout);
            var unmapped = registry.EnsureProjectForStorage(@"D:\Elsewhere\Jobs", "Unmapped", "ws-1", clock);
            var rows = registry.List().Select(project => ProjectPathImport.Plan(project,
                [new ProjectPathMapping(@"C:\Workspace", root)])).ToList();
            registry.ApplyPathImport(rows);
            var reloaded = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            Assert.Equal(storage, reloaded.FindById(ready.Id)!.StorageLocation);
            Assert.Equal(checkout, reloaded.FindById(ready.Id)!.RootPath);
            Assert.Equal(@"D:\Elsewhere\Jobs", reloaded.FindById(unmapped.Id)!.StorageLocation);
        }
        finally { Directory.Delete(root, true); }
    }
}
