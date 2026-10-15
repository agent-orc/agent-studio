using System.Security.Cryptography;
using System.Text;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewDecisionEvidenceReaderTests
{
    [Fact]
    public void Reader_returns_typed_signal_digest_and_redacted_prompt_window()
    {
        var path = Path.GetTempFileName();
        try
        {
            var secret = "api_key=" + new string('x', 24);
            var sessionPath = "/tmp/sessions/run/cli-output.log";
            var source = "[agent] first\n[agent] [[TASK_NEEDS_INPUT: choose a path]]\n[agent] "
                + secret + " " + sessionPath;
            File.WriteAllText(path, source);
            var read = ReviewDecisionEvidenceReader.Read(path);
            Assert.Equal("needs-input", read.Signal?.Kind);
            Assert.Equal("choose a path", read.Signal?.Reason);
            Assert.Equal("sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(source))), read.SourceDigest);
            Assert.DoesNotContain(secret, read.PromptWindow);
            Assert.DoesNotContain(sessionPath, read.PromptWindow);
            Assert.Contains("secret", read.Omissions);
            Assert.Contains("raw-session-path", read.Omissions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Oversize_source_holds_instead_of_guessing_from_a_tail()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('a', ReviewDecisionEvidenceReader.SourceWindowBytes)
                + "\n[agent] [[TASK_DONE]]\n");
            var read = ReviewDecisionEvidenceReader.Read(path);
            Assert.Null(read.Signal);
            Assert.Contains("source-window-truncated", read.Omissions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Captured_source_digest_remains_stable_after_log_rotation()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[agent] [[TASK_DONE]]\n");
            var captured = ReviewDecisionEvidenceReader.Read(path);
            File.WriteAllText(path, "[agent] replacement\n");

            Assert.Equal("done", captured.Signal?.Kind);
            Assert.NotEqual(captured.SourceDigest, ReviewDecisionEvidenceReader.Read(path).SourceDigest);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Recovery_progress_is_typed_and_oversize_source_cannot_drive_a_choice()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[orchestrator] [reissue] NOOP recovery\n"
                + "{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\"}}\n"
                + "[[TASK_NOOP]]\n");
            var progress = ReviewDecisionEvidenceReader.ReadRecoveryProgress(path, 3);
            Assert.True(progress.SawNoOpRecoveryReissue);
            Assert.Equal(1, progress.FileChanges);

            File.WriteAllText(path, new string('x', ReviewDecisionEvidenceReader.SourceWindowBytes)
                + "\n[[TASK_NOOP]]\n");
            Assert.Equal(NoOpProgressEvidence.None,
                ReviewDecisionEvidenceReader.ReadRecoveryProgress(path, 2));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Omission_reason_survives_when_a_secret_signal_is_outside_the_prompt_window()
    {
        var path = Path.GetTempFileName();
        try
        {
            var secret = "api_key=" + new string('x', 24);
            File.WriteAllText(path, "[agent] [[TASK_NEEDS_INPUT: " + secret + "]]\n"
                + string.Concat(Enumerable.Repeat("ordinary line\n", ReviewDecisionEvidenceReader.PromptLines + 1)));

            var read = ReviewDecisionEvidenceReader.Read(path);

            Assert.Equal("needs-input", read.Signal?.Kind);
            Assert.Contains("[omitted: secret]", read.Signal?.Reason);
            Assert.DoesNotContain(secret, read.Signal?.Reason);
            Assert.Contains("secret", read.Omissions);
        }
        finally { File.Delete(path); }
    }
}
