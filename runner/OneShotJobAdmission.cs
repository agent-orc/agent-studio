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
            var process = await RunBoundedTurnAsync(request, directory, timeout.Token);
            var parsed = request.Cli == "claude"
                ? RemoteProjectChatRunner.ParseClaude(process, request.Model)
                : RemoteProjectChatRunner.ParseCodex(process, request.Model);
            var text = parsed.ReplyText;
            var tooLarge = Encoding.UTF8.GetByteCount(text ?? string.Empty) > request.MaxOutputBytes;
            var usage = parsed.TokenUsage;
            completion = new(options.RunnerId, client.RunnerInstanceId, work.JobId, work.ClaimToken,
                parsed.Success && !tooLarge, tooLarge ? null : text,
                tooLarge ? "one-shot output exceeds the byte cap" : parsed.ErrorMessage,
                usage is null ? null : new(usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens, usage.Model));
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            completion = new(options.RunnerId, client.RunnerInstanceId, work.JobId, work.ClaimToken, false, null,
                $"one-shot job timed out after {request.TimeoutSeconds} seconds", null);
        }
        catch (Exception ex)
        {
            completion = new(options.RunnerId, client.RunnerInstanceId, work.JobId, work.ClaimToken, false, null, ex.Message, null);
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

    internal async Task<ProcessResult> RunBoundedTurnAsync(OneShotJobRequest request, string directory,
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
        WorkerEdgeCredentialBoundary.RemoveFrom(start.Environment);
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
            var outputExceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawBudget = new RawOutputBudget(Math.Min(262_144, request.MaxOutputBytes * 4));
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream,
                rawBudget, process, outputExceeded, ct);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream,
                rawBudget, process, outputExceeded, ct, 8_192);
            var input = request.Cli == "claude" ? ClaudeImageEnvelope(request) : request.Prompt;
            var all = Task.WhenAll(WriteInputAsync(process.StandardInput, input, ct),
                stdout, stderr, process.WaitForExitAsync(ct));
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

    private sealed class RawOutputBudget(int maxBytes)
    {
        private long _bytes;
        public bool TryAdd(int count) => Interlocked.Add(ref _bytes, count) <= maxBytes;
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, RawOutputBudget budget,
        Process process, TaskCompletionSource outputExceeded, CancellationToken ct, int? streamCap = null)
    {
        var buffer = new byte[4096];
        using var result = new MemoryStream();
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            if (!budget.TryAdd(count) || streamCap is { } cap && result.Length + count > cap)
            {
                outputExceeded.TrySetResult();
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new InvalidDataException("one-shot raw output exceeds the byte cap");
            }
            result.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, (int)result.Length);
    }
}
