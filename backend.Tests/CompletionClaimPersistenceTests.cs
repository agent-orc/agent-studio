using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Registry;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2817 - the persistence boundary for a card's completion claim and for
/// the <c>next-attempt</c> placeholder resolution. The pure decisions are
/// covered by <see cref="CompletionContractPolicyTests"/>; these pin that the
/// decision survives a write and a read, and that the placeholder is cleared
/// only for the commits the caller can prove are contained.
/// </summary>
public class CompletionClaimPersistenceTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace;
    private readonly string _watchPath;

    public CompletionClaimPersistenceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "agt2817-claim-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void CompletionClaimRoundTripsThroughTaskJson()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJob("claimed");

        Assert.True(mutations.SetCompletionClaimOnFolder(jobDir, new TaskCompletionClaim
        {
            Basis = CompletionClaimBases.OperatorOverride,
            Evidence = "Completed by operator override.",
            Reason = "Delivery failed review and was abandoned; closing the card.",
            Actor = "human:operator",
            IntegrationBranch = "develop",
            RecordedAtUtc = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc),
        }));

        var claim = scanner.FindJob("claimed", _watchPath)?.CompletionClaim;
        Assert.NotNull(claim);
        Assert.Equal(CompletionClaimBases.OperatorOverride, claim!.Basis);
        Assert.Equal("Delivery failed review and was abandoned; closing the card.", claim.Reason);
        Assert.Equal("develop", claim.IntegrationBranch);

        // task.json is written with the serializer's default casing and read
        // back case-insensitively, as every other record in the file is.
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "task.json")));
        var persisted = Property(json.RootElement, "completionClaim");
        Assert.Equal("operator-override", Property(persisted, "basis").GetString());
    }

    /// <summary>
    /// A hand-edited or future-version basis is not a claim. Surfacing it as a
    /// verified ground is exactly the kind of unchecked statement this card
    /// exists to stop.
    /// </summary>
    [Fact]
    public void AnUnknownBasisIsNotReadAsAClaim()
    {
        var (scanner, _) = Build();
        var jobDir = SeedJob("bogus");
        WriteRaw(jobDir, """
            {
              "id": "bogus",
              "title": "Fixture",
              "state": "6-completed",
              "agent": "claude",
              "createdAt": "2026-09-14T08:00:00Z",
              "completionClaim": { "basis": "vibes", "evidence": "trust me" }
            }
            """);

        Assert.Null(scanner.FindJob("bogus", _watchPath)?.CompletionClaim);
    }

    /// <summary>
    /// AGT-2706: the shipped commit keeps the placeholder because no
    /// replacement ever published. Containment clears it; a commit that is not
    /// contained, and a commit with a named successor, are both left alone.
    /// </summary>
    [Fact]
    public void ContainmentClearsOnlyThePlaceholderItCanProve()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJob("shipped");
        WriteRaw(jobDir, """
            {
              "id": "shipped",
              "title": "Fixture",
              "state": "6-completed",
              "agent": "claude",
              "createdAt": "2026-09-07T08:00:00Z",
              "commits": [
                {
                  "sha": "79c2dcf8c00000000000000000000000000000aa",
                  "shortSha": "79c2dcf8c",
                  "message": "feat: delivered",
                  "filesChanged": 17,
                  "files": [],
                  "at": "2026-09-07T21:31:00Z",
                  "supersededByAttempt": "next-attempt"
                },
                {
                  "sha": "de829d6e800000000000000000000000000000bb",
                  "shortSha": "de829d6e8",
                  "message": "feat: earlier round",
                  "filesChanged": 3,
                  "files": [],
                  "at": "2026-09-06T10:00:00Z",
                  "supersededByAttempt": "next-attempt"
                },
                {
                  "sha": "aaaaaaaaa00000000000000000000000000000cc",
                  "shortSha": "aaaaaaaaa",
                  "message": "feat: replaced by a named successor",
                  "filesChanged": 4,
                  "files": [],
                  "at": "2026-09-05T10:00:00Z",
                  "supersededByAttempt": "run_e1fbb2898c6a4e99a0fd5612b94104c2"
                }
              ]
            }
            """);

        var contained = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "79c2dcf8c00000000000000000000000000000aa",
            "aaaaaaaaa00000000000000000000000000000cc",
        };
        var result = mutations.ResolvePendingSupersessionOnFolder(
            jobDir,
            commit => contained.Contains(commit.Sha));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.MarkedCommits);

        var commits = scanner.FindJob("shipped", _watchPath)!.Commits;
        Assert.Equal(CommitSupersessionStates.Current, TaskCommitSupersession.State(Find(commits, "79c2dcf8c")));
        Assert.Equal(CommitSupersessionStates.ReplacementPending, TaskCommitSupersession.State(Find(commits, "de829d6e8")));
        Assert.Equal(CommitSupersessionStates.Replaced, TaskCommitSupersession.State(Find(commits, "aaaaaaaaa")));
    }

    [Fact]
    public void ResolvingAPlaceholderIsANoOpWhenNothingIsContained()
    {
        var (_, mutations) = Build();
        var jobDir = SeedJob("untouched");

        var result = mutations.ResolvePendingSupersessionOnFolder(jobDir, _ => true);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.MarkedCommits);
    }

    /// <summary>
    /// A card that produced a report names it, so the code-free ground is
    /// available to it. An empty results folder names nothing.
    /// </summary>
    [Theory]
    [InlineData("deliverables.md", "results/deliverables.md")]
    [InlineData("report.html", "results/report.html")]
    [InlineData("analysis.json", "results/analysis.json")]
    public void AProducedResultArtifactIsANamedDeliverable(string fileName, string expected)
    {
        var (scanner, _) = Build();
        var jobDir = SeedJob("reported");
        var results = Path.Combine(jobDir, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, fileName), "content");

        var task = scanner.FindJob("reported", _watchPath)!;
        Assert.Equal(expected, NamedDeliverableReader.Read(task).Path);
        Assert.True(NamedDeliverableReader.Exists(task));
    }

    [Fact]
    public void ACardThatProducedNothingNamesNothing()
    {
        var (scanner, _) = Build();
        SeedJob("empty");

        Assert.False(NamedDeliverableReader.Exists(scanner.FindJob("empty", _watchPath)!));
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.EnumerateObject()
            .Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value;

    private static TaskCommitInfo Find(IEnumerable<TaskCommitInfo> commits, string shortSha)
        => commits.Single(commit => commit.ShortSha == shortSha);

    private string SeedJob(string id)
    {
        var laneDir = Path.Combine(_watchPath, "6-completed");
        Directory.CreateDirectory(laneDir);
        var jobDir = Path.Combine(laneDir, id);
        Directory.CreateDirectory(jobDir);
        WriteRaw(jobDir, $$"""
            {
              "id": "{{id}}",
              "title": "Fixture",
              "state": "6-completed",
              "agent": "claude",
              "createdAt": "2026-09-14T08:00:00Z"
            }
            """);
        return jobDir;
    }

    private static void WriteRaw(string jobDir, string json)
    {
        File.WriteAllText(Path.Combine(jobDir, "task.json"), json);
        File.WriteAllText(Path.Combine(jobDir, "prompt.md"), "fixture");
    }

    private (TaskScannerService Scanner, TaskMutationService Mutations) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        return (scanner, mutations);
    }
}
