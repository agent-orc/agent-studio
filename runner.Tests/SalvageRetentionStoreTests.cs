using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>Tarball naming and inventory, card-state lookup, and ref helpers (AGT-2999).</summary>
public sealed class SalvageRetentionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "salvage-store-" + Guid.NewGuid().ToString("N"));

    public SalvageRetentionStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("AGT-2139-2009.tgz", null, "AGT-2139")]
    [InlineData("PROJ-002-AGT-2177-1430.tgz", "PROJ-002", "AGT-2177")]
    [InlineData("PROJ-002-AGT-2309-integration-0201.tgz", "PROJ-002", "AGT-2309")]
    [InlineData("PROJ-002-QS-12-0930.tgz", "PROJ-002", "QS-12")]
    [InlineData("PROJ-002-MKT-9-2359.tgz", "PROJ-002", "MKT-9")]
    [InlineData("agt-2155-20260720-140052.tgz", null, "AGT-2155")]
    public void Snapshot_names_resolve_to_project_and_card(string name, string? project, string card)
    {
        Assert.True(SalvageTarballStore.TryParseName(name, out var parsedProject, out var parsedCard));
        Assert.Equal(project, parsedProject);
        Assert.Equal(card, parsedCard);
    }

    [Theory]
    [InlineData("snap.sh")]
    [InlineData("snap.log")]
    [InlineData("AIP-integration-26jul.bundle")]
    [InlineData("AGT-2139.tgz")]
    [InlineData("AGT-2139-2009.tar")]
    [InlineData("backup-2009.tgz")]
    public void Other_names_are_not_retention_candidates(string name)
        => Assert.False(SalvageTarballStore.TryParseName(name, out _, out _));

    [Fact]
    public void Inventory_measures_every_entry_but_lists_only_tarballs()
    {
        var oldest = new DateTime(2026, 7, 11, 8, 0, 0, DateTimeKind.Utc);
        Write("AGT-1-0100.tgz", 100, oldest.AddDays(3));
        Write("PROJ-002-AGT-2-0200.tgz", 50, oldest.AddDays(4));
        Write("snap.log", 7, oldest);
        var quarantine = Directory.CreateDirectory(Path.Combine(_root, "runner-state-quarantine"));
        File.WriteAllBytes(Path.Combine(quarantine.FullName, "slot.json"), new byte[20]);
        Directory.SetLastWriteTimeUtc(quarantine.FullName, oldest.AddDays(10));

        var inventory = SalvageTarballStore.Inventory(_root);

        Assert.True(inventory.Exists);
        Assert.Equal(177, inventory.SizeBytes);
        Assert.Equal(4, inventory.EntryCount);
        Assert.Equal(2, inventory.UnrecognizedCount);
        Assert.Equal("snap.log", inventory.OldestEntry);
        Assert.Equal(oldest, inventory.OldestEntryAt);
        Assert.Equal(
            ["AGT-1", "AGT-2"],
            inventory.Tarballs.Select(entry => entry.CardKey).Order(StringComparer.Ordinal));
        Assert.Equal(100, inventory.Tarballs.Single(entry => entry.CardKey == "AGT-1").SizeBytes);
    }

    [Fact]
    public void Missing_directory_is_reported_as_absent()
    {
        var inventory = SalvageTarballStore.Inventory(Path.Combine(_root, "missing"));

        Assert.False(inventory.Exists);
        Assert.Equal(0, inventory.EntryCount);
        Assert.Empty(inventory.Tarballs);
    }

    [Fact]
    public void Delete_refuses_anything_outside_the_salvage_root()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Guid.NewGuid().ToString("N") + "-AGT-1-0100.tgz");
        File.WriteAllText(outside, "keep");
        try
        {
            var escape = new SalvageEntry(
                SalvageEntryKind.Tarball, "../" + Path.GetFileName(outside), null, "AGT-1", DateTime.UtcNow, 4);

            Assert.False(SalvageTarballStore.TryDelete(_root, escape, out var error));
            Assert.NotNull(error);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Delete_refuses_a_link_and_removes_a_regular_tarball()
    {
        var target = Path.Combine(_root, "target.bin");
        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(Path.Combine(_root, "AGT-1-0200.tgz"), target);
        Write("AGT-1-0100.tgz", 10, DateTime.UtcNow);

        Assert.False(SalvageTarballStore.TryDelete(_root, Entry("AGT-1-0200.tgz"), out _));
        Assert.True(File.Exists(target));
        Assert.True(SalvageTarballStore.TryDelete(_root, Entry("AGT-1-0100.tgz"), out var error));
        Assert.Null(error);
        Assert.False(File.Exists(Path.Combine(_root, "AGT-1-0100.tgz")));
    }

    [Theory]
    [InlineData("6-completed", null, true)]
    [InlineData("7-archive", null, true)]
    [InlineData("3-in-progress", "archived", true)]
    [InlineData("2-ready", null, false)]
    [InlineData("5-human-review", null, false)]
    public void Completed_and_archived_cards_are_terminal(string state, string? archiveState, bool terminal)
    {
        var facts = TaskServerSalvageCardDirectory.FromTask("AGT-1", Card("AGT-1", state, archiveState: archiveState));

        Assert.Equal(terminal ? SalvageCardLifecycle.Terminal : SalvageCardLifecycle.Open, facts.Lifecycle);
    }

    [Fact]
    public void Terminal_since_is_the_later_of_update_and_archive_time()
    {
        var updated = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var archived = updated.AddDays(5);

        Assert.Equal(archived, TaskServerSalvageCardDirectory.FromTask(
            "AGT-1", Card("AGT-1", "7-archive", updated, archived)).TerminalSince);
        Assert.Equal(updated, TaskServerSalvageCardDirectory.FromTask(
            "AGT-1", Card("AGT-1", "7-archive", updated, updated.AddDays(-3))).TerminalSince);
    }

    [Fact]
    public async Task Lookup_uses_named_project_then_key_prefix_and_separates_missing_from_unknown()
    {
        var lookups = new List<string>();
        var directory = new TaskServerSalvageCardDirectory(
            _ => Task.FromResult<IReadOnlyList<ProjectDto>>(
                [Project("PROJ-002", "AGT"), Project("PROJ-005", "QS")]),
            (project, key, _) =>
            {
                lookups.Add($"{project}/{key}");
                return key switch
                {
                    "AGT-1" => Task.FromResult<TaskDto?>(Card(key, "6-completed")),
                    "QS-7" => throw new HttpRequestException("down"),
                    _ => Task.FromResult<TaskDto?>(null),
                };
            });

        var facts = await directory.ResolveAsync(
            [
                new SalvageCardReference("PROJ-002", "AGT-1"),
                new SalvageCardReference(null, "agt-1"),
                new SalvageCardReference(null, "AGT-2"),
                new SalvageCardReference(null, "QS-7"),
                new SalvageCardReference(null, "ZZ-1"),
            ],
            CancellationToken.None);

        Assert.Equal(SalvageCardLifecycle.Terminal, facts["AGT-1"].Lifecycle);
        Assert.Equal(SalvageCardLifecycle.Missing, facts["AGT-2"].Lifecycle);
        Assert.Equal(SalvageCardLifecycle.Unknown, facts["QS-7"].Lifecycle);
        Assert.Equal(SalvageCardLifecycle.Missing, facts["ZZ-1"].Lifecycle);
        Assert.Equal(["PROJ-002/AGT-1", "PROJ-002/AGT-2", "PROJ-005/QS-7"], lookups);
    }

    [Fact]
    public async Task Unavailable_project_list_still_looks_up_a_named_project_and_marks_the_rest_unknown()
    {
        var directory = new TaskServerSalvageCardDirectory(
            _ => throw new HttpRequestException("down"),
            (_, key, _) => Task.FromResult<TaskDto?>(Card(key, "2-ready")));

        var facts = await directory.ResolveAsync(
            [new SalvageCardReference("PROJ-002", "AGT-1"), new SalvageCardReference(null, "AGT-2")],
            CancellationToken.None);

        Assert.Equal(SalvageCardLifecycle.Open, facts["AGT-1"].Lifecycle);
        Assert.Equal(SalvageCardLifecycle.Unknown, facts["AGT-2"].Lifecycle);
    }

    [Theory]
    [InlineData("agent-studio/salvage/runner-01/AGT-2869/attempt-1/fence-2/8c3c943", "AGT-2869")]
    [InlineData("agent-studio/salvage/runner-01/qs-12/attempt-1/fence-2/8c3c943", "QS-12")]
    [InlineData("agent-studio/salvage/runner-01/task/attempt-1/fence-2/8c3c943", null)]
    [InlineData("agent-studio/salvage/runner-01/AGT-1", null)]
    public void Ref_card_key_comes_from_the_fourth_segment(string branch, string? expected)
        => Assert.Equal(expected, GitSalvageRefStore.CardKeyOf(branch));

    [Fact]
    public void Push_porcelain_maps_deleted_and_rejected_refs()
    {
        const string porcelain =
            "To /tmp/origin.git\n" +
            "-\t:refs/heads/agent-studio/salvage/r/AGT-1/a/fence-1/abc\t[deleted]\n" +
            "!\t:refs/heads/agent-studio/salvage/r/AGT-2/a/fence-1/def\t[rejected] (stale info)\n" +
            "Done\n";

        var statuses = GitSalvageRefStore.ParsePushStatuses(porcelain);

        Assert.Null(statuses["refs/heads/agent-studio/salvage/r/AGT-1/a/fence-1/abc"]);
        Assert.Equal("[rejected] (stale info)", statuses["refs/heads/agent-studio/salvage/r/AGT-2/a/fence-1/def"]);
        Assert.Equal(2, statuses.Count);
    }

    private void Write(string name, int bytes, DateTime modified)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, modified);
    }

    private static SalvageEntry Entry(string name)
        => new(SalvageEntryKind.Tarball, name, null, "AGT-1", DateTime.UtcNow, 10);

    private static ProjectDto Project(string id, string prefix)
        => new(id, "ws", id, prefix, 1, DateTime.UtcNow, DateTime.UtcNow);

    private static TaskDto Card(
        string key,
        string state,
        DateTime? updated = null,
        DateTime? archivedAt = null,
        string? archiveState = null)
        => new(
            "id-" + key,
            "PROJ-002",
            key,
            "title",
            state,
            1,
            DateTime.UtcNow.AddDays(-60),
            updated ?? DateTime.UtcNow.AddDays(-30),
            ArchiveState: archiveState,
            ArchivedAt: archivedAt);
}
