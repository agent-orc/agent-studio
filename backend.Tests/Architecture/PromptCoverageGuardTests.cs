using AgentStudio.Prompts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The prompt-coverage guard (T3b). Mirrors the SilentCatch precedent: a
/// deterministic detector plus a build-breaking arch-test. The standing rule
/// (2026-06-10 prompt-management review): no instruction text is composed inline
/// in product code - every prompt lives in the runtime template registry. This
/// guard breaks the build on the crudest violation of that rule, a whole
/// multi-line agent-instruction block pasted into a <c>.cs</c> file.
/// </summary>
public class PromptCoverageGuardTests
{
    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found above test base directory.");
    }

    /// <summary>
    /// The build-breaker. After T3a the product source tree must carry ZERO
    /// inline instruction blocks; a regression here fails the build with the
    /// exact file:line and snippet to move into a template.
    /// </summary>
    [Fact]
    public void ProductSource_HasNoInlinePromptBlocks()
    {
        var findings = PromptCoverageScanner.ScanProductSource(RepoRoot());

        Assert.True(findings.Count == 0,
            "Inline agent-instruction blocks must live in the runtime prompt template registry, "
            + "not in product .cs files (prompt-coverage guard, T3b). Move each block to a template "
            + "or, for a genuinely-non-prompt literal, add a 'prompt-coverage:allow' marker:\n  "
            + string.Join("\n  ", findings.Select(f => $"{f.File}:{f.Line}  [{f.Signal}]  {f.Snippet}")));
    }

    /// <summary>
    /// Proves the guard actually fires on a deliberate inline-prompt violation
    /// (the acceptance "Guard triggers on an intentional inline-prompt case").
    /// The fixture carries one verbatim and one raw multi-line instruction block.
    /// </summary>
    [Fact]
    public void Guard_FiresOnDeliberateInlinePromptFixture()
    {
        var fixture = Path.Combine(
            RepoRoot(), "backend.Tests", "Architecture", "Fixtures", "InlinePromptViolation.cs.fixture");
        Assert.True(File.Exists(fixture), $"missing guard fixture: {fixture}");

        var findings = PromptCoverageScanner.ScanText(
            "backend.Tests/Architecture/Fixtures/InlinePromptViolation.cs.fixture",
            File.ReadAllText(fixture));

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Signal)));
    }

    /// <summary>The escape hatch: a flagged block carrying the allow marker is skipped.</summary>
    [Fact]
    public void AllowMarker_SuppressesTheFinding()
    {
        const string body = """
            class C {
                // prompt-coverage:allow - this multi-line block is data, not a prompt
                string S = @"You are reading a fixture.
            Your task spans
            three or more lines.";
            }
            """;

        Assert.Empty(PromptCoverageScanner.ScanText("X.cs", body));
    }

    /// <summary>
    /// Calibration guard for the "0 findings after T3a" acceptance: the residual
    /// inline prompts left intentionally after T3a are assembled from many
    /// single-line concatenated fragments (CodeReviewStepService / AspectRunner
    /// shape). The heuristic targets the pasted multi-line BLOCK only, so a
    /// fragment-assembled prompt must NOT be flagged.
    /// </summary>
    [Fact]
    public void FragmentAssembledPrompt_IsNotFlagged()
    {
        const string body =
            """
            class C {
                string Build(string diff) =>
                    "## Diff\n\n" +
                    diff + "\n\n" +
                    "Reply with a short paragraph plus exactly one sentinel on its own line:\n\n" +
                    "[[ASPECT_VERDICT: status=<pass|concerns|block>]]\n";
            }
            """;

        Assert.Empty(PromptCoverageScanner.ScanText("X.cs", body));
    }

    /// <summary>A short (sub-three-line) instruction literal is out of scope on purpose.</summary>
    [Fact]
    public void ShortInstructionLiteral_IsNotFlagged()
    {
        const string body =
            """
            class C {
                string S = @"You are concise.
            Reply with one word.";
            }
            """;

        Assert.Empty(PromptCoverageScanner.ScanText("X.cs", body));
    }

    [Fact]
    public void SummaryProtocol_CoversEveryTaskLevelEvidencePlaceholder()
    {
        var path = Path.Combine(RepoRoot(), "prompts", "runtime", "summary-protocol.md");
        var template = File.ReadAllText(path);

        foreach (var slot in new[]
                 {
                     "taskTitle", "taskPrompt", "rounds", "agentStatus", "delivery", "log",
                 })
        {
            Assert.Contains("{{" + slot + "}}", template, StringComparison.Ordinal);
        }
    }
}
