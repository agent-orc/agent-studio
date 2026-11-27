using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewAcceptanceScopeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "review-brief-" + Guid.NewGuid().ToString("N"));

    public ReviewAcceptanceScopeTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_brief_preserves_opening_authority_tail_and_raw_source_identity(bool utf16)
    {
        var credential = "ghp_" + new string('q', 24);
        var body = "Partial delivery is success. Do not change orchestration ownership.\n" + credential + "\n"
            + string.Concat(Enumerable.Repeat("😀 middle details\n", 8_000)) + "FINAL-ACCEPTANCE-CHECK";
        var path = Path.Combine(_folder, "prompt.md");
        File.WriteAllText(path, body, utf16 ? Encoding.Unicode : new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(path);
        var brief = RemoteReviewPlanBuilder.ReviewBrief.Read(path, "fallback");
        var prompt = RequirementPrompt();

        Assert.True(brief.Text.Length <= 64_000);
        Assert.Contains("Partial delivery is success", brief.Text);
        Assert.Contains("Do not change orchestration ownership", brief.Text);
        Assert.Contains("FINAL-ACCEPTANCE-CHECK", brief.Text);
        Assert.Contains("brief middle omitted", brief.Text);
        Assert.Contains($"source bytes={bytes.Length}", brief.Text);
        Assert.Contains($"source UTF-16 units={body.Length}", brief.Text);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(bytes)), brief.Text);
        Assert.Contains("[REDACTED_CREDENTIAL]", brief.Text);
        Assert.DoesNotContain(credential, prompt);
        Assert.DoesNotContain("�", brief.Text);
        Assert.Contains("Acceptance mode: bounded slice", prompt);
        Assert.Contains("Do not change orchestration ownership", prompt);
        Assert.Contains("brief middle omitted", prompt);
    }

    [Fact]
    public void Legacy_declaration_in_omitted_middle_is_inferred_before_selection()
    {
        // The declaration is entirely outside the retained head and tail.
        var body = new string('a', 81_910) + "one slice per delivery" + new string('z', 90_000);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), body);
        var brief = RemoteReviewPlanBuilder.ReviewBrief.Read(Path.Combine(_folder, "prompt.md"), "fallback");
        Assert.DoesNotContain("one slice per delivery", brief.Text);
        Assert.True(brief.DeclaresBoundedLegacyScope);
        Assert.Contains("Acceptance mode: bounded slice", RequirementPrompt());
    }

    [Fact]
    public void Structured_full_task_scope_remains_authoritative_over_a_legacy_declaration()
    {
        Assert.Contains("Acceptance mode: full task", RequirementAcceptanceScope.Describe(
            new TaskAcceptanceScope { DeliveryMode = TaskAcceptanceDeliveryModes.FullTask }, "one slice per delivery"));
    }

    [Fact]
    public void Small_and_missing_briefs_keep_their_content_or_fallback()
    {
        var path = Path.Combine(_folder, "prompt.md");
        Assert.Equal("fallback", RemoteReviewPlanBuilder.ReviewBrief.Read(path, "fallback").Text);
        File.WriteAllText(path, "Implement the approved change. 😀");
        var brief = RemoteReviewPlanBuilder.ReviewBrief.Read(path, "fallback");
        Assert.Equal("Implement the approved change. 😀", brief.Text);
        Assert.False(brief.DeclaresBoundedLegacyScope);
        Assert.Contains("Acceptance mode: full task", RequirementPrompt());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redaction_expansion_and_surrogate_boundaries_stay_within_the_brief_budget(bool utf16)
    {
        var body = string.Concat(Enumerable.Repeat("password=x 😀\n", 4_000));
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), body,
            utf16 ? Encoding.Unicode : new UTF8Encoding(false));
        Assert.True(body.Length < 64_000);
        var brief = RemoteReviewPlanBuilder.ReviewBrief.Read(Path.Combine(_folder, "prompt.md"), "fallback");
        Assert.True(brief.Text.Length <= 64_000);
        Assert.DoesNotContain("password=x", brief.Text);
        Assert.DoesNotContain("�", brief.Text);
        Assert.Contains("brief middle omitted", brief.Text);
        for (var i = 0; i < brief.Text.Length; i++)
        {
            if (char.IsHighSurrogate(brief.Text[i])) Assert.True(++i < brief.Text.Length && char.IsLowSurrogate(brief.Text[i]));
            else Assert.False(char.IsLowSurrogate(brief.Text[i]));
        }
    }

    [Fact]
    public void Source_over_the_byte_limit_refuses_a_misleading_frozen_plan()
    {
        using (var file = File.Create(Path.Combine(_folder, "prompt.md")))
            file.SetLength(RemoteReviewPlanBuilder.ReviewBrief.SourceBytes + 1L);
        var error = Assert.Throws<InvalidDataException>(() => RequirementPrompt());
        Assert.Contains("complete-source limit", error.Message);
        Assert.Contains("prompt.md", error.Message);
    }

    [Fact]
    public void Omission_count_accounts_for_every_unselected_redacted_character()
    {
        var body = "HEAD" + new string('x', 100_000) + "TAIL";
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), body);
        var brief = RemoteReviewPlanBuilder.ReviewBrief.Read(Path.Combine(_folder, "prompt.md"), "fallback");
        var start = brief.Text.IndexOf("\n[brief middle omitted:", StringComparison.Ordinal);
        var end = brief.Text.IndexOf("]\n", start, StringComparison.Ordinal) + 2;
        var count = System.Text.RegularExpressions.Regex.Match(brief.Text, @"brief middle omitted: (\d+)");
        Assert.Equal(body.Length, brief.Text.Length - (end - start) + int.Parse(count.Groups[1].Value));
    }

    private string RequirementPrompt()
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var plan = new RemoteReviewPlanBuilder(aspects, config).Build(new TaskInfo
        {
            Id = "AGT-4092", Title = "Review evidence", Mode = TaskModes.Coding, FolderPath = _folder, ProjectName = "Test",
        }, null, null, "refs/heads/main");
        return Assert.Single(plan.Commands, command => command.Aspect == "requirement-fit").Prompt;
    }
}
