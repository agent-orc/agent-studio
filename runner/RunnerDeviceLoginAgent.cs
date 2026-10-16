using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
        using var process = CreateProcess(work.Provider, status: false);
        if (!process.Start()) return false;
        using var cancellation = ct.Register(() => Kill(process));
        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var stdout = PumpAsync(process.StandardOutput, lines.Writer, ct);
        var stderr = PumpAsync(process.StandardError, lines.Writer, ct);
        string? claudeToken = null;
        var reader = Task.Run(async () =>
        {
            string? url = null;
            string? code = null;
            var sent = false;
            await foreach (var line in lines.Reader.ReadAllAsync(ct))
            {
                url ??= SafeVerificationUrl(line);
                code ??= DeviceCode(line);
                if (work.Provider == "claude") claudeToken ??= ClaudeToken(line);
                if (sent || url is null || work.Provider == "codex" && code is null) continue;
                await client.ReportDeviceLoginInstructionsAsync(options.RunnerId, work.Id,
                    new ReportRunnerDeviceInstructionsRequest(url, code), ct);
                sent = true;
            }
            return sent;
        }, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        lines.Writer.TryComplete();
        var instructionsSent = await reader;
        if (process.ExitCode != 0 || !instructionsSent) return false;
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
            using var status = CreateProcess(work.Provider, status: true);
            if (!status.Start()) return false;
            using var statusCancellation = ct.Register(() => Kill(status));
            var stdoutDrain = status.StandardOutput.ReadToEndAsync(ct);
            var stderrDrain = status.StandardError.ReadToEndAsync(ct);
            await status.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutDrain, stderrDrain);
            if (status.ExitCode != 0) return false;
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

    private Process CreateProcess(string provider, bool status)
    {
        var start = new ProcessStartInfo
        {
            FileName = provider == "codex" ? options.CodexCliBin : options.ClaudeCliBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] arguments = provider == "codex"
            ? status ? ["login", "status"] : ["login", "--device-auth"]
            : status ? ["auth", "status", "--text"] : ["setup-token"];
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["NO_BROWSER"] = "1";
        start.Environment["BROWSER"] = "false";
        return new Process { StartInfo = start };
    }

    private static async Task PumpAsync(StreamReader stream, ChannelWriter<string> writer, CancellationToken ct)
    {
        while (await stream.ReadLineAsync(ct) is { } line)
            writer.TryWrite(line);
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

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
