using Microsoft.Extensions.Logging.Abstractions;
using TokenEconomy;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BetterModelCandidateTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Find_ProjectsEveryRequiredNoteFieldAndCachesTheEvidenceSnapshot()
    {
        var service = CreateService();

        var first = service.Find("gpt-5.6-sol", "medium", "CodingAgent", Now);
        var second = service.Find("gpt-5.6-sol", "medium", "CodingAgent", Now.AddHours(1));

        var candidate = Assert.Single(first);
        Assert.Same(first, second);
        Assert.Equal("gpt-5.6-terra", candidate.Model);
        Assert.Equal("medium", candidate.Effort);
        Assert.Equal("studio-coding-v1", candidate.BenchmarkType);
        Assert.Equal(5m, candidate.ScoreDelta);
        Assert.True(candidate.CostDeltaUsd < 0);
        Assert.Equal(1, candidate.EvidenceAgeDays);
        Assert.Contains("evidence 1d old", candidate.Note);
        Assert.Equal(BetterModelCandidateService.MatrixUrl, candidate.MatrixUrl);
    }

    [Fact]
    public void ReadyProjection_AttachesTheSameNoteToCardAndQuotaWait()
    {
        var service = CreateService();
        var settings = new AgentStudio.Projects.ProjectSettingsService(
            NullLogger<AgentStudio.Projects.ProjectSettingsService>.Instance,
            new ConfigurationBuilder().Build());
        var task = new TaskInfo
        {
            Id = "task-1",
            ProjectName = "sample",
            State = TaskStates.Ready,
            Model = "gpt-5.6-sol",
            ThinkingLevel = "medium",
            QuotaWait = new QuotaWaitStatus("codex", Now, Now.AddMinutes(10), 15, "waiting"),
        };

        var projected = TaskEndpointHelpers.WithBetterCandidates(task, service, settings);

        Assert.Single(projected.BetterCandidates);
        Assert.Same(projected.BetterCandidates, projected.QuotaWait!.BetterCandidates);
    }

    [Fact]
    public void WeeklyReport_CountsOnlyUsageAfterABetterCandidateDecision()
    {
        var candidate = CreateService().Find("gpt-5.6-sol", "medium", "CodingAgent", Now).Single();
        var entries = new[]
        {
            Entry(Now.AddDays(-3), "task-1", 100, 10),
            Entry(Now.AddDays(-1), "task-1", 1_000, 100),
            Entry(Now.AddHours(-2), "task-2", 5_000, 500),
        };
        var decisions = new Dictionary<string, IReadOnlyList<BetterCandidateDecisionSnapshot>>
        {
            ["task-1"] =
            [
                new BetterCandidateDecisionSnapshot
                {
                    At = Now.AddDays(-2),
                    Model = "gpt-5.6-sol",
                    Effort = "medium",
                    Candidates = [candidate],
                },
            ],
        };

        var summary = ProjectTokenUsageService.BuildSummaryFromEntries(
            "sample",
            entries,
            new Dictionary<string, TaskInfo>(),
            Now,
            decisions);

        Assert.Equal(1, summary.Last7dBetterCandidateCalls);
        Assert.Equal(1_100, summary.Last7dBetterCandidateTokens);
        Assert.True(summary.Last7dBetterCandidateCostUsd > 0);
        Assert.True(summary.AllBetterCandidateModelsPriced);
    }

    private static BetterModelCandidateService CreateService()
    {
        var type = new BenchmarkType
        {
            Id = "studio-coding-v1",
            Version = "1",
            Name = "Studio coding",
            Publisher = "test",
            CapabilityClass = BenchmarkCapabilityClass.CodingAgent,
            Unit = "points",
            MinimumScore = 0,
            MaximumScore = 100,
            Direction = BenchmarkScoreDirection.HigherIsBetter,
            MethodologyUrl = "https://example.test/method",
            CitationNote = "test",
            ValidFrom = new DateOnly(2026, 9, 1),
            CapturedAt = new DateOnly(2026, 9, 11),
            RetrievedAt = new DateOnly(2026, 9, 11),
        };
        var results = new[]
        {
            Result("sol", "gpt-5.6-sol", 50),
            Result("terra", "gpt-5.6-terra", 55),
        };
        var evidence = new BenchmarkEvidenceCatalog([type], results, ModelPriceCatalog.Default, 90);
        return new BetterModelCandidateService(new ModelBenchmarkMatrix(evidence, ModelPriceCatalog.Default), evidence);
    }

    private static BenchmarkResult Result(string id, string model, decimal score) => new()
    {
        Id = id,
        BenchmarkTypeId = "studio-coding-v1",
        ModelId = ModelId.Of(model),
        ReasoningEffort = EffortLevel.Medium,
        Score = score,
        PublishedAt = new DateOnly(2026, 9, 11),
        RetrievedAt = new DateOnly(2026, 9, 11),
        SourceUrl = "https://example.test/result",
        RetrievalMethod = BenchmarkRetrievalMethod.Script,
        EvidenceExcerpt = id,
        Confidence = BenchmarkConfidence.OwnRun,
    };

    private static OrchestratorLogEntry Entry(DateTime at, string jobId, int input, int output) => new()
    {
        Ts = at,
        JobId = jobId,
        TokenUsage = new OrchestratorTokenUsage
        {
            Model = "gpt-5.6-sol",
            InputTokens = input,
            OutputTokens = output,
        },
    };
}
