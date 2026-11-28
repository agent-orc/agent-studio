extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using AgentStudio.Pipeline;
using AgentStudio.Clients;
using AgentStudio.Registry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
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
    public void A_skipped_unchanged_tip_does_not_clear_an_open_line_stop()
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("green", First, "window");
        store.Record(Project, run);
        store.Record(Project, run with { Status = "green", FinishedAtUtc = Now.AddMinutes(1),
            Verdicts = [new("dotnet test", 0, false)] });
        store.SetDevelopDisposition(Project, "red", "Base suite failed twice");
        var skipped = store.Record(Project, Run("skip", First, "window") with { Status = "skipped" });
        Assert.NotNull(skipped);
        Assert.Equal("red", skipped.DevelopState);
        Assert.Equal("Base suite failed twice", skipped.LineStop);
    }

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

    [Theory]
    [InlineData("omitted")]
    [InlineData("duplicate")]
    public void GreenRequiresTheStartedPlanAndDistinctCommandCoverage(string invalid)
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("coverage", First, "window") with { Commands = ["build", "test"] };
        Assert.NotNull(store.Record(Project, run));
        var terminal = run with { Status = "green", FinishedAtUtc = Now,
            Verdicts = [new("build", 0, false), new("test", 0, false)] };
        var bad = invalid == "omitted"
            ? terminal with { Commands = ["build"], Verdicts = [terminal.Verdicts[0]] }
            : terminal with { Verdicts = [terminal.Verdicts[0], terminal.Verdicts[0]] };
        Assert.Null(store.Record(Project, bad));
        Assert.Null(store.Read(Project).LastGreenSha);
        Assert.Equal(run.Commands, store.Read(Project).ActiveRun!.Commands);
        Assert.NotNull(store.Record(Project, terminal));
        Assert.Equal(First, store.Read(Project).LastGreenSha);
        Assert.NotNull(new PostMergeRunStore(_root).Record(Project, terminal));
        Assert.Null(store.Record(Project, terminal with { Reason = "conflicting replay" }));
    }

    [Theory]
    [InlineData("substituted")]
    [InlineData("incomplete")]
    [InlineData("extra")]
    [InlineData("kind")]
    [InlineData("id")]
    [InlineData("tip")]
    [InlineData("timeout")]
    public void InvalidEvidenceCannotAdvanceOrReplaceTheActiveRun(string invalid)
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("coverage", First, "window") with { Commands = ["build", "test"] };
        Assert.NotNull(store.Record(Project, run));
        var receipt = run with { Status = "green", FinishedAtUtc = Now,
            Verdicts = [new("build", 0, false), new("test", 0, false)] };
        var bad = invalid switch
        {
            "substituted" => receipt with { Verdicts = [new("build", 0, false), new("other", 0, false)] },
            "incomplete" => receipt with { Verdicts = [receipt.Verdicts[0]] },
            "extra" => receipt with { Verdicts = [.. receipt.Verdicts, new("other", 0, false)] },
            "kind" => receipt with { Kind = "nightly" },
            "id" => receipt with { Id = "stale" },
            "tip" => receipt with { TipSha = Second },
            _ => receipt with { Verdicts = [receipt.Verdicts[0], new("test", 0, true)] },
        };
        Assert.Null(store.Record(Project, bad));
        Assert.Null(store.Read(Project).LastGreenSha);
        Assert.Null(store.Read(Project).LastWindowTipSha);
        Assert.Empty(store.Read(Project).Runs);
        Assert.Null(store.Record(Project, run with { Commands = ["build"] }));
        Assert.NotNull(store.Record(Project, receipt));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("inconclusive")]
    public void PartialTerminalEvidenceStillUsesTheStartedCommands(string status)
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("partial", First, "window") with { Commands = ["build", "test"] };
        store.Record(Project, run);
        var receipt = run with { Status = status, FinishedAtUtc = Now, Verdicts = [new("build", 1, false)] };
        Assert.Null(store.Record(Project, receipt with { Commands = ["build"] }));
        Assert.Null(store.Record(Project, receipt with { Verdicts = [new("other", 1, false)] }));
        Assert.NotNull(store.Record(Project, receipt));
        Assert.Null(store.Read(Project).LastGreenSha);
    }

    [Theory]
    [Trait("Category", "Guard")]
    [InlineData("valid", true)]
    [InlineData("fixed-valid", true)]
    [InlineData("fixed-missing")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unplanned")]
    [InlineData("wrong-suite")]
    [InlineData("mixed")]
    [InlineData("same-index")]
    [InlineData("wrong-command")]
    [InlineData("no-metadata")]
    [InlineData("out-of-plan")]
    [InlineData("fixed-out-of-plan")]
    [InlineData("partial-out-of-plan")]
    [InlineData("incomplete-settings")]
    [InlineData("truncated-settings")]
    [InlineData("empty-settings")]
    [InlineData("empty-index")]
    [InlineData("extra-filter")]
    [InlineData("extra-settings")]
    [InlineData("extra-argument")]
    [InlineData("extra-positional")]
    [InlineData("valid-quoted-path", true)]
    [InlineData("windows-drive", true, @"C:\runner\post-merge-shards\shard-0.runsettings", @"C:/runner/post-merge-shards\shard-1.runsettings")]
    [InlineData("windows-unc", true, @"\\host\share\post-merge-shards\shard-0.runsettings", @"\\host\share\post-merge-shards\shard-1.runsettings")]
    [InlineData("windows-unc-slash-backslash", true, @"\\host\share\post-merge-shards\shard-0.runsettings", @"/\host/share\post-merge-shards\shard-1.runsettings")]
    [InlineData("windows-unc-backslash-slash", true, @"\\host\share\post-merge-shards\shard-0.runsettings", @"\/host/share\post-merge-shards\shard-1.runsettings")]
    [InlineData("windows-unc-forward-prefix", true, @"\\host\share\post-merge-shards\shard-0.runsettings", @"//host/share/post-merge-shards\shard-1.runsettings")]
    [InlineData("windows-unc-other-share", false, @"\\host\share\post-merge-shards\shard-0.runsettings", @"/\host/other\post-merge-shards\shard-1.runsettings")]
    [InlineData("posix-literal-backslash", false, "/runner/post-merge-shards/shard-0.runsettings", @"/runner/post-merge-shards\shard-1.runsettings")]
    [InlineData("posix-redundant-leading-slashes", false, "///runner/post-merge-shards/shard-0.runsettings", @"///runner/post-merge-shards\shard-1.runsettings")]
    [InlineData("leading-zero")]
    [InlineData("plus-sign")]
    [InlineData("index-whitespace")]
    public void ExplicitShardsCoverTheirOriginalCommandExactlyOnce(string evidence, bool accepted = false,
        string? firstSettings = null, string? secondSettings = null)
    {
        const string command = "dotnet test tests.csproj --no-build --filter Category!=MachineBound";
        var envelope = new PostMergeEnvelopeFingerprint("a", "b", "worker", null, 2, null, "tests=auto", null, null);
        var run = Run("shards", First, "nightly") with { Commands = ["build", command], Envelope = envelope };
        if (evidence == "unplanned") run = run with { Envelope = envelope with { ShardPlan = "tests=1" } };
        if (evidence.StartsWith("fixed-", StringComparison.Ordinal)) run = run with { Envelope = envelope with { ShardPlan = "tests=2" } };
        var store = new PostMergeRunStore(_root);
        Assert.NotNull(store.Record(Project, run));
        var settingsRoot = evidence == "valid-quoted-path" ? Path.Combine(_root, "runner's files") : _root;
        var shardCommands = Runner::AgentRunner.PostMergeShards.Commands(command, ["Tests.One", "Tests.Two"], 2,
            Path.Combine(settingsRoot, "post-merge-shards"));
        Assert.Equal(2, shardCommands.Count);
        var shard = new PostMergeCommandVerdict(shardCommands[0], 0, false) { SuiteId = "tests" };
        var second = shard with { Command = shardCommands[1] };
        if (firstSettings is not null && secondSettings is not null)
        {
            shard = shard with { Command = $"dotnet test tests.csproj --no-build --settings '{firstSettings}'" };
            second = second with { Command = $"dotnet test tests.csproj --no-build --settings '{secondSettings}'" };
        }
        var receipt = run with { Status = "green", FinishedAtUtc = Now,
            Verdicts = [new("build", 0, false), shard, second],
            Envelope = envelope with { ShardPlan = "tests=2" },
            SuiteSelection = new(["tests"], [], []) { SuiteCommands = new Dictionary<string, string> { ["tests"] = command } } };
        receipt = evidence switch
        {
            "leading-zero" or "plus-sign" or "index-whitespace" => receipt with
            {
                Verdicts = [receipt.Verdicts[0], shard, second with
                {
                    Command = second.Command.Replace("shard-1.runsettings", evidence switch
                    {
                        "leading-zero" => "shard-01.runsettings",
                        "plus-sign" => "shard-+1.runsettings",
                        _ => "shard- 1.runsettings",
                    }, StringComparison.Ordinal),
                }],
            },
            "fixed-missing" => receipt with { Envelope = envelope with { ShardPlan = "tests=1" },
                Verdicts = [receipt.Verdicts[0], shard] },
            "missing" => receipt with { Verdicts = [receipt.Verdicts[0], shard] },
            "duplicate" => receipt with { Verdicts = [receipt.Verdicts[0], shard, shard] },
            "wrong-suite" => receipt with { Verdicts = [receipt.Verdicts[0], shard, second with { SuiteId = "other" }] },
            "mixed" => receipt with { Verdicts = [.. receipt.Verdicts, new(command, 0, false)] },
            "same-index" => receipt with { Verdicts = [receipt.Verdicts[0], shard, shard with
                { Command = shard.Command.Replace(_root, Path.Combine(_root, "other"), StringComparison.Ordinal) }] },
            "wrong-command" => receipt with { Verdicts = [receipt.Verdicts[0], shard, second with
                { Command = second.Command.Replace("tests.csproj", "other.csproj", StringComparison.Ordinal) }] },
            "no-metadata" => receipt with { Envelope = null },
            "out-of-plan" or "fixed-out-of-plan" => receipt with
            {
                Verdicts = [receipt.Verdicts[0], shard, second with
                    { Command = second.Command.Replace("shard-1.runsettings", "shard-2.runsettings", StringComparison.Ordinal) }],
            },
            "partial-out-of-plan" => receipt with
            {
                Status = "red", Verdicts = [receipt.Verdicts[0], second with
                    { Command = second.Command.Replace("shard-1.runsettings", "shard-2.runsettings", StringComparison.Ordinal), ExitCode = 1 }],
            },
            "incomplete-settings" or "truncated-settings" or "empty-settings" or "empty-index" => receipt with
            {
                Verdicts = [receipt.Verdicts[0], shard with
                {
                    Command = command[..command.IndexOf(" --filter", StringComparison.Ordinal)]
                        + " --settings '" + (evidence is "empty-settings" or "incomplete-settings" ? "" : evidence == "empty-index"
                            ? "/post-merge-shards/shard-.runsettings" : "/post-merge-shards/shard-.x.runsettings") + (evidence == "incomplete-settings" ? "" : "'"),
                }, second],
            },
            _ => receipt,
        };
        if (evidence.StartsWith("extra-", StringComparison.Ordinal))
        {
            var extra = evidence switch
            {
                "extra-filter" => " --filter 'FullyQualifiedName~Only.One'",
                "extra-settings" => " --settings '/tmp/other.runsettings'",
                "extra-argument" => " --list-tests",
                _ => "",
            };
            receipt = receipt with { Verdicts = receipt.Verdicts.Select(verdict => verdict.Command == "build"
                ? verdict : verdict with { Command = verdict.Command.Replace(" --settings '",
                    " --settings '/tmp/ignored'" + extra + " '", StringComparison.Ordinal) }).ToArray() };
        }
        if (!accepted)
        {
            Assert.Null(store.Record(Project, receipt));
            var state = store.Read(Project);
            Assert.Null(state.LastGreenSha);
            Assert.Equal(run.Id, state.ActiveRun?.Id);
            return;
        }
        Assert.NotNull(store.Record(Project, receipt));
        Assert.Equal(First, store.Read(Project).LastGreenSha);
        Assert.NotNull(new PostMergeRunStore(_root).Record(Project, receipt));
    }

    [Theory]
    [Trait("Category", "Guard")]
    [InlineData(true, "exact", true)]
    [InlineData(false, "exact", true)]
    [InlineData(true, "reason", false)]
    [InlineData(true, "id", false)]
    [InlineData(true, "command-spacing", false)]
    [InlineData(true, "verdict-order", false)]
    public void PersistedReceiptRequiresExactReplay(bool duplicate, string change, bool accepted)
    {
        var store = new PostMergeRunStore(_root);
        var receipt = Run("legacy", First, "window") with
        {
            Commands = duplicate ? ["build", "dotnet test", "build"] : ["build", "dotnet test"],
            Status = "green", FinishedAtUtc = Now,
            Verdicts = duplicate ? [new("build", 0, false), new("dotnet test", 0, false), new("build", 0, false)]
                : [new("build", 0, false), new("dotnet test", 0, false)],
        };
        Directory.CreateDirectory(_root);
        var state = PostMergeProjectState.Empty(Project) with
        {
            LastGreenSha = First, DevelopState = "green", Runs = [receipt],
        };
        File.WriteAllText(Path.Combine(_root, Project + ".json"),
            System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var replay = change switch
        {
            "exact" => receipt,
            "reason" => receipt with { Reason = "changed receipt" },
            "id" => receipt with { Id = "new-receipt" },
            "command-spacing" => receipt with { Commands = ["build", "dotnet  test", "build"] },
            "verdict-order" => receipt with { Verdicts = [receipt.Verdicts[1], receipt.Verdicts[0], receipt.Verdicts[2]] },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(accepted, store.Record(Project, replay) is not null);
        Assert.Equal(First, store.Read(Project).LastGreenSha);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(receipt),
            System.Text.Json.JsonSerializer.Serialize(Assert.Single(store.Read(Project).Runs)));
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

    [Fact]
    public void GreenReceiptClearsEscalatedRepairCard()
    {
        var store = new PostMergeRunStore(_root);
        store.RecordAttributionEscalation(Project, "repair-one");
        Assert.Equal("repair-one", store.Read(Project).AttributionEscalatedTaskId);
        var run = Run("one", First, "window");
        store.Record(Project, run);
        Assert.Equal("repair-one", store.Read(Project).AttributionEscalatedTaskId);
        store.Record(Project, run with { Status = "green", FinishedAtUtc = Now.AddMinutes(1),
            Verdicts = [new("dotnet test", 0, false)] });
        Assert.Null(store.Read(Project).AttributionEscalatedTaskId);
    }

    [Fact]
    public void StarvationObservationSurvivesRestartIgnoresOldPollAndClearsOnAdmission()
    {
        var store = new PostMergeRunStore(_root);
        var observation = new PostMergeWindowObservation(First, Now.AddMinutes(-31), Now, 30, "busy");
        store.ObserveWindow(Project, observation);
        Assert.True(new PostMergeRunStore(_root).Read(Project).PendingWindow!.Starved);
        store.ObserveWindow(Project, observation with { ObservedAtUtc = Now.AddMinutes(-1), AdmissionReason = null });
        Assert.True(store.Read(Project).PendingWindow!.Starved);
        store.Record(Project, Run("admitted", First, "window"));
        Assert.Null(store.Read(Project).PendingWindow);
    }

    [Fact]
    public void FlakyNamesSurviveRestartAreDeduplicatedAndExpireAfterFourteenDays()
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("flaky", First, "window");
        store.Record(Project, run);
        var receipt = run with { Status = "green", FinishedAtUtc = Now,
            Verdicts = [new("dotnet test", 0, false, FlakyTests: ["Runner.One", "Runner.One"], Retried: true)] };
        var state = store.Record(Project, receipt)!;
        Assert.Equal(1, receipt.FlakyCount);
        Assert.Equal(["Runner.One"], receipt.FlakyTests);
        Assert.Equal(1, state.FlakyCount);
        Assert.Single(new PostMergeRunStore(_root).Read(Project).FlakyHistory!);
        store.Record(Project, receipt);
        Assert.Single(store.Read(Project).FlakyHistory!);
        var next = Run("next", Second, "window") with { StartedAtUtc = Now.AddDays(15) };
        store.Record(Project, next);
        store.Record(Project, next with { Status = "green", FinishedAtUtc = Now.AddDays(15),
            Verdicts = [new("dotnet test", 0, false)] });
        Assert.Empty(store.Read(Project).FlakyHistory!);
    }

    [Fact]
    public void InterruptedBeforeFirstCommandCanSettleWithoutInventingAProductFailure()
    {
        var store = new PostMergeRunStore(_root);
        var run = Run("interrupted", First, "window");
        store.Record(Project, run);
        var settled = new PostMergeRunStore(_root).Record(Project, run with
        {
            Status = "inconclusive", FinishedAtUtc = Now.AddMinutes(1),
            Reason = "host interrupted before a command checkpoint",
        });
        Assert.NotNull(settled);
        Assert.Null(settled.ActiveRun);
        Assert.Equal("inconclusive", settled.DevelopState);
        Assert.Null(settled.LastGreenSha);
        Assert.Null(settled.LastWindowTipSha);
    }

    [Fact]
    public void RenewableCustodySurvivesRestartAndRejectsStaleOwnersAndUnfencedReceipts()
    {
        var owner = Guid.NewGuid().ToString("N");
        var store = new PostMergeRunStore(_root);
        var run = Run("owned", First, "window");
        Assert.NotNull(store.Record(Project, run, owner, 0, Now));
        Assert.Equal(1, store.ReadCustody(Project)!.Fence);
        var restarted = new PostMergeRunStore(_root);
        Assert.Null(restarted.Record(Project, run));
        Assert.Null(restarted.RenewCustody(Project, run.Id, "wrong", 1, Now, true));
        Assert.Null(restarted.RenewCustody(Project, run.Id, owner, 2, Now, true));
        Assert.NotNull(restarted.RenewCustody(Project, run.Id, owner, 1, Now.AddMinutes(1), false));
        var recovered = restarted.RenewCustody(Project, run.Id, owner, 1, Now.AddMinutes(10), true)!;
        Assert.Equal(2, recovered.Fence);
        var receipt = run with { Status = "green", FinishedAtUtc = Now.AddMinutes(11),
            Verdicts = [new("dotnet test", 0, false)] };
        Assert.Null(restarted.Record(Project, receipt, owner, 1, Now.AddMinutes(11)));
        Assert.Null(restarted.Record(Project, Run("successor", Second, "window"), observedAt: Now.AddMinutes(11)));
        var accepted = restarted.Record(Project, receipt, owner, 2, Now.AddHours(1))!;
        Assert.Null(accepted.ActiveRun);
        Assert.Equal(First, accepted.LastGreenSha);
        Assert.NotNull(new PostMergeRunStore(_root).Record(Project, receipt, owner, 2, Now.AddHours(2)));
        Assert.Null(restarted.Record(Project, receipt with { Reason = "changed evidence" }, owner, 2, Now.AddHours(2)));
    }

    [Theory]
    [InlineData("window", 5)]
    [InlineData("nightly", 15)]
    public void RenewedLeasePreventsLegacyTakeoverAndExpiredFallbackIsInfrastructure(string kind, int hours)
    {
        var owner = Guid.NewGuid().ToString("N");
        var store = new PostMergeRunStore(_root);
        var run = Run("owned", First, kind);
        store.Record(Project, run, owner, 0, Now);
        // A returning owner explicitly recovers the fence before renewing work.
        store.RenewCustody(Project, run.Id, owner, 1, Now.AddHours(hours), true);
        var successor = Run("successor", Second, kind) with { StartedAtUtc = Now.AddHours(hours) };
        Assert.Null(store.Record(Project, successor, observedAt: Now.AddHours(hours)));
        var replaced = store.Record(Project, successor with { StartedAtUtc = Now.AddHours(hours).AddMinutes(3) },
            observedAt: Now.AddHours(hours).AddMinutes(3))!;
        Assert.Equal("inconclusive", Assert.Single(replaced.Runs).Status);
        Assert.Equal("successor", replaced.ActiveRun!.Id);
        Assert.Null(store.Record(Project, run with { Status = "inconclusive", FinishedAtUtc = Now.AddHours(hours) },
            owner, 2, Now.AddHours(hours).AddMinutes(3)));
    }

    [Fact]
    public void RecoverAcknowledgementCanBeLostWithoutLosingTheNewFence()
    {
        var owner = Guid.NewGuid().ToString("N");
        var recovery = Guid.NewGuid().ToString("N");
        var store = new PostMergeRunStore(_root);
        var run = Run("owned", First, "window");
        store.Record(Project, run, owner, 0, Now);
        var first = store.RenewCustody(Project, run.Id, owner, 1, Now.AddMinutes(1), true, recovery)!;
        var replay = new PostMergeRunStore(_root).RenewCustody(Project, run.Id, owner, 1,
            Now.AddMinutes(1), true, recovery)!;
        Assert.Equal(first, replay);
        Assert.Equal(2, replay.Fence);
        Assert.Null(store.RenewCustody(Project, run.Id, owner, 1, Now.AddMinutes(1), true, "another-request"));
        Assert.NotNull(store.Record(Project, run, owner, 0, Now.AddHours(1)));
        Assert.Equal(2, store.ReadCustody(Project)!.Fence);
    }

    [Fact]
    public async Task ExistingRunRouteAdmitsRecoversFencesAndSettlesAnInterruptedOwner()
    {
        var clock = new FakeTimeProvider(Now);
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root, ["Security:Profile"] = "local", ["AllowedHosts"] = "*" }));
            builder.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.Replace(ServiceDescriptor.Singleton(new PostMergeRunStore(Path.Combine(_root, "state"))));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var projectPath = Path.Combine(_root, "demo");
        Directory.CreateDirectory(projectPath);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .EnsureProjectForStorage(projectPath, "demo", DefaultWorkspace.Id, clock);
        var run = Run("wire", First, "window") with { Project = "demo" };
        var owner = Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Send(PostMergeRunRecord receipt, long fence, string? operation = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runner/post-merge/projects/demo/runs")
                { Content = JsonContent.Create(receipt) };
            request.Headers.Add("X-Post-Merge-Owner", owner);
            request.Headers.Add("X-Post-Merge-Fence", fence.ToString());
            if (operation is not null)
            {
                request.Headers.Add("X-Post-Merge-Operation", operation);
                request.Headers.Add("X-Post-Merge-Recovery-Id", Guid.NewGuid().ToString("N"));
            }
            return await client.SendAsync(request);
        }
        using var admitted = await Send(run, 0);
        admitted.EnsureSuccessStatusCode();
        Assert.Equal("1", Assert.Single(admitted.Headers.GetValues("X-Post-Merge-Fence")));
        using var recovered = await Send(run, 1, "recover");
        recovered.EnsureSuccessStatusCode();
        Assert.Equal(2, (await recovered.Content.ReadFromJsonAsync<PostMergeCustody>())!.Fence);
        var interrupted = run with { Status = "inconclusive", FinishedAtUtc = Now, Reason = "host interrupted" };
        using var stale = await Send(interrupted, 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var settled = await Send(interrupted, 2);
        settled.EnsureSuccessStatusCode();
        var state = await settled.Content.ReadFromJsonAsync<PostMergeProjectState>();
        Assert.Null(state!.ActiveRun);
        Assert.Equal("inconclusive", state.DevelopState);
        Assert.Null(new PostMergeRunStore(Path.Combine(_root, "state")).Read(project.Id).ActiveRun);
    }

    private static PostMergeRunRecord Run(string id, string tip, string kind) =>
        new(id, "Agent Studio", kind, tip, null, Now, Now, 1, ["dotnet test"], [], "running", Now, null);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
