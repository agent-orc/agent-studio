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
            var options = new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            };
            var runner = new OneShotJobAdmission(options, null!, _ => { });
            var request = new OneShotJobRequest("codex", "test-model", "describe", null, 10, 65_536);

            var result = await runner.RunProviderTurnAsync(request, directory, CancellationToken.None);

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
            await File.WriteAllTextAsync(Path.Combine(directory, "payload"), new string('x', 270_000));
            var options = new RunnerOptions
            {
                ServerUrl = "http://localhost/", RunnerId = "test-runner", RunnerName = "test-runner",
                Hostname = "localhost", BackendName = "test", WorkDir = directory,
                BaseBranch = "develop", CodexCliBin = cli,
            };
            var runner = new OneShotJobAdmission(options, null!, _ => { });
            var request = new OneShotJobRequest("codex", "test-model", "describe", null,
                20, 65_536);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var elapsed = Stopwatch.StartNew();

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunProviderTurnAsync(request, directory, deadline.Token));

            Assert.Contains("byte cap", error.Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Exceeded cap after {elapsed}.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
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
                runner.RunProviderTurnAsync(request, directory, deadline.Token));

            Assert.Contains("byte cap", error.Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Exceeded cap after {elapsed.Elapsed}.");
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
            "runner-a", work.JobId, work.ClaimToken, true, "done", null,
            new(1, 2, 0, 0, "gpt-6-sol")), default));
        Assert.Contains("claim-1", handler.CompletionBody);
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
}
