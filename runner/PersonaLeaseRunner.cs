using AgentStudio.TaskServer.Contracts;
using System.Diagnostics;

namespace AgentRunner;

public sealed record PersonaLeaseWire(string Key, string ClaimToken, string WorkspaceId,
    string PersonaId, string RoleId, string Prompt, string Model, string Thinking,
    int TimeoutSeconds, int MaxInputTokens, int MaxOutputTokens, bool Investigation,
    string? SessionId, IReadOnlyList<string>? RepositoryPaths);
public sealed record PersonaLeaseCompletionWire(string RunnerId, string WorkspaceId, string Key,
    string ClaimToken, bool Success, string? Response, long InputTokens, long OutputTokens,
    string? Model, string? Error, long CacheReadTokens = 0, long CacheWriteTokens = 0);

/// <summary>One host-owned, bounded persona call at a time.</summary>
internal sealed class PersonaLeaseRunner(RunnerOptions options, TaskServerClient client, Action<string> log)
{
    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var lease = await client.ClaimPersonaAsync(options.RunnerId, shutdown);
                if (lease is not null) await RunAsync(lease, shutdown);
                else await Task.Delay(TimeSpan.FromSeconds(5), shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log($"persona-lease-poll-failed error={ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), shutdown); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunAsync(PersonaLeaseWire lease, CancellationToken shutdown)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-persona", lease.ClaimToken);
        Directory.CreateDirectory(directory);
        var completion = new PersonaLeaseCompletionWire(options.RunnerId, lease.WorkspaceId,
            lease.Key, lease.ClaimToken, false, null, 0, 0, lease.Model, "persona-call-failed");
        try
        {
            if (lease.MaxInputTokens > 12_000 || lease.MaxOutputTokens > 2_500 ||
                PromptOverrun(lease.Prompt, lease.MaxInputTokens))
                throw new InvalidDataException("persona-ground-cap-exceeded");
            var seconds = Math.Min(lease.Investigation ? 900 : 300,
                Math.Max(1, lease.TimeoutSeconds));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            // The bounded one-shot runner starts Claude with no tools, strict
            // empty MCP configuration and stripped runner credentials.
            var request = new OneShotJobRequest("claude", lease.Model, lease.Prompt,
                lease.Thinking, seconds, 65_536);
            var workingDirectory = lease.Investigation
                ? await PrepareInvestigationAsync(lease.RepositoryPaths ?? [], directory, timeout.Token)
                : directory;
            var process = await new OneShotJobAdmission(options, client, log)
                .RunBoundedTurnAsync(request, workingDirectory, timeout.Token,
                    readOnlyTools: lease.Investigation, personaIsolation: true);
            var parsed = RemoteProjectChatRunner.ParseClaude(process, lease.Model);
            completion = completion with { Success = parsed.Success, Response = parsed.ReplyText,
                InputTokens = parsed.TokenUsage?.InputTokens ?? 0,
                OutputTokens = parsed.TokenUsage?.OutputTokens ?? 0,
                CacheReadTokens = parsed.TokenUsage?.CacheReadTokens ?? 0,
                CacheWriteTokens = parsed.TokenUsage?.CacheCreationTokens ?? 0,
                Model = parsed.TokenUsage?.Model ?? lease.Model, Error = parsed.ErrorMessage };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !shutdown.IsCancellationRequested)
        {
            completion = completion with { Error = ex.Message };
        }
        finally
        {
            try
            {
                if (OperatingSystem.IsLinux())
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(path => path.Length))
                        if (Directory.Exists(path)) File.SetUnixFileMode(path,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(directory, true);
            }
            catch (IOException ex) { log($"persona-checkout-cleanup-failed: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { log($"persona-checkout-cleanup-denied: {ex.Message}"); }
        }
        if (!await client.CompletePersonaAsync(completion, CancellationToken.None))
            log($"persona-lease-completion-rejected key={lease.Key}");
    }

    internal static bool PromptOverrun(string prompt, int maxInput)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(prompt);
        var estimate = Math.Max((bytes + 1) / 2, prompt.Count(c => !char.IsLetterOrDigit(c)));
        return estimate + Math.Max(256, estimate / 5) > maxInput;
    }

    private static async Task<string> PrepareInvestigationAsync(IReadOnlyList<string> repositories,
        string directory, CancellationToken ct)
    {
        if (repositories.Count == 0 || repositories.Count > 4)
            throw new InvalidDataException("investigation-repository-unavailable");
        var checkout = Path.Combine(directory, "checkout");
        Directory.CreateDirectory(checkout);
        for (var n = 0; n < repositories.Count; n++)
        {
            var source = repositories[n];
            if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source))
                throw new InvalidDataException("investigation-repository-unavailable");
            var target = Path.Combine(checkout, "repo-" + n);
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory, RedirectStandardError = true,
                RedirectStandardOutput = true, UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.symlinks=false");
            start.ArgumentList.Add("clone");
            start.ArgumentList.Add("--no-local");
            start.ArgumentList.Add("--no-hardlinks");
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(source);
            start.ArgumentList.Add(target);
            WorkerEdgeCredentialBoundary.RemoveFrom(start.Environment);
            WorkerEdgeCredentialBoundary.RemovePersonaDeploymentCredentials(start.Environment);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            if (!OperatingSystem.IsWindows()) start.Environment["GIT_ASKPASS"] = "/bin/false";
            start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "credential.helper";
            start.Environment["GIT_CONFIG_VALUE_0"] = "";
            using var process = Process.Start(start) ?? throw new InvalidDataException("investigation-clone-failed");
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode != 0) throw new InvalidDataException("investigation-clone-failed");
            foreach (var path in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (File.Exists(path))
                {
                    if (OperatingSystem.IsWindows())
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    else File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
                else if (Directory.Exists(path) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        return checkout;
    }
}
