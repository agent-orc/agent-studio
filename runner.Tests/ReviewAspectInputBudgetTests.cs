using System.Text;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewAspectInputBudgetTests
{
    [Theory]
    [InlineData("visual-review")]
    [InlineData("code-quality")]
    public void Ag_t_3403_sized_delivery_fits_and_names_omitted_diff(string aspect)
    {
        var material = new StringBuilder("## Authoritative delivery review material\n### Changed-file list\n");
        material.AppendLine("- `frontend/src/app/status.component.html`");
        material.AppendLine("- `frontend/src/app/status.component.scss`");
        material.AppendLine("- `frontend/src/app/status.component.ts`");
        material.AppendLine("### Unified diff\n```diff");
        foreach (var (name, size) in new[]
                 {
                     ("frontend/src/app/status.component.ts", 500_000),
                     ("frontend/src/app/status.component.html", 300_000),
                     ("frontend/src/app/status.component.scss", 250_000),
                 })
        {
            material.Append("diff --git a/").Append(name).Append(" b/").AppendLine(name);
            material.Append('+').Append('x', size).AppendLine();
        }
        material.AppendLine("```");
        var original = "Review the delivery: " + new string('p', 21_000);
        Assert.True(original.Length + material.Length > 1_071_145);

        var result = ReviewAspectInputBudget.Compose(original, material.ToString(),
            aspect == "visual-review" ? "\n## Delivered screenshot files\n- after.png: /tmp/after.png\n" : "");

        Assert.True(result.Prompt.Length <= ReviewAspectInputBudget.MaximumCharacters);
        Assert.Contains("status.component.html", result.Prompt);
        Assert.Contains("status.component.scss", result.Prompt);
        Assert.Contains("status.component.ts", result.Report);
        Assert.Contains("diff characters omitted", result.Report);
        Assert.Contains("Review input omissions", result.Prompt);
    }

    [Theory]
    [InlineData("input_too_large", ReviewCliRefusalPolicy.InputTooLarge)]
    [InlineData("error: unexpected argument '--foo'", ReviewCliRefusalPolicy.ArgumentRejected)]
    [InlineData("turn/start failed: refused", ReviewCliRefusalPolicy.TurnRefused)]
    public void Pre_model_cli_refusals_have_review_infrastructure_causes(string error, string expected)
    {
        var command = new ReviewCommandDto("aspect-visual-review", "visual-review", "codex", [],
            ExecutionKind: ReviewCommandKinds.AgentAspect, Prompt: "Review", CliType: "codex");
        Assert.Equal(expected, ReviewCliRefusalPolicy.Classify(command,
            new ProcessResult(1, "", error)));
    }

    [Fact]
    public void Verdict_usage_and_model_reply_text_are_not_cli_refusals()
    {
        var command = new ReviewCommandDto("aspect-code-quality", "code-quality", "codex", [],
            ExecutionKind: ReviewCommandKinds.AgentAspect, Prompt: "Review", CliType: "codex");
        Assert.Null(ReviewCliRefusalPolicy.Classify(command,
            new ProcessResult(1, "[[ASPECT_VERDICT aspect=code-quality verdict=pass]]", "input_too_large")));
        Assert.Null(ReviewCliRefusalPolicy.Classify(command,
            new ProcessResult(1, "", "turn/start failed"), hasUsage: true));
        Assert.Null(ReviewCliRefusalPolicy.Classify(command,
            new ProcessResult(1, "input_too_large", "")));
    }

    [Fact]
    public void Dossier_has_its_own_budget_and_keeps_its_truncation_marker()
    {
        var dossier = "\n## Dossier excerpt\n" + new string('d', 65_000)
            + "\n[[DOSSIER_EXCERPT_TRUNCATED: shown 65000 of 80000 characters]]";
        var result = ReviewAspectInputBudget.Compose("Review", new string('m', 790_000), "", dossier, 36_000);
        Assert.True(result.Prompt.Length <= ReviewAspectInputBudget.MaximumCharacters);
        Assert.Contains("[[DOSSIER_EXCERPT_TRUNCATED:", result.Prompt);
        Assert.DoesNotContain("Screenshot section:", result.Report);
        Assert.Contains("Dossier excerpt:", result.Report);
        Assert.Contains(new string('d', 30_000), result.Prompt);
    }

    [Fact]
    public void Package_members_share_the_same_diff_budget_and_keep_each_members_file_list()
    {
        var material = new StringBuilder();
        for (var member = 1; member <= 4; member++)
        {
            var file = $"frontend/member-{member}.scss";
            material.Append("### Member ").Append(member).Append("\n- `").Append(file)
                .Append("`\n```diff\ndiff --git a/").Append(file).Append(" b/")
                .Append(file).Append("\n+").Append('x', 300_000).Append("\n```\n");
        }

        var result = ReviewAspectInputBudget.Compose("Package review", material.ToString(), "");

        Assert.True(result.Prompt.Length <= ReviewAspectInputBudget.MaximumCharacters);
        for (var member = 1; member <= 4; member++)
            Assert.Contains($"### Member {member}", result.Prompt);
        Assert.Contains("member-4.scss", result.Report);
        Assert.Equal(4, result.Prompt.Split("```diff", StringSplitOptions.None).Length - 1);
    }
}
