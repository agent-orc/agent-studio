using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewAspectCachePromptTests
{
    [Theory]
    [InlineData("codex", "\n")]
    [InlineData("codex", "\r\n")]
    [InlineData("claude", "\n")]
    [InlineData("claude", "\r\n")]
    public void Four_stock_aspects_share_evidence_prefix_before_their_own_instructions(string cli, string newline)
    {
        var prompts = new RuntimePromptService(new ConfigurationBuilder().Build(),
            NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var inputs = new AspectRunInputs("Project", "AGT-1", "Cache review", "/tmp/AGT-1",
            "Brief body\n```\nexample\n```", "Recent execution", "Branch diff", "Status report")
        {
            AcceptanceScope = "Acceptance criteria",
            StatusReportNote = "Report provenance: inspect results/status.md for round requirements.",
            ResultsInventory = "results/proof.txt",
            CardMode = "Coding delivery",
        };
        var ids = new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" };
        var rendered = ids.Select(id => ReviewAspectCachePrompt.ComposeStock(
            aspects.BuildAspectPrompt(AspectRunnerService.Catalogue[id], inputs,
                cli == "claude" ? "claude-opus-5" : "gpt-5.4-mini").ReplaceLineEndings(newline), inputs, cli, id, null)).ToArray();
        var prefixes = rendered.Select(prompt => prompt[..prompt.IndexOf(
            ReviewAspectCachePrompt.MaterialMarker, StringComparison.Ordinal)]).ToArray();

        Assert.All(prefixes, prefix => Assert.Equal(prefixes[0], prefix));
        Assert.Contains("Brief body", prefixes[0]);
        Assert.Contains("Branch diff", prefixes[0]);
        Assert.Contains("Acceptance criteria", prefixes[0]);
        Assert.Contains("results/proof.txt", prefixes[0]);
        Assert.Contains("## Status summary (the agent's own report)", prefixes[0]);
        Assert.Contains("Status report", prefixes[0]);
        Assert.Contains(inputs.StatusReportNote, prefixes[0]);
        Assert.DoesNotContain("Recent execution", prefixes[0]);
        var activity = rendered.Where(prompt => prompt.Contains("Recent execution", StringComparison.Ordinal))
            .Select(prompt => prompt[..prompt.IndexOf(ReviewAspectCachePrompt.InstructionsMarker, StringComparison.Ordinal)])
            .ToArray();
        Assert.Equal(3, activity.Length);
        Assert.All(activity, prefix => Assert.Equal(activity[0], prefix));
        Assert.DoesNotContain("Recent execution", rendered[2]);
        foreach (var (id, prompt) in ids.Zip(rendered))
        {
            Assert.True(prompt.IndexOf("# Aspect review:", StringComparison.Ordinal)
                > prompt.IndexOf(ReviewAspectCachePrompt.MaterialMarker, StringComparison.Ordinal));
            Assert.Contains("[[ASPECT_VERDICT:", prompt);
            Assert.Equal(1, prompt.Split("Brief body").Length - 1);
            Assert.Equal(1, prompt.Split("example").Length - 1);
            Assert.Equal(1, prompt.Split("Branch diff").Length - 1);
            Assert.Equal(1, prompt.Split("Status report").Length - 1);
            Assert.Equal(1, prompt.Split(inputs.StatusReportNote).Length - 1);
            Assert.DoesNotContain("Status report", prompt[(prompt.IndexOf(ReviewAspectCachePrompt.InstructionsMarker,
                StringComparison.Ordinal) + ReviewAspectCachePrompt.InstructionsMarker.Length)..]);
            Assert.Contains(id switch
            {
                "requirement-fit" => "requirement fit only",
                "code-quality" => "code quality only",
                "documentation-impact" => "documentation impact only",
                _ => "tests and evidence only",
            }, prompt);
        }
    }

    [Fact]
    public void Crlf_authored_prompt_drops_its_duplicate_evidence_sections()
    {
        var prompts = new RuntimePromptService(new ConfigurationBuilder().Build(),
            NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var inputs = new AspectRunInputs("Project", "AGT-1", "Cache review", "/tmp/AGT-1",
            "Brief body\nsecond line", "Recent execution\nlast line", "Branch diff\nfile", "Status report\nreport tail")
        {
            AcceptanceScope = "Acceptance criteria",
            ResultsInventory = "results/proof.txt",
            CardMode = "Coding delivery",
        };
        var authored = aspects.BuildAspectPrompt(AspectRunnerService.Catalogue["code-quality"], inputs, "claude-opus-5")
            .ReplaceLineEndings("\r\n");

        var prompt = ReviewAspectCachePrompt.Compose(authored, inputs);

        Assert.Equal(1, prompt.Split("Brief body").Length - 1);
        Assert.Equal(1, prompt.Split("Branch diff").Length - 1);
        Assert.Equal(1, prompt.Split("results/proof.txt").Length - 1);
    }

    [Theory]
    [InlineData("claude", "requirement-fit", "Explicit override")]
    [InlineData("codex", "code-quality", "Explicit override")]
    [InlineData("claude", "visual-review", null)]
    [InlineData("codex", "concept-fit", null)]
    [InlineData("gemini", "tests-and-evidence", null)]
    public void Overrides_and_specialised_aspects_remain_authored(string cli, string aspect, string? custom)
    {
        const string authored = "Authored instructions\r\n## Status summary\r\nCustom evidence";
        var inputs = new AspectRunInputs("Project", "AGT-1", "Review", "/tmp", "Brief", "Log", "Diff", "Status");

        Assert.Equal(authored, ReviewAspectCachePrompt.ComposeStock(authored, inputs, cli, aspect, custom));
    }

    [Theory]
    [InlineData("requirement-fit")]
    [InlineData("tests-and-evidence")]
    public void MissingEvidenceFindingMustNameTheInspectedMaterial(string aspect)
    {
        var prompts = new RuntimePromptService(new ConfigurationBuilder().Build(),
            NullLogger<RuntimePromptService>.Instance);
        var service = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var inputs = new AspectRunInputs("Project", "AGT-1", "Evidence review", "/tmp/AGT-1",
            "Require measurements", "", "No diff", "")
        {
            ResultsInventory = "No results/ folder present for this task.",
        };

        var prompt = service.BuildAspectPrompt(AspectRunnerService.Catalogue[aspect], inputs,
            "claude-opus-5");

        Assert.Contains("evidence_checked", prompt);
        Assert.Contains("results/", prompt);
        Assert.Contains("truncated excerpt", prompt);
        Assert.Contains("missing item", prompt);
    }
}
