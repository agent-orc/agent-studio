using System.Diagnostics;
using AgentStudio.Dispatch;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class DispatchGitFactsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "dispatch-git-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    public DispatchGitFactsTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
    }

    [Fact]
    public void IndexUsesTheIntegrationTreeAndSixtyDayWindow()
    {
        File.WriteAllText(Path.Combine(_repo, "a.cs"), "old");
        File.WriteAllText(Path.Combine(_repo, "b.cs"), "old");
        Commit(At.AddDays(-61));
        for (var i = 0; i < 3; i++)
        {
            File.AppendAllText(Path.Combine(_repo, "a.cs"), "change");
            File.AppendAllText(Path.Combine(_repo, "b.cs"), "change");
            Commit(At.AddDays(-i - 1));
        }
        var tip = Git("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_repo, "checkout-only.cs"), "not in the integration tip");
        var index = DispatchForecastService.BuildIndex(_repo, tip, At, CancellationToken.None);
        Assert.Equal(new[] { "a.cs", "b.cs" }, index.TrackedFiles);
        Assert.Equal(new[] { "b.cs" }, index.Expansion["a.cs"]);
        Assert.Equal(At, index.AsOf);
        Assert.Equal(tip, index.IntegrationTip);
    }

    [Fact]
    public void DeliveryDiffExcludesSyncedIntegrationChangesAndIncludesDeletedCode()
    {
        File.WriteAllText(Path.Combine(_repo, "a.cs"), "base");
        File.WriteAllText(Path.Combine(_repo, "deleted.cs"), "base");
        Commit(At.AddDays(-3));
        var leaseBase = Git("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_repo, "other.cs"), "other card");
        Commit(At.AddDays(-2));
        var integrationTip = Git("rev-parse", "HEAD").Trim();
        Git("checkout", "--detach", leaseBase);
        File.WriteAllText(Path.Combine(_repo, "a.cs"), "delivery");
        File.Delete(Path.Combine(_repo, "deleted.cs"));
        File.WriteAllText(Path.Combine(_repo, "note.md"), "documentation");
        Commit(At.AddDays(-1));
        Git("merge", "--no-edit", integrationTip);
        var result = Git("rev-parse", "HEAD").Trim();
        var forecast = TouchForecast.Compute("a.cs", TouchForecast.BuildIndex(integrationTip, At, ["a.cs"], []), [])
            with { IntegrationTip = leaseBase };
        var subject = new ReviewSubjectRecord { ResultSha = result, BaseSha = leaseBase };
        var delivery = DispatchForecastService.ReadDelivery(_repo, integrationTip, subject, forecast, At, CancellationToken.None);
        Assert.Equal(new[] { "a.cs", "deleted.cs" }, delivery.RealFiles);
        Assert.Equal(new ForecastAccuracy(1, 0.5), delivery.FileAccuracy);
        Assert.Equal(integrationTip, delivery.MergeBase);
        Assert.Equal(forecast.IndexRevision, delivery.Forecast!.IndexRevision);
    }

    private void Commit(DateTimeOffset date)
    {
        Git("add", ".");
        Run(date, "commit", "-qm", "fixture change");
    }
    private string Git(params string[] args) => Run(At, args);
    private string Run(DateTimeOffset date, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_AUTHOR_DATE"] = date.ToString("O");
        start.Environment["GIT_COMMITTER_DATE"] = date.ToString("O");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
    public void Dispose() { if (Directory.Exists(_repo)) Directory.Delete(_repo, true); }
}
