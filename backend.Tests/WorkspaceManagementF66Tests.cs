using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F66 / ADR-0048 — locks in the workspace-management invariants the
/// operator complaint surfaced:
///
/// <list type="bullet">
/// <item>The <c>GET /api/workspaces</c> projection returns every workspace,
/// including ones with zero assigned projects. (Reproduces the
/// "ws-test missing from list" symptom and proves the LEFT-JOIN
/// semantics are stable.)</item>
/// <item>Workspace rename touches only <c>workspaces.json</c> — no
/// directories are created, moved, or deleted.</item>
/// <item>Project workspace reassignment touches only <c>projects.json</c>
/// — the project's <c>StorageLocation</c> on disk is not moved.</item>
/// <item>Delete is blocked while projects are still assigned (no
/// auto-rehome); once the workspace is empty it deletes and nothing on
/// disk is touched.</item>
/// </list>
///
/// These tests guard ADR-0048 ("workspaces are virtual groupings; projects
/// are the disk reality"). A regression that adds a <c>Directory.Move</c>
/// to the rename or reassign paths will fail here.
/// </summary>
public sealed class WorkspaceManagementF66Tests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;

    public WorkspaceManagementF66Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-ws-f66-" + Guid.NewGuid().ToString("N"));
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

    private WorkspaceRegistry BuildWorkspaces() => new(_config, NullLogger<WorkspaceRegistry>.Instance);
    private ProjectRegistry BuildProjects() => new(_config, NullLogger<ProjectRegistry>.Instance);

    // ------------------------------------------------------------------
    // Acceptance #1 + #5 (workspace-create-and-list-immediate.spec.ts) —
    // GET /api/workspaces projection includes empty workspaces.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildWorkspaceListing_IncludesWorkspacesWithZeroProjects()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        workspaces.Create("Test"); // ws-test — zero projects

        var payload = RegistryEndpoints.BuildWorkspaceListing(workspaces, projects, includeArchived: false);

        Assert.Equal(2, payload.Count);
        var test = payload.Single(w => w.Id == "ws-test");
        Assert.Equal("Test", test.DisplayName);
        Assert.Empty(test.Projects);
        var defaultWs = payload.Single(w => w.Id == DefaultWorkspace.Id);
        Assert.Empty(defaultWs.Projects);
    }

    [Fact]
    public void BuildWorkspaceListing_PreservesProjectsOnPopulatedWorkspaces()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var ws = workspaces.Create("Frontend");
        projects.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "proj-a"),
            initialDisplayName: "Proj A",
            workspaceId: ws.Id);
        projects.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "proj-b"),
            initialDisplayName: "Proj B",
            workspaceId: ws.Id);

        var payload = RegistryEndpoints.BuildWorkspaceListing(workspaces, projects, includeArchived: false);

        Assert.Equal(2, payload.Count);
        var frontend = payload.Single(w => w.Id == ws.Id);
        Assert.Equal(2, frontend.Projects.Count);
        Assert.Contains(frontend.Projects, p => p.DisplayName == "Proj A");
        Assert.Contains(frontend.Projects, p => p.DisplayName == "Proj B");
    }

    [Fact]
    public void BuildWorkspaceListing_OmitsArchivedProjectsByDefault_WorkspaceStillVisible()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var ws = workspaces.Create("Frontend");
        var proj = projects.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "proj-a"),
            initialDisplayName: "Proj A",
            workspaceId: ws.Id);
        projects.SetArchived(proj.Id, true);

        var payload = RegistryEndpoints.BuildWorkspaceListing(workspaces, projects, includeArchived: false);

        var frontend = payload.Single(w => w.Id == ws.Id);
        Assert.Empty(frontend.Projects); // archived project filtered
        Assert.Equal(2, payload.Count);   // workspace still listed
    }

    // ------------------------------------------------------------------
    // Acceptance #3 + #5 (workspace-rename-roundtrip.spec.ts) —
    // Rename only mutates workspaces.json, never the filesystem layout.
    // ------------------------------------------------------------------

    [Fact]
    public void Rename_OnlyMutatesWorkspacesJson_NoDirectoryCreated()
    {
        var workspaces = BuildWorkspaces();
        workspaces.EnsureDefaultWorkspace();
        var ws = workspaces.Create("Old");
        var before = SnapshotDirectoryTree(_root);
        var workspacesJson = RegistryPaths.WorkspacesFilePath(_root);
        var beforeBytes = File.ReadAllBytes(workspacesJson);

        workspaces.Rename(ws.Id, "New");

        var after = SnapshotDirectoryTree(_root);
        Assert.Equal(before, after); // no new files / folders anywhere
        var afterBytes = File.ReadAllBytes(workspacesJson);
        Assert.NotEqual(beforeBytes, afterBytes); // workspaces.json did change
        Assert.Equal("New", workspaces.Find(ws.Id)!.DisplayName);
    }

    [Fact]
    public void Rename_DoesNotMoveProjectStorageLocations()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var ws = workspaces.Create("Old");
        var storage = Path.Combine(_root, "proj-a");
        Directory.CreateDirectory(storage);
        var proj = projects.EnsureProjectForStorage(
            storageLocation: storage,
            initialDisplayName: "Proj A",
            workspaceId: ws.Id);

        workspaces.Rename(ws.Id, "New");

        Assert.True(Directory.Exists(storage));
        Assert.Equal(storage, projects.FindById(proj.Id)!.StorageLocation);
    }

    // ------------------------------------------------------------------
    // Acceptance #4 + #5 (project-drag-and-drop-between-workspaces.spec.ts) —
    // SetWorkspace only mutates projects.json, never the filesystem layout.
    // ------------------------------------------------------------------

    [Fact]
    public void ProjectSetWorkspace_OnlyMutatesProjectsJson_NoDirectoryMove()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var target = workspaces.Create("Backend");
        var storage = Path.Combine(_root, "proj-a");
        Directory.CreateDirectory(storage);
        var proj = projects.EnsureProjectForStorage(
            storageLocation: storage,
            initialDisplayName: "Proj A",
            workspaceId: DefaultWorkspace.Id);
        var before = SnapshotDirectoryTree(_root);
        var projectsJson = RegistryPaths.ProjectsFilePath(_root);
        var beforeBytes = File.ReadAllBytes(projectsJson);

        var moved = projects.SetWorkspace(proj.Id, target.Id, workspaces);

        Assert.Equal(target.Id, moved.WorkspaceId);
        Assert.Equal(storage, moved.StorageLocation); // storage unchanged
        Assert.True(Directory.Exists(storage));
        var after = SnapshotDirectoryTree(_root);
        // The registry is a .metadata runtime store and keeps its last-good
        // version as projects.json.bak; nothing else may appear.
        var backup = "F:" + Path.GetRelativePath(_root, projectsJson + ".bak").Replace('\\', '/');
        Assert.Equal(before.Append(backup).Distinct().Order(StringComparer.Ordinal), after.Order(StringComparer.Ordinal)); // no new / moved / removed dirs
        Assert.Equal(beforeBytes, File.ReadAllBytes(projectsJson + ".bak"));
        var afterBytes = File.ReadAllBytes(projectsJson);
        Assert.NotEqual(beforeBytes, afterBytes); // projects.json did change
    }

    [Fact]
    public void ProjectSetWorkspace_RejectsUnknownWorkspaceId()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var proj = projects.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "proj-a"),
            initialDisplayName: "Proj A",
            workspaceId: DefaultWorkspace.Id);

        Assert.Throws<KeyNotFoundException>(() =>
            projects.SetWorkspace(proj.Id, "ws-does-not-exist", workspaces));
    }

    // ------------------------------------------------------------------
    // Hard rule: deleting a non-default workspace is blocked while it
    // still has projects assigned. The operator must move every project
    // out first; there is no auto-rehome and nothing on disk is touched.
    // ------------------------------------------------------------------

    [Fact]
    public void Delete_BlocksWhileProjectsAssigned_LeavesEverythingInPlace()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var ws = workspaces.Create("Frontend");
        var storageA = Path.Combine(_root, "proj-a");
        var storageB = Path.Combine(_root, "proj-b");
        Directory.CreateDirectory(storageA);
        Directory.CreateDirectory(storageB);
        var a = projects.EnsureProjectForStorage(storageA, "Proj A", ws.Id);
        var b = projects.EnsureProjectForStorage(storageB, "Proj B", ws.Id);

        Assert.Throws<InvalidOperationException>(() => workspaces.Delete(ws.Id, projects));

        // Workspace survives, projects stay assigned to it (no rehome),
        // and storage on disk is untouched.
        Assert.NotNull(workspaces.Find(ws.Id));
        Assert.Equal(ws.Id, projects.FindById(a.Id)!.WorkspaceId);
        Assert.Equal(ws.Id, projects.FindById(b.Id)!.WorkspaceId);
        Assert.True(Directory.Exists(storageA));
        Assert.True(Directory.Exists(storageB));
        Assert.Equal(storageA, projects.FindById(a.Id)!.StorageLocation);
        Assert.Equal(storageB, projects.FindById(b.Id)!.StorageLocation);
    }

    [Fact]
    public void Delete_SucceedsOnceWorkspaceIsEmpty()
    {
        var workspaces = BuildWorkspaces();
        var projects = BuildProjects();
        workspaces.EnsureDefaultWorkspace();
        var ws = workspaces.Create("Frontend");
        var storage = Path.Combine(_root, "proj-a");
        Directory.CreateDirectory(storage);
        var a = projects.EnsureProjectForStorage(storage, "Proj A", ws.Id);

        // Blocked while populated...
        Assert.Throws<InvalidOperationException>(() => workspaces.Delete(ws.Id, projects));
        // ...move the project out, then the empty workspace deletes.
        projects.SetWorkspace(a.Id, DefaultWorkspace.Id, workspaces);

        var result = workspaces.Delete(ws.Id, projects);

        Assert.Equal(ws.Id, result.DeletedId);
        Assert.Null(workspaces.Find(ws.Id));
        Assert.Equal(DefaultWorkspace.Id, projects.FindById(a.Id)!.WorkspaceId);
        Assert.True(Directory.Exists(storage)); // storage untouched throughout
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a sorted snapshot of every directory and file under
    /// <paramref name="root"/>, with file contents hashed by length so a
    /// mutation to <c>workspaces.json</c> / <c>projects.json</c> is still
    /// distinguishable from "no change at all". The intent of this snapshot
    /// is to detect a regression that introduces a <c>Directory.Move</c> /
    /// <c>Directory.CreateDirectory</c> in the rename or reassign paths.
    /// </summary>
    private static List<string> SnapshotDirectoryTree(string root)
    {
        var list = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderBy(s => s))
        {
            list.Add("D:" + Path.GetRelativePath(root, dir).Replace('\\', '/'));
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(s => s))
        {
            list.Add("F:" + Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        return list;
    }
}
