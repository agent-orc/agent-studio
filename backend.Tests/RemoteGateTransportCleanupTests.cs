using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3078: the local transport folder of every remote gate run must be
/// removed, including git's read-only pack files, and abandoned run folders
/// under the shared transport root are swept by name shape and age.
/// </summary>
public sealed class RemoteGateTransportCleanupTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("gate-transport");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-12T18:36:00Z"));
    private readonly GitStaleLockGuardTests.CapturingLogger<RemoteGateTransport> _logger = new();

    public void Dispose() => _workspace.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private RemoteGateTransport Transport(string root, Action<string>? delete = null)
        => new(new ConfigurationBuilder().Build(), _logger, _clock, root,
            delete ?? WorktreeDirectoryCleanup.DeleteDirectoryWithoutFollowingReparsePoints);

    [Fact]
    public void Local_cleanup_removes_a_run_folder_with_read_only_git_pack_files()
    {
        var runId = Guid.NewGuid().ToString("N");
        var local = _workspace.Directory("agentstudio-gate-transport", runId);
        var pack = Directory.CreateDirectory(Path.Combine(local, "source.git", "objects", "pack")).FullName;
        foreach (var name in new[] { "pack-x.idx", "pack-x.pack", "pack-x.rev" })
        {
            var file = Path.Combine(pack, name);
            File.WriteAllText(file, name);
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }
        File.WriteAllText(Path.Combine(local, "source.bundle"), "bundle");
        File.WriteAllText(Path.Combine(local, "request.json"), "{}");
        // Only an empty leaf directory is marked read-only: on Linux that
        // removes the write bit, which would block deleting its children.
        var emptyLeaf = Directory.CreateDirectory(Path.Combine(local, "source.git", "refs", "tags")).FullName;
        File.SetAttributes(emptyLeaf, File.GetAttributes(emptyLeaf) | FileAttributes.ReadOnly);

        Transport(Path.GetDirectoryName(local)!).DeleteLocalRunFolder(local, runId);

        Assert.False(Directory.Exists(local));
        Assert.DoesNotContain(_logger.Messages, message => message.Contains("cleanup_failed"));
    }

    [Fact]
    public void Local_cleanup_failure_is_logged_with_the_run_id_and_never_escapes()
    {
        var runId = Guid.NewGuid().ToString("N");
        var local = _workspace.Directory("agentstudio-gate-transport", runId);
        var transport = Transport(Path.GetDirectoryName(local)!,
            _ => throw new UnauthorizedAccessException("Access to the path 'pack-x.idx' is denied."));

        transport.DeleteLocalRunFolder(local, runId);

        var warning = Assert.Single(_logger.Messages);
        Assert.Contains("remote_gate_local_transport_cleanup_failed", warning);
        Assert.Contains("run_id=" + runId, warning);
    }

    [Fact]
    public void Sweep_removes_only_guid_named_run_folders_older_than_the_retention()
    {
        var root = _workspace.Directory("agentstudio-gate-transport");
        var old = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(27));
        var young = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(25));
        var foreign = RunFolder(root, "not-a-gate-run", TimeSpan.FromHours(30));
        var upperCase = RunFolder(root, Guid.NewGuid().ToString("N").ToUpperInvariant(), TimeSpan.FromHours(30));
        var stale = RunFolder(root, Guid.NewGuid().ToString("N") + ".stale-20261010000000000", TimeSpan.FromHours(30));
        var looseFile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(looseFile, "not a folder");
        File.SetLastWriteTimeUtc(looseFile, Now - TimeSpan.FromHours(30));

        Assert.True(Transport(root).TrySweepStaleRunFolders());

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(upperCase));
        Assert.True(Directory.Exists(stale));
        Assert.True(File.Exists(looseFile));
        Assert.Contains("remote_gate_local_transport_sweep removed=1 failed=0", Assert.Single(_logger.Messages));
    }

    [Fact]
    public void Sweep_counts_a_folder_that_vanished_before_its_delete_as_removed()
    {
        var root = _workspace.Directory("agentstudio-gate-transport");
        var vanished = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(27));
        var transport = Transport(root, path =>
        {
            // The other instance sharing the root swept it first.
            Directory.Delete(path, recursive: true);
            throw new DirectoryNotFoundException("Could not find a part of the path '" + path + "'.");
        });

        Assert.True(transport.TrySweepStaleRunFolders());

        Assert.False(Directory.Exists(vanished));
        Assert.Contains("removed=1 failed=0", Assert.Single(_logger.Messages));
    }

    [Fact]
    public void Sweep_of_a_missing_root_does_nothing()
    {
        var root = _workspace.Combine("agentstudio-gate-transport");

        Assert.True(Transport(root).TrySweepStaleRunFolders());

        Assert.False(Directory.Exists(root));
        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public void Sweep_continues_after_one_folder_fails_and_reports_it()
    {
        var root = _workspace.Directory("agentstudio-gate-transport");
        var locked = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(27));
        var first = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(28));
        var second = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(29));
        var transport = Transport(root, path =>
        {
            if (path == locked) throw new IOException("The process cannot access the file because it is being used by another process.");
            WorktreeDirectoryCleanup.DeleteDirectoryWithoutFollowingReparsePoints(path);
        });

        Assert.True(transport.TrySweepStaleRunFolders());

        Assert.True(Directory.Exists(locked));
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
        Assert.Contains("remote_gate_local_transport_sweep removed=2 failed=1", Assert.Single(_logger.Messages));
    }

    [Fact]
    public void Sweep_runs_at_most_once_per_hour()
    {
        var root = _workspace.Directory("agentstudio-gate-transport");
        var transport = Transport(root);
        Assert.True(transport.TrySweepStaleRunFolders());
        var old = RunFolder(root, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(27));

        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(transport.TrySweepStaleRunFolders());
        Assert.True(Directory.Exists(old));

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(transport.TrySweepStaleRunFolders());
        Assert.False(Directory.Exists(old));
        Assert.Equal(2, _logger.Messages.Count(message => message.StartsWith("remote_gate_local_transport_sweep ")));
    }

    [Fact]
    public async Task Parallel_callers_sweep_exactly_once()
    {
        var root = _workspace.Directory("agentstudio-gate-transport");
        var transport = Transport(root);

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(transport.TrySweepStaleRunFolders)));

        Assert.Equal(1, results.Count(swept => swept));
    }

    /// <summary>
    /// Creates the folder contents first and sets the folder's write time last:
    /// creating a child would otherwise refresh it.
    /// </summary>
    private string RunFolder(string root, string name, TimeSpan age)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "request.json"), "{}");
        Directory.SetLastWriteTimeUtc(folder, Now - age);
        return folder;
    }
}
