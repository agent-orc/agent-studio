using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using AgentStudio.Security;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F45a — id allocation, lookup, and per-project task-key counter
/// behaviour for <see cref="ProjectRegistry"/>. Tests use a temp
/// TaskRepository so the production state is never touched.
/// </summary>
public class ProjectRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string _repository;
    private readonly IConfiguration _config;

    public ProjectRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-proj-reg-" + Guid.NewGuid().ToString("N"));
        _repository = Path.Combine(Path.GetTempPath(), "rdo-product-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));
        Directory.CreateDirectory(Path.Combine(_repository, "src"));
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            }).Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_repository, recursive: true); } catch { /* best-effort */ }
    }

    private ProjectRegistry Build(IAtomicJsonFileWriter? fileWriter = null) =>
        new(_config, NullLogger<ProjectRegistry>.Instance, fileWriter);

    private ProjectRegistry Build(ILogger<ProjectRegistry> logger) =>
        new(_config, logger);

    [Fact]
    public void ExistingInvalidFile_FailsClosed_AndPreservesOriginalBytes()
    {
        var seeded = Build();
        seeded.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "protected"),
            "Protected",
            DefaultWorkspace.Id);

        var projectsFile = RegistryPaths.ProjectsFilePath(_root);
        var corrupt = File.ReadAllText(projectsFile)
            .Replace("\"Urls\": []", "\"Urls\": null", StringComparison.Ordinal);
        Assert.Contains("\"Urls\": null", corrupt, StringComparison.Ordinal);
        File.WriteAllText(projectsFile, corrupt);

        var logger = new RegistryLogger();
        var reloaded = Build(logger);

        Assert.Throws<ProjectRegistryLoadException>(() =>
            reloaded.EnsureProjectForStorage(
                Path.Combine(_root, "projects", "legacy-reseed"),
                "Legacy Reseed",
                DefaultWorkspace.Id));

        Assert.Equal(corrupt, File.ReadAllText(projectsFile));
        Assert.Empty(Directory.GetFiles(
            RegistryPaths.MetadataDir(_root),
            $"{RegistryPaths.ProjectsFileName}.quarantine-*"));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("project-registry-load-failed", StringComparison.Ordinal)
            && entry.Message.Contains("action=fail-closed", StringComparison.Ordinal));
    }

    [Fact]
    public void ShrinkingPersist_QuarantinesPreviousRegistry_AndLogsCounts()
    {
        var seeded = Build();
        var first = seeded.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "first"),
            "First",
            DefaultWorkspace.Id);
        seeded.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "second"),
            "Second",
            DefaultWorkspace.Id);

        var projectsFile = RegistryPaths.ProjectsFilePath(_root);
        var beforeDelete = File.ReadAllText(projectsFile);
        var logger = new RegistryLogger();
        var reloaded = Build(logger);
        Assert.Equal(2, reloaded.List().Count);

        reloaded.Delete(first.Id);

        var quarantines = Directory.GetFiles(
            RegistryPaths.MetadataDir(_root),
            $"{RegistryPaths.ProjectsFileName}.quarantine-*");
        var quarantine = Assert.Single(quarantines);
        Assert.Equal(beforeDelete, File.ReadAllText(quarantine));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("project-registry-shrink-quarantined", StringComparison.Ordinal)
            && entry.Message.Contains("previousCount=2", StringComparison.Ordinal)
            && entry.Message.Contains("nextCount=1", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureProjectForStorage_AllocatesPROJ001_FirstTime()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "projects", "demo"),
            initialDisplayName: "Agent Task Processor",
            workspaceId: DefaultWorkspace.Id);

        Assert.Equal("PROJ-001", p.Id);
        Assert.Equal("ATP", p.ShortCode);
        Assert.Equal("Agent Task Processor", p.DisplayName);
        Assert.Equal(DefaultWorkspace.Id, p.WorkspaceId);
        Assert.Equal(1, p.NextTaskKeySeq);
        Assert.Equal(1, p.NextWorkbenchKeySeq);
        Assert.False(p.Archived);
    }

    [Fact]
    public void EnsureProjectForStorage_PersistFailure_RestoresProjectAndIdAllocation()
    {
        var writer = new ControllableAtomicJsonFileWriter
        {
            ShouldFail = (_, _) => true,
        };
        var reg = Build(writer);

        Assert.Throws<ProjectPersistenceException>(() => reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "failed"),
            "Failed Project",
            DefaultWorkspace.Id));
        Assert.Empty(reg.List());

        writer.ShouldFail = null;
        var created = reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "created"),
            "Created Project",
            DefaultWorkspace.Id);
        Assert.Equal("PROJ-001", created.Id);
        Assert.Single(Build().List());
    }

    [Fact]
    public void EnsureProjectForStorage_Idempotent_ForSamePath()
    {
        var reg = Build();
        var path = Path.Combine(_root, "projects", "demo");
        var first = reg.EnsureProjectForStorage(path, "Demo", DefaultWorkspace.Id);
        var second = reg.EnsureProjectForStorage(path, "Demo Renamed", DefaultWorkspace.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Demo", second.DisplayName); // initial name preserved
        Assert.Single(reg.List());
    }

    [Fact]
    public void EnsureProjectForStorage_NormalisesPath()
    {
        var reg = Build();
        var asWindows = "C:\\Demo\\Path";
        var asPosix = "C:/Demo/Path";
        var first = reg.EnsureProjectForStorage(asWindows, "Demo", DefaultWorkspace.Id);
        var lookup = reg.FindByStorageLocation(asPosix);

        Assert.NotNull(lookup);
        Assert.Equal(first.Id, lookup!.Id);
    }

    [Fact]
    public void AllocateIds_AreMonotonic_AcrossProjects()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "One",   DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "Two",   DefaultWorkspace.Id);
        var c = reg.EnsureProjectForStorage(Path.Combine(_root, "p3"), "Three", DefaultWorkspace.Id);

        Assert.Equal(["PROJ-001", "PROJ-002", "PROJ-003"], new[] { a.Id, b.Id, c.Id });
    }

    [Fact]
    public void ShortCode_Collision_GetsNumericSuffix()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Agent Task Processor", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "Agent Task Processor", DefaultWorkspace.Id);

        Assert.Equal("ATP", a.ShortCode);
        Assert.Equal("ATP2", b.ShortCode);
    }

    [Fact]
    public void IssueNextTaskKey_IsMonotonic_PerProject()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "A", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "B", DefaultWorkspace.Id);

        var a1 = reg.IssueNextTaskKey(a.Id);
        var a2 = reg.IssueNextTaskKey(a.Id);
        var b1 = reg.IssueNextTaskKey(b.Id);
        var a3 = reg.IssueNextTaskKey(a.Id);

        Assert.Equal(1, a1);
        Assert.Equal(2, a2);
        Assert.Equal(3, a3);
        Assert.Equal(1, b1);
    }

    [Fact]
    public void IssueNextTaskKey_ThrowsForUnknownProjectId()
    {
        var reg = Build();
        Assert.Throws<KeyNotFoundException>(() => reg.IssueNextTaskKey("PROJ-999"));
    }

    [Fact]
    public void EnsureTaskKeyFloor_RaisesButNeverLowers()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        reg.EnsureTaskKeyFloor(p.Id, 50);
        Assert.Equal(50, reg.IssueNextTaskKey(p.Id));

        // Floor below current counter is a no-op.
        reg.EnsureTaskKeyFloor(p.Id, 10);
        Assert.Equal(51, reg.IssueNextTaskKey(p.Id));
    }

    [Fact]
    public void IssueNextWorkbenchKey_IsMonotonicPerProject_AndFloorPersists()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "A", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "B", DefaultWorkspace.Id);

        Assert.Equal(1, reg.IssueNextWorkbenchKey(a.Id));
        reg.EnsureWorkbenchKeyFloor(a.Id, 8);
        Assert.Equal(8, reg.IssueNextWorkbenchKey(a.Id));
        Assert.Equal(1, reg.IssueNextWorkbenchKey(b.Id));

        var reloaded = Build();
        Assert.Equal(9, reloaded.FindById(a.Id)!.NextWorkbenchKeySeq);
        Assert.Equal(2, reloaded.FindById(b.Id)!.NextWorkbenchKeySeq);
    }

    [Fact]
    public void State_RoundTrips_ThroughFreshInstance()
    {
        var reg = Build();
        reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "A", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "B", DefaultWorkspace.Id);
        reg.IssueNextTaskKey(b.Id);
        reg.IssueNextTaskKey(b.Id);
        reg.IssueNextTaskKey(b.Id);
        reg.IssueNextWorkbenchKey(b.Id);

        var reloaded = Build();
        var pa = reloaded.FindById("PROJ-001")!;
        var pb = reloaded.FindById("PROJ-002")!;

        Assert.Equal("A", pa.DisplayName);
        Assert.Equal(1, pa.NextTaskKeySeq);
        Assert.Equal("B", pb.DisplayName);
        Assert.Equal(4, pb.NextTaskKeySeq);
        Assert.Equal(2, pb.NextWorkbenchKeySeq);
    }

    // ------------------------------------------------------------------
    // F45b mutation tests (ADR-0042)
    // ------------------------------------------------------------------

    [Fact]
    public void Rename_ChangesDisplayName_KeepsIdAndShortCode()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Old Name", DefaultWorkspace.Id);

        var renamed = reg.Rename(p.Id, "New Name");

        Assert.Equal(p.Id, renamed.Id);
        Assert.Equal(p.ShortCode, renamed.ShortCode);
        Assert.Equal("New Name", renamed.DisplayName);
    }

    private sealed class RegistryLogger : ILogger<ProjectRegistry>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void Rename_PersistFailure_RestoresInMemoryAndDurableRecord()
    {
        var writer = new ControllableAtomicJsonFileWriter();
        var reg = Build(writer);
        var project = reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "demo"), "Before Failure", DefaultWorkspace.Id);
        var projectsFile = RegistryPaths.ProjectsFilePath(_root);
        var durableBefore = File.ReadAllText(projectsFile);
        writer.ShouldFail = (path, _) =>
            string.Equals(path, projectsFile, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ProjectPersistenceException>(() => reg.Rename(project.Id, "Must Roll Back"));

        Assert.Equal("Before Failure", reg.FindById(project.Id)!.DisplayName);
        Assert.Equal(durableBefore, File.ReadAllText(projectsFile));
        Assert.Equal("Before Failure", Build().FindById(project.Id)!.DisplayName);
    }

    [Fact]
    public void SetShortCode_NormalisesUppercase_AndValidates()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var updated = reg.SetShortCode(p.Id, "rbk");
        Assert.Equal("RBK", updated.ShortCode);

        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "a"));         // too short
        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "abcdefg"));   // too long
        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "ab-cd"));     // invalid char
        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "1abc"));      // must start with a letter
    }

    [Fact]
    public void SetShortCode_RejectsDuplicate()
    {
        var reg = Build();
        reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Project One", DefaultWorkspace.Id);
        var second = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "Project Two", DefaultWorkspace.Id);
        reg.SetShortCode(second.Id, "ZZZ");
        var third = reg.EnsureProjectForStorage(Path.Combine(_root, "p3"), "Project Three", DefaultWorkspace.Id);

        Assert.Throws<InvalidOperationException>(() => reg.SetShortCode(third.Id, "ZZZ"));
    }

    [Fact]
    public void SetColor_SetsAndClears()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        Assert.Null(p.Color);
        Assert.Equal("#abcdef", reg.SetColor(p.Id, "#abcdef").Color);
        Assert.Null(reg.SetColor(p.Id, null).Color);
    }

    [Fact]
    public void SetWikiSourceBranch_PersistsAndCheckoutIsNull()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        Assert.Null(p.WikiSourceBranch);
        Assert.Equal("origin/develop", reg.SetWikiSourceBranch(p.Id, " origin/develop ").WikiSourceBranch);
        Assert.Equal("origin/develop", Build().FindById(p.Id)!.WikiSourceBranch);
        Assert.Null(reg.SetWikiSourceBranch(p.Id, null).WikiSourceBranch);
        Assert.Throws<ArgumentException>(() => reg.SetWikiSourceBranch(p.Id, "develop^{tree}"));
    }

    [Fact]
    public void SetWorkspace_ReassignsAndRefusesUnknownWorkspace()
    {
        var workspaces = new WorkspaceRegistry(_config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var frontendWs = workspaces.Create("Frontend");

        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var moved = reg.SetWorkspace(p.Id, frontendWs.Id, workspaces);
        Assert.Equal(frontendWs.Id, moved.WorkspaceId);

        Assert.Throws<KeyNotFoundException>(() =>
            reg.SetWorkspace(p.Id, "ws-does-not-exist", workspaces));
    }

    [Fact]
    public void SetArchived_TogglesFlag()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        Assert.False(p.Archived);
        Assert.True(reg.SetArchived(p.Id, true).Archived);
        Assert.False(reg.SetArchived(p.Id, false).Archived);
    }

    [Fact]
    public void Update_ValidatesWholePatchBeforePersistingAnyField()
    {
        var workspaces = new WorkspaceRegistry(_config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var reg = Build();
        var first = reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "first"), "First Project", DefaultWorkspace.Id);
        var second = reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "second"), "Second Project", DefaultWorkspace.Id);

        Assert.Throws<InvalidOperationException>(() => reg.Update(first.Id, new UpdateProjectRequest
        {
            DisplayName = "Must Not Persist",
            ShortCode = second.ShortCode,
            Color = "#123456",
        }, workspaces));

        var reloaded = Build().FindById(first.Id)!;
        Assert.Equal("First Project", reloaded.DisplayName);
        Assert.Equal(first.ShortCode, reloaded.ShortCode);
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void Update_PersistFailure_RestoresInMemoryAndDurableRecord()
    {
        var workspaces = new WorkspaceRegistry(_config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var writer = new ControllableAtomicJsonFileWriter();
        var reg = Build(writer);
        var project = reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "demo"), "Before Failure", DefaultWorkspace.Id);
        var projectsFile = RegistryPaths.ProjectsFilePath(_root);
        var durableBefore = File.ReadAllText(projectsFile);
        writer.ShouldFail = (path, _) => string.Equals(path, projectsFile, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ProjectPersistenceException>(() => reg.Update(project.Id, new UpdateProjectRequest
        {
            DisplayName = "Must Roll Back",
            Color = "#123456",
        }, workspaces));

        var inMemory = reg.FindById(project.Id)!;
        Assert.Equal("Before Failure", inMemory.DisplayName);
        Assert.Null(inMemory.Color);
        Assert.Equal(durableBefore, File.ReadAllText(projectsFile));

        var reloaded = Build().FindById(project.Id)!;
        Assert.Equal("Before Failure", reloaded.DisplayName);
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void Networked_repository_audit_is_atomic_and_uses_each_committed_prior_url()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = SecurityProfiles.Networked,
        }).Build();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var writer = new ControllableAtomicJsonFileWriter();
        var workspaces = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var reg = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance, writer, clock);
        var project = reg.EnsureProjectForStorage(Path.Combine(_root, "projects", "audit"),
            "Audit Project", DefaultWorkspace.Id);
        var owner = new StudioUser { Id = "usr_owner", Username = "owner", DisplayName = "Owner",
            Role = StudioRoles.Owner, PasswordHash = "unused" };
        var first = "https://example.test/first.git";
        var second = "https://example.test/second.git";
        var firstRecord = reg.Update(project.Id, new UpdateProjectRequest { RepositoryUrl = first },
            workspaces, out var original, owner);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, reg.RepositoryAudit().Single().Timestamp);

        var projectsFile = RegistryPaths.ProjectsFilePath(_root);
        writer.ShouldFail = (path, _) => path == projectsFile;
        Assert.Throws<ProjectPersistenceException>(() => reg.Update(project.Id,
            new UpdateProjectRequest { RepositoryUrl = second }, workspaces, owner));
        Assert.Equal(first, RemoteProjectRepositoryResolver.ResolveRepositoryUrl(reg.FindById(project.Id)));
        Assert.Single(reg.RepositoryAudit());
        writer.ShouldFail = null;
        var renamed = reg.Update(project.Id, new UpdateProjectRequest { DisplayName = "Temporary Name" },
            workspaces, out var beforeRename, owner);
        reg.RollbackUpdate(renamed, beforeRename);
        Assert.Single(reg.RepositoryAudit());

        var urls = Enumerable.Range(0, 12).Select(index =>
            $"https://example.test/concurrent-{index}.git").ToArray();
        Parallel.ForEach(urls, url => reg.Update(project.Id,
            new UpdateProjectRequest { RepositoryUrl = url }, workspaces, owner));
        var rows = reg.RepositoryAudit().Where(row => row.ProjectId == project.Id).ToArray();
        Assert.Equal(urls.Length + 1, rows.Length);
        for (var index = 1; index < rows.Length; index++)
            Assert.Equal(rows[index - 1].RepositoryUrl, rows[index].PreviousRepositoryUrl);
        Assert.Equal(rows[^1].RepositoryUrl,
            RemoteProjectRepositoryResolver.ResolveRepositoryUrl(reg.FindById(project.Id)));
        Assert.Throws<InvalidOperationException>(() => reg.RollbackUpdate(firstRecord, original));
        Assert.Equal(rows, reg.RepositoryAudit());
        var reloaded = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        Assert.Equal(JsonSerializer.Serialize(rows), JsonSerializer.Serialize(reloaded.RepositoryAudit()));
    }

    [Fact]
    public void Networked_start_rule_changes_require_owner_and_are_audited_atomically()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = SecurityProfiles.Networked,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(Path.Combine(_root, "projects", "rules"),
            "Rules", DefaultWorkspace.Id);
        var owner = new StudioUser { Id = "owner", Username = "owner", DisplayName = "Owner",
            Role = StudioRoles.Owner, PasswordHash = "unused" };
        var operatorUser = new StudioUser { Id = "operator", Username = "operator", DisplayName = "Operator",
            Role = StudioRoles.Operator, PasswordHash = "unused" };
        var rule = new ProjectUrlStartRule { Command = "echo ready", Port = 4200 };

        Assert.Throws<UnauthorizedAccessException>(() => registry.AddUrl(project.Id,
            "preview", "http://localhost:4200", rule, operatorUser));
        Assert.Empty(registry.FindById(project.Id)!.Urls);
        Assert.Empty(registry.StartRuleAudit());

        var created = registry.AddUrl(project.Id, "preview", "http://localhost:4200", rule, owner);
        var url = Assert.Single(created.Urls);
        Assert.Throws<UnauthorizedAccessException>(() => registry.UpdateUrl(project.Id, url.Id,
            "preview", url.Url, rule with { Command = "echo changed" }, operatorUser));
        Assert.Single(registry.StartRuleAudit());
        registry.UpdateUrl(project.Id, url.Id, "preview", url.Url,
            rule with { Command = "echo changed" }, owner);
        Assert.Equal(2, registry.StartRuleAudit().Count);
        Assert.All(registry.StartRuleAudit(), entry => Assert.Equal(owner.Id, entry.ActorId));
        Assert.Equal(JsonSerializer.Serialize(registry.StartRuleAudit()),
            JsonSerializer.Serialize(new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance).StartRuleAudit()));
    }

    [Fact]
    public void Networked_project_create_with_repository_url_requires_owner_and_audits_creation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = SecurityProfiles.Networked,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var owner = new StudioUser { Id = "owner", Username = "owner", DisplayName = "Owner",
            Role = StudioRoles.Owner, PasswordHash = "unused" };
        var operatorUser = new StudioUser { Id = "operator", Username = "operator", DisplayName = "Operator",
            Role = StudioRoles.Operator, PasswordHash = "unused" };
        var project = new ProjectRecord
        {
            Id = "PROJ-001", DisplayName = "New project", ShortCode = "NEW",
            WorkspaceId = DefaultWorkspace.Id, StorageLocation = Path.Combine(_root, "new"),
            Urls = [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = "https://example.test/repo.git" }]
        };
        Assert.Throws<UnauthorizedAccessException>(() => registry.Append(project, operatorUser));
        registry.Append(project, owner);
        var audit = Assert.Single(registry.RepositoryAudit());
        Assert.Null(audit.PreviousRepositoryUrl);
        Assert.Equal(project.Urls[0].Url, audit.RepositoryUrl);
        Assert.Equal(owner.Id, audit.ActorId);
    }

    [Fact]
    public void Update_RoundTripsAllBasics_WithoutChangingStableStorageOrIdentity()
    {
        var workspaces = new WorkspaceRegistry(_config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var targetWorkspace = workspaces.Create("Product Engineering");
        var reg = Build();
        var storage = Path.Combine(_root, "projects", "PROJ-001", "tasks");
        var project = reg.EnsureProjectForStorage(storage, "Initial Project", DefaultWorkspace.Id);
        reg.IssueNextTaskKey(project.Id);
        reg.IssueNextWorkbenchKey(project.Id);

        var updated = reg.Update(project.Id, new UpdateProjectRequest
        {
            DisplayName = "Edited Project",
            ShortCode = "edt",
            Color = " #123456 ",
            WorkspaceId = targetWorkspace.Id,
            RepositoryPath = _repository,
            RootPath = Path.Combine(_repository, "src"),
            RepositoryUrl = " https://example.test/org/repo.git ",
            CliDefault = " codex ",
            ModelDefault = " gpt-test ",
        }, workspaces);

        Assert.Equal(project.Id, updated.Id);
        Assert.Equal(project.SourceType, updated.SourceType);
        Assert.Equal(project.CreatedAt, updated.CreatedAt);
        Assert.Equal(storage, updated.StorageLocation);
        Assert.Equal(2, updated.NextTaskKeySeq);
        Assert.Equal(2, updated.NextWorkbenchKeySeq);
        Assert.Equal("Edited Project", updated.DisplayName);
        Assert.Equal("EDT", updated.ShortCode);
        Assert.Equal("#123456", updated.Color);
        Assert.Equal(targetWorkspace.Id, updated.WorkspaceId);
        Assert.Equal(_repository, updated.RepositoryPath);
        Assert.Equal(Path.Combine(_repository, "src"), updated.RootPath);
        Assert.Equal("https://example.test/org/repo.git", updated.Urls.Single(url => url.Id == "repo").Url);
        Assert.Equal("codex", updated.CliDefault);
        Assert.Equal("gpt-test", updated.ModelDefault);
        Assert.False(Directory.Exists(Path.Combine(_repository, "tasks")));
        Assert.False(Directory.Exists(Path.Combine(_repository, ".orchestrator", "jobs")));

        var reloaded = Build().FindById(project.Id)!;
        Assert.Equal(updated.Id, reloaded.Id);
        Assert.Equal(updated.DisplayName, reloaded.DisplayName);
        Assert.Equal(updated.ShortCode, reloaded.ShortCode);
        Assert.Equal(updated.StorageLocation, reloaded.StorageLocation);
        Assert.Equal(updated.RepositoryPath, reloaded.RepositoryPath);
        Assert.Equal(updated.RootPath, reloaded.RootPath);
        Assert.Equal(updated.CliDefault, reloaded.CliDefault);
        Assert.Equal(updated.ModelDefault, reloaded.ModelDefault);
        Assert.Equal(updated.Urls.Single(url => url.Id == "repo").Url,
            reloaded.Urls.Single(url => url.Id == "repo").Url);
    }

    [Fact]
    public void Update_ClearSemantics_RemoveEditableOptionalBasicsOnly()
    {
        var workspaces = new WorkspaceRegistry(_config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var reg = Build();
        var project = reg.EnsureProjectForStorage(
            Path.Combine(_root, "projects", "demo"), "Demo", DefaultWorkspace.Id);
        reg.Update(project.Id, new UpdateProjectRequest
        {
            Color = "#abcdef",
            RepositoryPath = _repository,
            RootPath = Path.Combine(_repository, "src"),
            RepositoryUrl = "https://example.test/repo",
            CliDefault = "codex",
            ModelDefault = "gpt-test",
        }, workspaces);

        var cleared = reg.Update(project.Id, new UpdateProjectRequest
        {
            ClearColor = true,
            ClearRepositoryPath = true,
            ClearRootPath = true,
            ClearRepositoryUrl = true,
            ClearCliDefault = true,
            ClearModelDefault = true,
        }, workspaces);

        Assert.Null(cleared.Color);
        Assert.Null(cleared.RepositoryPath);
        Assert.Null(cleared.RootPath);
        Assert.DoesNotContain(cleared.Urls, url => url.Id == "repo");
        Assert.Null(cleared.CliDefault);
        Assert.Null(cleared.ModelDefault);
        Assert.Equal(project.StorageLocation, cleared.StorageLocation);
    }

    [Fact]
    public void Mutations_UnknownId_Throws()
    {
        var reg = Build();
        Assert.Throws<KeyNotFoundException>(() => reg.Rename("PROJ-999", "x"));
        Assert.Throws<KeyNotFoundException>(() => reg.SetColor("PROJ-999", "#fff"));
        Assert.Throws<KeyNotFoundException>(() => reg.SetArchived("PROJ-999", true));
    }

    [Fact]
    public void FindByIdOrDisplayName_AcceptsBothForms()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Runbook", DefaultWorkspace.Id);
        Assert.Equal(p.Id, reg.FindByIdOrDisplayName("PROJ-001")?.Id);
        Assert.Equal(p.Id, reg.FindByIdOrDisplayName("Runbook")?.Id);
        Assert.Equal(p.Id, reg.FindByIdOrDisplayName("runbook")?.Id);
        Assert.Null(reg.FindByIdOrDisplayName("does-not-exist"));
    }

    // ------------------------------------------------------------------
    // Project URLs mutation tests
    // ------------------------------------------------------------------

    [Fact]
    public void AddUrl_AppendsWithAllocatedIdAndSortOrder()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        Assert.Empty(p.Urls);

        var afterFirst = reg.AddUrl(p.Id, "Dev frontend", "http://localhost:4010");
        var afterSecond = reg.AddUrl(p.Id, "Stable frontend", "http://localhost:4011");

        Assert.Equal(2, afterSecond.Urls.Count);
        var first = afterSecond.Urls[0];
        var second = afterSecond.Urls[1];
        Assert.Equal("url-1", first.Id);
        Assert.Equal("url-2", second.Id);
        Assert.Equal("Dev frontend", first.Label);
        Assert.Equal("http://localhost:4010", first.Url);
        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);
        Assert.Null(first.StartRule);
    }

    [Fact]
    public void AddUrl_WithStartRule_NormalisesAndKeepsIt()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var updated = reg.AddUrl(p.Id, "Website", "http://localhost:4202",
            new ProjectUrlStartRule { Command = "  npm run website  ", Port = 4202, Source = "package-json" });

        var rule = updated.Urls.Single().StartRule;
        Assert.NotNull(rule);
        Assert.Equal("npm run website", rule!.Command); // trimmed
        Assert.Equal(4202, rule.Port);
        Assert.Equal(30, rule.ReadinessTimeoutSeconds);
        Assert.Equal(600, rule.StartupTimeoutSeconds);
        Assert.Equal("package-json", rule.Source);
    }

    [Fact]
    public void AddUrl_ExistingReadinessTimeoutBecomesSilenceWindowAndKeepsConfiguredStartupLimit()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var updated = reg.AddUrl(p.Id, "Website", "http://localhost:4202",
            new ProjectUrlStartRule
            {
                Command = "npm run website",
                Port = 4202,
                ReadinessTimeoutSeconds = 20,
                StartupTimeoutSeconds = 900,
            });

        var rule = Assert.IsType<ProjectUrlStartRule>(updated.Urls.Single().StartRule);
        Assert.Equal(20, rule.ReadinessTimeoutSeconds);
        Assert.Equal(900, rule.StartupTimeoutSeconds);
    }

    [Fact]
    public void AddUrl_EmptyCommandStartRule_BecomesNull()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var updated = reg.AddUrl(p.Id, "Static", "http://localhost:5000",
            new ProjectUrlStartRule { Command = "   ", Source = "manual" });

        Assert.Null(updated.Urls.Single().StartRule); // a rule with no command is no rule
    }

    [Fact]
    public void AddUrl_ValidatesLabelAndUrl()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        Assert.Throws<ArgumentException>(() => reg.AddUrl(p.Id, "  ", "http://localhost:4010")); // blank label
        Assert.Throws<ArgumentException>(() => reg.AddUrl(p.Id, "X", ""));                        // blank url
        Assert.Throws<ArgumentException>(() => reg.AddUrl(p.Id, "X", "not-a-url"));               // not absolute
        Assert.Throws<ArgumentException>(() => reg.AddUrl(p.Id, "X", "ftp://host/x"));            // wrong scheme
    }

    [Fact]
    public void AddUrl_UnknownProject_Throws()
    {
        var reg = Build();
        Assert.Throws<KeyNotFoundException>(() => reg.AddUrl("PROJ-999", "X", "http://localhost:1"));
    }

    [Fact]
    public void UpdateUrl_ChangesFieldsKeepsIdAndSortOrder()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        reg.AddUrl(p.Id, "First", "http://localhost:4010");
        var withSecond = reg.AddUrl(p.Id, "Second", "http://localhost:4011");
        var secondId = withSecond.Urls[1].Id;

        var updated = reg.UpdateUrl(p.Id, secondId, "Renamed", "http://localhost:4999",
            new ProjectUrlStartRule { Command = "npm start" });

        var row = updated.Urls.Single(u => u.Id == secondId);
        Assert.Equal("Renamed", row.Label);
        Assert.Equal("http://localhost:4999", row.Url);
        Assert.Equal(1, row.SortOrder); // preserved
        Assert.Equal("npm start", row.StartRule!.Command);
    }

    [Fact]
    public void UpdateUrl_UnknownUrlId_Throws()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        reg.AddUrl(p.Id, "First", "http://localhost:4010");
        Assert.Throws<KeyNotFoundException>(() =>
            reg.UpdateUrl(p.Id, "url-999", "X", "http://localhost:1", null));
    }

    [Fact]
    public void RemoveUrl_DropsRowAndLeavesOthers()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        reg.AddUrl(p.Id, "First", "http://localhost:4010");
        var withSecond = reg.AddUrl(p.Id, "Second", "http://localhost:4011");
        var firstId = withSecond.Urls[0].Id;

        var updated = reg.RemoveUrl(p.Id, firstId);

        Assert.Single(updated.Urls);
        Assert.Equal("Second", updated.Urls[0].Label);
        Assert.Throws<KeyNotFoundException>(() => reg.RemoveUrl(p.Id, "url-999"));
    }

    [Fact]
    public void ReorderUrls_ReassignsSortOrderFromSequence()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        reg.AddUrl(p.Id, "A", "http://localhost:4010");
        reg.AddUrl(p.Id, "B", "http://localhost:4011");
        var withThird = reg.AddUrl(p.Id, "C", "http://localhost:4012");
        var ids = withThird.Urls.Select(u => u.Id).ToList(); // [url-1, url-2, url-3]

        var reordered = reg.ReorderUrls(p.Id, [ids[2], ids[0], ids[1]]);

        // New order by SortOrder: C, A, B.
        var ordered = reordered.Urls.OrderBy(u => u.SortOrder).Select(u => u.Label).ToList();
        Assert.Equal(["C", "A", "B"], ordered);
    }

    [Fact]
    public void ReorderUrls_RejectsNonPermutation()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        reg.AddUrl(p.Id, "A", "http://localhost:4010");
        var withB = reg.AddUrl(p.Id, "B", "http://localhost:4011");
        var ids = withB.Urls.Select(u => u.Id).ToList();

        Assert.Throws<ArgumentException>(() => reg.ReorderUrls(p.Id, [ids[0]]));            // missing one
        Assert.Throws<ArgumentException>(() => reg.ReorderUrls(p.Id, [ids[0], ids[0]]));    // duplicate
        Assert.Throws<ArgumentException>(() => reg.ReorderUrls(p.Id, [ids[0], ids[1], "url-9"])); // unknown
    }

    [Fact]
    public void Urls_RoundTrip_ThroughFreshInstance()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        reg.AddUrl(p.Id, "Dev", "http://localhost:4010",
            new ProjectUrlStartRule { Command = "npm run dev", Port = 4010, Source = "package-json" });

        var reloaded = Build();
        var reloadedProject = reloaded.FindById(p.Id)!;
        Assert.Single(reloadedProject.Urls);
        var url = reloadedProject.Urls[0];
        Assert.Equal("Dev", url.Label);
        Assert.Equal("npm run dev", url.StartRule!.Command);
        Assert.Equal(4010, url.StartRule.Port);
    }
}
