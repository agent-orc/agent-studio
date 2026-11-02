using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: the dates are literal log text in command output fixtures and never meet "now".

public sealed class ReviewCommandExitDiagnosisTests
{
    [Theory]
    [InlineData(0, 1, 1, true)]
    [InlineData(0, 1, 2, true)]
    [InlineData(1, 1, 1, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 127, 127, false)]
    [InlineData(0, 1, 127, false)]
    [InlineData(0, 124, 124, false)]
    [InlineData(0, 137, 137, false)]
    [InlineData(0, 139, 139, false)]
    public void Only_repeated_candidate_command_failures_on_a_green_baseline_charge_the_card(
        int baseline, int candidate, int repeat, bool expected)
        => Assert.Equal(expected,
            ReviewCommandExitDiagnosis.Reproduced(baseline, candidate, repeat));

    [Fact]
    public void A_timed_out_repeat_does_not_prove_a_product_failure()
        => Assert.False(ReviewCommandExitDiagnosis.Reproduced(0, 1, 1, repeatSignal: "timeout"));

    [Fact]
    public void Evidence_is_bounded_and_scrubs_secret_shaped_values()
    {
        var credential = "token=" + new string('s', 50);
        var evidence = ReviewCommandExitDiagnosis.Evidence(
            "npm --prefix frontend run build", new string('a', 40), new string('b', 40),
            0, 1, 1, new string('x', 4000) + "\n" + credential + "\nerror: compile failed", "");

        Assert.Contains("candidate-output-tail:", evidence);
        Assert.Contains("error: compile failed", evidence);
        Assert.Contains("[redacted]", evidence);
        Assert.DoesNotContain(credential, evidence);
        Assert.True(evidence.Length < 2400);
    }

    [Theory]
    [InlineData("NPM_TOKEN=abc", "abc")]
    [InlineData("DB_PASSWORD=def", "def")]
    [InlineData("AWS_SECRET_ACCESS_KEY=ghi", "ghi")]
    [InlineData("{\"password\": \"jkl\"}", "jkl")]
    [InlineData("Authorization: Basic bW5v", "bW5v")]
    [InlineData("https://user:pass@example.invalid/path", "pass")]
    [InlineData("{\"Authorization\": \"Bearer abcdefghi\"}", "abcdefghi")]
    [InlineData("https://abcdefghi@example.invalid/path", "abcdefghi")]
    [InlineData("ghp_abcdefghij", "ghp_abcdefghij")]
    [InlineData("ghs_abcdefghij", "ghs_abcdefghij")]
    [InlineData("gho_abcdefghij", "gho_abcdefghij")]
    [InlineData("ghu_abcdefghij", "ghu_abcdefghij")]
    [InlineData("ghr_abcdefghij", "ghr_abcdefghij")]
    [InlineData("Authorization: token abcdefghi", "abcdefghi")]
    [InlineData("github_pat_abcdefghij", "github_pat_abcdefghij")]
    [InlineData("glpat-abcdefghij", "glpat-abcdefghij")]
    [InlineData("xoxb-123456-abcdef", "xoxb-123456-abcdef")]
    public void Evidence_scrubs_common_credential_forms(string output, string secret)
    {
        var evidence = ReviewCommandExitDiagnosis.Evidence("build", "base", "candidate", 0, 1, 1, output, "");
        Assert.Contains("[redacted]", evidence);
        Assert.DoesNotContain(secret, evidence);
    }

    [Fact]
    public void Underscore_heavy_output_finishes_and_keeps_the_failure_tail()
    {
        var output = new string('_', 200) + "\n" +
            string.Join('_', Enumerable.Repeat("identifier", 31)) + "\nerror: failure";
        var evidence = ReviewCommandExitDiagnosis.Evidence("build", "base", "candidate", 0, 1, 1,
            output, "");
        Assert.Contains("error: failure", evidence);
        Assert.True(evidence.Length < 2400);
    }

    [Theory]
    [InlineData("a_")]
    [InlineData("token_")]
    public void Long_key_like_output_preserves_error(string fragment)
    {
        var output = string.Concat(Enumerable.Repeat(fragment, 16 * 1024 / fragment.Length))
                     + "\nerror: failed";
        var evidence = ReviewCommandExitDiagnosis.Evidence("build", "base", "candidate", 0, 1, 1, output, "");
        Assert.Contains("error: failed", evidence);
        Assert.DoesNotContain("[output withheld]", evidence);
    }

    [Fact]
    public void Evidence_keeps_long_test_names_and_git_shas_and_stdout_errors()
    {
        var testName = "Product.Tests." + new string('T', 60);
        var sha = new string('a', 40);
        var evidence = ReviewCommandExitDiagnosis.Evidence("build", sha, sha, 0, 1, 1,
            $"Failed {testName}\nerror CS1234: compile failed", new string('e', 4000));
        Assert.Contains(testName, evidence);
        Assert.Contains(sha, evidence);
        Assert.Contains("error CS1234: compile failed", evidence);
    }
    // AGT-3430 (Quality Studio, 2026-11-02): the baseline Karma run failed in
    // stdout while stderr started with npm's version notice.
    private const string KarmaStdout =
        "> quality-studio@1.0.0 test\n" +
        "> ng test --watch=false --browsers=ChromeHeadless\n" +
        "02 11 2026 09:14:07.311:INFO [karma-server]: Karma v6.4.4 server started at http://localhost:9876/\n" +
        "02 11 2026 09:14:07.840:FATAL [karma-server]: Socket path too long: /review/attempt/baseline-runtime-0123456789ab/tmp/karma-1/socket\n" +
        "02 11 2026 09:14:09.902:ERROR [launcher]: ChromeHeadless failed 2 times (cannot start). Giving up.\n";

    private const string NpmNoticeStderr =
        "npm notice\n" +
        "npm notice New minor version of npm available! 10.8.2 -> 10.9.0\n" +
        "npm notice To update run: npm install -g npm@10.9.0\n" +
        "npm notice\n";

    [Fact]
    public void Failure_text_quotes_the_stdout_cause_and_skips_npm_notice()
    {
        var text = ReviewCommandExitDiagnosis.FailureText(KarmaStdout, NpmNoticeStderr);

        Assert.NotNull(text);
        Assert.Contains("FATAL [karma-server]: Socket path too long", text, StringComparison.Ordinal);
        Assert.Contains("ChromeHeadless failed 2 times (cannot start)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("npm notice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("server started", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrun_baseline_summary_names_the_cause_instead_of_the_npm_notice()
    {
        var command = new ReviewCommandDto("verify-4", "build-tests", "sh", ["-c", "npm test"]);

        var summary = RemoteReviewWorkspace.UnrunBaselineSummary(
            command, new ProcessResult(1, KarmaStdout, NpmNoticeStderr));

        Assert.StartsWith(
            "baseline verify-4 exited 1 without reporting a test result: ", summary, StringComparison.Ordinal);
        Assert.Contains("Socket path too long", summary, StringComparison.Ordinal);
        Assert.Contains("ChromeHeadless failed 2 times (cannot start)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("npm notice", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_text_puts_npm_wrapper_lines_behind_the_real_cause()
    {
        var text = ReviewCommandExitDiagnosis.FailureText(
            "Build at: 2026-11-02\nError: Cannot find module 'karma-chrome-launcher'\n",
            "npm warn deprecated inflight@1.0.6\nnpm error code 1\nnpm error command failed\n");

        Assert.NotNull(text);
        Assert.StartsWith("Error: Cannot find module 'karma-chrome-launcher'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("npm warn", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_text_ignores_zero_counts_and_falls_back_to_the_first_stderr_line()
    {
        var text = ReviewCommandExitDiagnosis.FailureText(
            "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n",
            "npm notice\nThe argument /review/Fixture.dll is invalid.\n");

        Assert.Equal("The argument /review/Fixture.dll is invalid.", text);
        Assert.Null(ReviewCommandExitDiagnosis.FailureText("", NpmNoticeStderr));
    }

    [Fact]
    public void Failure_text_is_bounded_and_redacted()
    {
        var credential = "token=" + new string('s', 50);
        var stdout = string.Join("\n", Enumerable.Range(1, 20).Select(index =>
            $"error {index}: {new string('x', 400)}")) + $"\nerror: login failed with {credential}";

        var text = ReviewCommandExitDiagnosis.FailureText(stdout, "");

        Assert.NotNull(text);
        Assert.Equal(3, text.Split(" | ").Length);
        Assert.True(text.Length <= 3 * 243 + 2 * 3, $"length={text.Length}");
        Assert.StartsWith("error 1:", text, StringComparison.Ordinal);

        var redacted = ReviewCommandExitDiagnosis.FailureText($"error: login failed with {credential}", "");
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_tail_keeps_the_end_of_each_stream_within_its_limit()
    {
        const int limit = ReviewCommandOutputTailDto.MaximumCharacters;
        var stdout = new string('x', limit * 3) + "\nFATAL: Socket path too long";

        var tail = ReviewCommandExitDiagnosis.OutputTail(stdout, NpmNoticeStderr);

        Assert.Equal(limit, tail.Stdout.Length);
        Assert.True(tail.StdoutTruncated);
        Assert.EndsWith("FATAL: Socket path too long", tail.Stdout, StringComparison.Ordinal);
        Assert.Equal(NpmNoticeStderr, tail.Stderr);
        Assert.False(tail.StderrTruncated);
    }

    [Fact]
    public void Output_tail_redacts_secrets_like_other_report_text()
    {
        var credential = "NPM_TOKEN=" + new string('s', 50);

        var tail = ReviewCommandExitDiagnosis.OutputTail($"publishing\n{credential}\nerror: failed", credential);

        Assert.DoesNotContain(credential, tail.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, tail.Stderr, StringComparison.Ordinal);
        Assert.Contains("[redacted]", tail.Stdout, StringComparison.Ordinal);
        Assert.Contains("error: failed", tail.Stdout, StringComparison.Ordinal);
    }
}
