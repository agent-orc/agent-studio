using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for the evidence-completion helpers (AGT-2022): the results/ folder
/// inventory and the card-mode framing that every review / aspect prompt now
/// carries so an empty git diff is never mis-read as "deliverables missing".
/// </summary>
public class ReviewEvidenceContextTests : IDisposable
{
    private readonly string _jobFolder;

    public ReviewEvidenceContextTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "review-evidence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResultsInventory_NoFolder_ReportsAbsenceExplicitly()
    {
        var inventory = ResultsInventory.Render(_jobFolder);
        Assert.Equal("No results/ folder present for this task.", inventory);
    }

    [Fact]
    public void ResultsInventory_EmptyFolder_ReportsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_jobFolder, "results"));
        var inventory = ResultsInventory.Render(_jobFolder);
        Assert.Contains("empty", inventory);
    }

    [Fact]
    public void ResultsInventory_ReadOnlyTaskWithArtifacts_ListsFilesAndExcerptsText()
    {
        // A read-only / concept task with results/ artefacts must produce a
        // non-empty inventory so the reviewer never false-BLOCKs it as
        // "deliverables missing" on an empty code diff (AGT-1915).
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "analysis.md"), "# Findings\n\nThe root cause is X.");
        File.WriteAllBytes(Path.Combine(results, "shot--real.png"), new byte[] { 1, 2, 3, 4 });

        var inventory = ResultsInventory.Render(_jobFolder);

        Assert.Contains("results/ folder contains 2 file(s)", inventory);
        Assert.Contains("analysis.md", inventory);
        Assert.Contains("shot--real.png", inventory);
        // Text artefact excerpted; binary artefact listed but not excerpted.
        Assert.Contains("The root cause is X.", inventory);
        Assert.DoesNotContain("Excerpt of shot--real.png", inventory);
    }

    [Fact]
    public void ResultsInventory_OversizedTextArtifact_ExcerptsTheSameLeadingCharacters()
    {
        // AGT-2991: the excerpt reads only its leading bytes, so an agent-written
        // file of many MiB yields exactly what the whole-file read produced.
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        var content = "# Report ü\n" + string.Concat(Enumerable.Repeat("résumé ✓ line\n", 400_000));
        var path = Path.Combine(results, "report.md");
        File.WriteAllText(path, content);
        Assert.True(new FileInfo(path).Length > 4 * 1024 * 1024);

        var inventory = ResultsInventory.Render(_jobFolder);

        var expected = File.ReadAllText(path)[..600] + "\n... (truncated)";
        Assert.Contains(expected.Trim(), inventory);
        Assert.DoesNotContain('\uFFFD', inventory);
    }

    [Fact]
    public void ResultsInventory_WalksNestedFolders()
    {
        var nested = Path.Combine(_jobFolder, "results", "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "note.txt"), "nested deliverable");

        var inventory = ResultsInventory.Render(_jobFolder);

        Assert.Contains("sub/note.txt", inventory);
        Assert.Contains("nested deliverable", inventory);
    }

    [Fact]
    public void ResultsInventory_ExcludesRotatedReviewHistory()
    {
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "current-report.md"), "fresh evidence");
        var history = Path.Combine(results, "history", "review-epoch-0001", "operator-requeue");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "aspect-code-quality.md"), "stale BLOCK");
        File.WriteAllText(Path.Combine(history, "status.md"), "Result: Escalated");

        var inventory = ResultsInventory.Render(_jobFolder);

        Assert.Contains("results/ folder contains 1 file(s)", inventory);
        Assert.Contains("current-report.md", inventory);
        Assert.DoesNotContain("stale BLOCK", inventory);
        Assert.DoesNotContain("Result: Escalated", inventory);
        Assert.True(ResultsInventory.HasActiveArtifacts(_jobFolder));
    }

    [Fact]
    public void ResultsInventory_HistoryAloneIsNotActiveCompletionEvidence()
    {
        var history = Path.Combine(_jobFolder, "results", "history", "review-epoch-0001", "operator-requeue");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "status.md"), "Result: Escalated");

        Assert.False(ResultsInventory.HasActiveArtifacts(_jobFolder));
    }

    [Fact]
    public void ResultsInventory_BlankJobFolderPath_ReportsUnavailableInsteadOfThrowing()
    {
        // Fallback path: a caller with no resolved job-folder path must get a
        // stable "unavailable" line, never a thrown exception that would break the
        // review pass.
        Assert.Equal(
            "No results/ folder (job folder path unavailable).",
            ResultsInventory.Render("   "));
    }

    [Fact]
    public void ResultsInventory_LargeTextArtifact_ExcerptIsTruncatedNotDumped()
    {
        // Fallback path: a big text deliverable must be excerpted, not dumped
        // whole, so the review prompt stays bounded. The truncation marker proves
        // the cap fired.
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        var body = new string('x', 5000);
        File.WriteAllText(Path.Combine(results, "big.md"), body);

        var inventory = ResultsInventory.Render(_jobFolder, maxExcerptChars: 200);

        Assert.Contains("big.md", inventory);
        Assert.Contains("... (truncated)", inventory);
        // The full 5000-char body must not be inlined.
        Assert.DoesNotContain(body, inventory);
    }

    [Fact]
    public void ResultsInventory_MoreFilesThanCap_ListsCapThenSummarisesRemainder()
    {
        // Fallback path: a results/ folder with many artefacts lists up to the cap
        // and then states how many more exist, rather than flooding the prompt.
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        for (var i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(results, $"shot-{i}--real.png"), new byte[] { 1 });

        var inventory = ResultsInventory.Render(_jobFolder, maxFiles: 2);

        Assert.Contains("results/ folder contains 5 file(s)", inventory);
        Assert.Contains("and 3 more file(s).", inventory);
    }

    [Fact]
    public void ReviewEvidence_QuotesMeasurementsThatExistOnlyInResults()
    {
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "duration-comparison.md"),
            "Before: 120 seconds. After: 80 seconds.\n");
        File.WriteAllText(Path.Combine(results, "class-before-after.csv"),
            "class,before,after\nSuite,120,80\n");
        File.WriteAllBytes(Path.Combine(results, "screenshot.png"), [1, 2, 3]);

        var evidence = ResultsInventory.RenderForReview(_jobFolder);

        Assert.Contains("Before: 120 seconds. After: 80 seconds.", evidence);
        Assert.Contains("class,before,after", evidence);
        Assert.Contains("results/screenshot.png (3 bytes)", evidence);
        Assert.DoesNotContain("Head of results/screenshot.png", evidence);
    }

    [Fact]
    public void ReviewEvidence_MissingResultsStatesAbsence()
    {
        Assert.Equal("No results/ folder present for this task.",
            ResultsInventory.RenderForReview(_jobFolder));
    }

    [Fact]
    public void ReviewEvidence_BoundsTextAndNamesButHashesEveryFile()
    {
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "a-report.md"), new string('x', 30_000));
        for (var i = 0; i < 42; i++)
            File.WriteAllText(Path.Combine(results, $"evidence-{i:00}.csv"), "value\n1\n");
        var first = ResultsInventory.RenderForReview(_jobFolder);
        File.AppendAllText(Path.Combine(results, "evidence-41.csv"), "2\n");
        var second = ResultsInventory.RenderForReview(_jobFolder);

        Assert.Contains("... and 3 more file(s)", first);
        Assert.Contains("(truncated", first);
        Assert.True(first.Length < 30_000);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("results/evidence-41.csv (", first);
    }

    [Fact]
    public void ReviewEvidence_ExcludesHistoryAndLinksOutsideResults()
    {
        if (OperatingSystem.IsWindows()) return;
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(_jobFolder, "outside.md"), "outside secret");
        File.CreateSymbolicLink(Path.Combine(results, "linked.md"), Path.Combine(_jobFolder, "outside.md"));
        var history = Path.Combine(results, "history");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "old.md"), "old evidence");
        File.WriteAllText(Path.Combine(results, "current.md"), "current evidence");

        var evidence = ResultsInventory.RenderForReview(_jobFolder);

        Assert.Contains("current evidence", evidence);
        Assert.DoesNotContain("outside secret", evidence);
        Assert.DoesNotContain("linked.md", evidence);
        Assert.DoesNotContain("old evidence", evidence);
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("research")]
    public void ReviewCardMode_ReadOnlyModes_SayNoCodeDiffIsExpected(string mode)
    {
        var described = ReviewCardMode.Describe(mode);
        Assert.Contains("read-only", described);
        Assert.Contains("NO code diff", described);
        Assert.Contains("Do NOT treat an empty", described);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData(null)]
    [InlineData("something-unknown")]
    public void ReviewCardMode_CodingOrUnknown_ExpectsACodeChangeSet(string? mode)
    {
        var described = ReviewCardMode.Describe(mode);
        Assert.Contains("coding", described);
        Assert.Contains("code change set is expected", described);
    }
}
