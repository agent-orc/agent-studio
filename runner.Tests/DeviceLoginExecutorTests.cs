using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
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

    [Fact]
    public async Task Executor_runs_only_the_bounded_login_command_of_each_provider()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        try
        {
            var code = new string('A', 4) + "-" + new string('B', 4);
            var token = "sk-ant-" + new string('x', 32);
            var credential = Path.Combine(directory, "auth.json");
            var script = Path.Combine(directory, "fake-provider");
            await File.WriteAllTextAsync(script,
                "#!/bin/sh\ncase \"$*\" in\n"
                + $"  'login --device-auth') printf '%s\\n' 'https://auth.openai.com/codex/device' '{code}'; printf fixture > '{credential}';;\n"
                + $"  'setup-token') printf '%s\\n' 'https://claude.ai/oauth/authorize' '{token}';;\n"
                + "  *) exit 9;;\n"
                + "esac\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal("completed", (await new DeviceLoginExecutor(script, credential, _ => Task.FromResult(true))
                .RunAsync((_, _) => Task.CompletedTask, default)).Result);
            Assert.Equal("completed", (await new DeviceLoginExecutor(script, Path.Combine(directory, "claude-token"),
                _ => Task.FromResult(true), provider: "claude").RunAsync((_, _) => Task.CompletedTask, default)).Result);
            var unsupported = await new DeviceLoginExecutor(script, credential, _ => Task.FromResult(true),
                provider: "unknown").RunAsync((_, _) => Task.CompletedTask, default);
            Assert.Equal("failed", unsupported.Result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Claude_setup_token_completes_only_after_a_changed_token_file_and_probe()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        try
        {
            var token = "sk-ant-" + new string('y', 32);
            var tokenFile = DeviceLoginExecutor.ClaudeTokenPath(directory);
            var cli = Path.Combine(directory, "fake-claude");
            await File.WriteAllTextAsync(cli,
                $"#!/bin/sh\nprintf '%s\\n' 'Visit https://claude.ai/oauth/authorize?client_id=studio' '{token}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeviceLoginInstructionsRequest? instructions = null;
            string? probedToken = null;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "previous-token");

            var rejected = await new DeviceLoginExecutor(cli, tokenFile, _ => Task.FromResult(false), provider: "claude")
                .RunAsync((_, _) => Task.CompletedTask, default);
            Assert.Equal("failed", rejected.Result);
            Assert.Contains("probe did not succeed", rejected.Detail);
            Assert.False(File.Exists(tokenFile));
            Assert.Equal("previous-token", Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));

            var result = await new DeviceLoginExecutor(cli, tokenFile, _ =>
                {
                    probedToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
                    return Task.FromResult(true);
                }, provider: "claude")
                .RunAsync((message, _) => { instructions = message; return Task.CompletedTask; }, default);
            Assert.Equal("completed", result.Result);
            Assert.Equal("https://claude.ai/oauth/authorize?client_id=studio", instructions?.VerificationUrl);
            Assert.Null(instructions?.UserCode);
            Assert.Equal(token, probedToken);
            Assert.Equal(token, (await File.ReadAllTextAsync(tokenFile)).Trim());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
            Assert.DoesNotContain(token, result.Detail);

            var unchanged = await new DeviceLoginExecutor(cli, tokenFile, _ => Task.FromResult(true), provider: "claude")
                .RunAsync((_, _) => Task.CompletedTask, default);
            Assert.Equal("failed", unchanged.Result);
            Assert.Contains("did not update", unchanged.Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Extracts_only_provider_verification_urls_and_short_codes()
    {
        Assert.Equal("https://auth.openai.com/codex/device",
            DeviceLoginExecutor.SafeVerificationUrl("Visit https://auth.openai.com/codex/device to continue", "codex"));
        Assert.Equal("ABCD-EFGH", DeviceLoginExecutor.DeviceCode("Enter ABCD-EFGH"));
        Assert.Equal("https://auth.openai.com/codex/device",
            DeviceLoginExecutor.SafeVerificationUrl("\u001b[32mhttps://auth.openai.com/codex/device\u001b[0m", "codex"));
        Assert.Null(DeviceLoginExecutor.SafeVerificationUrl("Visit https://evil.example/codex/device to continue", "codex"));
        Assert.Null(DeviceLoginExecutor.SafeVerificationUrl("Visit https://claude.ai/oauth/authorize", "codex"));
        Assert.Equal("https://claude.ai/oauth/authorize",
            DeviceLoginExecutor.SafeVerificationUrl("Visit https://claude.ai/oauth/authorize.", "claude"));
        Assert.Null(DeviceLoginExecutor.DeviceCode("test-" + new string('x', 48)));
        var claudeToken = "sk-ant-" + new string('x', 32);
        Assert.Equal(claudeToken, DeviceLoginExecutor.ClaudeToken(claudeToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
