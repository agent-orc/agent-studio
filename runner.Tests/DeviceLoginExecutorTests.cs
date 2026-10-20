using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeviceLoginExecutorTests
{
    [Fact]
    public async Task Runner_claims_reports_instructions_and_completes_over_the_outbound_channel()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var code = new string('A', 4) + "-" + new string('B', 4);
            var credential = Path.Combine(directory, "auth.json");
            var cli = Path.Combine(directory, "fake-codex");
            await File.WriteAllTextAsync(cli, $"#!/bin/sh\nprintf '%s\\n' 'https://auth.openai.com/codex/device' '{code}'\nprintf fixture > '{credential}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var calls = new List<(string Path, string Body)>();
            using var http = new HttpClient(new Handler(async request =>
            {
                calls.Add((request.RequestUri!.AbsolutePath,
                    request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
                return request.RequestUri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                        new DeviceLoginClaimDto("dlogin_fixture", "codex", clock.GetUtcNow().UtcDateTime.AddMinutes(15))) }
                    : new HttpResponseMessage(HttpStatusCode.OK);
            })) { BaseAddress = new Uri("https://task-server.test") };
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            var claim = await client.ClaimDeviceLoginAsync(default);
            Assert.NotNull(claim);
            var result = await new DeviceLoginExecutor(cli, credential, _ => Task.FromResult(true), clock)
                .RunAsync((message, ct) => client.ReportDeviceLoginInstructionsAsync(claim.Handle, message, ct), default);
            await client.CompleteDeviceLoginAsync(claim.Handle, result, default);
            Assert.Equal("completed", result.Result);
            Assert.Equal(3, calls.Count);
            Assert.EndsWith("/claim", calls[0].Path);
            Assert.Contains(code, calls[1].Body);
            Assert.EndsWith("/completion", calls[2].Path);
            Assert.DoesNotContain(code, calls[2].Body);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Fake_cli_streams_code_and_completes_after_credential_and_probe()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var code = new string('A', 4) + "-" + new string('B', 4);
            var credential = Path.Combine(directory, "auth.json");
            var cli = Path.Combine(directory, "fake-codex");
            await File.WriteAllTextAsync(cli, $"#!/bin/sh\nprintf '%s\\n' 'https://auth.openai.com/codex/device' '{code}'\nprintf fixture > '{credential}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeviceLoginInstructionsRequest? instructions = null;
            var result = await new DeviceLoginExecutor(cli, credential, _ => Task.FromResult(true))
                .RunAsync((message, _) => { instructions = message; return Task.CompletedTask; }, default);
            Assert.Equal("completed", result.Result);
            Assert.Equal(code, instructions?.UserCode);
            Assert.Equal("https://auth.openai.com/codex/device", instructions?.VerificationUrl);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Fake_cli_timeout_kills_login_without_credential()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var code = new string('A', 4) + "-" + new string('B', 4);
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
            var cli = Path.Combine(directory, "fake-codex");
            await File.WriteAllTextAsync(cli, $"#!/bin/sh\nprintf '%s\\n' 'https://auth.openai.com/codex/device' '{code}'\nsleep 10\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            var instructions = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = new DeviceLoginExecutor(cli, Path.Combine(directory, "auth.json"),
                _ => Task.FromResult(true), clock).RunAsync((_, _) =>
                {
                    instructions.TrySetResult(true);
                    return Task.CompletedTask;
                }, cancel.Token);
            await instructions.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(6));
            var result = await run;
            Assert.Equal("failed", result.Result);
            Assert.Contains("timed out", result.Detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Login_file_without_successful_real_probe_is_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var code = new string('A', 4) + "-" + new string('B', 4);
            var credential = Path.Combine(directory, "auth.json");
            var cli = Path.Combine(directory, "fake-codex");
            await File.WriteAllTextAsync(cli, $"#!/bin/sh\nprintf '%s\\n' 'https://auth.openai.com/codex/device' '{code}'\nprintf fixture > '{credential}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var result = await new DeviceLoginExecutor(cli, credential, _ => Task.FromResult(false))
                .RunAsync((_, _) => Task.CompletedTask, default);
            Assert.Equal("failed", result.Result);
            Assert.Contains("probe did not succeed", result.Detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
