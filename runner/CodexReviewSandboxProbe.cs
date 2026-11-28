namespace AgentRunner;

internal sealed record CodexReviewSandboxProbe(bool Ready, string Detail, string? NamespaceFailure = null)
{
    private static IReadOnlyList<string>? Arguments()
    {
        string[] prefix = ["-c", "sandbox_mode=\"read-only\"", "sandbox"];
        if (OperatingSystem.IsLinux()) return [.. prefix, "linux", "--", "/bin/cat", "probe.txt"];
        if (OperatingSystem.IsMacOS()) return [.. prefix, "macos", "--", "/bin/cat", "probe.txt"];
        if (OperatingSystem.IsWindows()) return [.. prefix, "windows", "--", "cmd.exe", "/d", "/c", "type", "probe.txt"];
        return null;
    }

    public static async Task<CodexReviewSandboxProbe> RunAsync(
        RunnerOptions options, CancellationToken ct,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<ProcessResult>>? run = null,
        Action<string>? log = null)
    {
        var arguments = Arguments();
        if (arguments is null) return new(false, "No Codex sandbox probe is available for this platform.");
        DirectoryInfo? directory = null;
        const string marker = "codex-review-checkout-read";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            directory = Directory.CreateTempSubdirectory("codex-review-sandbox-");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "probe.txt"), marker, timeout.Token);
            var result = await (run ?? RunProcessAsync)(options.CodexCliBin, arguments, directory.FullName, timeout.Token);
            if (result.Success && result.StdOut.Trim() == marker)
                return new(true, "The Codex read-only sandbox read the probe file.");
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
            var namespaceFailure = result.Success ? null : CodexReviewCheckoutFailure.ReadNamespaceError(result.StdErr);
            return new(false, $"Codex read-only sandbox probe exited {result.ExitCode}. {detail[..Math.Min(detail.Length, 400)]}",
                namespaceFailure);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "Codex read-only sandbox probe timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, $"Codex read-only sandbox probe failed: {exception.Message}");
        }
        finally
        {
            try { directory?.Delete(recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Codex read-only sandbox probe cleanup failed: {exception.Message}");
            }
        }
    }

    private static Task<ProcessResult> RunProcessAsync(
        string binary, IReadOnlyList<string> arguments, string directory, CancellationToken ct)
        => ProcessRunner.RunAsync(binary, arguments, directory, ct: ct);
}
