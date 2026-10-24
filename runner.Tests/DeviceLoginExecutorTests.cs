using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using System.Diagnostics;
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
            Assert.False(File.Exists(credential));

            await File.WriteAllTextAsync(credential, "previous credential");
            var renewed = await new DeviceLoginExecutor(cli, credential, _ => Task.FromResult(false))
                .RunAsync((_, _) => Task.CompletedTask, default);
            Assert.Equal("failed", renewed.Result);
            Assert.Equal("previous credential", await File.ReadAllTextAsync(credential));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Cancel_during_parked_codex_probe_restores_the_previous_auth_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var credential = Path.Combine(directory, "auth.json");
            await File.WriteAllTextAsync(credential, "previous credential");
            var cli = Path.Combine(directory, "fake-codex");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\nprintf '%s\\n' 'https://auth.openai.com/codex/device' 'ABCD-EFGH'\n"
                + $"printf replacement > '{credential}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var probing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = new CancellationTokenSource();
            var run = new DeviceLoginExecutor(cli, credential, async ct =>
            {
                probing.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, clock, ct);
                return true;
            }, clock).RunAsync((_, _) => Task.CompletedTask, cancel.Token);
            await probing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("replacement", await File.ReadAllTextAsync(credential));
            cancel.Cancel();
            Assert.Equal("failed", (await run.WaitAsync(TimeSpan.FromSeconds(10))).Result);
            Assert.Equal("previous credential", await File.ReadAllTextAsync(credential));
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
            string? processTokenDuringProbe = null;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "previous-token");

            var rejected = await new DeviceLoginExecutor(cli, tokenFile, _ => Task.FromResult(false), provider: "claude")
                .RunAsync((_, _) => Task.CompletedTask, default);
            Assert.Equal("failed", rejected.Result);
            Assert.Contains("probe did not succeed", rejected.Detail);
            Assert.False(File.Exists(tokenFile));
            Assert.Equal("previous-token", Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));

            var result = await new DeviceLoginExecutor(cli, tokenFile, _ =>
                {
                    probedToken = ProviderAuthEnvironment.ClaudeTokenForChild();
                    processTokenDuringProbe = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
                    return Task.FromResult(true);
                }, provider: "claude")
                .RunAsync((message, _) => { instructions = message; return Task.CompletedTask; }, default);
            Assert.Equal("completed", result.Result);
            Assert.Equal("https://claude.ai/oauth/authorize?client_id=studio", instructions?.VerificationUrl);
            Assert.Null(instructions?.UserCode);
            Assert.Equal(token, probedToken);
            // Only the probe child saw the unverified token; the daemon switched after success.
            Assert.Equal("previous-token", processTokenDuringProbe);
            Assert.Equal(token, Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claude_candidate_gets_a_fresh_real_request_without_prior_credential_or_with_recent_old_success(bool oldSuccess)
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        try
        {
            var candidate = "sk-ant-" + new string('c', 32);
            var oldToken = "sk-ant-" + new string('o', 32);
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken,
                oldSuccess ? oldToken : null);
            var realCalls = 0;
            var probe = new ProviderAuthProbe(
                launcher: (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
                executableExists: _ => true,
                clock: clock.GetUtcNow,
                credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "fixture",
                    EffectiveSource: oldSuccess ? "runner-env" : "absent",
                    CredentialGeneration: oldSuccess ? "old-generation" : null));
            probe.UseRealRequest((_, _, _) =>
            {
                realCalls++;
                var seen = ProviderAuthEnvironment.ClaudeTokenForChild();
                return Task.FromResult(new ProcessResult(seen is not null ? 0 : 1, "OK", ""));
            }, null!);
            if (oldSuccess)
            {
                var old = await probe.RefreshAsync("claude", CancellationToken.None);
                Assert.Equal(clock.GetUtcNow(), old.LastRealSuccessAt);
                clock.Advance(TimeSpan.FromMinutes(5));
            }
            var beforeCandidate = realCalls;
            var cli = Path.Combine(directory, "fake-claude");
            await File.WriteAllTextAsync(cli,
                $"#!/bin/sh\nprintf '%s\\n' 'https://claude.ai/oauth/authorize' '{candidate}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = DeviceLoginExecutor.ClaudeTokenPath(directory);
            var result = await new DeviceLoginExecutor(cli, file, clock: clock, provider: "claude", authProbe: probe)
                .RunAsync((_, _) => Task.CompletedTask, CancellationToken.None);
            Assert.Equal("completed", result.Result);
            Assert.Equal(beforeCandidate + 1, realCalls);
            Assert.Equal(candidate, (await File.ReadAllTextAsync(file)).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Claude_probe_success_after_session_failure_accepts_no_credential()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        try
        {
            var server = new FakeDeviceLoginServer(clock, "claude");
            using var http = server.Client();
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            var candidate = "sk-ant-" + new string('q', 32);
            var cli = Path.Combine(directory, "fake-claude");
            await File.WriteAllTextAsync(cli,
                $"#!/bin/sh\nprintf '%s\\n' 'https://claude.ai/oauth/authorize' '{candidate}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = DeviceLoginExecutor.ClaudeTokenPath(directory);
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, "previous-token");
            var result = await new DeviceLoginExecutor(cli, file, _ =>
                {
                    server.State = "failed";
                    return Task.FromResult(true);
                }, clock, "claude", sessionRunning: async ct =>
                    await client.DeviceLoginStateAsync("dlogin_fixture", ct) == "running")
                .RunAsync((message, ct) => client.ReportDeviceLoginInstructionsAsync(
                    "dlogin_fixture", message, ct), CancellationToken.None);
            Assert.Equal("failed", result.Result);
            Assert.False(File.Exists(file));
            Assert.Equal("previous-token", Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rejected_Claude_completion_restores_the_previous_credential()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        try
        {
            var oldToken = "sk-ant-" + new string('o', 32);
            var candidate = "sk-ant-" + new string('n', 32);
            var file = DeviceLoginExecutor.ClaudeTokenPath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, oldToken + "\n");
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, oldToken);
            var cli = Path.Combine(directory, "fake-claude");
            await File.WriteAllTextAsync(cli,
                $"#!/bin/sh\nprintf '%s\\n' 'https://claude.ai/oauth/authorize' '{candidate}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var probe = new ProviderAuthProbe(clock: clock.GetUtcNow);
            probe.UseRealRequest((_, _, _) => Task.FromResult(new ProcessResult(
                ProviderAuthEnvironment.ClaudeTokenForChild() == candidate ? 0 : 1, "OK", "")), null!);
            var server = new FakeDeviceLoginServer(clock, "claude") { RejectCompletion = true };
            using var http = server.Client();
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            using var stop = new CancellationTokenSource();
            var run = new DeviceLoginPoller(client, _ => { }, directory,
                claudeBinary: cli, clock: clock, authProbe: probe).RunAsync(stop.Token);
            Assert.Equal("completed", (await server.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Result);
            await WaitUntilAsync(() => File.ReadAllText(file).Trim() == oldToken
                && Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken) == oldToken);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Lost_completion_response_keeps_the_accepted_Claude_credential()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        try
        {
            var oldToken = "sk-ant-" + new string('o', 32);
            var candidate = "sk-ant-" + new string('n', 32);
            var file = DeviceLoginExecutor.ClaudeTokenPath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, oldToken + "\n");
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, oldToken);
            var cli = Path.Combine(directory, "fake-claude");
            await File.WriteAllTextAsync(cli,
                $"#!/bin/sh\nprintf '%s\\n' 'https://claude.ai/oauth/authorize' '{candidate}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var probe = new ProviderAuthProbe(clock: clock.GetUtcNow);
            probe.UseRealRequest((_, _, _) => Task.FromResult(new ProcessResult(
                ProviderAuthEnvironment.ClaudeTokenForChild() == candidate ? 0 : 1, "OK", "")), null!);
            var server = new FakeDeviceLoginServer(clock, "claude") { LoseCompletionResponse = true };
            using var http = server.Client();
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            using var stop = new CancellationTokenSource();
            var run = new DeviceLoginPoller(client, _ => { }, directory,
                claudeBinary: cli, clock: clock, authProbe: probe).RunAsync(stop.Token);
            Assert.Equal("completed", (await server.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Result);
            await WaitUntilAsync(() => File.ReadAllText(file).Trim() == candidate
                && Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken) == candidate);
            Assert.Equal("completed", server.State);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Studio_cancel_stops_a_parked_claude_login_and_accepts_no_token()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "previous-token");
            var token = "sk-ant-" + new string('z', 32);
            var pidFile = Path.Combine(directory, "cli.pid");
            var cli = ParkedCli(directory, pidFile, "https://claude.ai/oauth/authorize", token);
            var server = new FakeDeviceLoginServer(clock, "claude");
            using var http = server.Client();
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            var logs = new List<string>();
            using var stop = new CancellationTokenSource();
            var run = new DeviceLoginPoller(client, line => { lock (logs) logs.Add(line); }, directory,
                claudeBinary: cli, clock: clock).RunAsync(stop.Token);

            await server.Instructions.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pid = await ReadPidAsync(pidFile);
            Assert.True(Alive(pid));
            server.State = "failed"; // The operator cancelled in Studio.
            clock.Advance(DeviceLoginPoller.PollInterval);

            var completion = await server.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("failed", completion.Result);
            await WaitUntilAsync(() => !Alive(pid));
            Assert.False(File.Exists(DeviceLoginExecutor.ClaudeTokenPath(directory)));
            Assert.Equal("previous-token", Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));
            Assert.Contains(server.Paths, path => path.StartsWith("GET ", StringComparison.Ordinal)
                && path.EndsWith("/device-login/dlogin_fixture", StringComparison.Ordinal));
            lock (logs) Assert.DoesNotContain(logs, line => line.Contains(token, StringComparison.Ordinal));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_shutdown_still_reports_the_stopped_sign_in()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pidFile = Path.Combine(directory, "cli.pid");
            var cli = ParkedCli(directory, pidFile, "https://auth.openai.com/codex/device",
                new string('A', 4) + "-" + new string('B', 4));
            var server = new FakeDeviceLoginServer(clock, "codex");
            using var http = server.Client();
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            using var stop = new CancellationTokenSource();
            var run = new DeviceLoginPoller(client, _ => { }, directory, codexBinary: cli, clock: clock)
                .RunAsync(stop.Token);

            await server.Instructions.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pid = await ReadPidAsync(pidFile);
            stop.Cancel();

            var completion = await server.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("failed", completion.Result);
            await WaitUntilAsync(() => !Alive(pid));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Publish_failure_stops_the_login_at_once()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pidFile = Path.Combine(directory, "cli.pid");
            var credential = Path.Combine(directory, "auth.json");
            var cli = ParkedCli(directory, pidFile, "https://auth.openai.com/codex/device",
                new string('A', 4) + "-" + new string('B', 4));
            var result = await new DeviceLoginExecutor(cli, credential, _ => Task.FromResult(true), clock)
                .RunAsync((_, _) => Task.FromException(new HttpRequestException("Task Server unavailable")), default)
                .WaitAsync(TimeSpan.FromSeconds(10)); // The parked CLI would sleep for 30 s.
            Assert.Equal("failed", result.Result);
            Assert.Contains("could not be published", result.Detail);
            Assert.False(Alive(await ReadPidAsync(pidFile)));
            Assert.False(File.Exists(credential));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Login_child_reads_a_closed_stdin_pipe_instead_of_the_daemon_stdin()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var code = new string('A', 4) + "-" + new string('B', 4);
            var credential = Path.Combine(directory, "auth.json");
            var cli = Path.Combine(directory, "fake-codex");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\n"
                + "[ -p /dev/stdin ] || exit 7\n"
                + "if read -r line; then exit 8; fi\n"
                + $"printf '%s\\n' 'https://auth.openai.com/codex/device' '{code}'\nprintf fixture > '{credential}'\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var result = await new DeviceLoginExecutor(cli, credential, _ => Task.FromResult(true))
                .RunAsync((_, _) => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("completed", result.Result);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Verified_token_file_wins_over_runner_env_and_only_the_source_is_logged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "device-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        try
        {
            var stored = "sk-ant-" + new string('f', 32);
            var configured = "sk-ant-" + new string('e', 32);
            var logs = new List<string>();
            using var http = new HttpClient { BaseAddress = new Uri("https://task-server.test") };
            using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);
            var poller = new DeviceLoginPoller(client, logs.Add, directory);
            Directory.CreateDirectory(Path.GetDirectoryName(DeviceLoginExecutor.ClaudeTokenPath(directory))!);
            await File.WriteAllTextAsync(DeviceLoginExecutor.ClaudeTokenPath(directory), stored + "\n");

            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", configured);
            poller.RestoreClaudeToken();
            Assert.Equal(stored, Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));
            Assert.Equal("device-login claude token source=provider-auth-file (overrides runner.env)", logs[^1]);

            File.Delete(DeviceLoginExecutor.ClaudeTokenPath(directory));
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", configured);
            poller.RestoreClaudeToken();
            Assert.Equal(configured, Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));
            Assert.Equal("device-login claude token source=runner-env", logs[^1]);
            Assert.DoesNotContain(logs, line => line.Contains(stored, StringComparison.Ordinal)
                || line.Contains(configured, StringComparison.Ordinal));
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

    /// <summary>Prints the instructions, records its PID and parks like a CLI waiting for the browser.</summary>
    private static string ParkedCli(string directory, string pidFile, string url, string secondLine)
    {
        var cli = Path.Combine(directory, "fake-parked-cli");
        File.WriteAllText(cli, $"#!/bin/sh\necho $$ > '{pidFile}'\nprintf '%s\\n' '{url}' '{secondLine}'\nexec sleep 30\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return cli;
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        await WaitUntilAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        return int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(50);
        Assert.True(condition());
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; } // No such process.
    }

    /// <summary>The runner-bound device-login routes of one claimed session.</summary>
    private sealed class FakeDeviceLoginServer(FakeTimeProvider clock, string provider)
    {
        private int _claims;
        public volatile string State = "running";
        public bool RejectCompletion { get; init; }
        public bool LoseCompletionResponse { get; init; }
        public List<string> Paths { get; } = [];
        public TaskCompletionSource<bool> Instructions { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DeviceLoginCompletionRequest> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient Client() => new(new Handler(RespondAsync)) { BaseAddress = new Uri("https://task-server.test") };

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add($"{request.Method} {path}");
            if (path.EndsWith("/claim", StringComparison.Ordinal))
                return Interlocked.Increment(ref _claims) == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                        new DeviceLoginClaimDto("dlogin_fixture", provider, clock.GetUtcNow().UtcDateTime.AddMinutes(15))) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = JsonContent.Create(new DeviceLoginRunnerStateDto(State)) };
            if (path.EndsWith("/instructions", StringComparison.Ordinal)) Instructions.TrySetResult(true);
            if (path.EndsWith("/completion", StringComparison.Ordinal))
            {
                Completion.TrySetResult((await request.Content!.ReadFromJsonAsync<DeviceLoginCompletionRequest>())!);
                if (RejectCompletion) return new HttpResponseMessage(HttpStatusCode.Conflict);
                if (LoseCompletionResponse)
                {
                    State = "completed";
                    throw new HttpRequestException("Response lost after the transition.");
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
