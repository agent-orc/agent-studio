using System.Diagnostics;
using AgentStudio.Deploy;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StudioReleaseProjectionTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "studio-releases-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    public StudioReleaseProjectionTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "develop");
        Git("config", "user.name", "Fixture");
        Git("config", "user.email", "fixture@example.invalid");
        Git("config", "commit.gpgsign", "false");
    }

    [Fact]
    public void Maps_merged_delivery_between_revisions_and_waiting_delivery_after_running_revision()
    {
        var baseline = Commit("baseline");
        Git("checkout", "-qb", "delivery");
        var delivered = Commit("fix without a task key");
        Git("checkout", "-q", "develop");
        Commit("mentions DEMO-9 but does not deliver it");
        Git("merge", "-q", "--no-ff", "-m", "batch", "delivery");
        var live = Git("rev-parse", "HEAD");
        var pending = Commit("next fix");
        var view = Build(live, pending, [Revision(baseline, -2), Revision(live, -1)],
            [Card("DEMO-1", delivered), Card("DEMO-2", pending), Card("DEMO-9", new string('f', 40))]);
        Assert.Empty(view.Releases[0].Cards);
        Assert.Equal("DEMO-1", Assert.Single(view.Releases[1].Cards).Key);
        Assert.Equal("DEMO-2", Assert.Single(view.Pending).Key);
        Assert.Equal("live", view.CardStates["DEMO-1"].State);
        Assert.Equal(live, view.CardStates["DEMO-1"].Revision);
        Assert.Equal("integrated-not-live", view.CardStates["DEMO-2"].State);
        Assert.Equal("unknown", view.CardStates["DEMO-9"].State);
        Assert.Equal("Title DEMO-1", view.Releases[1].Cards[0].Title);
    }

    [Fact]
    public void Running_revision_overrides_newer_deploy_records_after_a_rollback()
    {
        var baseline = Commit("baseline");
        var delivery = Commit("delivery");
        var view = Build(baseline, delivery, [Revision(baseline, -2), Revision(delivery, -1)], [Card("DEMO-1", delivery)]);
        Assert.Equal("integrated-not-live", view.CardStates["DEMO-1"].State);
        Assert.Single(view.Pending);
        Assert.True(view.Releases[0].Running);
        Assert.False(view.Releases[1].Running);
    }

    [Fact]
    public void Baseline_membership_is_live_but_missing_git_facts_never_claim_not_live()
    {
        var baseline = Commit("baseline");
        var view = Build(baseline, baseline, [Revision(baseline, -1)], [Card("DEMO-1", baseline)]);
        Assert.Empty(Assert.Single(view.Releases).Cards);
        Assert.Equal("live", view.CardStates["DEMO-1"].State);
        var unknown = StudioReleaseProjection.Build("demo", baseline, baseline, _now,
            [Revision(baseline, -1)], [Card("DEMO-1", baseline)], (_, _) => null);
        Assert.Empty(unknown.Pending);
        Assert.Equal("unknown", unknown.CardStates["DEMO-1"].State);
    }

    [Fact]
    public void Partial_delivery_is_not_live_until_all_commits_or_its_verified_integration_tree_are_contained()
    {
        var baseline = Commit("baseline");
        var first = Commit("first part");
        var last = Commit("last part");
        var card = Card("DEMO-1", first) with { Shas = [first, last] };
        var view = Build(first, last, [Revision(baseline, -2), Revision(first, -1)], [card]);
        Assert.Single(view.Pending);
        Assert.Equal("integrated-not-live", view.CardStates[card.Key].State);
        var rewritten = card with { Shas = [new string('f', 40)], IntegrationSha = last };
        var mapped = Build(last, last, [Revision(baseline, -2), Revision(last, -1)], [rewritten]);
        Assert.Equal("live", mapped.CardStates[card.Key].State);
    }

    private StudioReleasesView Build(string running, string target, StudioRevision[] revisions, StudioReleaseCard[] cards)
        => StudioReleaseProjection.Build("demo", running, target, _now, revisions, cards, Ancestor);
    private StudioRevision Revision(string sha, int hours) => new(sha, _now.AddHours(hours), false);
    private StudioReleaseCard Card(string key, string sha) => new(key, "Title " + key, "bug", "#/tasks/" + key, [sha], _now.AddHours(-9));
    private bool? Ancestor(string from, string to)
    {
        var (code, _) = Run("merge-base", "--is-ancestor", from, to);
        return code switch { 0 => true, 1 => false, _ => null };
    }
    private string Commit(string message) { Git("commit", "--allow-empty", "-qm", message); return Git("rev-parse", "HEAD"); }
    private string Git(params string[] args) { var (code, output) = Run(args); Assert.Equal(0, code); return output.Trim(); }
    private (int, string) Run(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
    public void Dispose() => Directory.Delete(_repo, true);
}
