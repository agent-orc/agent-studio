using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal sealed record DetachedReviewSpec(
    ReviewSubjectDto Subject,
    ReviewLeaseDto Lease,
    string ReviewWorkDir,
    IReadOnlyList<string> ReviewCredentialEnvironment,
    string? CliBin = null,
    string? CliArgs = null,
    string? CodexCliBin = null,
    string? ClaudeCliBin = null);

internal sealed record DetachedReviewIdentity(
    int ProcessId,
    DateTime ProcessStartedAtUtc,
    string WorkspacePath);

internal sealed record DetachedReviewResult(
    ReviewExecutionEvidence? Evidence,
    string? FailureClassification,
    string? Summary,
    DateTime CompletedAtUtc);

/// <summary>
/// Runs a prepared review plan behind a runner-owned detached worker. The
/// worker writes identity, command checkpoints, and its terminal evidence to
/// durable files so a replacement daemon can continue the same fenced attempt
/// without repeating completed test work.
/// </summary>
internal sealed class DurableReviewProcess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    private DurableReviewProcess(string directory, int processId, DateTime processStartedAtUtc)
    {
        _directory = directory;
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
    }

    public int ProcessId { get; }
    public DateTime ProcessStartedAtUtc { get; }
    public string ResultPath => Path.Combine(_directory, "review-result.json");
    public string ProgressPath => Path.Combine(_directory, "review-progress.json");
    public string IdentityPath => Path.Combine(_directory, "review-worker.json");

    public static DurableReviewProcess Start(
        RunnerOptions options,
        PersistedReviewSlot slot)
    {
        var claim = slot.Claim;
        var spec = BuildSpec(options, claim);
        Directory.CreateDirectory(slot.WorkerDirectory);
        var specPath = Path.Combine(slot.WorkerDirectory, "review-spec.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException(
                             "Cannot resolve the runner executable for detached review launch.");
        var managedHost = string.Equals(
                              Path.GetFileNameWithoutExtension(executable),
                              "dotnet",
                              StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = managedHost ? "dotnet" : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = slot.WorkspacePath,
        };
        if (managedHost) start.ArgumentList.Add(typeof(DurableReviewProcess).Assembly.Location);
        start.ArgumentList.Add("--detached-review-worker");
        start.ArgumentList.Add(specPath);
        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Failed to start detached review worker.");
        var started = process.StartTime.ToUniversalTime();
        var handle = new DurableReviewProcess(slot.WorkerDirectory, process.Id, started);
        process.Dispose();
        return handle;
    }

    public static DurableReviewProcess Attach(PersistedReviewSlot slot)
        => new(
            slot.WorkerDirectory,
            slot.ProcessId ?? -1,
            slot.ProcessStartedAtUtc ?? DateTime.MinValue);

    public static bool HasCompleted(PersistedReviewSlot slot)
        => File.Exists(Path.Combine(slot.WorkerDirectory, "review-result.json"));

    public static bool TryRecoverIdentity(
        PersistedReviewSlot slot,
        out PersistedReviewSlot recovered,
        out string reason)
    {
        recovered = slot;
        if (slot.ProcessId is not null && slot.ProcessStartedAtUtc is not null)
            return VerifyLive(slot, out reason);

        var path = Path.Combine(slot.WorkerDirectory, "review-worker.json");
        if (!File.Exists(path))
        {
            reason = "review worker identity has not been recorded";
            return false;
        }

        DetachedReviewIdentity? identity;
        try
        {
            identity = JsonSerializer.Deserialize<DetachedReviewIdentity>(File.ReadAllText(path), Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            reason = $"review worker identity is unreadable: {exception.Message}";
            return false;
        }
        if (identity is null)
        {
            reason = "review worker identity is empty";
            return false;
        }
        if (!PathsEqual(identity.WorkspacePath, slot.WorkspacePath))
        {
            reason = $"review worker workspace '{identity.WorkspacePath}' does not match slot workspace '{slot.WorkspacePath}'";
            return false;
        }

        recovered = slot with
        {
            ProcessId = identity.ProcessId,
            ProcessStartedAtUtc = identity.ProcessStartedAtUtc,
        };
        return VerifyLive(recovered, out reason);
    }

    public static bool VerifyLive(PersistedReviewSlot slot, out string reason)
    {
        if (slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
        {
            reason = "no persisted review process identity";
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(slot.ProcessId.Value);
            if (process.HasExited)
            {
                reason = "review process exited without durable evidence";
                return false;
            }
            if (Math.Abs((process.StartTime.ToUniversalTime() - slot.ProcessStartedAtUtc.Value).TotalSeconds) > 2)
            {
                reason = "review PID was reused (process start time differs)";
                return false;
            }
            if (OperatingSystem.IsLinux())
            {
                var cwdLink = new DirectoryInfo($"/proc/{slot.ProcessId.Value}/cwd");
                var target = cwdLink.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (string.IsNullOrWhiteSpace(target) || !PathsEqual(target, slot.WorkspacePath))
                {
                    reason = $"review process cwd '{target ?? "unavailable"}' does not match workspace '{slot.WorkspacePath}'";
                    return false;
                }
                if (DetachedWorkerTmpMountGuard.TmpMountWasTornDown(slot.ProcessId.Value, out var tmpDetail))
                {
                    reason = tmpDetail;
                    return false;
                }
            }
            reason = "live review process generation and workspace match";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or IOException)
        {
            reason = $"review process verification failed: {exception.Message}";
            return false;
        }
    }

    public DetachedReviewResult? ReadResult()
    {
        if (!File.Exists(ResultPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DetachedReviewResult>(File.ReadAllText(ResultPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ReviewExecutionCheckpoint? ReadProgress()
    {
        if (!File.Exists(ProgressPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReviewExecutionCheckpoint>(File.ReadAllText(ProgressPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Kill()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Authority loss is already the canonical outcome. Reaping is best
            // effort here and the persisted loss record stays available.
        }
    }

    private static DetachedReviewSpec BuildSpec(
        RunnerOptions options,
        ReviewClaimResponse claim)
        => new(
            claim.Subject!,
            claim.Lease!,
            Path.GetFullPath(options.ReviewWorkDir),
            options.ReviewCredentialEnvironment,
            options.CliBin,
            options.CliArgs,
            options.CodexCliBin,
            options.ClaudeCliBin);

    public static async Task<int> RunWorkerAsync(string specPath)
    {
        var spec = JsonSerializer.Deserialize<DetachedReviewSpec>(
                       await File.ReadAllTextAsync(specPath),
                       Json)
                   ?? throw new InvalidDataException($"Detached review spec is empty: {specPath}");
        var directory = Path.GetDirectoryName(specPath)!;
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = spec.Lease.ExecutorId,
            RunnerName = spec.Lease.ExecutorId,
            Hostname = spec.Lease.HostId,
            BackendName = "detached-review-worker",
            Role = "review",
            WorkDir = Path.Combine(spec.ReviewWorkDir, ".coding-not-used"),
            ReviewWorkDir = spec.ReviewWorkDir,
            ReviewCredentialEnvironment = spec.ReviewCredentialEnvironment,
            BaseBranch = "main",
            CliBin = spec.CliBin ?? "unused",
            CliArgs = spec.CliArgs ?? string.Empty,
            CodexCliBin = spec.CodexCliBin ?? "codex",
            ClaudeCliBin = spec.ClaudeCliBin ?? "claude",
        };
        var workspace = new RemoteReviewWorkspace(options, spec.Subject, spec.Lease, _ => { });
        using (var current = Process.GetCurrentProcess())
        {
            var identity = new DetachedReviewIdentity(
                current.Id,
                current.StartTime.ToUniversalTime(),
                Path.GetFullPath(workspace.RepositoryPath));
            if (!await WriteAtomicAsync(
                Path.Combine(directory, "review-worker.json"),
                JsonSerializer.Serialize(identity, Json)))
                return 0;
        }

        DetachedReviewResult result;
        try
        {
            await workspace.AdoptPreparedAsync(CancellationToken.None);
            var evidence = await workspace.ExecutePlanAsync(
                CancellationToken.None,
                async (progress, _) =>
                {
                    if (!await WriteAtomicAsync(
                            Path.Combine(directory, "review-progress.json"),
                            JsonSerializer.Serialize(progress, Json)))
                        throw new ReviewAttemptStateRemovedException();
                });
            result = new DetachedReviewResult(
                evidence,
                null,
                null,
                DateTime.UtcNow);
        }
        catch (ReviewInfrastructureException exception)
        {
            result = new DetachedReviewResult(
                exception.Evidence,
                exception.Classification,
                exception.Message,
                DateTime.UtcNow);
        }
        catch (ReviewAttemptStateRemovedException)
        {
            // The daemon accepted the terminal report and reaped this attempt
            // while the detached process was still unwinding. There is no
            // authority or durable location left for another result.
            return 0;
        }
        catch (Exception exception)
        {
            result = new DetachedReviewResult(
                null,
                "ReviewWorkerFailed",
                $"Detached review worker failed: {exception.Message}",
                DateTime.UtcNow);
        }

        if (!await WriteAtomicAsync(
            Path.Combine(directory, "review-result.json"),
            JsonSerializer.Serialize(result, Json)))
            return 0;
        return result.FailureClassification is null ? 0 : 3;
    }

    internal static async Task<bool> WriteAtomicAsync(string path, string content)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            // State deletion is the daemon's acknowledgement that this fenced
            // worker no longer owns a reportable attempt.
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class ReviewAttemptStateRemovedException : Exception;

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
