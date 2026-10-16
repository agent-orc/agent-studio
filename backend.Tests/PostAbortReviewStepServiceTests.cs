using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct tests against <see cref="PostAbortReviewStepService"/>. The CLI is
/// stubbed so the suite runs offline. Invariants: (1) a parseable verdict
/// drives the decider's action and hangs the matching tag, (2) the input +
/// output contracts land under <c>contracts/</c> (the first ADR-0032
/// instance), (3) an unparseable / empty / throwing CLI fails closed to
/// human review rather than silently accepting, (4) the budget rule is
/// enforced end-to-end through the service, and (5) a report MD is always
/// written.
/// </summary>
public class PostAbortReviewStepServiceTests : IDisposable
{
    private readonly string _jobFolder;

    public PostAbortReviewStepServiceTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "post-abort-review-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
        File.WriteAllText(Path.Combine(_jobFolder, "task.json"), "{ \"id\": \"demo\", \"title\": \"Demo\" }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task RerunVerdict_WithBudget_DecidesRerun_HangsTag_WritesContracts()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult(
            "ng serve was alive when the watchdog fired.\n" +
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; confidence=0.85; reason=dev server still serving]]\n[[TASK_DONE]]"));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 2), CancellationToken.None);

        Assert.NotNull(report.Verdict);
        Assert.Equal(PostAbortRecommendation.Rerun, report.Verdict!.Recommendation);
        Assert.Equal(PostAbortAction.Rerun, report.Action);
        Assert.Equal("abort-review:rerun", report.TagId);
        Assert.Contains("abort-review:rerun", ReadTags());
        Assert.True(File.Exists(report.FilePath));

        var input = ReadContract(PostAbortReviewStepService.InputContractName);
        Assert.Equal("watchdog timeout after 120s of silence", input.GetProperty("abortReason").GetString());
        Assert.Equal("ToolExecuting", input.GetProperty("abortPhase").GetString());

        var output = ReadContract(PostAbortReviewStepService.OutputContractName);
        Assert.True(output.GetProperty("parsed").GetBoolean());
        Assert.Equal("rerun", output.GetProperty("recommendation").GetString());
        Assert.Equal("rerun", output.GetProperty("action").GetString());
        Assert.Equal(0.85, output.GetProperty("confidence").GetDouble(), 3);
    }

    [Fact]
    public async Task RerunVerdict_BudgetExhausted_FailsClosedToHuman()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult(
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; confidence=0.9; reason=x]]"));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 0), CancellationToken.None);

        Assert.Equal(PostAbortRecommendation.Rerun, report.Verdict!.Recommendation);
        Assert.Equal(PostAbortAction.EscalateHuman, report.Action);
        Assert.Equal("abort-review:human-review", report.TagId);

        var output = ReadContract(PostAbortReviewStepService.OutputContractName);
        Assert.Equal("human-review", output.GetProperty("action").GetString());
    }

    [Fact]
    public async Task HumanReviewVerdict_Escalates_EvenWithBudget()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult(
            "[[ABORT_REVIEW: legitimate=true; recommendation=human-review; confidence=0.8; reason=real dead end]]"));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 5), CancellationToken.None);

        Assert.Equal(PostAbortAction.EscalateHuman, report.Action);
        Assert.Contains("abort-review:human-review", ReadTags());
    }

    [Fact]
    public async Task AcceptVerdict_DecidesAccept_RegardlessOfBudget()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult(
            "[[ABORT_REVIEW: legitimate=false; recommendation=accept; confidence=0.7; reason=work landed]]"));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 0), CancellationToken.None);

        Assert.Equal(PostAbortAction.AcceptAndContinue, report.Action);
        Assert.Equal("abort-review:accept", report.TagId);
    }

    [Fact]
    public async Task UnparseableReply_FailsClosedToHuman_ContractMarksUnparsed()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult("I have no idea what happened here."));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 5), CancellationToken.None);

        Assert.Null(report.Verdict);
        Assert.Equal(PostAbortAction.EscalateHuman, report.Action);
        Assert.True(File.Exists(report.FilePath));

        var output = ReadContract(PostAbortReviewStepService.OutputContractName);
        Assert.False(output.GetProperty("parsed").GetBoolean());
        Assert.Equal("human-review", output.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, output.GetProperty("recommendation").ValueKind);
    }

    [Fact]
    public async Task CliThrows_FailsClosedToHuman_ReportAndContractsStillProduced()
    {
        var service = BuildService((_, _, _, _, _) => throw new InvalidOperationException("CLI exploded"));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 5), CancellationToken.None);

        Assert.Null(report.Verdict);
        Assert.Equal(PostAbortAction.EscalateHuman, report.Action);
        Assert.True(File.Exists(report.FilePath));
        Assert.True(File.Exists(Path.Combine(_jobFolder, PostAbortReviewStepService.ContractsDirName, PostAbortReviewStepService.OutputContractName)));
        Assert.Contains("abort-review:human-review", ReadTags());
    }

    [Fact]
    public async Task ReportMd_CarriesFrontmatterVerdictAndEvidence()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult(
            "[[ABORT_REVIEW: legitimate=false; recommendation=stronger-reissue; confidence=0.6; reason=agent looped]]"));

        var report = await service.RunAsync(BuildRequest(rerunBudget: 2), CancellationToken.None);

        Assert.Equal(PostAbortAction.RerunWithStrongerFraming, report.Action);
        var content = File.ReadAllText(report.FilePath);
        Assert.Contains("type: post-abort-review", content);
        Assert.Contains("recommendation: stronger-reissue", content);
        Assert.Contains("action: rerun-stronger", content);
        Assert.Contains("tag: abort-review:rerun-stronger", content);
        Assert.Contains("agent looped", content);
    }

    [Fact]
    public async Task Secret_and_oversize_evidence_are_omitted_before_the_prompt_and_contract()
    {
        string? prompt = null;
        var service = BuildService((_, _, text, _, _) =>
        {
            prompt = text;
            return Task.FromResult("[[ABORT_REVIEW: legitimate=true; recommendation=human-review; confidence=0.8; reason=unclear]]");
        });
        var secret = "api_key=" + new string('q', 24);
        var request = BuildRequest(1) with
        {
            TaskBody = new string('x', 10_000),
            CliOutputTail = "started " + secret,
            ToolCallsLiveness = "/private/sessions/raw.log",
        };

        await service.RunAsync(request, CancellationToken.None);

        Assert.NotNull(prompt);
        Assert.DoesNotContain(secret, prompt);
        Assert.DoesNotContain("/private/sessions", prompt);
        Assert.Contains("[omitted: secret]", prompt);
        var input = ReadContract(PostAbortReviewStepService.InputContractName);
        var omissions = input.GetProperty("omissions").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("taskBody:oversize", omissions);
        Assert.Contains("cliOutputTail:secret", omissions);
        Assert.Contains("toolCallsLiveness:raw-path", omissions);
    }

    [Fact]
    public void StaticTokenMappers_AreExhaustive()
    {
        Assert.Equal("abort-review:rerun", PostAbortReviewStepService.TagFor(PostAbortAction.Rerun));
        Assert.Equal("abort-review:rerun-stronger", PostAbortReviewStepService.TagFor(PostAbortAction.RerunWithStrongerFraming));
        Assert.Equal("abort-review:accept", PostAbortReviewStepService.TagFor(PostAbortAction.AcceptAndContinue));
        Assert.Equal("abort-review:human-review", PostAbortReviewStepService.TagFor(PostAbortAction.EscalateHuman));
        Assert.Equal("rerun", PostAbortReviewStepService.ActionToken(PostAbortAction.Rerun));
        Assert.Equal("human-review", PostAbortReviewStepService.RecommendationToken(PostAbortRecommendation.HumanReview));
    }

    private PostAbortReviewRequest BuildRequest(int rerunBudget) => new(
        Project: "demo",
        JobId: "test-job",
        JobFolderPath: _jobFolder,
        TaskTitle: "Test job",
        TaskBody: "# Task\n\nStart the dev server and wait.",
        AbortReason: "watchdog timeout after 120s of silence",
        AbortPhase: "ToolExecuting",
        CliOutputTail: "> ng serve\n... compiled successfully\n** Angular Live Development Server is listening on localhost:4200 **",
        ToolCallsLiveness: "last tool call Bash(ng serve) started 4s before abort, never returned",
        GitState: "0 commits, working tree clean",
        TranscriptUsage: "in=1200 out=300 over 6 messages",
        CliType: "claude",
        Model: "claude-haiku-4-5")
    {
        RerunBudgetRemaining = rerunBudget,
        Timeout = TimeSpan.FromSeconds(5),
    };

    private PostAbortReviewStepService BuildService(Func<string, string, string, TimeSpan, CancellationToken, Task<string>> stub)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var service = new PostAbortReviewStepService(prompts, NullLogger<PostAbortReviewStepService>.Instance);
        service.CliRunner = stub;
        return service;
    }

    private JsonElement ReadContract(string name)
    {
        var path = Path.Combine(_jobFolder, PostAbortReviewStepService.ContractsDirName, name);
        Assert.True(File.Exists(path), $"contract {name} must exist at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private List<string> ReadTags()
    {
        var jobJsonPath = Path.Combine(_jobFolder, "task.json");
        if (!File.Exists(jobJsonPath)) return new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jobJsonPath));
        if (!doc.RootElement.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var list = new List<string>();
        foreach (var t in tagsEl.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}
