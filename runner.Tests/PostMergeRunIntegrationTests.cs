using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeRunIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-integration-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NightlyRunsFetchedShaEvenWhenOriginAdvancesAfterClaim()
    {
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        var checkout = Path.Combine(_root, "checkout");
        await Git(_root, "init", "--quiet", "--bare", origin);
        await Git(_root, "init", "--quiet", "-b", "develop", seed);
        await Git(seed, "config", "user.name", "Test");
        await Git(seed, "config", "user.email", "test@example.invalid");
        Directory.CreateDirectory(Path.Combine(seed, ".agent-studio"));
        File.WriteAllText(Path.Combine(seed, ".agent-studio", "project.yml"), """
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - "true"
              test:
                - "git rev-parse HEAD > tested.sha"
              lint:
            testSuites:
              - id: routine
                category: routine
                expectedDurationSeconds: 5
                command: "git rev-parse HEAD > tested.sha"
            cachePaths:
            capabilities: [git]
            """);
        var prepare = Path.Combine(seed, ".agent-studio", "prepare");
        File.WriteAllText(prepare, "#!/bin/sh\ntrue\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(prepare, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "first");
        await Git(seed, "push", "--quiet", origin, "develop");
        var first = (await Git(seed, "rev-parse", "HEAD")).Trim();
        await Git(_root, "clone", "--quiet", "--branch", "develop", origin, checkout);

        var received = new List<PostMergeRunRecord>();
        string? lastGreenForGet = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(PostMergeProjectState.Empty("PROJ-002") with { LastGreenSha = lastGreenForGet });
            var run = JsonSerializer.Deserialize<PostMergeRunRecord>(
                await request.Content!.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            received.Add(run);
            if (run.Status == "running")
            {
                File.WriteAllText(Path.Combine(seed, "after.txt"), "after fetch\n");
                await Git(seed, "add", ".");
                await Git(seed, "commit", "--quiet", "-m", "second");
                await Git(seed, "push", "--quiet", origin, "develop");
            }
            return Json(PostMergeProjectState.Empty("PROJ-002"));
        })) { BaseAddress = new Uri("http://test/") };
        var options = new PostMergeRunOptions("Agent Studio", checkout, "http://test/",
            "develop", "origin", "unused", "nightly", 6, TimeSpan.FromMinutes(30), 1, [], false);
        var output = new StringWriter();
        var exit = await new PostMergeRunCommand(options, output, new FixedClock(), http, () => new(0.1, 0))
            .ExecuteAsync(CancellationToken.None);

        Assert.True(exit == 0, output.ToString());
        Assert.Equal(["running", "green"], received.Select(run => run.Status));
        Assert.All(received, run => Assert.Equal(first, run.TipSha));
        Assert.Equal(first, File.ReadAllText(Path.Combine(checkout, "tested.sha")).Trim());
        var second = (await Git(seed, "rev-parse", "HEAD")).Trim();
        Assert.NotEqual(first, second);

        lastGreenForGet = second;
        Assert.Equal(0, await new PostMergeRunCommand(options, output, new FixedClock(), http,
            () => new(0.1, 0)).ExecuteAsync(CancellationToken.None));
        Assert.Equal("skipped", received[^1].Status);
        Assert.Equal(second, received[^1].TipSha);
        Assert.Equal(first, File.ReadAllText(Path.Combine(checkout, "tested.sha")).Trim());
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = System.Net.Http.Json.JsonContent.Create(value),
    };

    private static async Task<string> Git(string root, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, root, ct: CancellationToken.None);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
