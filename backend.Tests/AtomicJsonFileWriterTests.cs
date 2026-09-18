using System.Collections.Concurrent;
using System.Text.Json;

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

    private static bool IsSharingViolation(IOException exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException) return false;
        if (!OperatingSystem.IsWindows()) return false;
        return (exception.HResult & 0xffff) is 32 or 33;
    }
}
