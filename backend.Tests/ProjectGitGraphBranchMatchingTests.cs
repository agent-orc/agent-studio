using AgentStudio.Git;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3369: the Git inventory matched every branch against every card and
/// read each card's review subject from disk per branch. Branch names are
/// now indexed once per card. The matching rules stay: recorded and worktree
/// branches match exactly, a <c>runner/</c> branch matches the card whose key,
/// id or display key ends it, also with a collision suffix, and cards keep
/// their input order.
/// </summary>
public sealed class ProjectGitGraphBranchMatchingTests
{
    [Fact]
    public void Branches_match_their_cards_by_name_and_runner_suffix_in_card_order()
    {
        var alpha = Card("alpha-id", "AGT-10", provenanceBranch: "feature/alpha");
        var beta = Card("beta-id", "AGT-11");
        var gamma = Card("gamma-id", "agt-11");
        var tasks = new[] { alpha, beta, gamma };
        var branches = new[]
        {
            Branch("refs/heads/feature/alpha"),
            Branch("task/beta-id"),
            Branch("runner/host-a/AGT-11"),
            Branch("runner/host-a/agt-10-collision-0123abcd-4567ef01"),
            Branch("feature/AGT-11"),
            Branch("runner/host-b/unrelated"),
        };

        var result = AgentStudio.Git.ProjectGitGraphService.BuildTaskByBranch(tasks, branches);

        Assert.Equal(["alpha-id"], Keys(result["refs/heads/feature/alpha"]));
        Assert.Equal(["beta-id"], Keys(result["task/beta-id"]));
        // Key comparison on runner branches ignores case, so both cards whose
        // key is AGT-11 belong to the branch, in input order.
        Assert.Equal(["beta-id", "gamma-id"], Keys(result["runner/host-a/AGT-11"]));
        Assert.Equal(["alpha-id"], Keys(result["runner/host-a/agt-10-collision-0123abcd-4567ef01"]));
        Assert.False(result.ContainsKey("feature/AGT-11"));
        Assert.False(result.ContainsKey("runner/host-b/unrelated"));
    }

    [Fact]
    public void Card_matched_by_name_and_suffix_is_listed_once()
    {
        var card = Card("delta-id", "AGT-12", provenanceBranch: "runner/host-a/AGT-12");

        var result = AgentStudio.Git.ProjectGitGraphService.BuildTaskByBranch([card], [Branch("runner/host-a/AGT-12")]);

        Assert.Equal(["delta-id"], Keys(result["runner/host-a/AGT-12"]));
    }

    private static string[] Keys(List<GitTaskBadge> cards) => cards.Select(card => card.TaskKey).ToArray();

    private static TaskInfo Card(string id, string key, string? provenanceBranch = null) => new()
    {
        Id = id,
        TaskKey = id,
        Key = key,
        Title = id,
        State = "2-ready",
        Provenance = provenanceBranch is null ? null : new TaskProvenance { Branch = provenanceBranch },
    };

    private static GitBranchEntry Branch(string name)
        => new(name, "local", null, null, false, null, 0, 0, null, null, null);
}
