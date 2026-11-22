using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3971: the aspects of one review share their review context and diff byte
/// for byte, and it reaches Claude as the end of the system prompt, which is a
/// prompt-cache breakpoint; the aspect question goes last, on stdin.
/// </summary>
public sealed class ReviewAspectSharedPrefixTests : IDisposable
{
    private const string Marker = "<!-- remote-review-shared-material-v1 -->";
    private const string Material = "## Authoritative diff\n\n```diff\ndiff --git a/feature.txt b/feature.txt\n+added\n```\n";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agt3971-" + Guid.NewGuid().ToString("N"));

    public ReviewAspectSharedPrefixTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Authored(string aspect)
        => "# Review context\n\n## Task body\n\n```\nBrief\n```\n" + Marker + "\n\n# Aspect review: " + aspect
           + "\n\nJudge " + aspect + " only.\n";

    [Fact]
    public void Prompt_parts_run_shared_context_then_diff_then_the_aspect_question()
    {
        var results = new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" }
            .Select(aspect => (aspect, ReviewAspectInputBudget.Compose(Authored(aspect), Material, string.Empty)))
            .ToArray();

        var shared = results.Select(item => item.Item2.Prompt[..item.Item2.SharedPrefixLength]).ToArray();
        Assert.All(shared, prefix => Assert.Equal(shared[0], prefix));
        foreach (var (aspect, result) in results)
        {
            var prompt = result.Prompt;
            var brief = prompt.IndexOf("Brief", StringComparison.Ordinal);
            var diff = prompt.IndexOf("diff --git", StringComparison.Ordinal);
            var question = prompt.IndexOf("# Aspect review: " + aspect, StringComparison.Ordinal);
            Assert.True(brief >= 0 && brief < diff && diff < result.SharedPrefixLength
                        && result.SharedPrefixLength <= question);
            Assert.DoesNotContain(Marker, prompt);
            Assert.DoesNotContain("Aspect review", shared[0]);
        }
    }

    [Fact]
    public void Prompt_without_the_marker_has_no_shared_part()
        => Assert.Equal(0, ReviewAspectInputBudget.Compose("Grade the change.", Material, string.Empty).SharedPrefixLength);

    [Fact]
    public void Shared_part_goes_to_the_system_prompt_file_and_the_question_to_stdin()
    {
        var result = ReviewAspectInputBudget.Compose(Authored("code-quality"), Material, string.Empty);

        var launch = ClaudeSharedPrefixLaunch.Compose(result.Prompt, result.SharedPrefixLength, _directory);

        Assert.Equal(result.Prompt[..result.SharedPrefixLength], File.ReadAllText(launch.SharedContextPath));
        Assert.StartsWith("# Aspect review: code-quality", launch.Stdin);
        Assert.Equal(
            [ClaudeSharedPrefixLaunch.AppendSystemPromptFileFlag, launch.SharedContextPath,
                ClaudeSharedPrefixLaunch.ExcludeDynamicSectionsFlag],
            launch.Arguments);
    }

    [Theory]
    [InlineData("  --append-system-prompt <prompt>\n  --exclude-dynamic-system-prompt-sections\n", true)]
    [InlineData("  --append-system-prompt <prompt>\n", false)]
    [InlineData("", false)]
    public void Cli_support_needs_both_flags(string help, bool supported)
        => Assert.Equal(supported, ClaudeSharedPrefixLaunch.Supports(help));

    [Fact]
    public void First_usage_is_read_from_the_first_assistant_message()
    {
        const string assistant = """{"type":"assistant","message":{"usage":{"input_tokens":3,"output_tokens":1,"cache_read_input_tokens":10700,"cache_creation_input_tokens":52000}}}""";
        const string init = """{"type":"system","subtype":"init","usage":{"input_tokens":1}}""";

        Assert.False(ClaudeSharedPrefixLaunch.TryReadFirstUsage(init, "claude-opus-5-5", out _));
        Assert.False(ClaudeSharedPrefixLaunch.TryReadFirstUsage("not json \"usage\"", "claude-opus-5-5", out _));
        Assert.True(ClaudeSharedPrefixLaunch.TryReadFirstUsage(assistant, "claude-opus-5-5", out var usage));
        Assert.Equal(10_700, usage.CacheReadTokens);
        Assert.Equal(52_000, usage.CacheCreationTokens);
    }

    [Theory]
    [InlineData(new[] { "-p", "--output-format", "stream-json" }, new[] { "-p", "--x", "v", "--output-format", "stream-json" })]
    [InlineData(new[] { "--model", "m", "--", "rest" }, new[] { "--model", "m", "--x", "v", "--", "rest" })]
    [InlineData(new[] { "--model", "m" }, new[] { "--model", "m", "--x", "v" })]
    public void Extra_cli_options_go_after_the_print_flag_and_before_a_separator(string[] arguments, string[] expected)
    {
        var list = arguments.ToList();

        SignalRecordingCliSpawner.AddArguments(list, ["--x", "v"]);

        Assert.Equal(expected, list);
    }
}
