using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2828 acceptance: "Evidence assets the task explicitly asks for
/// (screenshots under the project's declared asset paths) are committable
/// without a manual step, within size limits."
///
/// <para>Direct matrix over the pure rule. Every dimension that can turn the
/// answer around gets a row, including the two the WEB-21 report leaves
/// implicit: a project that declared nothing still admits nothing, and an
/// oversized asset still needs a person.</para>
/// </summary>
public class EvidenceAssetPolicyTests
{
    private static readonly string[] Declared = ["docs/assets", "frontend/e2e/screenshots"];

    [Theory]
    // Screenshots under a declared path are the deliverable, not a surprise.
    [InlineData("docs/assets/board-dark.png", 120_000, true, EvidenceAssetCodes.Admitted)]
    [InlineData("docs/assets/nested/board-light.jpeg", 120_000, true, EvidenceAssetCodes.Admitted)]
    [InlineData("frontend/e2e/screenshots/lane.webp", 1, true, EvidenceAssetCodes.Admitted)]
    [InlineData("docs/assets/report.pdf", 4_000_000, true, EvidenceAssetCodes.Admitted)]
    // Exactly at the limit is inside it; one byte over is not.
    [InlineData("docs/assets/edge.png", 5 * 1024 * 1024, true, EvidenceAssetCodes.Admitted)]
    [InlineData("docs/assets/edge.png", 5 * 1024 * 1024 + 1, false, EvidenceAssetCodes.Oversized)]
    // Outside every declared prefix, including the near-miss sibling directory.
    [InlineData("screenshot.png", 120_000, false, EvidenceAssetCodes.NotDeclared)]
    [InlineData("docs/assets-private/leak.png", 120_000, false, EvidenceAssetCodes.NotDeclared)]
    [InlineData("docs/assets", 120_000, false, EvidenceAssetCodes.NotDeclared)]
    // Under a declared path, but not an evidence asset: a binary blob that
    // happens to live next to the screenshots still needs a person.
    [InlineData("docs/assets/tool.exe", 120_000, false, EvidenceAssetCodes.UnsupportedType)]
    [InlineData("docs/assets/keystore.p12", 900, false, EvidenceAssetCodes.UnsupportedType)]
    public void Decide_matrix(string path, long size, bool admitted, string code)
    {
        var decision = EvidenceAssetPolicy.Decide(path, size, Declared);

        Assert.Equal(admitted, decision.Admitted);
        Assert.Equal(code, decision.Code);
    }

    [Fact]
    public void Project_without_declared_paths_admits_nothing()
    {
        foreach (IReadOnlyCollection<string>? declared in new IReadOnlyCollection<string>?[] { null, [], ["  "] })
        {
            var decision = EvidenceAssetPolicy.Decide("docs/assets/board.png", 1_000, declared);

            Assert.False(decision.Admitted);
            Assert.Equal(EvidenceAssetCodes.NotDeclared, decision.Code);
        }
    }

    [Fact]
    public void Declared_paths_are_normalized_and_escaping_entries_dropped()
    {
        var normalized = EvidenceAssetPolicy.NormalizeDeclaredPaths(
            ["/docs/assets/", "docs\\assets", "  ", "../../etc", "results"]);

        Assert.Equal(["docs/assets", "results"], normalized);
    }

    [Fact]
    public void Escaping_declaration_cannot_admit_a_path_outside_the_repository()
    {
        var decision = EvidenceAssetPolicy.Decide("../secrets/key.png", 10, ["../secrets"]);

        Assert.False(decision.Admitted);
        Assert.Equal(EvidenceAssetCodes.NotDeclared, decision.Code);
    }

    [Fact]
    public void Windows_separators_in_the_candidate_path_still_match_a_declaration()
    {
        var decision = EvidenceAssetPolicy.Decide(@"docs\assets\board.png", 10, Declared);

        Assert.True(decision.Admitted);
    }
}
