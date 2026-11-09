using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every timestamp is fixture data; the matcher only orders entries by it.

/// <summary>
/// AGT-3436 - a gate that passed on a published integration tree verifies
/// every delivery that tree contains, while the branch still carries it.
/// Containment alone still never verifies (AGT-3002).
/// </summary>
public sealed class PublishedGateEvidenceTests : IDisposable
{
    private const string Tree = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherTree = "89abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _projectFolder =
        Path.Combine(Path.GetTempPath(), "published-gate-evidence-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_projectFolder)) Directory.Delete(_projectFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"PublishedGateEvidenceTests cleanup: {ex.Message}");
        }
    }

    [Fact]
    public void PassedGateOnACarriedTreeThatContainsTheDelivery_VerifiesAndNamesTheGate()
    {
        var found = IntegrationVerificationProjection.FindPublishedGate(
            [Gate(Tree)], "origin/main", _ => true, _ => true);

        Assert.NotNull(found);
        var projected = IntegrationVerificationProjection.FromPublishedGate(found!, "main", OtherTree);
        Assert.Equal(IntegrationVerificationStates.Verified, projected.State);
        Assert.True(IntegrationVerificationStates.PermitsCompletion(projected));
        Assert.Equal(Tree, projected.Sha);
        Assert.Equal(IntegrationVerificationEvidence.PublishedGate, projected.Evidence);
        Assert.Contains("CAR-26", projected.Reason);
        Assert.Contains(IntegrationGateJournal.PreDevelopBuildGateStep, projected.Reason);
        Assert.Contains(OtherTree, projected.Reason);
    }

    [Fact]
    public void TreeWithoutTheDelivery_IsNoEvidence()
        => Assert.Null(IntegrationVerificationProjection.FindPublishedGate(
            [Gate(Tree)], "main", _ => true, _ => false));

    [Fact]
    public void TreeTheRewrittenBranchNoLongerCarries_IsNoEvidence()
        => Assert.Null(IntegrationVerificationProjection.FindPublishedGate(
            [Gate(Tree)], "main", _ => false, _ => true));

    [Theory]
    [InlineData(nameof(BuildTestGateVerdict.Fail))]
    [InlineData(nameof(BuildTestGateVerdict.Warn))]
    [InlineData(nameof(BuildTestGateVerdict.NotApplicable))]
    [InlineData(nameof(BuildTestGateVerdict.Skipped))]
    public void RedOrMissingGate_IsNoEvidence_AndIsNeverRecorded(string verdict)
    {
        var red = Gate(Tree) with { GateVerdict = verdict };
        Assert.Null(IntegrationVerificationProjection.FindPublishedGate(
            [red], "main", _ => true, _ => true));
        Assert.False(PublishedGateEvidenceStore.Append(_projectFolder, red));
        Assert.Empty(PublishedGateEvidenceStore.Read(_projectFolder));
    }

    [Fact]
    public void GateOnAnotherBranch_IsNoEvidence()
        => Assert.Null(IntegrationVerificationProjection.FindPublishedGate(
            [Gate(Tree) with { IntegrationBranch = "develop" }], "main", _ => true, _ => true));

    [Fact]
    public void NewestQualifyingGate_IsTheOneTheCardNames()
    {
        var older = Gate(Tree);
        var newer = Gate(OtherTree) with { SourceJobId = "CAR-27", RecordedAtUtc = RecordedAt.AddMinutes(5) };
        var found = IntegrationVerificationProjection.FindPublishedGate(
            [older, newer], "main", _ => true, _ => true);
        Assert.Equal("CAR-27", found!.SourceJobId);
    }

    [Fact]
    public void Store_KeepsTheNewestEntriesPerBranch_AndSkipsADuplicateTree()
    {
        var limit = PublishedGateEvidenceStore.MaxEntriesPerBranch;
        var records = Enumerable.Range(0, limit + 3)
            .Select(index => Gate(index.ToString("x40")) with { RecordedAtUtc = RecordedAt.AddMinutes(index) })
            .Append(Gate(Tree) with { IntegrationBranch = "develop", RecordedAtUtc = RecordedAt.AddDays(-1) })
            .ToList();

        var kept = PublishedGateEvidenceStore.Bound(records);

        var main = kept.Where(record => record.IntegrationBranch == "main").ToList();
        Assert.Equal(limit, main.Count);
        Assert.DoesNotContain(main, record => record.TreeSha == 2.ToString("x40"));
        Assert.Contains(main, record => record.TreeSha == 3.ToString("x40"));
        Assert.Contains(main, record => record.TreeSha == (limit + 2).ToString("x40"));
        // Another branch keeps its own quota, even with older entries.
        Assert.Single(kept, record => record.IntegrationBranch == "develop");

        Assert.True(PublishedGateEvidenceStore.Append(_projectFolder, Gate(Tree)));
        Assert.False(PublishedGateEvidenceStore.Append(_projectFolder, Gate(Tree) with { SourceJobId = "CAR-27" }));
        Assert.True(PublishedGateEvidenceStore.Append(_projectFolder, Gate(OtherTree)));
        Assert.Equal([Tree, OtherTree], PublishedGateEvidenceStore.Read(_projectFolder).Select(record => record.TreeSha));
    }

    [Fact]
    public void Store_AppendHoldsTheLimitOnDisk()
    {
        var limit = PublishedGateEvidenceStore.MaxEntriesPerBranch;
        var seeded = Enumerable.Range(0, limit)
            .Select(index => Gate(index.ToString("x40")) with { RecordedAtUtc = RecordedAt.AddMinutes(index) })
            .ToList();
        Directory.CreateDirectory(_projectFolder);
        File.WriteAllText(Path.Combine(_projectFolder, PublishedGateEvidenceStore.FileName),
            System.Text.Json.JsonSerializer.Serialize(seeded,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.True(PublishedGateEvidenceStore.Append(_projectFolder,
            Gate(Tree) with { RecordedAtUtc = RecordedAt.AddDays(1) }));

        var stored = PublishedGateEvidenceStore.Read(_projectFolder);
        Assert.Equal(limit, stored.Count);
        Assert.DoesNotContain(stored, record => record.TreeSha == 0.ToString("x40"));
        Assert.Equal(Tree, stored[^1].TreeSha);
    }

    [Fact]
    public void Store_ReadsAMalformedFileAsNoEvidence()
    {
        Directory.CreateDirectory(_projectFolder);
        File.WriteAllText(Path.Combine(_projectFolder, PublishedGateEvidenceStore.FileName), "{broken");
        Assert.Empty(PublishedGateEvidenceStore.Read(_projectFolder));
    }

    private static PublishedGateEvidence Gate(string tree) => new(
        "Fixture",
        "main",
        tree,
        IntegrationGateJournal.PreDevelopBuildGateStep,
        "CAR-26",
        nameof(BuildTestGateVerdict.Ok),
        "The integration lane created this merge and its gate returned Ok.",
        tree,
        RecordedAt);
}
