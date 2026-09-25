using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class MechanicalResumePolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Session = "11111111-2222-3333-4444-555555555555";

    private static MechanicalResumeCandidateDto Candidate() => new(
        "AGT-1", "attempt-1", Session, "codex", "AGT-1", "project-1",
        "https://example.test/repo.git", Path.GetFullPath("/tmp/AGT-1"),
        "runner/host/AGT-1", Sha, "refs/heads/develop", ["src/a.cs"],
        "Reconcile the new base.", "Run targeted checks.", Now.AddMinutes(-10), "fresh",
        "gpt-5.6-sol", "xhigh", "semantic-conflict-floor:sol-xhigh", "v1",
        "gpt-5.6-sol", "medium");

    private static MechanicalResumeDecision Decide(
        MechanicalResumeCandidateDto? candidate,
        string provider = "codex",
        string? repositoryId = "project-1",
        string? repositoryUrl = "https://example.test/repo.git",
        string? contextMode = "clean",
        string? baseSha = Sha,
        bool sessionExists = true)
        => MechanicalResumePolicy.Decide(candidate, "AGT-1", provider, contextMode,
            repositoryId, repositoryUrl, Path.GetFullPath("/tmp/AGT-1"),
            "runner/host/AGT-1", baseSha, Now, sessionExists);

    [Fact]
    public void Exact_lineage_allows_one_resumed_round_and_a_compact_delta()
    {
        var candidate = Candidate();
        Assert.True(Decide(candidate).Resume);
        var prompt = RemoteRunPrompt.BuildMechanicalDelta(candidate, Sha, "/tmp/results");
        Assert.Contains(Sha, prompt);
        Assert.Contains("src/a.cs", prompt);
        Assert.Contains("Run targeted checks", prompt);
        Assert.DoesNotContain("prompt.md", prompt);
        Assert.DoesNotContain("Consult `docs/start/contribution", prompt);
    }

    [Theory]
    [InlineData("provider", "provider-changed")]
    [InlineData("repository", "repository-mismatch")]
    [InlineData("base", "branch-lineage-mismatch")]
    [InlineData("missing", "missing-session")]
    [InlineData("repeated", "continuation-already-used")]
    [InlineData("stale", "stale-session")]
    [InlineData("path", "worktree-path-mismatch")]
    [InlineData("task", "task-key-mismatch")]
    [InlineData("clean", "clean-context-mismatch")]
    public void Identity_or_session_mismatch_is_a_typed_fresh_decision(string mutation, string reason)
    {
        var candidate = Candidate();
        var provider = "codex";
        var repositoryId = "project-1";
        var baseSha = Sha;
        var exists = true;
        switch (mutation)
        {
            case "provider": provider = "claude"; break;
            case "repository": repositoryId = "other"; break;
            case "base": baseSha = new string('b', 40); break;
            case "missing": exists = false; break;
            case "repeated": candidate = candidate with { PriorResumeDecision = "resumed" }; break;
            case "stale": candidate = candidate with { CapturedAtUtc = Now.AddDays(-8) }; break;
            case "path": candidate = candidate with { WorktreePath = Path.GetFullPath("/tmp/other") }; break;
            case "task": candidate = candidate with { TaskKey = "AGT-2" }; break;
            case "clean": candidate = candidate with { CleanContextKey = "AGT-2" }; break;
        }
        var decision = Decide(candidate, provider, repositoryId, baseSha: baseSha, sessionExists: exists);
        Assert.False(decision.Resume);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void Provider_token_summaries_keep_cache_and_turn_accounting_distinct()
    {
        var codex = MechanicalTokenUsage.Parse("""
            {"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":10,"cached_input_tokens":70}}
            {"type":"turn.completed","usage":{"input_tokens":50,"output_tokens":5,"cached_input_tokens":40}}
            """);
        Assert.Equal(165, codex.Snapshot().Total);
        Assert.Equal(110, codex.Snapshot().CacheRead);

        var claude = MechanicalTokenUsage.Parse("""
            {"type":"result","usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}
            """);
        Assert.Equal(135, claude.Snapshot().Total);
    }
}
