using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Output and usage of one package aspect call.</summary>
internal sealed record PackageAgentRun(
    string Output, int? ExitCode, string? Signal, DateTime StartedAt, DateTime FinishedAt,
    RemoteAgentUsage? Usage, string Error = "");

/// <summary>
/// Runs one claimed shadow package attempt (AGT-3400 S4): materialize, stack,
/// render the material once, run every package aspect once, attribute each
/// cited finding to its hunk owner, and report. A failure becomes a reported
/// outcome; nothing here can change a member's review, lane or settlement.
/// </summary>
internal sealed class PackageReviewExecutor
{
    private readonly RunnerOptions _options;
    private readonly IReviewPackagePlaneClient _client;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly string? _sourceCacheRoot;
    private readonly Func<PackageReviewWorkspace, ReviewPackageLeaseDto, ReviewCommandDto, CancellationToken, Task<PackageAgentRun>> _agent;
    private readonly Func<string> _instanceId;

    public PackageReviewExecutor(RunnerOptions options, IReviewPackagePlaneClient client, Func<string> instanceId,
        Action<string> log, Func<DateTime>? utcNow = null, string? sourceCacheRoot = null,
        Func<PackageReviewWorkspace, ReviewPackageLeaseDto, ReviewCommandDto, CancellationToken, Task<PackageAgentRun>>? agent = null)
    {
        _options = options;
        _client = client;
        _instanceId = instanceId;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sourceCacheRoot = sourceCacheRoot;
        _agent = agent ?? RunAgentAsync;
    }

    public async Task<ReviewPackageReportDto?> RunClaimedAsync(ReviewPackageClaimResponse claim, CancellationToken ct)
    {
        var subject = claim.Subject ?? throw new ArgumentException("A claimed package carries its subject.");
        var lease = claim.Lease ?? throw new ArgumentException("A claimed package carries its lease.");
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopRenewal = new CancellationTokenSource();
        var renewal = RenewLoopAsync(lease, leaseLost, stopRenewal.Token);
        var workspace = new PackageReviewWorkspace(_options, subject, lease, _log, _utcNow, _sourceCacheRoot);
        ReviewPackageReportRequest report;
        try
        {
            report = await ExecuteAsync(subject, lease, workspace, leaseLost.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log($"review_package_lease_lost package={subject.PackageId} attempt={lease.AttemptId}");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report = Report(lease, ReviewPackageOutcomes.Infrastructure,
                $"Package review could not run: {exception.Message}", null, null, [], [], [], [], 0, 0);
        }
        finally
        {
            await stopRenewal.CancelAsync();
            await renewal;
            workspace.Delete();
        }
        return await SubmitAsync(lease, report, ct);
    }

    private async Task<ReviewPackageReportRequest> ExecuteAsync(ReviewPackageSubjectDto subject,
        ReviewPackageLeaseDto lease, PackageReviewWorkspace workspace, CancellationToken ct)
    {
        if (subject.Plan.PrivateRepository)
            return Report(lease, ReviewPackageOutcomes.Infrastructure,
                "Package shadow review reads public repositories only; a private repository stays on per-card review.",
                null, null, [], [], [], [], 0, 0);
        var tip = await workspace.MaterializeAsync(ct);
        var stack = await workspace.StackAsync(tip, ct);
        if (stack.ConflictMember is { } conflicted)
            return Report(lease, ReviewPackageOutcomes.StackConflict,
                $"{conflicted} does not stack on the earlier members: {string.Join(", ", stack.ConflictFiles.Take(10))}.",
                tip, null, stack.Steps, [], [], [], 0, 0);
        var material = await workspace.MaterialAsync(stack, ct);
        if (material.OverBudget)
            return Report(lease, ReviewPackageOutcomes.OverBudget,
                $"The stacked material has {material.Files} files and {material.Lines} diff lines, over the budget of "
                + $"{subject.Plan.MaximumFiles} files and {subject.Plan.MaximumLines} lines; the package splits, it is never truncated.",
                tip, stack.CandidateSha, stack.Steps, [], [], [], material.Files, material.Lines);

        var members = subject.Members.OrderBy(member => member.Order).Select(member => member.TaskKey).ToArray();
        var verdicts = new List<ReviewPackageMemberVerdictDto>();
        var findings = new List<ReviewPackageFindingDto>();
        var evidence = new List<ReviewPackageCommandEvidenceDto>();
        var inputOmissions = new List<string>();
        string? dossier = null;
        foreach (var command in subject.Plan.Commands)
        {
            var appended = string.Empty;
            if (command.Aspect == ReviewPackageAspects.CrossCardConsistency)
                appended += "\n" + (dossier ??= await workspace.DossierAsync(stack.CandidateSha!, ct));
            var input = ReviewAspectInputBudget.Compose(command.Prompt ?? string.Empty, material.Text, appended);
            var prompt = input.Prompt;
            if (input.Report != "No review input omitted.")
                inputOmissions.Add($"{command.Aspect}: {input.Report}");
            var budget = ReviewAspectBudgetPolicy.Derive(command.CliType, command.Model, command.ThinkingLevel,
                ReviewAspectBudgetPolicy.MaterialCharacters(prompt));
            var run = await _agent(workspace, lease,
                command with { Prompt = prompt, TimeoutSeconds = Math.Max(command.TimeoutSeconds, budget.Seconds) }, ct);
            var refusal = ReviewCliRefusalPolicy.Classify(command,
                new ProcessResult(run.ExitCode ?? 1, run.Output, run.Error));
            if (refusal is not null)
            {
                evidence.Add(new ReviewPackageCommandEvidenceDto(command.StepId, command.Aspect, command.CliType,
                    run.Usage?.Model ?? command.Model, command.ThinkingLevel, run.StartedAt, run.FinishedAt,
                    run.ExitCode, refusal, run.Usage?.InputTokens ?? 0, run.Usage?.OutputTokens ?? 0,
                    run.Usage?.CacheReadTokens ?? 0, run.Usage?.CacheCreationTokens ?? 0, Sha256(run.Output)));
                return Report(lease, ReviewPackageOutcomes.Infrastructure,
                    $"Package aspect '{command.StepId}' was refused before a model verdict: {refusal}. "
                    + (input.Report == "No review input omitted." ? string.Empty : "Input omissions: " + input.Report),
                    tip, stack.CandidateSha, stack.Steps, verdicts, findings, evidence, material.Files, material.Lines);
            }
            var parsed = ReviewPackageOutputParser.Parse(run.Output, command.Aspect, members);
            verdicts.AddRange(parsed.Verdicts);
            findings.AddRange(parsed.Findings.Select(finding =>
                ReviewPackageFindingAttribution.Attribute(command.Aspect, finding, material.Ownership, members)));
            evidence.Add(new ReviewPackageCommandEvidenceDto(command.StepId, command.Aspect, command.CliType,
                run.Usage?.Model ?? command.Model, command.ThinkingLevel, run.StartedAt, run.FinishedAt, run.ExitCode,
                Classification(run, parsed), run.Usage?.InputTokens ?? 0, run.Usage?.OutputTokens ?? 0,
                run.Usage?.CacheReadTokens ?? 0, run.Usage?.CacheCreationTokens ?? 0, Sha256(run.Output)));
            _log($"review_package_aspect package={subject.PackageId} aspect={command.Aspect} exit={run.ExitCode} "
                + $"findings={parsed.Findings.Count} malformed={string.Join(",", parsed.Malformed.Distinct())}");
        }
        return Report(lease, ReviewPackageOutcomes.Completed,
            $"{evidence.Count} package aspects over {members.Length} member(s); {findings.Count} cited finding(s), "
            + $"{findings.Count(finding => finding.CrossCard)} cross-card."
            + (inputOmissions.Count == 0 ? string.Empty
                : " Input omissions: " + string.Join("; ", inputOmissions)),
            tip, stack.CandidateSha, stack.Steps, verdicts, findings, evidence, material.Files, material.Lines);
    }

    private static string Classification(PackageAgentRun run, ReviewPackageAspectOutput parsed)
        => run.Signal == "timeout" ? "AspectTimeout"
            : run.ExitCode is not 0 ? "CommandFailed"
            : parsed.Malformed.Contains(ReviewPackageOutputParser.NoBlock)
              || parsed.Malformed.Contains(ReviewPackageOutputParser.InvalidJson) ? "review:unparseable"
            : "ok";

    private ReviewPackageReportRequest Report(ReviewPackageLeaseDto lease, string outcome, string summary,
        string? tip, string? candidate, IReadOnlyList<ReviewPackageMemberStepDto> steps,
        IReadOnlyList<ReviewPackageMemberVerdictDto> verdicts, IReadOnlyList<ReviewPackageFindingDto> findings,
        IReadOnlyList<ReviewPackageCommandEvidenceDto> commands, int files, int lines)
        => new(_options.RunnerId, InstanceId, lease.LeaseId, lease.Fence,
            $"review-package-report:{lease.AttemptId}:{lease.Fence}", outcome, summary, tip, candidate, steps,
            verdicts, findings, commands, files, lines, System.Environment.MachineName);

    private string InstanceId => _instanceId();

    private async Task<ReviewPackageReportDto?> SubmitAsync(ReviewPackageLeaseDto lease, ReviewPackageReportRequest report,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var receipt = await _client.ReportReviewPackageAsync(lease.AttemptId, report, ct);
                _log($"review_package_reported attempt={lease.AttemptId} outcome={report.Outcome} duplicate={receipt.Duplicate}");
                return receipt;
            }
            catch (Exception exception) when (attempt < 3 && exception is HttpRequestException or TaskServerException
                                                  { StatusCode: >= 500 })
            {
                _log($"review_package_report_retry attempt={lease.AttemptId} try={attempt} error={exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskServerException)
            {
                _log($"review_package_report_failed attempt={lease.AttemptId} error={exception.Message}");
                return null;
            }
        }
    }

    private async Task RenewLoopAsync(ReviewPackageLeaseDto lease, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        var ttl = Math.Max(60, (int)(lease.ExpiresAt - _utcNow()).TotalSeconds);
        var period = TimeSpan.FromSeconds(Math.Max(15, ttl / 3));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(period, stop);
                try
                {
                    await _client.RenewReviewPackageAsync(lease.AttemptId, new ReviewPackageRenewRequest(
                        _options.RunnerId, InstanceId, lease.LeaseId, lease.Fence, ttl), stop);
                }
                catch (TaskServerException exception) when (exception.StatusCode is 404 or 409)
                {
                    _log($"review_package_renew_refused attempt={lease.AttemptId} error={exception.Message}");
                    await leaseLost.CancelAsync();
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskServerException)
                {
                    // A transient fault keeps the attempt running; the next
                    // renewal or the report finds out whether the lease held.
                    _log($"review_package_renew_failed attempt={lease.AttemptId} error={exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _log($"review_package_renewal_stopped attempt={lease.AttemptId}");
        }
    }

    private async Task<PackageAgentRun> RunAgentAsync(PackageReviewWorkspace workspace, ReviewPackageLeaseDto lease,
        ReviewCommandDto command, CancellationToken ct)
    {
        var commands = new RemoteReviewAgentCommandRunner(_options,
            new ReviewLeaseDto(lease.LeaseId, lease.AttemptId, lease.PackageId, _options.RunnerId, InstanceId,
                System.Environment.MachineName, lease.Fence, _utcNow(), lease.ExpiresAt, "leased",
                PackageReviewWorkspace.AttemptPrefix + RemoteReviewWorkspace.SafeSegment(lease.AttemptId), 0),
            workspace.RepositoryPath, workspace.ArtifactPath, workspace.HomePath, _log);
        var execution = await commands.RunAsync(command, ct);
        return new PackageAgentRun(execution.Process.StdOut, execution.Process.ExitCode, execution.Signal,
            execution.StartedAt, execution.FinishedAt, execution.AgentUsage, execution.Process.StdErr);
    }

    private static string Sha256(string? text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
}
