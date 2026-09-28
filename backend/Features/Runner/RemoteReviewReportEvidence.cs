using System.Text;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Persists the fenced Remote Review report beside the task. The attempt
/// authority remains the canonical protocol record; this Markdown projection
/// makes the report visible and keeps it attached when the task folder moves.
/// </summary>
internal static class RemoteReviewReportEvidence
{
    /// <summary>
    /// Deterministic grade-file name for an attempt, computable without the I/O
    /// in <see cref="WriteAsync"/>. The report endpoint uses this to reference
    /// the file (e.g. as a timeline PayloadRef) before the background evidence
    /// queue has actually written it (AGT-2762).
    /// </summary>
    public static string EvidenceFileName(string attemptId)
        => $"remote-review-grade-{SafeFilePart(attemptId)}.md";

    public static async Task<string> WriteAsync(
        string jobFolder,
        string attemptId,
        string subjectId,
        Contract.ReviewReportRequest request,
        string reportSha256,
        DateTime receivedAt,
        CancellationToken ct)
    {
        var fileName = EvidenceFileName(attemptId);
        var path = Path.Combine(jobFolder, fileName);
        var artifactFiles = await PersistArtifactsAsync(
            jobFolder,
            attemptId,
            request.Artifacts,
            ct);
        var report = Render(
            attemptId,
            subjectId,
            request,
            reportSha256,
            receivedAt,
            artifactFiles);
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false), ct);
        return fileName;
    }

    private static string Render(
        string attemptId,
        string subjectId,
        Contract.ReviewReportRequest request,
        string reportSha256,
        DateTime receivedAt,
        IReadOnlyDictionary<string, string> artifactFiles)
    {
        var text = new StringBuilder();
        text.AppendLine("---");
        text.AppendLine("type: remote-review-grade");
        text.AppendLine($"attemptId: {Yaml(attemptId)}");
        text.AppendLine($"subjectId: {Yaml(subjectId)}");
        text.AppendLine($"receivedAt: {receivedAt:O}");
        text.AppendLine($"outcome: {Yaml(request.Outcome)}");
        if (!string.IsNullOrWhiteSpace(request.FailureClassification))
            text.AppendLine($"failureClassification: {Yaml(request.FailureClassification)}");
        text.AppendLine($"expectedResultSha: {Yaml(request.Workspace.ExpectedResultSha)}");
        text.AppendLine($"actualHead: {Yaml(request.Workspace.ActualHead)}");
        text.AppendLine($"reportSha256: {Yaml(reportSha256)}");
        if (request.Environment.Worker is { } workerProvenance)
        {
            text.AppendLine($"workerReleaseId: {Yaml(workerProvenance.WorkerReleaseId)}");
            text.AppendLine($"daemonReleaseId: {Yaml(workerProvenance.DaemonReleaseId)}");
            if (Contract.ReviewWorkerProvenancePolicy.SupersededNotice(workerProvenance)
                is { } supersededNotice)
                text.AppendLine($"workerReleaseSuperseded: {Yaml(supersededNotice)}");
        }
        var baselineReuse = BaselineReuseCitations(request.Commands);
        if (baselineReuse is not null)
        {
            text.AppendLine("baselineReused: true");
            text.AppendLine($"baselineReuse: {Yaml(baselineReuse)}");
        }
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine("# Remote Review Grade");
        text.AppendLine();
        var status = string.IsNullOrWhiteSpace(request.FailureClassification)
            ? request.Outcome
            : $"{request.Outcome} / {request.FailureClassification}";
        text.AppendLine($"**Outcome:** {status}");
        text.AppendLine();
        if (!string.IsNullOrWhiteSpace(request.Summary))
        {
            text.AppendLine($"**Detail:** {request.Summary.Trim()}");
            text.AppendLine();
        }

        text.AppendLine("## Aspect verdicts");
        text.AppendLine();
        if (request.Verdicts.Count == 0)
        {
            text.AppendLine("_No aspect verdicts were supplied._");
        }
        else
        {
            text.AppendLine("| Aspect | Status | Classification | Evidence checked | Missing | Summary |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var verdict in request.Verdicts)
            {
                text.AppendLine(
                    $"| [{Cell(verdict.Aspect)}](aspect-{SafeFilePart(verdict.Aspect)}.md) | {Cell(verdict.Status)} | {Cell(verdict.Classification)} | " +
                    $"{Cell(verdict.EvidenceChecked ?? "not reported")} | {Cell(verdict.Missing ?? "not reported")} | " +
                    $"{Cell(verdict.Summary)} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Command evidence");
        text.AppendLine();
        if (request.Commands.Count == 0)
        {
            text.AppendLine("_No command evidence was supplied._");
        }
        else
        {
            text.AppendLine("| Phase | Workspace | Step | Location | Host / executor | Command | Exit | Budget | Output | Errors | Baseline |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- |");
            foreach (var command in request.Commands)
            {
                var budget = command.Budget is null
                    ? "not reported"
                    : $"{command.Budget.Name}: {command.Budget.ConsumedMs}/{command.Budget.LimitMs} ms" +
                      (command.Budget.Violated ? " (violated)" : "");
                text.AppendLine(
                    $"| {Cell(command.Phase)} | {Cell(command.WorkspaceRole)} | {Cell(command.StepId)} | " +
                    $"{Cell(command.ExecutionLocation)} | " +
                    $"{Cell($"{command.HostId ?? request.Environment.HostId} / {command.ExecutorId ?? request.ExecutorId}")} | " +
                    $"`{Cell(CommandLine(command))}` | {Cell(command.ExitCode?.ToString() ?? command.Signal ?? "n/a")} | " +
                    $"{Cell(budget)} | {ArtifactLink("stdout", command.StdoutSha256, artifactFiles)} | " +
                    $"{ArtifactLink("stderr", command.StderrSha256, artifactFiles)} | " +
                    $"{Cell(BaselineProvenance(command))} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Immutable subject proof");
        text.AppendLine();
        text.AppendLine($"- Repository: `{request.Workspace.RepositoryId}`");
        text.AppendLine($"- Expected result: `{request.Workspace.ExpectedResultSha}`");
        text.AppendLine($"- Materialized result: `{request.Workspace.ActualHead}`");
        text.AppendLine($"- Tree: `{request.Workspace.TreeHash}`");
        text.AppendLine($"- Integration ref: `{request.Workspace.IntegrationRef ?? "unknown"}`");
        text.AppendLine($"- Reviewed integration tip: `{request.Workspace.IntegrationTipSha ?? "unknown"}`");
        text.AppendLine($"- Merge base: `{request.Workspace.MergeBaseSha ?? "unknown"}`");
        text.AppendLine($"- Dirty before: `{request.Workspace.DirtyBefore.ToString().ToLowerInvariant()}`");
        text.AppendLine($"- Dirty after: `{request.Workspace.DirtyAfter.ToString().ToLowerInvariant()}`");
        text.AppendLine($"- Executor: `{request.ExecutorId}`");
        text.AppendLine($"- Fence: `{request.Fence}`");
        text.AppendLine($"- Authority epoch: `{request.AuthorityEpoch}`");
        // AGT-2863: a detached review worker outlives the daemon that launched
        // it, so the executor and the fence alone cannot say which agent-host
        // build produced this verdict. The worker names itself.
        if (request.Environment.Worker is { } worker)
        {
            text.AppendLine($"- Worker release: `{worker.WorkerReleaseId}`");
            text.AppendLine($"- Worker binary: `{worker.WorkerBinaryPath}`");
            text.AppendLine($"- Daemon release: `{worker.DaemonReleaseId}`");
            if (Contract.ReviewWorkerProvenancePolicy.SupersededNotice(worker) is { } notice)
                text.AppendLine($"- Release provenance: {notice}");
        }
        return text.ToString();
    }

    private static async Task<IReadOnlyDictionary<string, string>> PersistArtifactsAsync(
        string jobFolder,
        string attemptId,
        IReadOnlyList<Contract.ReviewArtifactEvidenceDto> artifacts,
        CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            if (artifact.ContentBase64 is null) continue;
            var bytes = Convert.FromBase64String(artifact.ContentBase64);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant();
            if (!string.Equals(digest, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || bytes.LongLength != artifact.SizeBytes)
                throw new InvalidDataException(
                    $"Remote Review artifact '{artifact.Name}' does not match its declared digest or size.");
            var fileName =
                $"remote-review-{SafeFilePart(attemptId)}-{SafeFilePart(artifact.Name)}";
            await File.WriteAllBytesAsync(Path.Combine(jobFolder, fileName), bytes, ct);
            files[artifact.Sha256] = fileName;
        }
        return files;
    }

    /// <summary>
    /// How this command's baseline side was produced. A reused result names the
    /// attempt it came from and how old it was, so a reader can tell a skipped
    /// baseline run from one this attempt actually executed (AGT-2843).
    /// </summary>
    private static string BaselineProvenance(Contract.ReviewCommandEvidenceDto command)
    {
        if (!string.IsNullOrWhiteSpace(command.BaselineReusedFromAttemptId))
            return Contract.ReviewBaselineReuse.Citation(
                command.BaselineReusedFromAttemptId,
                command.BaselineReusedAgeSeconds);
        return string.IsNullOrWhiteSpace(command.BaselineSha)
            ? "n/a"
            : Contract.ReviewBaselineReuse.ExecutedInThisAttempt;
    }

    private static string? BaselineReuseCitations(
        IReadOnlyList<Contract.ReviewCommandEvidenceDto> commands)
    {
        var citations = commands
            .Where(command => !string.IsNullOrWhiteSpace(command.BaselineReusedFromAttemptId))
            .Select(command => Contract.ReviewBaselineReuse.Citation(
                command.BaselineReusedFromAttemptId!,
                command.BaselineReusedAgeSeconds))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return citations.Count == 0 ? null : string.Join("; ", citations);
    }

    private static string ArtifactLink(
        string label,
        string digest,
        IReadOnlyDictionary<string, string> artifactFiles)
        => artifactFiles.TryGetValue(digest, out var file)
            ? $"[{label}]({file})"
            : $"{label} `{digest[..Math.Min(12, digest.Length)]}`";

    private static string CommandLine(Contract.ReviewCommandEvidenceDto command)
        => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
            ? $"{command.FileName} read-only ({command.Model ?? "default model"})"
            : string.Join(' ', new[] { command.FileName }.Concat(command.Arguments));

    private static string SafeFilePart(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());

    private static string Yaml(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static string Cell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
