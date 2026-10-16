using System.Text;
using Xunit;

namespace AgentStudio.Tests;

public class OrchestratorChatProductPreviewTests
{
    [Fact]
    public void PreviewChatAsksForMissingDetailsBeforeCardConfirmation()
    {
        var prompt = new StringBuilder();
        OrchestratorChatService.AppendNavigationContext(prompt,
            new ChatNavigationContext(CurrentPage: "kanban-board", ObservedSurface: "URL preview"));

        var rendered = prompt.ToString();
        Assert.Contains("scope, page location, and examples", rendered);
        Assert.Contains("rough quota estimate", rendered);
        Assert.Contains("Create this change", rendered);
        Assert.Contains("plain product language", rendered);
    }
}
