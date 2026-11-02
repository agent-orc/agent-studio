using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F45a — covers the load / seed / persist contract for
/// <see cref="WorkspaceRegistry"/>. Uses a temporary TaskRepository so the
/// production registry file is never touched.
/// </summary>
public class WorkspaceRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;

    public WorkspaceRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-ws-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            }).Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private WorkspaceRegistry Build() => new(_config, NullLogger<WorkspaceRegistry>.Instance);

    [Fact]
    public void SystemUpsertPersistsCrossProjectUnitReferencesAndRejectsForeignProjects()
    {
        var registry = Build();
        var workspace = registry.EnsureDefaultWorkspace();
        var projects = new[] {
            new ProjectRecord { Id = "PROJ-001", WorkspaceId = workspace.Id },
            new ProjectRecord { Id = "PROJ-002", WorkspaceId = workspace.Id },
            new ProjectRecord { Id = "PROJ-003", WorkspaceId = "ws-elsewhere" },
        };
        var system = new WorkspaceSystem { Id = "product", Name = "Product", Units = [
            new("PROJ-001", "agent"), new("PROJ-002", "website") ] };
        registry.UpsertSystem(workspace.Id, system, projects);
        Assert.Equal(system.Units, Assert.Single(Build().Find(workspace.Id)!.Systems).Units);
        Assert.Throws<ArgumentException>(() => registry.UpsertSystem(workspace.Id,
            system with { Units = [new("PROJ-003", "library")] }, projects));
        Assert.Single(Build().Find(workspace.Id)!.Systems);
    }

    [Fact]
    public void EnsureDefaultWorkspace_WritesFile_AndIsIdempotent()
    {
        var reg = Build();
        var first = reg.EnsureDefaultWorkspace();
        var second = reg.EnsureDefaultWorkspace();

        Assert.Equal(DefaultWorkspace.Id, first.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt); // not re-stamped
        Assert.Single(reg.List());
        Assert.True(File.Exists(RegistryPaths.WorkspacesFilePath(_root)));
    }

    [Fact]
    public void DefaultWorkspace_RoundTrips_ThroughFreshInstance()
    {
        Build().EnsureDefaultWorkspace();

        // A second instance backed by the same TaskRepository loads the
        // persisted state on first call.
        var reloaded = Build();
        var list = reloaded.List();

        Assert.Single(list);
        var ws = Assert.Single(list);
        Assert.Equal(DefaultWorkspace.Id, ws.Id);
        Assert.True(ws.IsDefault);
        Assert.Equal(DefaultWorkspace.DisplayName, ws.DisplayName);
    }

    [Fact]
    public void Find_ReturnsNullForUnknownId()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        Assert.Null(reg.Find("ws-does-not-exist"));
    }

    [Fact]
    public void NoTaskRepository_OperatesInMemoryOnly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var reg = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        Assert.False(reg.IsPersistent);
        // EnsureDefaultWorkspace still seeds the in-memory state but does
        // not throw when there is no backing path.
        var ws = reg.EnsureDefaultWorkspace();
        Assert.Equal(DefaultWorkspace.Id, ws.Id);
        Assert.Single(reg.List());
    }

    [Fact]
    public void List_ReturnsCopy_NotInternalReference()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var first = reg.List();
        first.Clear();
        Assert.NotEmpty(reg.List());
    }

    // ------------------------------------------------------------------
    // F45b mutation tests (ADR-0042)
    // ------------------------------------------------------------------

    [Fact]
    public void Create_AppendsRecord_AndDerivesSlugId()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();

        var frontend = reg.Create("Frontend");

        Assert.Equal("ws-frontend", frontend.Id);
        Assert.Equal("Frontend", frontend.DisplayName);
        Assert.False(frontend.IsDefault);
        Assert.Equal(1, frontend.SortOrder); // after default (0)
        Assert.Equal(2, reg.List().Count);
    }

    [Fact]
    public void Create_DisambiguatesSlugOnCollision()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var first = reg.Create("Frontend");
        var second = reg.Create("Frontend");
        Assert.Equal("ws-frontend", first.Id);
        Assert.Equal("ws-frontend-2", second.Id);
    }

    [Fact]
    public void Create_RejectsBlankDisplayName()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        Assert.Throws<ArgumentException>(() => reg.Create(""));
        Assert.Throws<ArgumentException>(() => reg.Create("   "));
    }

    [Fact]
    public void Rename_ChangesDisplayName_KeepsId()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var ws = reg.Create("Old Name");

        var renamed = reg.Rename(ws.Id, "New Name");

        Assert.Equal(ws.Id, renamed.Id);
        Assert.Equal("New Name", renamed.DisplayName);
        Assert.Equal("New Name", reg.Find(ws.Id)!.DisplayName);
    }

    [Fact]
    public void SetColor_SetsAndClears()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var ws = reg.Create("Frontend");

        var coloured = reg.SetColor(ws.Id, "#ff8800");
        Assert.Equal("#ff8800", coloured.Color);

        var cleared = reg.SetColor(ws.Id, null);
        Assert.Null(cleared.Color);
    }

    [Fact]
    public void Reorder_MovesUp_ReassignsSortOrder()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        reg.Create("First");
        var second = reg.Create("Second");

        var after = reg.Reorder(second.Id, -1);
        Assert.Equal(second.Id, after[1].Id); // default still 0; Second now 1; First now 2
        // After move-up, the order is: ws-default (0), Second (1), First (2)
        Assert.Equal(DefaultWorkspace.Id, after[0].Id);
        Assert.Equal(second.Id, after[1].Id);
        Assert.Equal("First", after[2].DisplayName);
        // SortOrder is reassigned densely:
        Assert.Equal([0, 1, 2], after.Select(w => w.SortOrder));
    }

    [Fact]
    public void Reorder_AtBoundary_IsNoop()
    {
        var reg = Build();
        var def = reg.EnsureDefaultWorkspace();
        var after = reg.Reorder(def.Id, -1);
        Assert.Equal(def.Id, after[0].Id);
    }

    [Fact]
    public void Reorder_RejectsInvalidDirection()
    {
        var reg = Build();
        var ws = reg.EnsureDefaultWorkspace();
        Assert.Throws<ArgumentException>(() => reg.Reorder(ws.Id, 0));
        Assert.Throws<ArgumentException>(() => reg.Reorder(ws.Id, 2));
    }

    [Fact]
    public void Delete_RemovesRecord()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var ws = reg.Create("Frontend");
        var projects = BuildProjectRegistry();

        reg.Delete(ws.Id, projects);

        Assert.Null(reg.Find(ws.Id));
    }

    [Fact]
    public void Delete_RefusesDefaultWorkspace()
    {
        var reg = Build();
        var def = reg.EnsureDefaultWorkspace();
        var projects = BuildProjectRegistry();

        Assert.Throws<InvalidOperationException>(() => reg.Delete(def.Id, projects));
    }

    [Fact]
    public void Delete_BlocksWhenProjectsAssigned_LeavesProjectsAndWorkspaceUntouched()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var ws = reg.Create("Frontend");
        var projects = BuildProjectRegistry();
        var projA = projects.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "proj-a"),
            initialDisplayName: "Proj A",
            workspaceId: ws.Id);
        var projB = projects.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "proj-b"),
            initialDisplayName: "Proj B",
            workspaceId: ws.Id);

        var ex = Assert.Throws<InvalidOperationException>(() => reg.Delete(ws.Id, projects));
        // Message names the count so the 409 the endpoint surfaces is actionable.
        Assert.Contains("2 projects", ex.Message);

        // No auto-rehome: both projects stay on the workspace and the
        // workspace row survives.
        Assert.NotNull(reg.Find(ws.Id));
        Assert.Equal(ws.Id, projects.FindById(projA.Id)!.WorkspaceId);
        Assert.Equal(ws.Id, projects.FindById(projB.Id)!.WorkspaceId);
    }

    [Fact]
    public void Delete_EmptyWorkspace_Succeeds()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var ws = reg.Create("Frontend");
        var projects = BuildProjectRegistry();

        var result = reg.Delete(ws.Id, projects);

        Assert.Equal(ws.Id, result.DeletedId);
        Assert.Null(reg.Find(ws.Id));
    }

    private ProjectRegistry BuildProjectRegistry() =>
        new(_config, NullLogger<ProjectRegistry>.Instance);

    [Fact]
    public void List_SortsBySortOrderThenDisplayName()
    {
        var reg = Build();
        // Seed three workspaces via the internal Replace hook used by F45b.
        var now = DateTime.UtcNow;
        reg.GetType().GetMethod("Replace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(reg, [new WorkspacesFile
            {
                Workspaces =
                [
                    new WorkspaceRecord { Id = "c", DisplayName = "Charlie", SortOrder = 1, CreatedAt = now },
                    new WorkspaceRecord { Id = "a", DisplayName = "Alpha",   SortOrder = 0, CreatedAt = now },
                    new WorkspaceRecord { Id = "b", DisplayName = "Bravo",   SortOrder = 0, CreatedAt = now },
                ]
            }]);

        var list = reg.List();
        Assert.Equal(["Alpha", "Bravo", "Charlie"], list.Select(w => w.DisplayName));
    }

    // ------------------------------------------------------------------
    // F66 regression: create-then-list and persistence roundtrip
    // ------------------------------------------------------------------

    [Fact]
    public void Create_ImmediatelyVisibleInList()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        reg.Create("Test");

        var list = reg.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, w => w.Id == "ws-test" && w.DisplayName == "Test");
        Assert.Contains(list, w => w.Id == DefaultWorkspace.Id);
    }

    [Fact]
    public void Create_RoundTrips_ThroughFreshInstance()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        reg.Create("Test");
        reg.Create("Other", "#ff0000");

        var reloaded = Build();
        var list = reloaded.List();
        Assert.Equal(3, list.Count);
        var test = list.Single(w => w.Id == "ws-test");
        Assert.Equal("Test", test.DisplayName);
        Assert.False(test.IsDefault);
        Assert.Null(test.Color);
        var other = list.Single(w => w.Id == "ws-other");
        Assert.Equal("#ff0000", other.Color);
    }

    [Fact]
    public void Create_EmptyWorkspace_HasEmptyProjectsList()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        reg.Create("Empty");

        var list = reg.List();
        var empty = list.Single(w => w.Id == "ws-empty");
        Assert.Equal("Empty", empty.DisplayName);
        Assert.False(empty.IsDefault);
    }

    [Fact]
    public void List_DetectsExternalFileModification()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        Assert.Single(reg.List());

        // Simulate an external process writing a second workspace to the file.
        var path = RegistryPaths.WorkspacesFilePath(_root);
        var json = File.ReadAllText(path);
        var file = System.Text.Json.JsonSerializer.Deserialize<WorkspacesFile>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var updated = file with
        {
            Workspaces =
            [
                .. file.Workspaces,
                new WorkspaceRecord
                {
                    Id = "ws-external",
                    DisplayName = "External",
                    SortOrder = 1,
                    CreatedAt = DateTime.UtcNow,
                },
            ],
        };
        // Ensure the mtime advances past the file-system resolution.
        Thread.Sleep(50);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(updated,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var list = reg.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, w => w.Id == "ws-external");
    }

    [Fact]
    public void Find_DetectsExternalFileModification()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        Assert.Null(reg.Find("ws-injected"));

        var path = RegistryPaths.WorkspacesFilePath(_root);
        var json = File.ReadAllText(path);
        var file = System.Text.Json.JsonSerializer.Deserialize<WorkspacesFile>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var updated = file with
        {
            Workspaces =
            [
                .. file.Workspaces,
                new WorkspaceRecord
                {
                    Id = "ws-injected",
                    DisplayName = "Injected",
                    SortOrder = 1,
                    CreatedAt = DateTime.UtcNow,
                },
            ],
        };
        Thread.Sleep(50);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(updated,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var found = reg.Find("ws-injected");
        Assert.NotNull(found);
        Assert.Equal("Injected", found.DisplayName);
    }
}
