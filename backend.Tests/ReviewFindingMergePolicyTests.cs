using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3268: aspect and deep-review findings become one fix round. A defect
/// two reviewers report at the same location is one entry; distinct findings
/// keep their exact previous rendering.
/// </summary>
public sealed class ReviewFindingMergePolicyTests
{
    [Fact]
    public void Findings_at_the_same_location_merge_into_one_entry_and_keep_both_statements()
    {
        var merged = ReviewFindingMergePolicy.Merge(
        [
            new("code-quality", "block", "Null path is dropped.", "backend/Foo.cs", "backend/Foo.cs:42 drops the error"),
            new("deep-review", "block", "Error path loses the write.", "backend/Foo.cs", "backend/Foo.cs:42 loses data"),
            new("tests-and-evidence", "block", "No regression test.", "backend.Tests", "backend.Tests/FooTests.cs:10"),
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(["code-quality", "deep-review"], merged[0].Aspects);
        Assert.Equal(2, merged[0].Sources.Count);
        Assert.Equal(["tests-and-evidence"], merged[1].Aspects);
    }

    [Fact]
    public void Identical_statements_from_two_aspects_are_kept_once()
    {
        var merged = ReviewFindingMergePolicy.Merge(
        [
            new("code-quality", "block", "The retry loop has no cap.", null, "The retry loop has no cap."),
            new("deep-review", "block", "The retry loop has no cap", null, "the retry  loop has no cap."),
        ]);

        var entry = Assert.Single(merged);
        Assert.Equal(["code-quality", "deep-review"], entry.Aspects);
        Assert.Single(entry.Sources);
    }

    [Fact]
    public void Fix_round_prompt_lists_a_merged_defect_once_with_each_reviewer_named()
    {
        var prompt = ReviewFindingRoundPrompt.Build(
        [
            new("code-quality", "block", "Null path is dropped.", "backend/Foo.cs", "backend/Foo.cs:42 drops the error"),
            new("deep-review", "block", "Error path loses the write.", "backend/Foo.cs", "backend/Foo.cs:42 loses data"),
        ], "rev-1", continuesReviewedDelivery: true, nonce: "abc");

        Assert.Single(prompt.Split('\n'), line => line == "## code-quality + deep-review");
        Assert.Contains("- Summary (code-quality): Null path is dropped.", prompt);
        Assert.Contains("- Finding (deep-review): backend/Foo.cs:42 loses data", prompt);
    }

    [Fact]
    public void Distinct_findings_render_exactly_as_before()
    {
        var block = ReviewFindingRoundPrompt.RenderAspectFindings(
        [
            new("code-quality", "block", "Dead branch.", "backend/A.cs", "backend/A.cs:1"),
            new("requirement-fit", "block", "Goal missed.", null, null),
        ], "abc");

        Assert.Contains(string.Join('\n',
            "## code-quality",
            "",
            "- Summary: Dead branch.",
            "- Evidence checked: backend/A.cs",
            "- Finding: backend/A.cs:1",
            "",
            "## requirement-fit",
            "",
            "- Summary: Goal missed."), block);
    }
}
