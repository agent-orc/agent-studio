using System.Net;
using System.Diagnostics;
using System.Text;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class OneShotJobClientTests
{
    [Fact]
    public async Task Missing_claim_route_logs_once_and_retries_after_five_minutes()
    {
        var attempts = 0;
        using var http = new HttpClient(new SequenceHandler(() =>
            ++attempts < 3 ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("null", Encoding.UTF8, "application/json") }))
        { BaseAddress = new Uri("http://task-server/") };
        using var client = new TaskServerClient(http, "runner-a", runnerInstanceId: "instance-a");
        using var stop = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var logs = new List<string>();
        var admission = new OneShotJobAdmission(new RunnerOptions {
            ServerUrl = "http://task-server/", RunnerId = "runner-a", RunnerName = "runner-a",
            Hostname = "localhost", BackendName = "test", WorkDir = Path.GetTempPath(),
            BaseBranch = "develop",
        },
            client, logs.Add, (interval, _) =>
            {
                delays.Add(interval);
                if (delays.Count == 3) stop.Cancel();
                return Task.CompletedTask;
            });

        await admission.PollAsync(stop.Token);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3)], delays);
        Assert.Single(logs, line => line.StartsWith("one-shot-job-poll-failed", StringComparison.Ordinal));
        Assert.Single(logs, line => line == "one-shot-job-poll-restored");
    }

    [Fact]
    public async Task Provider_turn_does_not_inherit_runner_edge_credentials()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string variable = "CONNECTOR_AGT3505_TEST";
        var previous = Environment.GetEnvironmentVariable(variable);
        var directory = Path.Combine(Path.GetTempPath(), "one-shot-env-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable(variable, "runtime-test-" + Guid.NewGuid().ToString("N"));
            var cli = Path.Combine(directory, "fake-cli.sh");
            await File.WriteAllTextAsync(cli,
                "#!/bin/sh\ncat >/dev/null\nif [ -n \"$CONNECTOR_AGT3505_TEST\" ]; then echo leaked; else echo clean; fi\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var runner = new OneShotJobAdmission(new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            }, null!, _ => { });
            var request = new OneShotJobRequest("codex", "test-model", "describe", null, 10, 65_536);

            var result = await runner.RunBoundedTurnAsync(request, directory, CancellationToken.None);

            Assert.Equal("clean", result.StdOut.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Non_image_turn_stops_when_raw_output_exceeds_the_cap()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "one-shot-text-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cli = Path.Combine(directory, "fake-cli.sh");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\ncat ./payload\nsleep 20\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(directory, "payload"), new string('x', 12_000));
            var runner = new OneShotJobAdmission(new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            }, null!, _ => { });
            var request = new OneShotJobRequest("codex", "test-model", "describe", null,
                20, 1_024);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var elapsed = Stopwatch.StartNew();

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunBoundedTurnAsync(request, directory, deadline.Token));

            Assert.Contains("byte cap", error.Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Exceeded cap after {elapsed}.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Large_input_does_not_block_detection_of_oversize_output()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "one-shot-large-input-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cli = Path.Combine(directory, "fake-cli.sh");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\ncat ./payload\nsleep 20\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(directory, "payload"), new string('x', 12_000));
            var runner = new OneShotJobAdmission(new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            }, null!, _ => { });
            var request = new OneShotJobRequest("codex", "test-model", new string('p', 220_000), null,
                20, 1_024);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var elapsed = Stopwatch.StartNew();

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunBoundedTurnAsync(request, directory, deadline.Token));

            Assert.Contains("byte cap", error.Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Exceeded cap after {elapsed.Elapsed}.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Text_turn_counts_utf8_bytes_newlines_and_both_streams()
    {
        var newlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
        var multibyte = new BoundedTurnOutput(4 + newlineBytes * 2 - 1);
        Assert.True(multibyte.TryAppend("stdout", "é"));
        Assert.False(multibyte.TryAppend("stdout", "é"));
        Assert.Equal("é" + Environment.NewLine, multibyte.Stdout);

        var output = new BoundedTurnOutput(4 + newlineBytes * 2);

        Assert.True(output.TryAppend("stdout", "éé"));
        Assert.False(output.TryAppend("stderr", "x"));
        Assert.True(output.Exceeded);
        Assert.Equal("éé" + Environment.NewLine, output.Stdout);
        Assert.Empty(output.Stderr);

        var combined = new BoundedTurnOutput(2 + newlineBytes * 2);
        Assert.True(combined.TryAppend("stdout", "a"));
        Assert.True(combined.TryAppend("stderr", "b"));
        Assert.False(combined.TryAppend("stdout", ""));
        Assert.Equal("a" + Environment.NewLine, combined.Stdout);
        Assert.Equal("b" + Environment.NewLine, combined.Stderr);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Image_turn_rejects_oversize_output_before_the_deadline()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "one-shot-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cli = Path.Combine(directory, "fake-cli.sh");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\ncat ./payload\nsleep 20\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(directory, "payload"), new string('x', 270_000));
            var runner = new OneShotJobAdmission(new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            }, null!, _ => { });
            var request = new OneShotJobRequest("codex", "test-model", "describe", null,
                20, 65_536, [new(Convert.ToBase64String(new byte[] { 1, 2, 3 }), "image/png")]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var elapsed = Stopwatch.StartNew();

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunBoundedTurnAsync(request, directory, deadline.Token));

            Assert.Contains("byte cap", error.Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Exceeded cap after {elapsed.Elapsed}.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Text_turn_rejects_raw_output_even_when_it_has_no_reply()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "one-shot-text-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cli = Path.Combine(directory, "fake-cli.sh");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\ncat ./payload\nsleep 20\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(directory, "payload"), new string('x', 1000));
            var runner = new OneShotJobAdmission(new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            }, null!, _ => { });
            // The child emits over-cap output without reading stdin. The prompt
            // fills the input pipe unless output is drained concurrently.
            var request = new OneShotJobRequest("codex", "test-model", new string('p', 200_000), null, 20, 100);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var elapsed = Stopwatch.StartNew();

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunBoundedTurnAsync(request, directory, deadline.Token));

            Assert.Contains("raw output exceeds the byte cap", error.Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Exceeded cap after {elapsed}.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Runner_claims_a_bounded_job_and_completes_with_its_claim_token()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server/") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true,
            runnerInstanceId: "instance-a");

        var work = await client.ClaimOneShotJobAsync("runner-a", default);
        Assert.NotNull(work);
        Assert.Equal("hello", work.Request.Prompt);
        Assert.Contains("instance-a", handler.ClaimBody);
        Assert.True(await client.CompleteOneShotJobAsync(new OneShotJobCompletion(
            "runner-a", "instance-a", work.JobId, work.ClaimToken, true, "done", null,
            new(1, 2, 0, 0, "gpt-6-sol")), default));
        Assert.Contains("claim-1", handler.CompletionBody);
        Assert.Contains("instance-a", handler.CompletionBody);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string ClaimBody { get; private set; } = string.Empty;
        public string CompletionBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string result;
            if (path == "/api/v1/one-shot-jobs/claim")
            {
                ClaimBody = body;
                result = """{"jobId":"job-1","claimToken":"claim-1","request":{"cli":"codex","model":"gpt-6-sol","prompt":"hello","thinkingLevel":"medium","timeoutSeconds":30,"maxOutputBytes":100}}""";
            }
            else
            {
                Assert.Equal("/api/v1/one-shot-jobs/complete", path);
                CompletionBody = body;
                result = "true";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SequenceHandler(Func<HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/one-shot-jobs/claim", request.RequestUri?.AbsolutePath);
            return Task.FromResult(next());
        }
    }
}
