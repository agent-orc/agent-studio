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
            var process = await RunProviderTurnAsync(request, directory, timeout.Token);
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

    internal async Task<ProcessResult> RunProviderTurnAsync(OneShotJobRequest request, string directory,
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
        WorkerEdgeCredentialBoundary.RemoveFrom(start.Environment);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Provider CLI did not start.");
        try
        {
            var outputExceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The requested cap also bounds raw CLI output, including JSON framing.
            // Reject it while reading so a provider that keeps running cannot turn
            // an over-cap result into a timeout.
            var stdout = ReadBoundedAsync(process.StandardOutput, request.MaxOutputBytes, process, outputExceeded, ct);
            var stderr = ReadBoundedAsync(process.StandardError, 8_192, process, outputExceeded, ct);
            var input = request.Cli == "claude" ? ClaudeImageEnvelope(request) : request.Prompt;
            // A large prompt can fill the child's stdin pipe while it writes
            // its response. Schedule the writer independently so the output
            // cap remains observable even if writing blocks synchronously.
            var write = Task.Run(() => WriteInputAsync(process.StandardInput, input, ct), ct);
            var all = Task.WhenAll(write, process.WaitForExitAsync(ct), stdout, stderr);
            if (ReferenceEquals(await Task.WhenAny(all, outputExceeded.Task), outputExceeded.Task))
                throw new InvalidDataException("one-shot raw output exceeds the byte cap");
            try { await all; }
            catch (Exception) when (outputExceeded.Task.IsCompleted)
            {
                throw new InvalidDataException("one-shot raw output exceeds the byte cap");
            }
            return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static async Task WriteInputAsync(StreamWriter writer, string input, CancellationToken ct)
    {
        await writer.WriteAsync(input.AsMemory(), ct);
        writer.Close();
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
        Process process, TaskCompletionSource outputExceeded, CancellationToken ct)
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
                outputExceeded.TrySetResult();
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new InvalidDataException("one-shot raw output exceeds the byte cap");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
