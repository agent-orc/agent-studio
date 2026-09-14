using AgentStudio.Areas;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The area / tag filter is shared by the task list, the Dossier list, and the
/// wiki tree, so its semantics are pinned once, directly.
/// </summary>
public class TagFilterTests
{
    [Fact]
    public void AnInactiveFilterMatchesEverything()
    {
        Assert.False(TagFilter.None.IsActive);
        Assert.True(TagFilter.None.Matches(null));
        Assert.True(TagFilter.None.Matches(["anything"]));
    }

    [Fact]
    public void IdsWithinOneParameterAreAlternatives()
    {
        var filter = TagFilter.Parse("delivery-chain,observation", null);

        Assert.True(filter.Matches(["observation"]));
        Assert.True(filter.Matches(["delivery-chain", "incident"]));
        Assert.False(filter.Matches(["security"]));
        Assert.False(filter.Matches([]));
    }

    [Fact]
    public void AreaAndTagAreAConjunction()
    {
        var filter = TagFilter.Parse("delivery-chain", "incident");

        Assert.True(filter.Matches(["delivery-chain", "incident"]));
        Assert.False(filter.Matches(["delivery-chain"]));
        Assert.False(filter.Matches(["incident"]));
    }

    [Fact]
    public void MatchingIsCaseInsensitive() =>
        Assert.True(TagFilter.Parse("Delivery-Chain", null).Matches(["delivery-chain"]));

    [Fact]
    public void FromQuery_ReadsCommaSeparatedAndRepeatedParameters()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["area"] = new(["delivery-chain, observation", "security"]),
            ["tag"] = new("incident"),
        });

        var filter = TagFilter.FromQuery(query);

        Assert.Equal(["delivery-chain", "observation", "security"], filter.Areas);
        Assert.Equal(["incident"], filter.Tags);
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void FromQuery_WithoutParametersIsInactive() =>
        Assert.False(TagFilter.FromQuery(new QueryCollection()).IsActive);
}
