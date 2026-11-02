using Xunit;

namespace AgentStudio.Tests;

public sealed class ResultHtmlDispositionPolicyTests
{
    [Theory]
    [InlineData("iframe", "same-origin", true)]
    [InlineData("IFRAME", "Same-Origin", true)]
    [InlineData("frame", "same-origin", true)]
    [InlineData("document", "same-origin", false)]
    [InlineData("document", "none", false)]
    [InlineData("iframe", "same-site", false)]
    [InlineData("iframe", "cross-site", false)]
    [InlineData("iframe", null, false)]
    [InlineData("empty", "same-origin", false)]
    [InlineData(null, "same-origin", false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void Html_renders_inline_only_inside_a_studio_frame(string? fetchDest, string? fetchSite, bool inline)
        => Assert.Equal(inline, ResultHtmlDispositionPolicy.ServesInline(fetchDest, fetchSite));
}
