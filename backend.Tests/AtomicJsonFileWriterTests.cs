using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The swap contract of <see cref="AtomicJsonFileWriter"/>: a concurrent plain
/// reader (<c>File.ReadAllText</c>, the way every task.json consumer opens the
/// file) may collide with the swap and see a transient sharing violation, but
/// it must never see the destination name missing, a truncated document, or a
/// stale temp file left behind. On Windows the missing-name case is exactly
/// what <c>File.Replace</c> (ReplaceFile's two renames) produced; the single
/// rename-with-replace keeps the name continuously valid.
/// </summary>
public sealed class AtomicJsonFileWriterTests : IDisposable
{
    // Generous on purpose: the loop below is bounded by an observable
    // condition (100 swaps and 50 reader samples), not by this deadline. The
    // deadline only guards against a genuine hang; under a loaded host the
    // writer and the 1ms-cadence reader both get less CPU, so a short budget
    // here would fail on scheduling pressure alone, not on a real regression.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "atomic-json-writer-" + Guid.NewGuid().ToString("N"));

    public AtomicJsonFileWriterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Write_UnderConcurrentPlainReader_KeepsTheDestinationNameValidAndWhole()
    {
        var path = Path.Combine(_dir, "task.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, """{"id":"fixture","tags":[]}""");

        var failures = new ConcurrentQueue<Exception>();
        var reading = true;
        long successfulReads = 0;
        using var started = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            started.Set();
            while (Volatile.Read(ref reading))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.GetProperty("id").GetString() != "fixture")
                        throw new InvalidDataException("reader saw a foreign document");
                    Interlocked.Increment(ref successfulReads);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    // A reader that opens while the rename is in flight can be
                    // refused for that instant; that is the one tolerated
                    // collision. FileNotFound, partial JSON and foreign ids
                    // are failures.
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
                // The cadence of a real consumer (scanner, watcher, API read):
                // open, read, close, come back a moment later. A reader that
                // re-opens the file back-to-back is not a consumer pattern this
                // store has; on a saturated CPU it merely starves the writer
                // and makes the run a scheduling lottery.
                Thread.Sleep(1);
            }
        });
        started.Wait();

        var large = Enumerable.Range(0, 2_000).Select(i => $"\"integration:test-{i:D4}\"");
        var bigDocument = $$"""{"id":"fixture","tags":[{{string.Join(",", large)}}]}""";
        const string smallDocument = """{"id":"fixture","tags":["integration:pending"]}""";
        var writes = 0;
        try
        {
            // At least 100 swaps, and keep swapping until the reader has
            // sampled the file often enough for the interleaving to be real.
            var deadline = DateTime.UtcNow + Deadline;
            while ((writes < 100 || Interlocked.Read(ref successfulReads) < 50)
                   && DateTime.UtcNow < deadline)
            {
                writer.Write(path, writes % 2 == 0 ? bigDocument : smallDocument);
                writes++;
            }
            if (writes % 2 == 1) { writer.Write(path, smallDocument); writes++; }
        }
        finally
        {
            Volatile.Write(ref reading, false);
            await reader.WaitAsync(Deadline);
        }

        Assert.Empty(failures);
        Assert.True(writes >= 100, $"expected at least 100 swaps, made {writes}");
        Assert.True(
            Interlocked.Read(ref successfulReads) >= 50,
            $"the reader must have sampled the file during the swaps (reads: {successfulReads})");
        Assert.Equal(smallDocument, File.ReadAllText(path));
        Assert.Equal(["task.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_FirstWrite_CreatesDirectoryAndFile()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "state.json");
        new AtomicJsonFileWriter().Write(path, """{"ok":true}""");

        Assert.Equal("""{"ok":true}""", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_Overwrite_ReplacesContentWithoutLeavingTempFiles()
    {
        var path = Path.Combine(_dir, "state.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, """{"version":1}""");
        writer.Write(path, """{"version":2}""");

        Assert.Equal("""{"version":2}""", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_WithLastGoodBackup_KeepsThePreviousVersionBesideTheStore()
    {
        var path = Path.Combine(_dir, "state.json");
        var writer = new AtomicJsonFileWriter().WithLastGoodBackup();
        writer.Write(path, """{"version":1}""");
        writer.Write(path, """{"version":2}""");

        Assert.Equal("""{"version":2}""", File.ReadAllText(path));
        Assert.Equal(["state.json", "state.json.bak"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
        Assert.Equal("""{"version":1}""", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void WithLastGoodBackup_LeavesInjectedTestDoublesUnchanged()
    {
        var injected = new RecordingWriter();

        Assert.Same(injected, injected.WithLastGoodBackup());
    }

    [Fact]
    public void ReadAllTextWithBackup_Uses_valid_backup_after_zero_tailed_primary()
    {
        var path = Path.Combine(_dir, "state.json");
        var writer = AtomicJsonFileWriter.KeepingLastGoodBackup();
        writer.Write(path, """{"version":1}""");
        writer.Write(path, """{"version":2}""");
        File.WriteAllText(path, """{"version":2}""" + new string('\0', 1024));
        string? recoveredFrom = null;

        Assert.Equal("""{"version":1}""",
            AtomicJsonFileWriter.ReadAllTextWithBackup(path, source => recoveredFrom = source));
        Assert.Equal(path + ".bak", recoveredFrom);
        writer.Write(path, """{"version":3}""");
        Assert.Equal("""{"version":1}""", File.ReadAllText(path + ".bak"));
        Assert.Equal("""{"version":3}""", File.ReadAllText(path));
    }

    [Fact]
    public void ReadAllTextWithBackup_WithoutABackup_RethrowsThePrimaryFailure()
    {
        // Stores that never opt into backups keep their own handling of a
        // damaged or missing file (a JsonException, a FileNotFoundException).
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, """{"version":2""" + new string('\0', 16));

        Assert.ThrowsAny<JsonException>(() => AtomicJsonFileWriter.ReadAllTextWithBackup(path));
        Assert.Throws<FileNotFoundException>(
            () => AtomicJsonFileWriter.ReadAllTextWithBackup(Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void ReplaceExisting_ByDefault_LeavesNoBackupBesideARepositoryDocument()
    {
        // Wiki pages and Dossier descriptors live in project repositories. A
        // sibling .bak there is an untracked file that dirties the worktree.
        var path = Path.Combine(_dir, "notes.md");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, "first note\n");
        writer.ReplaceExisting(path, "second note\n");

        Assert.Equal("second note\n", File.ReadAllText(path));
        Assert.Equal(["notes.md"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void ReplaceExisting_WhenTheFolderIsGone_FailsInsteadOfRecreatingIt()
    {
        // A task.json rewrite belongs to a folder another writer owns and may
        // move or delete. Re-creating that folder around the swap is what put the
        // same task in two lanes at once (AGT-2867), so the write has to fail and
        // leave the tree exactly as the other writer left it.
        var folder = Path.Combine(_dir, "3-progress", "moved-away");
        var path = Path.Combine(folder, "task.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, """{"id":"moved-away"}""");
        Directory.Delete(folder, recursive: true);

        Assert.Throws<DirectoryNotFoundException>(
            () => writer.ReplaceExisting(path, """{"id":"moved-away","ownerClientId":"local-default"}"""));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void ReplaceExisting_WhenTheFolderIsThere_SwapsTheContentLikeWrite()
    {
        var path = Path.Combine(_dir, "state.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, """{"version":1}""");

        writer.ReplaceExisting(path, """{"version":2}""");

        Assert.Equal("""{"version":2}""", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_KeepsCreatingTheFolder_ForStoresThatOwnTheirOwnDirectory()
    {
        // The contrast that keeps the two entry points apart: a state store such
        // as the project registry legitimately materialises its own folder.
        var folder = Path.Combine(_dir, "metadata");
        var path = Path.Combine(folder, "projects.json");

        new AtomicJsonFileWriter().Write(path, """{"projects":[]}""");

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void WriteJson_StreamsBeforeTheDocumentHasBeenFullyEnumerated()
    {
        var path = Path.Combine(_dir, "state.json");
        IAtomicJsonFileWriter writer = new AtomicJsonFileWriter();
        var observedStreaming = false;
        IEnumerable<int> Values()
        {
            for (var value = 0; value < 100_000; value++)
            {
                if (value == 90_000)
                {
                    var temporary = Assert.Single(Directory.GetFiles(_dir, "state.json.*.tmp"));
                    Assert.True(new FileInfo(temporary).Length > 0);
                    observedStreaming = true;
                }
                yield return value;
            }
        }

        writer.WriteJson(path, new { values = Values() });

        Assert.True(observedStreaming);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(100_000, document.RootElement.GetProperty("values").GetArrayLength());
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void WriteJson_SerializationFailure_PreservesDurableStateAndRemovesTempFile()
    {
        var path = Path.Combine(_dir, "state.json");
        IAtomicJsonFileWriter writer = new AtomicJsonFileWriter();
        const string durable = "{\"version\":1}";
        writer.Write(path, durable);
        IEnumerable<int> Values()
        {
            for (var value = 0; value < 10_000; value++) yield return value;
            throw new InvalidOperationException("serialization failed");
        }

        Assert.Throws<InvalidOperationException>(() => writer.WriteJson(path, new { values = Values() }));

        Assert.Equal(durable, File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void WriteJson_CreatesParentDirectoriesAndPreservesSerializerOptions()
    {
        var path = Path.Combine(_dir, "nested", "state.json");
        IAtomicJsonFileWriter writer = new AtomicJsonFileWriter();
        writer.WriteJson(path, new { Version = 2 }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void FlushDirectory_MissingDirectory_ReportsNativeOpenFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "missing", "state.json");

        var failure = Assert.Throws<DirectoryDurabilityException>(() => AtomicJsonFileWriter.FlushDirectory(path));

        Assert.Equal("open", failure.Operation);
        Assert.Equal(2, failure.Errno); // ENOENT
        Assert.Equal(path, failure.FilePath);
        Assert.Equal(Path.GetDirectoryName(path), failure.DirectoryPath);
        Assert.True(failure.CommitUncertain);
    }

    [Theory]
    [InlineData("open", "text")]
    [InlineData("fsync", "text")]
    [InlineData("open", "json")]
    [InlineData("fsync", "json")]
    [InlineData("open", "replace")]
    [InlineData("fsync", "replace")]
    public void Write_DirectoryFailure_IsAnExplicitUncertainCommit(string operation, string mode)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "state.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, "{\"version\":1}");
        var closed = 0;
        using var fault = AtomicJsonFileWriter.OverrideDirectoryOperationsForTests(
            _ => operation == "open" ? NativeFailure(13) : 42,
            _ => NativeFailure(5),
            _ => closed++);

        var failure = Assert.Throws<DirectoryDurabilityException>(() =>
        {
            if (mode == "json") writer.WriteJson(path, new { version = 2 });
            else if (mode == "replace") writer.ReplaceExisting(path, "{\"version\":2}");
            else writer.Write(path, "{\"version\":2}");
        });

        Assert.Equal(operation, failure.Operation);
        Assert.Equal(operation == "open" ? 13 : 5, failure.Errno);
        Assert.Contains($"errno={failure.Errno}", failure.Message);
        Assert.Contains(path, failure.Message);
        Assert.Contains(_dir, failure.Message);
        Assert.True(failure.CommitUncertain);
        Assert.Equal(operation == "open" ? 0 : 1, closed);
        // Rename already happened. A failed write must not be assumed rolled back.
        Assert.Equal("{\"version\":2}", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("fsync")]
    public void Backup_DirectoryFailure_DoesNotReplaceThePrimary(string operation)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "state.json");
        var writer = AtomicJsonFileWriter.KeepingLastGoodBackup();
        writer.Write(path, "{\"version\":1}");
        using var fault = AtomicJsonFileWriter.OverrideDirectoryOperationsForTests(
            _ => operation == "open" ? NativeFailure(13) : 42, _ => NativeFailure(5), _ => { });

        Assert.Throws<DirectoryDurabilityException>(() => writer.Write(path, "{\"version\":2}"));

        Assert.Equal("{\"version\":1}", File.ReadAllText(path));
        Assert.Equal("{\"version\":1}", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("fsync")]
    public void Authority_RecoversDiskStateAfterPostRenameFailureBeforeRetry(string operation)
    {
        if (OperatingSystem.IsWindows()) return;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _dir }).Build();
        // An adapter selects the existing snapshot persistence path. Its inner
        // writer still executes the real file flush and rename before the fault.
        var writer = new SnapshotWriter();
        var service = new AttemptAuthorityService(configuration,
            NullLogger<AttemptAuthorityService>.Instance, writer: writer);
        var first = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "first").RunAttempt!;
        using (AtomicJsonFileWriter.OverrideDirectoryOperationsForTests(
            _ => operation == "open" ? NativeFailure(13) : 42, _ => NativeFailure(5), _ => { }))
        {
            Assert.Throws<DirectoryDurabilityException>(() =>
                service.AcquireRun("AGT-2", "PROJ-1", null, "runner", "host", 60, "second"));
        }

        var recovered = service.GetTaskProjection("AGT-2").CurrentRunAttempt!;
        Assert.NotNull(recovered);
        var restarted = new AttemptAuthorityService(configuration,
            NullLogger<AttemptAuthorityService>.Instance, writer: writer);
        Assert.Equal(recovered.AttemptId, restarted.GetTaskProjection("AGT-2").CurrentRunAttempt!.AttemptId);
        Assert.Equal(first.AttemptId, service.GetTaskProjection("AGT-1").CurrentRunAttempt!.AttemptId);
        var retry = service.AcquireRun("AGT-2", "PROJ-1", null, "runner", "host", 60, "second");
        Assert.Equal(AttemptWriteStatus.Duplicate, retry.Status);
        Assert.Equal(recovered.AttemptId, retry.AttemptId);
    }

    private static int NativeFailure(int errno)
    {
        Marshal.SetLastPInvokeError(errno);
        return -1;
    }

    private sealed class SnapshotWriter : IAtomicJsonFileWriter
    {
        public void Write(string path, string content) => new AtomicJsonFileWriter().Write(path, content);
    }

    private static bool IsSharingViolation(IOException exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException) return false;
        if (!OperatingSystem.IsWindows()) return false;
        return (exception.HResult & 0xffff) is 32 or 33;
    }

    private sealed class RecordingWriter : IAtomicJsonFileWriter
    {
        public void Write(string path, string content)
        {
        }
    }
}
