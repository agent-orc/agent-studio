using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PostMergeRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Project = "PROJ-002";
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void GreenAdvancesLastGreenRedKeepsItAndSkippedRunIsRecorded()
    {
        var store = new PostMergeRunStore(_root);
        var green = Run("one", First, "window");
        Assert.Equal("running", store.Record(Project, green)!.DevelopState);
        var afterGreen = store.Record(Project, green with { Status = "green", FinishedAtUtc = Now.AddMinutes(1),
            Verdicts = [new("dotnet test", 0, false)] })!;
        Assert.Equal(First, afterGreen.LastGreenSha);
        Assert.Equal(First, afterGreen.LastWindowTipSha);

        var red = Run("two", Second, "nightly");
        Assert.Equal("running", store.Record(Project, red)!.DevelopState);
        var afterRed = store.Record(Project, red with { Status = "red", FinishedAtUtc = Now.AddMinutes(2),
            Verdicts = [new("dotnet test", 1, false)] })!;
        Assert.Equal("red", afterRed.DevelopState);
        Assert.Equal(First, afterRed.LastGreenSha);
        Assert.Equal(First, afterRed.LastWindowTipSha);

        var skipped = Run("three", First, "window") with { Status = "skipped", FinishedAtUtc = Now.AddMinutes(3) };
        var final = store.Record(Project, skipped)!;
        Assert.Equal(3, final.Runs.Count);
        Assert.Equal("skipped", final.Runs[^1].Status);
        Assert.Equal("green", store.Read(Project).DevelopState);
    }

    [Fact]
    public void OnlyClaimedExactTipCanComplete()
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("one", First, "window");
        store.Record(Project, run);
        Assert.Null(store.Record(Project, Run("two", Second, "window")));
        Assert.Null(store.Record(Project, run with { TipSha = Second, Status = "green", FinishedAtUtc = Now }));
        Assert.Null(store.Read(Project).LastGreenSha);
    }

    private static PostMergeRunRecord Run(string id, string tip, string kind) =>
        new(id, "Agent Studio", kind, tip, null, Now, Now, 1, ["dotnet test"], [], "running", Now, null);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
