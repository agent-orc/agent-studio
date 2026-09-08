using System.Net;
using System.Text.Json;

using AgentRunner;

using Xunit;
using Xunit.Abstractions;

namespace AgentRunner.Tests;

public sealed class RemoteProjectChatRunnerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "remote-project-chat-" + Guid.NewGuid().ToString("N"));

    public RemoteProjectChatRunnerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort for test cleanup */ }
    }

    [Fact]
    public void Claude_result_envelope_is_parsed_for_cross_family_chat_claim()
    {
        var process = new ProcessResult(
            0,
            "{\"type\":\"result\",\"result\":\"fallback reply\",\"is_error\":false,\"usage\":{\"input_tokens\":7,\"output_tokens\":3,\"cache_read_input_tokens\":2,\"cache_creation_input_tokens\":1}}\n",
            "");

        var parsed = RemoteProjectChatRunner.ParseClaude(process, "claude-opus-5");

        Assert.True(parsed.Success);
        Assert.Equal("fallback reply", parsed.ReplyText);
        Assert.Equal(7, parsed.TokenUsage?.InputTokens);
        Assert.Equal(3, parsed.TokenUsage?.OutputTokens);
        Assert.Equal(2, parsed.TokenUsage?.CacheReadTokens);
        Assert.Equal(1, parsed.TokenUsage?.CacheCreationTokens);
    }

    [Fact]
    public async Task Turn_runs_from_host_project_checkout_and_reports_path_branch_and_head()
    {
        if (OperatingSystem.IsWindows())
            return;

        var origin = Path.Combine(_root, "origin.git");
        var expectedHead = await SeedOriginAsync(origin);
        var codex = await WriteCodexFixtureAsync();
        var options = Options(codex);
        var handler = new CompletionHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, options.RunnerId);
        var runner = new RemoteProjectChatRunner(options, client, _output.WriteLine);
        var work = new RemoteChatWorkItem(
            "work-1",
            "claim-1",
            RemoteChatWorkKinds.Turn,
            "PROJ-002",
            "Agent Studio",
            origin,
            "main",
            "Report the current working directory.",
            "gpt-5.5",
            "high",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2));

        var exitCode = await runner.RunAsync(work, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(handler.CompletionJson);
        using var completion = JsonDocument.Parse(handler.CompletionJson!);
        var root = completion.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());

        var context = root.GetProperty("executionContext");
        var repoPath = context.GetProperty("repoPath").GetString();
        Assert.NotNull(repoPath);
        Assert.True(Directory.Exists(repoPath));
        Assert.StartsWith(Path.GetFullPath(options.WorkDir), Path.GetFullPath(repoPath!));
        Assert.Equal("remote", context.GetProperty("executionKind").GetString());
        Assert.Equal("agent-runner-01", context.GetProperty("hostName").GetString());
        Assert.Equal("main", context.GetProperty("branch").GetString());
        Assert.Equal(expectedHead, context.GetProperty("headSha").GetString());

        // The fake Codex process emits its actual PWD as tool output. Matching
        // that value to the reported context proves execution happened inside
        // the host checkout, not merely that metadata named such a path.
        var reply = root.GetProperty("replyText").GetString();
        Assert.Contains($"tool-output cwd={repoPath}", reply);
        _output.WriteLine(reply);
    }

    private RunnerOptions Options(string codex) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-01",
        RunnerName = "assigned-runner",
        Hostname = "agent-runner-01",
        BackendName = "test",
        WorkDir = Path.Combine(_root, "runner-work"),
        BaseBranch = "main",
        CliBin = codex,
        CodexCliBin = codex,
        CliArgs = "",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 20,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private async Task<string> WriteCodexFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "fake-codex");
        await File.WriteAllTextAsync(path, """
            #!/bin/sh
            cat >/dev/null
            printf '{"type":"item.completed","item":{"type":"agent_message","text":"tool-output cwd=%s"}}\n' "$PWD"
            printf '{"type":"turn.completed","usage":{"input_tokens":3,"output_tokens":2,"cached_input_tokens":1}}\n'
            """);
        var chmod = await ProcessRunner.RunAsync("chmod", ["u+x", path], workingDirectory: _root);
        Assert.True(chmod.Success, $"chmod failed ({chmod.ExitCode}): {chmod.StdErr}");
        return path;
    }

    private async Task<string> SeedOriginAsync(string origin)
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", origin);
        await GitAsync(_root, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "remote project chat fixture");
        await GitAsync(seed, "add", "--all");
        await GitAsync(
            seed,
            "-c", "user.name=Test",
            "-c", "user.email=test@example.invalid",
            "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory);
        Assert.True(
            result.Success,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
        return result;
    }

    private sealed class CompletionHandler : HttpMessageHandler
    {
        public string? CompletionJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/runner/project-chat/complete")
                CompletionJson = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}
