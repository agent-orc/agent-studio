using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: recorded timestamps are status document fixtures and are never compared with now.

/// <summary>
/// AGT-2989 - caller text cannot spell the owned section markers, and
/// preserved task text that quotes them is never truncated or removed.
/// </summary>
public sealed class AcceptanceIntegrationStatusDocumentTests : IDisposable
{
    private const string Start = AcceptanceIntegrationStatusDocument.StartMarker;
    private const string End = AcceptanceIntegrationStatusDocument.EndMarker;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "agt-2989-status-" + Guid.NewGuid().ToString("N"));

    public AcceptanceIntegrationStatusDocumentTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void CallerTextContainingMarkers_IsEscapedAndRetriesLeaveOneSection()
    {
        File.WriteAllText(StatusPath, "# Result\n\nDelivered the feature.\n");
        var hostile = $"merge failed {End}\n## Injected heading\n{Start} tail";

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict " + End, hostile, "develop" + Start);
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second attempt", "develop");

        var content = File.ReadAllText(StatusPath);
        Assert.Equal(1, Count(content, Start));
        Assert.Equal(1, Count(content, End));
        Assert.Contains("- Reason: second attempt", content);
        Assert.DoesNotContain("Injected heading", content);
        Assert.StartsWith("# Result\n\nDelivered the feature.", content.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void CallerMarkerText_IsRenderedInertOnOneLine()
    {
        AcceptanceIntegrationStatusDocument.WriteOperatorOverride(_folder, $"waived {End}\n{Start}");

        var content = File.ReadAllText(StatusPath);
        var reason = content.ReplaceLineEndings("\n").Split('\n').Single(line => line.StartsWith("- Reason:", StringComparison.Ordinal));
        Assert.Equal("- Reason: waived &lt;!-- agent-studio:acceptance-integration:end --&gt; &lt;!-- agent-studio:acceptance-integration:start --&gt;", reason);
        Assert.Equal(1, Count(content, Start));

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal("# Result", File.ReadAllText(StatusPath).Trim());
    }

    [Fact]
    public void PreservedTaskTextQuotingTheMarkers_SurvivesUpsertAndClear()
    {
        var result = "# Result\n\nThe section starts with `" + Start + "` inline.\n\n"
            + "```\n" + Start + "\n```\n\nClosing notes stay.\n";
        File.WriteAllText(StatusPath, result);

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second", "develop");
        var written = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.StartsWith(result.TrimEnd(), written);
        Assert.DoesNotContain("- Reason: first", written);
        Assert.Contains("- Reason: second", written);

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal(result.TrimEnd(), File.ReadAllText(StatusPath).ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void UnterminatedQuotedStartMarker_IsNotTreatedAsATornSection()
        => Assert.Equal(
            "# Result\n" + Start + "\nkept\n",
            AcceptanceIntegrationStatusDocument.RemoveOwnedSection("# Result\n" + Start + "\nkept\n"));

    [Fact]
    public void CompleteQuotedPairWithoutOwnedSection_SurvivesClearAndUpsert()
    {
        var result = "# Result\n\nThe quoted markers are:\n" + Start + "\nexample text\n" + End + "\n";
        File.WriteAllText(StatusPath, result);

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal(result, File.ReadAllText(StatusPath));

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        Assert.StartsWith(result + "\n", File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));
        Assert.Equal(2, Count(File.ReadAllText(StatusPath), Start));

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal(result, File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void CompleteQuotedPairBeforeOwnedSection_SurvivesReplacementAndClear()
    {
        var result = "# Result\n\nQuoted pair:\n" + Start + "\nexample text\n" + End + "\n";
        File.WriteAllText(StatusPath, result);
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second", "develop");
        var updated = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.StartsWith(result + "\n", updated);
        Assert.Equal(2, Count(updated, Start));
        Assert.Equal(2, Count(updated, End));
        Assert.DoesNotContain("- Reason: first", updated);
        Assert.Contains("- Reason: second", updated);

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal(result, File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void CompleteQuotedPairAfterOwnedSection_PreservesTheQuoteAndReplacesOnlyTheOwnedSection()
    {
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        var original = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        var quote = original[original.IndexOf(Start, StringComparison.Ordinal)..];
        File.AppendAllText(StatusPath, "\nQuoted complete writer section:\n" + quote);

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second", "develop");
        var updated = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.Equal(1, Count(updated, "- Reason: first"));
        Assert.Contains("- Reason: second", updated);
        Assert.Equal(2, Count(updated, Start));
        Assert.Equal(2, Count(updated, End));
        Assert.Contains(quote, updated);

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        var cleared = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.DoesNotContain("- Reason: second", cleared);
        Assert.Equal(1, Count(cleared, "- Reason: first"));
        Assert.Contains(quote, cleared);
        Assert.Equal(1, Count(cleared, Start));
        Assert.Equal(1, Count(cleared, End));
    }

    [Fact]
    public void CompleteWriterLayoutQuotedWithoutAnOwnedSection_SurvivesClearAndUpsert()
    {
        var quoted = "# Result\n\nQuoted writer layout:\n" + Start
            + "\n## Acceptance integration\n\n- Outcome: `Conflict`\n- Lane: `5-human-review`"
            + "\n- Integration branch: `develop`\n- Reason: example\n- Recorded at: `2026-10-03T00:00:00.0000000Z`\n"
            + End + "\n";
        File.WriteAllText(StatusPath, quoted);

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal(quoted, File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "new", "develop");
        Assert.StartsWith(quoted + "\n", File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ClearWithCopiedWriterSectionAfterTheOwnedSection_RemovesOnlyTheOwnedSection()
    {
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        var original = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        var quote = original[original.IndexOf(Start, StringComparison.Ordinal)..];
        File.AppendAllText(StatusPath, "\nCopied section:\n" + quote);

        AcceptanceIntegrationStatusDocument.Clear(_folder);

        var cleared = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.StartsWith("# Result", cleared);
        Assert.Contains("Copied section:\n" + quote, cleared);
        Assert.Equal(1, Count(cleared, Start));
        Assert.Equal(1, Count(cleared, End));
    }

    [Fact]
    public void PlainTaskTextAfterOwnedSection_SurvivesReplacementAndClear()
    {
        File.WriteAllText(StatusPath, "# Result\n");
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        File.AppendAllText(StatusPath, "\nFollow-up task notes stay.\n");

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second", "develop");
        var updated = File.ReadAllText(StatusPath);
        Assert.DoesNotContain("- Reason: first", updated);
        Assert.Contains("- Reason: second", updated);
        Assert.Contains("Follow-up task notes stay.", updated);
        Assert.Equal(1, Count(updated, Start));

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        var cleared = File.ReadAllText(StatusPath);
        Assert.Contains("Follow-up task notes stay.", cleared);
        Assert.DoesNotContain(Start, cleared);
    }

    [Fact]
    public void EditedTaskTextBeforeOwnedSection_DoesNotPreventReplacementOrClear()
    {
        File.WriteAllText(StatusPath, "# Result\n\nInitial task result.\n");
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        var original = File.ReadAllText(StatusPath);
        File.WriteAllText(StatusPath, original.Replace("Initial task result.", "Edited task result.", StringComparison.Ordinal));

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second", "develop");
        var updated = File.ReadAllText(StatusPath);
        Assert.Contains("Edited task result.", updated);
        Assert.DoesNotContain("- Reason: first", updated);
        Assert.Equal(1, Count(updated, Start));
        Assert.Contains("- Reason: second", updated);

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal("# Result\n\nEdited task result.", File.ReadAllText(StatusPath).ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void EditedTaskTextAndCopiedEntireWriterBlockAfterIt_PreserveTheCopy()
    {
        File.WriteAllText(StatusPath, "# Result\n\nInitial task result.\n");
        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "first", "develop");
        var original = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        var ownershipStart = original.IndexOf("<!-- agent-studio:acceptance-integration:owned:", StringComparison.Ordinal);
        Assert.True(ownershipStart >= 0);
        var copiedBlock = original[ownershipStart..];
        File.WriteAllText(StatusPath,
            original.Replace("Initial task result.", "Edited task result.", StringComparison.Ordinal)
            + "\nCopied entire writer block:\n" + copiedBlock);

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "conflict", "second", "develop");
        var updated = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.Contains("Edited task result.", updated);
        Assert.Contains("Copied entire writer block:\n" + copiedBlock, updated);
        Assert.Equal(1, Count(updated, "- Reason: first"));
        Assert.Equal(1, Count(updated, "- Reason: second"));
        Assert.Equal(2, Count(updated, Start));

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        var cleared = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.True(cleared.Contains("Copied entire writer block:\n" + copiedBlock, StringComparison.Ordinal), cleared);
        Assert.DoesNotContain("- Reason: second", cleared);
        Assert.Equal(1, Count(cleared, Start));
    }

    [Fact]
    public void LegacyFailureSectionWithoutOwnershipMarker_IsReplacedAndCleared()
    {
        var taskText = "# Result\n\nDelivered work.\n\nQuoted markers:\n"
            + Start + "\nexample text\n" + End + "\n";
        File.WriteAllText(StatusPath, taskText + "\n" + LegacyFailureSection);

        AcceptanceIntegrationStatusDocument.WriteFailure(_folder, "Error", "retry failed", "develop");
        var updated = File.ReadAllText(StatusPath).ReplaceLineEndings("\n");
        Assert.StartsWith(taskText + "\n", updated);
        Assert.DoesNotContain("- Reason: old merge conflict", updated);
        Assert.Contains("- Reason: retry failed", updated);
        Assert.Equal(2, Count(updated, Start));
        Assert.Equal(1, Count(updated, "<!-- agent-studio:acceptance-integration:owned:"));

        AcceptanceIntegrationStatusDocument.Clear(_folder);
        Assert.Equal(taskText, File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void LegacyOperatorOverrideWithoutOwnershipMarker_CanBeCleared()
    {
        File.WriteAllText(StatusPath, "# Result\n\nDelivered work.\n\n"
            + Start + "\n## Acceptance integration\n\n"
            + "- Outcome: `OperatorOverride`\n- Lane: `6-completed`\n"
            + "- Integration: explicitly waived by the operator\n"
            + "- Reason: approved\n- Recorded at: `2026-09-28T00:00:00.0000000Z`\n"
            + End + "\n");

        AcceptanceIntegrationStatusDocument.Clear(_folder);

        Assert.Equal("# Result\n\nDelivered work.\n", File.ReadAllText(StatusPath).ReplaceLineEndings("\n"));
    }

    private static string LegacyFailureSection => Start + "\n## Acceptance integration\n\n"
        + "- Outcome: `Conflict`\n- Lane: `5-human-review`\n"
        + "- Integration branch: `develop`\n- Reason: old merge conflict\n"
        + "- Recorded at: `2026-09-28T00:00:00.0000000Z`\n" + End + "\n";

    private string StatusPath => Path.Combine(_folder, "status.md");

    private static int Count(string content, string marker)
    {
        var count = 0;
        for (var index = content.IndexOf(marker, StringComparison.Ordinal); index >= 0;
             index = content.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
