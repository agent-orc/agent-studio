using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PushProtectionTests
{
    // The AGT-2975 task artifact is unavailable in this checkout. This is the
    // GitHub stderr shape reported in the incident, with its fake fixture omitted.
    private const string IncidentStderr = """
        remote: error: GH013: Repository rule violations found for refs/heads/agent-studio/quarantine/runner-test/AGT-2975.
        remote: - GITHUB PUSH PROTECTION
        remote:   Push cannot contain secrets
        remote:   -- Mailgun API Key --
        remote:    locations:
        remote:      - commit: 4461bb2abc123456789012345678901234567890
        remote:        path: runner.Tests/LocalRepositoryKeyHostTests.cs:76
        """;

    [Fact]
    public void Parses_GH013_provider_commit_and_location()
    {
        var cause = PushProtection.ParseGitHubRejection(IncidentStderr);
        Assert.NotNull(cause);
        Assert.Equal("Mailgun API Key", cause.SecretType);
        Assert.Equal("4461bb2abc123456789012345678901234567890", cause.Commit);
        Assert.Equal("runner.Tests/LocalRepositoryKeyHostTests.cs", cause.Path);
        Assert.Equal(76, cause.Line);
    }

    [Fact]
    public void Rejection_reason_and_gate_include_typed_cause_and_recipe()
    {
        var exception = new WorktreeSalvageException(
            "/runner/worktrees/AGT-2975", "runner/host/AGT-2975",
            new InvalidOperationException(IncidentStderr),
            "4461bb2abc123456789012345678901234567890", "0123456789abcdef");

        Assert.Equal(
            "GitHub push protection: Mailgun API Key-like literal at runner.Tests/LocalRepositoryKeyHostTests.cs:76 in commit 4461bb2",
            RemoteTaskRunner.BuildUnsecuredWorktreeReason(exception));
        var gate = RemoteTaskRunner.BuildUnsecuredWorktreeGate("agent-runner-01", exception);
        Assert.Contains("Mailgun API Key-like literal", gate);
        Assert.Contains("host=agent-runner-01", gate);
        Assert.Contains("commit=4461bb2", gate);
        Assert.Contains("path=runner.Tests/LocalRepositoryKeyHostTests.cs:76", gate);
        Assert.Contains("git commit --amend", gate);
    }

    [Fact]
    public void Staged_diff_scans_added_lines_and_reports_new_line_number()
    {
        var fake = "key-" + new string('a', 32);
        var diff = "diff --git a/fixture.cs b/fixture.cs\n" +
                   "--- a/fixture.cs\n+++ b/fixture.cs\n@@ -75,0 +76,2 @@\n" +
                   $"+var value = \"{fake}\";\n+safe\n";
        var cause = PushProtection.ScanAddedLines(diff);
        Assert.Equal(new PushProtectionCause("Mailgun API Key", null, "fixture.cs", 76), cause);
        Assert.Null(PushProtection.ScanAddedLines(
            "--- a/fixture.cs\n+++ b/fixture.cs\n@@ -76 +76 @@\n-" + fake + "\n+var value = \"key-\" + new string('a', 32);\n"));
    }

    [Fact]
    public void Staged_scan_covers_common_provider_shapes()
    {
        var examples = new (string Name, string Value)[]
        {
            ("AWS Access Key ID", "AKIA" + new string('A', 16)),
            ("GitHub Personal Access Token", "ghp_" + new string('A', 36)),
            ("Stripe Secret Key", "sk_live_" + new string('A', 24)),
            ("Slack Bot Token", "xoxb-" + new string('1', 10) + "-" + new string('2', 10) + "-abcdef"),
        };
        foreach (var (name, value) in examples)
        {
            var diff = $"+++ b/fixture.txt\n@@ -0,0 +1 @@\n+{value}\n";
            Assert.Equal(name, PushProtection.ScanAddedLines(diff)?.SecretType);
        }
    }
}
