using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// The integration role (AGT-3061, AGT-W65 I11, D16 A): publishes accepted
/// deliveries to their project's integration ref while holding the project's
/// publication lease from the Task Server. The server decides when a delivery
/// is accepted and which lane follows the report; this host only merges and
/// pushes, never with force.
/// </summary>
public sealed class RemoteIntegrationDaemon
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public RemoteIntegrationDaemon(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        await _client.RegisterAsync(_options.RunnerName, IntegrationCapabilities.Executor, shutdown);
        var root = Path.Combine(_options.StateDir, "integration");
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var claim = await _client.ClaimIntegrationAsync(new IntegrationClaimRequest(
                    _options.RunnerId, _client.RunnerInstanceId), shutdown);
                if (claim is { Status: "claimed", Command: { } command, Lease: { } lease })
                {
                    var result = await IntegrationPublisher.PublishAsync(command, lease.Fence, root, shutdown);
                    var report = await _client.ReportIntegrationAsync(command.IntegrationId,
                        new IntegrationReportRequest(lease.RunnerId, lease.InstanceId, lease.LeaseId, lease.Fence,
                            $"integration:{command.IntegrationId}:{lease.Fence}", result.Outcome,
                            result.IntegratedSha, result.Detail), shutdown);
                    _log($"integration {command.IntegrationId} for {command.TaskId}: {result.Outcome}; " +
                         $"lane {report.TaskState ?? "unchanged"}");
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // An unreported claim expires with its lease and is queued again.
                _log($"integration poll failed: {exception.GetType().Name}: {exception.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), shutdown); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            }
        }
    }
}

public sealed record IntegrationPublishResult(string Outcome, string? IntegratedSha, string Detail);

/// <summary>
/// Git side of one integration in a disposable repository: fetch the
/// integration ref and the fenced result ref, fast-forward or merge, and push
/// the new tip without force. A tip that moved since the fetch is reported as
/// a stale base; the Task Server decides whether to retry.
/// </summary>
public static class IntegrationPublisher
{
    private const string TargetRef = "refs/remotes/publication/target";
    private const string ResultRef = "refs/remotes/publication/result";

    public static async Task<IntegrationPublishResult> PublishAsync(
        IntegrationCommandDto command, long fence, string root, CancellationToken ct)
    {
        var branch = BranchRef(command.IntegrationRef);
        var directory = Path.Combine(root, $"{SafeSegment(command.IntegrationId)}-{fence}");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        try
        {
            await Git(["init", "-q"], directory, ct);
            await Git(["fetch", "--no-tags", "-q", command.RepositoryUrl, $"+{branch}:{TargetRef}"], directory, ct);
            await Git(["fetch", "--no-tags", "-q", command.RepositoryUrl, $"+{command.ResultRef}:{ResultRef}"],
                directory, ct);
            var resultSha = (await Git(["rev-parse", ResultRef + "^{commit}"], directory, ct)).StdOut.Trim();
            if (!string.Equals(resultSha, command.ResultSha, StringComparison.OrdinalIgnoreCase))
                return new(IntegrationOutcomeKinds.Failed, null,
                    $"The result ref names {resultSha}, not the accepted {command.ResultSha}.");
            var target = (await Git(["rev-parse", TargetRef + "^{commit}"], directory, ct)).StdOut.Trim();
            if (await IsAncestorAsync(resultSha, target, directory, ct))
                return new(IntegrationOutcomeKinds.AlreadyIntegrated, target,
                    $"{branch} at {target} already contains {resultSha}.");
            string tip;
            if (await IsAncestorAsync(target, resultSha, directory, ct))
                tip = resultSha;
            else
            {
                await Git(["checkout", "-q", "--detach", target], directory, ct);
                var merge = await ProcessRunner.RunAsync("git",
                    ["-c", "user.name=Agent Studio Integration", "-c", "user.email=integration@agent-studio.invalid",
                        "merge", "--no-ff", "--no-edit", "-m",
                        $"Integrate {command.TaskId} ({resultSha[..Math.Min(12, resultSha.Length)]})", resultSha],
                    workingDirectory: directory, ct: ct);
                if (!merge.Success)
                    return new(IntegrationOutcomeKinds.Conflict, null, OneLine(merge.StdOut + " " + merge.StdErr));
                tip = (await Git(["rev-parse", "HEAD"], directory, ct)).StdOut.Trim();
            }
            var push = await ProcessRunner.RunAsync("git",
                ["push", "-q", command.RepositoryUrl, $"{tip}:{branch}"], workingDirectory: directory, ct: ct);
            if (push.Success)
                return new(IntegrationOutcomeKinds.Integrated, tip, $"{branch} advanced from {target} to {tip}.");
            var error = OneLine(push.StdErr);
            return error.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                ? new(IntegrationOutcomeKinds.StaleBase, null, error)
                : new(IntegrationOutcomeKinds.Failed, null, error);
        }
        catch (InvalidOperationException exception)
        {
            return new(IntegrationOutcomeKinds.Failed, null, OneLine(exception.Message));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string BranchRef(string integrationRef)
    {
        var value = integrationRef.Trim();
        if (value.StartsWith("origin/", StringComparison.Ordinal)) value = value["origin/".Length..];
        return value.StartsWith("refs/", StringComparison.Ordinal) ? value : "refs/heads/" + value;
    }

    private static async Task<bool> IsAncestorAsync(string ancestor, string descendant, string directory,
        CancellationToken ct)
        => (await ProcessRunner.RunAsync("git", ["merge-base", "--is-ancestor", ancestor, descendant],
            workingDirectory: directory, ct: ct)).Success;

    private static async Task<ProcessResult> Git(IReadOnlyList<string> args, string directory, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: directory, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException($"git {args[0]} failed ({result.ExitCode}): {OneLine(result.StdErr)}");
        return result;
    }

    private static string SafeSegment(string value)
        => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    private static string OneLine(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length > 2000 ? line[..2000] : line;
    }
}
