using System.Text;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal sealed class OneShotJobAdmission(RunnerOptions options, TaskServerClient client, Action<string> log)
{
    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var work = await client.ClaimOneShotJobAsync(options.RunnerId, shutdown);
                if (work is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), shutdown);
                    continue;
                }
                await ExecuteAsync(work, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log($"one-shot-job-poll-failed error={ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(3), shutdown); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ExecuteAsync(OneShotJobWork work, CancellationToken shutdown)
    {
        var request = work.Request;
        var directory = Path.Combine(Path.GetTempPath(), "agent-one-shot", work.JobId);
        Directory.CreateDirectory(directory);
        OneShotJobCompletion completion;
        try
        {
            if (Encoding.UTF8.GetByteCount(request.Prompt) > 220_000
                || request.TimeoutSeconds is < 1 or > 300
                || request.MaxOutputBytes is < 1 or > 65_536
                || request.Images is { Count: > 4 }
                || (request.Images?.Sum(image => (long)image.Base64.Length) ?? 0) > 5_600_000)
                throw new InvalidDataException("one-shot job exceeds its bounded request envelope");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            var process = request.Images is { Count: > 0 }
                ? await RunImageTurnAsync(request, directory, timeout.Token)
                : await ReadOnlyCarTurn.RunAsync(options, work.JobId, request.Cli,
                    request.Prompt, directory, request.Model, request.ThinkingLevel,
                    "agent-one-shot-log", "one-shot", null, log, timeout.Token, shutdown,
                    maxRawOutputBytes: Math.Min(262_144, request.MaxOutputBytes * 4));
            var parsed = request.Cli == "claude"
                ? RemoteProjectChatRunner.ParseClaude(process, request.Model)
                : RemoteProjectChatRunner.ParseCodex(process, request.Model);
            var text = parsed.ReplyText;
            var tooLarge = Encoding.UTF8.GetByteCount(text ?? string.Empty) > request.MaxOutputBytes;
            var usage = parsed.TokenUsage;
            completion = new(options.RunnerId, work.JobId, work.ClaimToken,
                parsed.Success && !tooLarge, tooLarge ? null : text,
                tooLarge ? "one-shot output exceeds the byte cap" : parsed.ErrorMessage,
                usage is null ? null : new(usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens, usage.Model));
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            completion = new(options.RunnerId, work.JobId, work.ClaimToken, false, null,
                $"one-shot job timed out after {request.TimeoutSeconds} seconds", null);
        }
        catch (Exception ex)
        {
            completion = new(options.RunnerId, work.JobId, work.ClaimToken, false, null, ex.Message, null);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log($"one-shot-job-cleanup-failed job={work.JobId} error={ex.Message}"); }
        }
        if (!await client.CompleteOneShotJobAsync(completion, CancellationToken.None))
            log($"one-shot-job-completion-rejected job={work.JobId}");
    }

    internal async Task<ProcessResult> RunImageTurnAsync(OneShotJobRequest request, string directory,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.Cli == "claude" ? options.ClaudeCliBin : options.CodexCliBin,
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (request.Cli == "claude")
        {
            foreach (var arg in new[] { "-p", "--output-format", "stream-json", "--input-format",
                         "stream-json", "--verbose", "--model", request.Model,
                         "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}" })
                start.ArgumentList.Add(arg);
        }
        else
        {
            foreach (var arg in new[] { "exec", "--experimental-json", "--skip-git-repo-check",
                         "--sandbox", "read-only", "-m", request.Model })
                start.ArgumentList.Add(arg);
            var index = 0;
            foreach (var image in request.Images ?? [])
            {
                var suffix = image.MediaType switch
                {
                    "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".png",
                };
                var path = Path.Combine(directory, $"image-{++index}{suffix}");
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(image.Base64), ct);
                start.ArgumentList.Add("--image");
                start.ArgumentList.Add(path);
            }
        }
        foreach (var flag in CodingAgentRunner.Model.CliReasoningFlags.For(
                     request.Cli, request.Model, request.ThinkingLevel))
            start.ArgumentList.Add(flag);
        if (request.Cli == "codex") start.ArgumentList.Add("-");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Provider CLI did not start.");
        try
        {
            var input = request.Cli == "claude" ? ClaudeImageEnvelope(request) : request.Prompt;
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput, 262_144, process, ct);
            var stderr = ReadBoundedAsync(process.StandardError, 8_192, process, ct);
            await process.WaitForExitAsync(ct);
            // Observe both readers before reporting a process error. Either reader
            // kills the child as soon as its cap is reached, so a chatty provider
            // cannot block a full pipe until the job deadline.
            await Task.WhenAll(stdout, stderr);
            return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static string ClaudeImageEnvelope(OneShotJobRequest request)
    {
        var content = new List<object> { new { type = "text", text = request.Prompt } };
        foreach (var image in request.Images ?? [])
            content.Add(new { type = "image", source = new { type = "base64",
                media_type = image.MediaType, data = image.Base64 } });
        return JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }) + "\n";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes,
        Process process, CancellationToken ct)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        var bytes = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, count);
            if (bytes > maxBytes)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new InvalidDataException("one-shot raw output exceeds the byte cap");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
