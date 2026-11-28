using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AspectVerdictMarkerParserTests
{
    private const string Agt2794Marker = """
        > [[ASPECT_VERDICT: status=concerns;
        > summary=Clean, well-structured diff with one dead no-op assertion in a new spec file;
        > evidence_checked=frontend/src/app/example.spec.ts and git diff;
        > missing=none; classification=code-quality;
        > summary=one leftover issue.]] [[TASK_DONE]]
        """;

    [Fact]
    public void ParseLast_Agt2794Marker_KeepsFirstSummaryAndFlagsDuplicateAsDetail()
    {
        var marker = Assert.IsType<AspectVerdictMarker>(
            AspectVerdictMarkerParser.ParseLast(Agt2794Marker));

        Assert.Equal("concerns", marker.Status);
        Assert.Equal(
            "Clean, well-structured diff with one dead no-op assertion in a new spec file",
            marker.Summary);
        Assert.Contains("frontend/src/app/example.spec.ts", marker.EvidenceChecked);
        Assert.Equal("none", marker.Missing);
        Assert.Equal("code-quality", marker.Classification);
        Assert.Equal("summary=one leftover issue.", marker.Detail);
        Assert.Equal([AspectVerdictMarkerParser.DuplicateKey], marker.Malformed);
        Assert.Equal(
            "code-quality; malformed: duplicate-key",
            AspectVerdictMarkerParser.ClassificationWithMalformed(marker, "RemoteAspectVerdict"));
    }

    [Fact]
    public void ParseLast_KeepsPreviousRecheckSeparateFromTheNewBlockerCitation()
    {
        var marker = Assert.IsType<AspectVerdictMarker>(AspectVerdictMarkerParser.ParseLast("""
            [[ASPECT_VERDICT: status=block; classification=quality-independent;
            summary=Verification failure still enables the launcher;
            evidence_checked=docs/setup.md migration step 3; missing=Abort before enabling;
            previous_evidence_checked=src/Parser.cs AssertionFingerprint;
            previous_recheck=Passing sibling output is excluded from the fingerprint.]]
            """));

        Assert.Equal("docs/setup.md migration step 3", marker.EvidenceChecked);
        Assert.Equal("src/Parser.cs AssertionFingerprint", marker.PreviousEvidenceChecked);
        Assert.Equal("Passing sibling output is excluded from the fingerprint.", marker.PreviousRecheck);
        Assert.Empty(marker.Malformed);
    }

    [Fact]
    public void ParseLast_UsesLastWrappedMarkerAndFirstValueForEveryDuplicate()
    {
        var marker = AspectVerdictMarkerParser.ParseLast("""
            [[ASPECT_VERDICT: status=pass; summary=old; missing=none]]
            ```text
            [[ASPECT_VERDICT: status=block; status=pass; summary=first;
            evidence_checked=a.cs; missing=test; classification=delivery]]
            ```
            """);

        Assert.NotNull(marker);
        Assert.Equal("block", marker.Status);
        Assert.Equal("first", marker.Summary);
        Assert.Equal("status=pass", marker.Detail);
        Assert.Contains(AspectVerdictMarkerParser.DuplicateKey, marker.Malformed);
    }
}
