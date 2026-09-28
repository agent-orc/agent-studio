using System.Text;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The contract of <see cref="BoundedFileRead"/> (AGT-2991): below the cap a
/// bounded read returns exactly what the whole-file read returned; above it,
/// whole-content readers refuse the file and line readers keep the newest
/// complete lines, without ever allocating the whole file.
/// </summary>
public sealed class BoundedFileReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bounded-file-read-" + Guid.NewGuid().ToString("N"));

    public BoundedFileReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Write(string name, string content, bool bom = false)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
        return path;
    }

    [Fact]
    public void TryReadAllText_UnderCap_MatchesFileReadAllText()
    {
        var path = Write("small.json", "{\"a\":\"ü\"}\n", bom: true);

        Assert.True(BoundedFileRead.TryReadAllText(path, 1024, out var text));
        Assert.Equal(File.ReadAllText(path), text);
    }

    [Fact]
    public void TryReadAllText_ExactlyAtCap_Succeeds()
    {
        var path = Write("exact.txt", new string('x', 64));

        Assert.True(BoundedFileRead.TryReadAllText(path, 64, out var text));
        Assert.Equal(64, text.Length);
    }

    [Fact]
    public void TryReadAllText_OverCap_RefusesWithEmptyText()
    {
        var path = Write("large.txt", new string('x', 65));

        Assert.False(BoundedFileRead.TryReadAllText(path, 64, out var text));
        Assert.Equal(string.Empty, text);
        Assert.Null(BoundedFileRead.ReadAllTextOrNull(path, 64));
    }

    [Fact]
    public void TryReadAllText_EmptyFile_ReturnsEmpty()
    {
        var path = Write("empty.txt", string.Empty);

        Assert.True(BoundedFileRead.TryReadAllText(path, 16, out var text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void TryReadAllText_MissingFile_ThrowsLikeFileReadAllText()
    {
        Assert.Throws<FileNotFoundException>(() =>
            BoundedFileRead.TryReadAllText(Path.Combine(_dir, "missing.txt"), 16, out _));
    }

    [Fact]
    public void ReadTailLines_UnderCap_MatchesFileReadAllLines()
    {
        var path = Write("ledger.jsonl", "{\"n\":1}\r\n{\"n\":2}\n\n{\"n\":3}\n");

        var lines = BoundedFileRead.ReadTailLines(path, 1024, out var truncated);

        Assert.False(truncated);
        Assert.Equal(File.ReadAllLines(path), lines);
    }

    [Fact]
    public void ReadTailLines_WithoutTrailingNewline_KeepsLastLine()
    {
        var path = Write("ledger.jsonl", "one\ntwo");

        Assert.Equal(["one", "two"], BoundedFileRead.ReadTailLines(path, 1024));
    }

    [Fact]
    public void ReadTailLines_OverCap_KeepsNewestCompleteLinesOnly()
    {
        var content = new StringBuilder();
        for (var i = 0; i < 1000; i++) content.Append("{\"n\":").Append(i).Append("}\n");
        var path = Write("ledger.jsonl", content.ToString());

        var lines = BoundedFileRead.ReadTailLines(path, 100, out var truncated);

        Assert.True(truncated);
        Assert.NotEmpty(lines);
        Assert.Equal("{\"n\":999}", lines[^1]);
        // Every returned row is a complete row from the file, never a torn prefix.
        var all = File.ReadAllLines(path);
        Assert.Equal(all[^lines.Count..], lines);
        Assert.True(Encoding.UTF8.GetByteCount(string.Join('\n', lines)) <= 100);
    }

    [Fact]
    public void ReadTailText_OverCapSingleLine_ReturnsEmptyRatherThanAFragment()
    {
        var path = Write("one-line.log", new string('x', 500));

        var text = BoundedFileRead.ReadTailText(path, 100, out var truncated);

        Assert.True(truncated);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void ReadTailText_WindowStartingInsideMultiByteChar_DecodesCleanly()
    {
        // 'é' is two bytes; the window boundary lands inside one of them and
        // the partial first line is dropped with it.
        var path = Write("utf8.log", new string('é', 200) + "\nlast line ü\n");

        var text = BoundedFileRead.ReadTailText(path, 51, out var truncated);

        Assert.True(truncated);
        Assert.Equal("last line ü\n", text);
        Assert.DoesNotContain('�', text);
    }

    [Fact]
    public void ReadTailLines_FileBeingAppended_IsReadable()
    {
        var path = Write("live.log", "first\n");
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("second\n"u8);
        writer.Flush();

        Assert.Equal(["first", "second"], BoundedFileRead.ReadTailLines(path, 1024));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(16_000)]
    public void ReadTailChars_MatchesTheSuffixOfTheWholeFileRead(int maxChars)
    {
        var path = Write("status.md", "# Status\n" + string.Concat(Enumerable.Repeat("résumé ✓ ü line\n", 5_000)), bom: true);
        var whole = File.ReadAllText(path);

        var tail = BoundedFileRead.ReadTailChars(path, maxChars);

        Assert.Equal(whole.Length <= maxChars ? whole : whole[^maxChars..], tail);
    }

    [Fact]
    public void ReadTailChars_ShortFile_ReturnsWholeFileWithoutBom()
    {
        var path = Write("prompt.md", "short ü\n", bom: true);

        Assert.Equal(File.ReadAllText(path), BoundedFileRead.ReadTailChars(path, 64_000));
    }

    [Fact]
    public void TryReadAllLines_SplitsLikeFileReadAllLines_AndRefusesOversizedFiles()
    {
        var path = Write("rows.jsonl", "a\r\nb\rc\n\nd");

        Assert.True(BoundedFileRead.TryReadAllLines(path, 1024, out var lines));
        Assert.Equal(File.ReadAllLines(path), lines);
        Assert.False(BoundedFileRead.TryReadAllLines(path, 4, out var refused));
        Assert.Empty(refused);
    }

    [Fact]
    public void ReadHeadText_OverCap_ReturnsPrefixWithoutSplitCharacter()
    {
        var path = Write("head.md", "ab" + new string('é', 50));

        // 5 bytes = "ab" + one 'é' (2 bytes) + the first byte of the next 'é'.
        var head = BoundedFileRead.ReadHeadText(path, 5);

        Assert.Equal("abé", head);
    }

    [Fact]
    public void ReadHeadText_UnderCap_ReturnsWholeFile()
    {
        var path = Write("head.md", "# Title\nbody\n", bom: true);

        Assert.Equal(File.ReadAllText(path), BoundedFileRead.ReadHeadText(path, 4096));
    }

    [Fact]
    public void Reads_RejectNonPositiveCaps()
    {
        var path = Write("any.txt", "x");

        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileRead.TryReadAllText(path, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileRead.ReadTailLines(path, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileRead.ReadHeadText(path, -1));
    }
}
