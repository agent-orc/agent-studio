using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Starts provider login in the runner's own user and credential volume.</summary>
public sealed class RunnerDeviceLoginAgent(RunnerOptions options, TaskServerClient client, Action<string> log)
{
    private string ClaudeTokenPath => Path.Combine(options.StateDir, "provider-auth", "claude-token");

    public async Task PollAsync(CancellationToken shutdown)
    {
        if (File.Exists(ClaudeTokenPath))
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", File.ReadAllText(ClaudeTokenPath).Trim());
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (await client.ClaimDeviceLoginAsync(options.RunnerId, shutdown) is { } work)
                    await ExecuteAsync(work, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Child output and HTTP response bodies are never logged.
                log($"device login deferred type={exception.GetType().Name}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    }

    private async Task ExecuteAsync(RunnerDeviceLoginWork work, CancellationToken shutdown)
    {
        var verified = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            verified = await RunLoginAsync(work, deadline.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            log($"device login failed type={exception.GetType().Name}");
        }
        try { await client.ReportDeviceLoginResultAsync(options.RunnerId, work.Id, verified, shutdown); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            log($"device login result deferred type={exception.GetType().Name}");
        }
    }

    private async Task<bool> RunLoginAsync(RunnerDeviceLoginWork work, CancellationToken ct)
    {
        if (work.Provider is not ("codex" or "claude")) return false;
        string? claudeToken = null;
        string? url = null;
        string? code = null;
        var instructionsSent = false;
        var launcher = new RunnerProviderSetupLauncher(options.CodexCliBin, options.ClaudeCliBin);
        var loginSucceeded = await launcher.RunAsync(work.Provider, status: false, async line =>
        {
            url ??= SafeVerificationUrl(line);
            code ??= DeviceCode(line);
            if (work.Provider == "claude") claudeToken ??= ClaudeToken(line);
            if (instructionsSent || url is null || work.Provider == "codex" && code is null) return;
            await client.ReportDeviceLoginInstructionsAsync(options.RunnerId, work.Id,
                new ReportRunnerDeviceInstructionsRequest(url, code), ct);
            instructionsSent = true;
        }, ct);
        if (!loginSucceeded || !instructionsSent) return false;
        var previousClaudeToken = work.Provider == "claude"
            ? Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") : null;
        if (work.Provider == "claude")
        {
            if (claudeToken is null) return false;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", claudeToken);
        }
        var accepted = false;
        try
        {
            if (!await launcher.RunAsync(work.Provider, status: true, _ => Task.CompletedTask, ct))
                return false;
            if (work.Provider == "claude")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ClaudeTokenPath)!);
                await WriteTokenAsync(ClaudeTokenPath, claudeToken!, ct);
            }
            accepted = true;
            return true;
        }
        finally
        {
            if (!accepted && work.Provider == "claude")
                Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", previousClaudeToken);
        }
    }

    internal static string? SafeVerificationUrl(string line)
    {
        foreach (Match match in Regex.Matches(Clean(line), @"https://[^\s<>]+", RegexOptions.IgnoreCase))
        {
            var value = match.Value.TrimEnd('.', ',', ')', '"', '\'');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
                || uri.Host is not ("auth.openai.com" or "login.openai.com" or "claude.ai" or "console.anthropic.com"))
                continue;
            return value;
        }
        return null;
    }

    internal static string? DeviceCode(string line)
        => Regex.Match(Clean(line), @"\b[A-Z0-9]{4,8}-[A-Z0-9]{4,8}\b").Value is { Length: > 0 } code
            ? code : null;

    internal static string? ClaudeToken(string line)
        => Regex.Match(Clean(line), @"\bsk-ant-[A-Za-z0-9_-]{10,}\b").Value is { Length: > 0 } token
            ? token : null;

    private static string Clean(string line)
        => Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);

    private static async Task WriteTokenAsync(string path, string token, CancellationToken ct)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync(token.AsMemory(), ct);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

}
