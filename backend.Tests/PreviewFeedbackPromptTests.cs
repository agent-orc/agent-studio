using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public class PreviewFeedbackPromptTests
{
    [Fact]
    public void FixRoundCarriesCropSelectorAddressAndComment()
    {
        var prompt = PreviewFeedbackPrompt.Build(
            "Make the heading easier to read.",
            "https://stage.example.test/pricing",
            "main h1",
            "attachments/marked-heading.png");

        Assert.Contains("https://stage.example.test/pricing", prompt);
        Assert.Contains("main h1", prompt);
        Assert.Contains("attachments/marked-heading.png", prompt);
        Assert.Contains("Make the heading easier to read.", prompt);
        Assert.Contains("same card", prompt);
        Assert.Contains("before and after screenshots", prompt);
    }

    [Theory]
    [InlineData("", "https://stage.example.test", null)]
    [InlineData("Change this", "file:///tmp/preview.html", null)]
    [InlineData("Change this", "not a URL", null)]
    public void RejectsIncompleteFeedback(string comment, string address, string? selector)
    {
        Assert.NotNull(PreviewFeedbackPrompt.Validate(comment, address, selector));
    }
}
