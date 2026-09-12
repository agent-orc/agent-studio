using Xunit;

namespace AgentStudio.Tests;

public sealed class BetterCandidateUsageReportServiceTests
{
    [Fact]
    public void Aggregate_EmitsOneProjectWeekLineAndStopsAtNextAdmissionBoundary()
    {
        var withCandidate = new DateTime(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc);
        var withoutCandidate = withCandidate.AddHours(2);
        var admissions = new List<OrchestratorLogEntry>
        {
            Boundary(withCandidate, CandidateNote()),
            Boundary(withoutCandidate, null),
        };
        var calls = new List<OrchestratorLogEntry>
        {
            Call(withCandidate.AddMinutes(30), "gpt-5.6-sol", 1_000, 200),
            Call(withCandidate.AddMinutes(45), "gpt-6-astra", 4_000, 500),
            Call(withoutCandidate.AddMinutes(30), "gpt-5.6-sol", 9_000, 800),
        };

        var line = Assert.Single(BetterCandidateUsageReportService.Aggregate(
            "Agent Studio",
            admissions,
            calls,
            withCandidate.Date,
            withCandidate.Date.AddDays(7)));

        Assert.Equal("Agent Studio", line.Project);
        Assert.Equal("2026-09-07", line.WeekStart);
        Assert.Equal("2026-09-14", line.WeekEnd);
        Assert.Equal(1, line.Calls);
        Assert.Equal(1_200, line.Tokens);
        Assert.True(line.CostUsd > 0);
        Assert.True(line.AllModelsPriced);
    }

    private static OrchestratorLogEntry Boundary(DateTime at, BetterCandidateNote? note) => new()
    {
        Ts = at,
        Topic = OrchestratorLogTopics.LoadDistribution,
        JobId = "AGT-2770",
        BetterCandidates = note,
    };

    private static OrchestratorLogEntry Call(DateTime at, string model, int input, int output) => new()
    {
        Ts = at,
        JobId = "AGT-2770",
        TokenUsage = new OrchestratorTokenUsage
        {
            Model = model,
            InputTokens = input,
            OutputTokens = output,
        },
    };

    private static BetterCandidateNote CandidateNote() => new()
    {
        CurrentModel = "gpt-5.6-sol",
        CapabilityClass = "CodingAgent",
        EvidenceSnapshot = "snapshot",
        EvaluatedAtUtc = new DateTime(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc),
        MatrixUrl = BetterCandidateService.MatrixUrl,
        Candidates =
        [
            new BetterCandidate
            {
                Model = "gpt-6-astra",
                BenchmarkType = "deepswe-v1.1",
                BenchmarkName = "DeepSWE v1.1",
                ScoreDelta = 1.1m,
                CostDeltaUsd = -4.96m,
                EvidenceAgeDays = 10,
            },
        ],
    };
}
