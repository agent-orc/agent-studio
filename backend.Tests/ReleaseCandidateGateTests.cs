using AgentStudio.Deploy;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReleaseCandidateGateTests : IDisposable
{
    private static readonly DateTimeOffset Finished = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Candidate = "86afada0123456789abcdef0123456789abcdef0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "release-candidate-gate-" + Guid.NewGuid().ToString("N"));
    private static readonly ReleaseSuiteVerdict Green = new("Project", new('a', 40), new('b', 64), "green", Finished);

    [Theory]
    [InlineData("required", true, "green", true)]
    [InlineData("required", true, "red", false)]
    [InlineData("required", true, "missing", false)]
    [InlineData("required", false, "green", false)]
    [InlineData("optional", true, "missing", true)]
    [InlineData("optional", true, "red", false)]
    [InlineData("optional", false, "missing", false)]
    [InlineData("off", false, "missing", true)]
    public void Release_admission_follows_setting_post_merge_and_exact_verdict(
        string mode, bool candidateGreen, string verdictStatus, bool expected)
    {
        var verdict = verdictStatus == "missing" ? null : Green with { Status = verdictStatus };
        Assert.Equal(expected, ReleaseCandidateGatePolicy.Allows(mode, candidateGreen, verdict));
    }

    [Fact]
    public void Delivery_setting_parses_and_rejects_unknown_values()
    {
        var head = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), ".agent-studio", "project.yml"))
            + "\ndelivery:\n  fullSuite: ";
        var valid = ProjectDefinitionReader.Parse(head + "required\n");
        Assert.Equal("required", valid.Definition?.DeliveryFullSuite);
        var invalid = ProjectDefinitionReader.Parse(head + "sometimes\n");
        Assert.Contains(invalid.Issues, issue => issue.Code == "delivery-full-suite-invalid");
        Assert.Equal("required", ReleaseCandidateGatePolicy.Mode(null, productionDeclared: true));
        Assert.Equal("optional", ReleaseCandidateGatePolicy.Mode(null, productionDeclared: false));
        Assert.Equal("off", ReleaseCandidateGatePolicy.Mode("off", productionDeclared: true));
    }

    /// <summary>
    /// F2: a project outside post-merge mode, no post-merge run ever recorded,
    /// and only a green pre-main gate on the exact candidate. Each mode decides
    /// from that evidence alone.
    /// </summary>
    [Theory]
    [InlineData("required", "green", true)]
    [InlineData("required", "missing", false)]
    [InlineData("required", "red", false)]
    [InlineData("optional", "missing", true)]
    [InlineData("optional", "red", false)]
    [InlineData("off", "missing", true)]
    public void Pre_main_evidence_alone_makes_the_candidate_green_outside_post_merge_mode(
        string mode, string verdictStatus, bool expected)
    {
        var candidate = ReleaseCandidateGatePolicy.Candidate(mode, postMergeGreen: false, postMergeMode: false,
            postMergeHistory: false, () => ReleaseCandidateEvidenceSources.PreMainGate);
        var verdict = verdictStatus == "missing" ? null : Green with { Status = verdictStatus };
        var result = new ReleaseCandidateGateResult(mode, Candidate, "digest", verdict, false, null)
            { CandidateEvidence = candidate };

        Assert.Equal(mode == "off" ? null : ReleaseCandidateEvidenceSources.PreMainGate, candidate);
        Assert.True(result.CandidateGreen);
        Assert.Equal(expected, ReleaseCandidateGatePolicy.Allows(mode, result.CandidateGreen, verdict));
    }

    [Fact]
    public void Post_merge_mode_keeps_the_post_merge_run_as_the_only_proof()
    {
        var asked = false;
        Assert.Null(ReleaseCandidateGatePolicy.Candidate("required", postMergeGreen: false, postMergeMode: true,
            postMergeHistory: true, () => { asked = true; return ReleaseCandidateEvidenceSources.PreMainGate; }));
        Assert.False(asked);
        Assert.Equal(ReleaseCandidateEvidenceSources.PostMerge, ReleaseCandidateGatePolicy.Candidate(
            "required", postMergeGreen: true, postMergeMode: true, postMergeHistory: true, () => null));
        var refusal = ReleaseCandidateGatePolicy.NotGreenCandidate(postMergeMode: true);
        Assert.Equal("The candidate must be the green post-merge tip before release.", refusal.Error);
        Assert.Contains("post-merge run", refusal.NextStep);
    }

    [Theory]
    [InlineData("optional", false, ReleaseCandidateEvidenceSources.NoPostMergeHistory)]
    [InlineData("optional", true, null)]
    [InlineData("required", false, null)]
    public void Optional_admits_a_candidate_without_post_merge_history(
        string mode, bool postMergeHistory, string? expected)
        => Assert.Equal(expected, ReleaseCandidateGatePolicy.Candidate(mode, postMergeGreen: false,
            postMergeMode: false, postMergeHistory, () => null));

    [Fact]
    public void Refusals_name_the_next_step()
    {
        var notGreen = ReleaseCandidateGatePolicy.NotGreenCandidate(postMergeMode: false);
        Assert.Equal("The candidate has no green pre-main or batch gate on this exact commit.", notGreen.Error);
        Assert.Contains("delivery.fullSuite: off", notGreen.NextStep);
        var missing = ReleaseCandidateGatePolicy.SuiteRefusal(null);
        Assert.Equal("The candidate has no green full-suite verdict.", missing.Error);
        Assert.Contains("promotion-full-gate.sh", missing.NextStep);
        Assert.Equal("The candidate full suite is red.",
            ReleaseCandidateGatePolicy.SuiteRefusal(Green with { Status = "red" }).Error);
    }

    [Fact]
    public void Pre_main_receipt_counts_only_for_its_exact_sha_and_a_full_green_verdict()
    {
        var green = Path.Combine(_root, "job-green");
        var red = Path.Combine(_root, "job-red");
        var docs = Path.Combine(_root, "job-docs");
        Record(green, BuildTestGateVerdict.Ok, Candidate);
        Record(red, BuildTestGateVerdict.Fail, Candidate);
        Record(docs, BuildTestGateVerdict.Skipped, Candidate);

        Assert.True(ReleaseCandidateEvidence.PreMainGateGreen([red, docs, green], Candidate));
        Assert.True(ReleaseCandidateEvidence.PreMainGateGreen([green], Candidate.ToUpperInvariant()));
        Assert.False(ReleaseCandidateEvidence.PreMainGateGreen([red, docs], Candidate));
        Assert.False(ReleaseCandidateEvidence.PreMainGateGreen([green], new string('c', 40)));
        Assert.False(ReleaseCandidateEvidence.PreMainGateGreen([Path.Combine(_root, "missing"), ""], Candidate));
    }

    [Fact]
    public void Batch_publication_counts_only_for_its_verified_candidate()
    {
        var published = new BatchGatePublication("batch-1", "digest", "run-1", new string('d', 40),
            Candidate, Candidate, 1, 1, Finished);

        Assert.True(ReleaseCandidateEvidence.BatchGateGreen([null, published], Candidate));
        Assert.False(ReleaseCandidateEvidence.BatchGateGreen([published], new string('c', 40)));
        Assert.False(ReleaseCandidateEvidence.BatchGateGreen([null], Candidate));
    }

    [Fact]
    public void Refusal_stays_until_cleared()
    {
        var store = new DeployStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build());
        var refusal = new ReleaseGateRefusal(Candidate, "required", "The candidate full suite is red.",
            "Fix the failing tests and release a newer commit.", Finished);

        Assert.Null(store.ReadReleaseRefusal("Demo Shop"));
        store.SaveReleaseRefusal("Demo Shop", refusal);
        Assert.Equal(refusal, store.ReadReleaseRefusal("Demo Shop"));
        store.SaveReleaseRefusal("Demo Shop", null);
        Assert.Null(store.ReadReleaseRefusal("Demo Shop"));
    }

    private static void Record(string jobFolder, BuildTestGateVerdict verdict, string sha)
        => IntegrationGateReceipts.Record(jobFolder, ReleaseCandidateEvidence.PreMainReceiptPrefix,
            new BuildTestGateResult(verdict, 0, 1000, "", verdict.ToString(), false, false)
            {
                ExpectedSha = sha,
                TestedSha = sha,
            }, time: new FakeTimeProvider(Finished));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
