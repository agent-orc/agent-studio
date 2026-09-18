using System.Text.Json;

namespace AgentStudio.Pipeline;

public enum DeliveryRefSource
{
    ImmutableResultEnvelope,
    AttributedCommit,
    RunnerConvention,
    LocalTaskFallback,
}

/// <summary>
/// The source ref selected from durable card truth for an acceptance merge.
/// Remote refs carry the exact reviewed result SHA whenever one is available,
/// allowing <see cref="GitService.MergeRemoteDeliveryIntoIntegration"/> to
/// fence the fetched branch tip before it mutates the integration branch.
/// </summary>
public sealed record DeliveryRefResolution(
    string Ref,
    string? ExpectedResultSha,
    DeliveryRefSource Source,
    bool IsRemote);

/// <summary>
/// Resolves an accepted card's delivery ref without treating its folder slug as
/// remote-runner truth. Resolution order is fixed:
/// ResultEnvelope immutable ref, attributed commit branches, canonical
/// runner/&lt;host&gt;/&lt;KEY&gt;, then the local task/&lt;slug&gt; compatibility
/// fallback.
/// </summary>
public static class DeliveryRefResolver
{
    public static DeliveryRefResolution Resolve(string jobId, string jobFolderPath)
    {
        var subject = ReviewSubjectStore.Read(jobFolderPath);
        var card = ReadCard(jobFolderPath);
        var subjectSha = ValidFullSha(subject?.ResultSha) ? subject!.ResultSha : null;

        var immutableRef = NormalizeBranch(subject?.ImmutableResultRef);
        if (immutableRef is not null)
        {
            return new DeliveryRefResolution(
                immutableRef,
                subjectSha,
                DeliveryRefSource.ImmutableResultEnvelope,
                IsRemote: true);
        }

        // AGT-2871: a commit whose delivery generation was replaced does not
        // name the card's delivery ref. The card is continued and re-delivered
        // often enough that the first round's result ref otherwise outlives the
        // generation that was actually reviewed and merged. The unfiltered
        // fallback stays, so a card with only superseded history still resolves
        // the ref it used to.
        var branchCommits = card.Commits
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Branch))
            .ToList();
        var attributed = branchCommits
                             .LastOrDefault(commit => !TaskCommitSupersession.IsReplaced(commit))
                         ?? branchCommits.LastOrDefault();
        var attributedRef = NormalizeBranch(attributed?.Branch);
        if (attributedRef is not null)
        {
            var expectedSha = subjectSha
                ?? (ValidFullSha(attributed?.Sha) ? attributed!.Sha : null);
            return new DeliveryRefResolution(
                attributedRef,
                expectedSha,
                DeliveryRefSource.AttributedCommit,
                IsRemote: true);
        }

        var taskKey = string.IsNullOrWhiteSpace(card.Key)
            ? subject?.TaskKey
            : card.Key;
        if (!string.IsNullOrWhiteSpace(subject?.Executor)
            && !string.IsNullOrWhiteSpace(taskKey))
        {
            var runnerRef =
                $"runner/{SafeSegment(subject!.Executor)}/{SafeSegment(taskKey!)}";
            return new DeliveryRefResolution(
                runnerRef,
                subjectSha,
                DeliveryRefSource.RunnerConvention,
                IsRemote: true);
        }

        return new DeliveryRefResolution(
            WorktreeTaskLifecycle.BranchFor(jobId),
            null,
            DeliveryRefSource.LocalTaskFallback,
            IsRemote: false);
    }

    private static CardDelivery ReadCard(string jobFolderPath)
    {
        try
        {
            var path = Path.Combine(jobFolderPath, "task.json");
            if (!File.Exists(path)) return new CardDelivery();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var key = root.TryGetProperty("key", out var keyElement)
                      && keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;
            var commits = new List<TaskCommitInfo>();
            if (root.TryGetProperty("commits", out var commitsElement)
                && commitsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in commitsElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    var commit = JsonSerializer.Deserialize<TaskCommitInfo>(
                        element.GetRawText(),
                        TaskJsonFile.ReadOpts);
                    if (commit is not null && !string.IsNullOrWhiteSpace(commit.Sha))
                        commits.Add(commit);
                }
            }
            else if (root.TryGetProperty("commit", out var commitElement)
                     && commitElement.ValueKind == JsonValueKind.Object)
            {
                var commit = JsonSerializer.Deserialize<TaskCommitInfo>(
                    commitElement.GetRawText(),
                    TaskJsonFile.ReadOpts);
                if (commit is not null && !string.IsNullOrWhiteSpace(commit.Sha))
                    commits.Add(commit);
            }
            return new CardDelivery(key, commits);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "DeliveryRefResolver: task card read is best-effort");
            return new CardDelivery();
        }
    }

    private static string? NormalizeBranch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var branch = TaskIntegrationBranch.Name(value, fallback: "");
        return string.IsNullOrWhiteSpace(branch) ? null : branch;
    }

    private static bool ValidFullSha(string? value)
        => ReviewSubjectStore.IsValidResultSha(value);

    private static string SafeSegment(string value)
    {
        var chars = value
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? character
                    : '-')
            .ToArray();
        var safe = new string(chars).Trim('-', '.');
        return safe.Length == 0 ? "task" : safe;
    }

    private sealed record CardDelivery(
        string? Key = null,
        IReadOnlyList<TaskCommitInfo>? CommitList = null)
    {
        public IReadOnlyList<TaskCommitInfo> Commits { get; } = CommitList ?? [];
    }
}
