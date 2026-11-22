using AgentStudio.Git;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3968: only a hunk where both sides added lines at the same place,
/// relative to the merge base, is resolved by keeping both sides.
/// </summary>
public sealed class AddAddUnionResolutionTests
{
    private static readonly string Start = new string('<', AddAddUnionResolution.MarkerSize) + " integration";
    private static readonly string Base = new string('|', AddAddUnionResolution.MarkerSize) + " merge-base";
    private static readonly string Separator = new('=', AddAddUnionResolution.MarkerSize);
    private static readonly string End = new string('>', AddAddUnionResolution.MarkerSize) + " delivery";

    [Fact]
    public void AddAddHunk_KeepsIntegrationThenDeliveryLines()
    {
        var text = string.Join('\n',
            "import { A } from './a';",
            Start, "import { B } from './b';", Base, Separator, "import { C } from './c';", End,
            "export const x = 1;", "");

        var result = AddAddUnionResolution.Resolve("src/x.ts", text);

        Assert.True(result.Resolved, result.Refusal);
        Assert.Equal(string.Join('\n',
            "import { A } from './a';", "import { B } from './b';", "import { C } from './c';",
            "export const x = 1;", ""), result.Content);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(new UnionResolvedHunk("src/x.ts", 2, 1, 1), hunk);
    }

    [Fact]
    public void HunkThatChangesAMergeBaseLine_IsRefused()
    {
        // Both sides rewrapped the same import line: AGT-3897 round 10:47.
        var text = string.Join('\n',
            Start, "import { a, b,", "  c } from './x';", Base, "import { a, b } from './x';", Separator,
            "import {", "  a, b, d,", "} from './x';", End, "");

        var result = AddAddUnionResolution.Resolve("src/y.ts", text);

        Assert.False(result.Resolved);
        Assert.Null(result.Content);
        Assert.Contains("src/y.ts:1: both sides changed 1 merge-base line", result.Refusal);
    }

    [Fact]
    public void OneRefusedHunk_RefusesTheWholeFile()
    {
        var text = string.Join('\n',
            Start, "one", Base, Separator, "two", End,
            "middle",
            Start, "three", Base, "base", Separator, "four", End, "");

        var result = AddAddUnionResolution.Resolve("f.cs", text);

        Assert.False(result.Resolved);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void TwoAddAddHunks_AreBothRecordedWithTheirLines()
    {
        var text = string.Join('\n',
            "a",
            Start, "b1", "b2", Base, Separator, "c1", End,
            "d",
            Start, "e1", Base, Separator, "f1", "f2", "f3", End, "");

        var result = AddAddUnionResolution.Resolve("f.cs", text);

        Assert.True(result.Resolved, result.Refusal);
        Assert.Equal("a\nb1\nb2\nc1\nd\ne1\nf1\nf2\nf3\n", result.Content);
        Assert.Equal([new UnionResolvedHunk("f.cs", 2, 2, 1), new UnionResolvedHunk("f.cs", 6, 1, 3)],
            result.Hunks);
    }

    [Fact]
    public void MarkerLikeContentShorterThanTheMarkerSize_StaysContent()
    {
        var text = string.Join('\n',
            "<<<<<<< not a marker", "=======",
            Start, "x", Base, Separator, "y", End, "");

        var result = AddAddUnionResolution.Resolve("f.md", text);

        Assert.True(result.Resolved, result.Refusal);
        Assert.Equal("<<<<<<< not a marker\n=======\nx\ny\n", result.Content);
    }

    [Fact]
    public void UnclosedMarkerOrNoConflict_IsRefused()
    {
        Assert.False(AddAddUnionResolution.Resolve("f", "plain\n").Resolved);
        Assert.False(AddAddUnionResolution.Resolve("f", Start + "\nx\n" + Base + "\n").Resolved);
    }
}
