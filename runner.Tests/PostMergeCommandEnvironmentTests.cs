using AgentRunner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PostMergeCommandEnvironmentTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Commands_receive_tool_paths_and_project_values_without_daemon_settings_or_credentials()
    {
        var fakeCredential = "fixture-" + Guid.NewGuid().ToString("N");
        var daemon = new Dictionary<string, string?>
        {
            ["RUNNER_SERVER_URL"] = "https://task-server.invalid",
            ["RUNNER_AUTH_TOKEN"] = fakeCredential,
            ["RUNNER_AUTH_TOKEN_FILE"] = "/daemon/token",
            ["POST_MERGE_TOKEN_FILE"] = "/daemon/post-merge-token",
            ["POST_MERGE_SERVER_URL"] = "https://studio.invalid",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = fakeCredential,
            ["OPENAI_API_KEY"] = fakeCredential,
            ["GH_TOKEN"] = fakeCredential,
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = fakeCredential,
            ["SSH_AUTH_SOCK"] = "/daemon/ssh-agent",
            ["AGENT_HOST_SERVER_URL"] = "https://host.invalid",
            ["DAEMON_UNRELATED_SETTING"] = "daemon-only",
            ["LANG"] = "C",
            ["DOTNET_ROOT"] = "/toolchain/dotnet",
            ["PLAYWRIGHT_BROWSERS_PATH"] = "/toolchain/playwright",
            ["PROJECT_SETTING"] = "daemon-value",
        };
        var original = daemon.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in daemon) Environment.SetEnvironmentVariable(name, value);
            using var http = new HttpClient();
            using var output = new StringWriter();
            var options = new PostMergeRunOptions("demo", Path.GetTempPath(), "http://test/",
                "develop", "origin", "unused", "nightly", 6, TimeSpan.Zero, 1, [], false);
            var worker = new PostMergeRunCommand(options, output, new FakeTimeProvider(), http);
            // Print presence only. Even the failing regression must never print a host secret.
            var forbidden = daemon.Keys.Except(["LANG", "DOTNET_ROOT", "PLAYWRIGHT_BROWSERS_PATH", "PROJECT_SETTING"]);
            var command = string.Join("; ", forbidden.Select(name =>
                $"printf '%s\\n' \"{name}=${{{name}+present}}\""))
                + "; printf '%s\\n' \"PATH=${PATH:+present}\" \"HOME=${HOME:+present}\""
                + " \"LANG=$LANG\" \"DOTNET_ROOT=$DOTNET_ROOT\" \"PROJECT_SETTING=$PROJECT_SETTING\""
                + " \"PLAYWRIGHT_BROWSERS_PATH=$PLAYWRIGHT_BROWSERS_PATH\"";
            var result = await worker.RunCommand(command,
                new Dictionary<string, string> { ["PROJECT_SETTING"] = "project-value" }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in forbidden) Assert.Contains(name + "=", lines);
            Assert.Contains("PATH=present", lines);
            Assert.Contains("HOME=present", lines);
            Assert.Contains("LANG=C", lines);
            Assert.Contains("DOTNET_ROOT=/toolchain/dotnet", lines);
            Assert.Contains("PLAYWRIGHT_BROWSERS_PATH=/toolchain/playwright", lines);
            Assert.Contains("PROJECT_SETTING=project-value", lines);
            Assert.DoesNotContain(fakeCredential, output.ToString());
        }
        finally
        {
            foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
