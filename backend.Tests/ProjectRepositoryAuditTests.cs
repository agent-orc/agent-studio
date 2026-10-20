using AgentStudio.Registry;
using AgentStudio.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectRepositoryAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repository-audit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private static readonly ProjectRepositoryMutationActor Owner = new("owner-id", "owner");
    private const string First = "https://example.test/first.git";
    private const string Second = "https://example.test/second.git";

    private (ProjectRegistry Registry, WorkspaceRegistry Workspaces, string Id) Create(
        IAtomicJsonFileWriter? writer = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root }).Build();
        var workspaces = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance, writer, _clock);
        var project = registry.EnsureProjectForStorage(Path.Combine(_root, "project"), "Audit Project", DefaultWorkspace.Id);
        return (registry, workspaces, project.Id);
    }

    [Fact]
    public void Persist_failure_leaves_no_effective_change_or_success_audit()
    {
        var writer = new ControllableAtomicJsonFileWriter();
        var (registry, workspaces, id) = Create(writer);
        writer.ShouldFail = (path, _) => path.EndsWith(RegistryPaths.ProjectsFileName, StringComparison.Ordinal);

        Assert.Throws<ProjectPersistenceException>(() => registry.UpdateWithRepositoryGuard(
            id, new UpdateProjectRequest { RepositoryUrl = First }, workspaces, true, Owner));
        Assert.Null(AgentStudio.Tasks.RemoteProjectRepositoryResolver.ResolveRepositoryUrl(registry.FindById(id)));
        Assert.Empty(registry.RepositoryAudit(id));
        var reloaded = CreateReloaded();
        Assert.Null(AgentStudio.Tasks.RemoteProjectRepositoryResolver.ResolveRepositoryUrl(reloaded.FindById(id)));
        Assert.Empty(reloaded.RepositoryAudit(id));
    }

    [Fact]
    public void Rollback_is_recorded_and_refuses_to_erase_a_concurrent_change()
    {
        var (registry, workspaces, id) = Create();
        var first = registry.UpdateWithRepositoryGuard(
            id, new UpdateProjectRequest { RepositoryUrl = First }, workspaces, true, Owner);
        registry.RollbackUpdate(first.Updated, first.Previous, true, Owner);
        Assert.Null(AgentStudio.Tasks.RemoteProjectRepositoryResolver.ResolveRepositoryUrl(registry.FindById(id)));
        Assert.Equal(new[] { "changed", "rolled-back" }, registry.RepositoryAudit(id).Select(row => row.Action));

        var again = registry.UpdateWithRepositoryGuard(
            id, new UpdateProjectRequest { RepositoryUrl = First }, workspaces, true, Owner);
        registry.UpdateWithRepositoryGuard(
            id, new UpdateProjectRequest { RepositoryUrl = Second }, workspaces, true, Owner);
        Assert.Throws<InvalidOperationException>(() =>
            registry.RollbackUpdate(again.Updated, again.Previous, true, Owner));
        Assert.Equal(Second, AgentStudio.Tasks.RemoteProjectRepositoryResolver.ResolveRepositoryUrl(registry.FindById(id)));
        Assert.Equal(new[] { ((string?)null, First), (First, (string?)null),
            ((string?)null, First), (First, Second) },
            registry.RepositoryAudit(id).Select(row => (row.PreviousRepositoryUrl, row.RepositoryUrl)));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, registry.RepositoryAudit(id)[0].Timestamp);
    }

    [Fact]
    public async Task Concurrent_updates_audit_the_actual_committed_predecessor()
    {
        var writer = new ControllableAtomicJsonFileWriter();
        var (registry, workspaces, id) = Create(writer);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pending = 0;
        writer.ShouldFail = (path, _) =>
        {
            if (path.EndsWith(RegistryPaths.ProjectsFileName, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref pending, 1, 0) == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Concurrent mutation was not released.");
            }
            return false;
        };
        var first = Task.Run(() => registry.UpdateWithRepositoryGuard(
            id, new UpdateProjectRequest { RepositoryUrl = First }, workspaces, true, Owner));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => registry.UpdateWithRepositoryGuard(
            id, new UpdateProjectRequest { RepositoryUrl = Second }, workspaces, true, Owner));
        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { ((string?)null, First), (First, Second) },
            registry.RepositoryAudit(id).Select(row => (row.PreviousRepositoryUrl, row.RepositoryUrl)));
        Assert.Equal(Second, AgentStudio.Tasks.RemoteProjectRepositoryResolver.ResolveRepositoryUrl(registry.FindById(id)));
        Assert.Equal(2, CreateReloaded().RepositoryAudit(id).Count);
    }

    private ProjectRegistry CreateReloaded()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root }).Build();
        return new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance, clock: _clock);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException ex) { SilentCatch.Note(ex, "ProjectRepositoryAuditTests: fixture cleanup is best-effort"); }
    }
}
