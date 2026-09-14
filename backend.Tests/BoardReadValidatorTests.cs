using AgentStudio.Tasks;

using Microsoft.Extensions.Primitives;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Matrix coverage for the pure half of the AGT-2703 conditional board read:
/// how signature parts become an entity tag, and which
/// <c>If-None-Match</c> headers count as "the client already has this".
/// The endpoints add the inputs; this class fixes the rules that decide whether
/// a client is told to keep its copy.
/// </summary>
public sealed class BoardReadValidatorTests
{
    [Fact]
    public void Identical_parts_produce_an_identical_tag()
    {
        var first = BoardReadValidator.FormatETag(BoardReadValidator.Compose("tasks/grouped", "a", "b"));
        var second = BoardReadValidator.FormatETag(BoardReadValidator.Compose("tasks/grouped", "a", "b"));

        Assert.Equal(first, second);
    }

    [Theory]
    // Any single differing part has to move the tag, whichever position it is in.
    [InlineData("tasks/list", "a", "b")]
    [InlineData("tasks/grouped", "z", "b")]
    [InlineData("tasks/grouped", "a", "z")]
    public void Any_differing_part_produces_a_different_tag(string route, string one, string two)
    {
        var baseline = BoardReadValidator.FormatETag(BoardReadValidator.Compose("tasks/grouped", "a", "b"));

        var other = BoardReadValidator.FormatETag(BoardReadValidator.Compose(route, one, two));

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Part_boundaries_cannot_be_forged_by_a_part_that_contains_the_neighbour()
    {
        // ("ab", "c") and ("a", "bc") only collide if the parts are concatenated
        // without a separator that cannot occur inside a part.
        var left = BoardReadValidator.FormatETag(BoardReadValidator.Compose("ab", "c"));
        var right = BoardReadValidator.FormatETag(BoardReadValidator.Compose("a", "bc"));

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Tag_is_a_quoted_strong_validator_safe_for_a_header()
    {
        // Composed tokens carry a unit separator; a raw token in the ETag
        // header throws in Kestrel, so the tag has to be the hash, not the token.
        var etag = BoardReadValidator.FormatETag(BoardReadValidator.Compose("tasks/grouped", "runtime=42"));

        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.EndsWith("\"", etag, StringComparison.Ordinal);
        Assert.DoesNotContain('', etag);
        Assert.DoesNotContain("W/", etag, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"abc\"", true)]                 // exact
    [InlineData("*", true)]                       // wildcard
    [InlineData("W/\"abc\"", true)]               // weakened by a proxy
    [InlineData("\"zzz\", \"abc\"", true)]        // list, ours second
    [InlineData("\"zzz\"", false)]                // a different representation
    [InlineData("", false)]                       // header present but empty
    [InlineData("abc", false)]                    // unquoted is not this tag
    public void Matches_follows_the_if_none_match_cases_a_board_client_can_send(
        string header, bool expected)
    {
        Assert.Equal(expected, BoardReadValidator.Matches(new StringValues(header), "\"abc\""));
    }

    [Fact]
    public void Matches_is_false_without_a_header()
    {
        Assert.False(BoardReadValidator.Matches(StringValues.Empty, "\"abc\""));
    }
}
