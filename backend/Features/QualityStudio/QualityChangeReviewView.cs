using System.Diagnostics;
using System.Text;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.QualityStudio;

public sealed record QualityChangeReviewView(
    string State, string? RepositoryId, string? CurrentHead,
    IReadOnlyList<QualityChangeReviewRound> Rounds,
    string? LandedSha, string? LandedBaseSha, bool? LandedPatchEqualsReviewedPatch);

public static class QualityChangeReviewProjection
{
    public static async Task<QualityChangeReviewView> BuildAsync(TaskInfo task, string? repositoryId,
        string? landedSha, Func<string, string, string?> firstParent, CancellationToken ct = default,
        bool landedIsMerge = true)
    {
        var rounds = QualityChangeReviewStore.Read(task.FolderPath);
        var subject = ReviewSubjectStore.Read(task.FolderPath);
        var head = subject?.ResultSha;
        var latest = rounds.LastOrDefault();
        var state = State(repositoryId, head, latest, task.State);
        string? landedBase = null;
        bool? samePatch = null;
        if (landedIsMerge && latest is not null && ReviewSubjectStore.IsValidResultSha(landedSha))
        {
            landedBase = firstParent(task.WatchPath, landedSha!);
            if (ReviewSubjectStore.IsValidResultSha(landedBase))
            {
                var reviewed = await PatchIdAsync(task.WatchPath, latest.BaseSha, latest.HeadSha, ct);
                var landed = await PatchIdAsync(task.WatchPath, landedBase!, landedSha!, ct);
                if (reviewed is not null && landed is not null)
                    samePatch = string.Equals(reviewed, landed, StringComparison.Ordinal);
            }
        }
        return new QualityChangeReviewView(state, repositoryId, head, rounds,
            landedSha, landedBase, samePatch);
    }

    public static string State(string? repositoryId, string? currentHead,
        QualityChangeReviewRound? latest, string lane)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) return "not-registered";
        if (latest is null) return currentHead is null ? "waiting" :
            lane == TaskStates.AutoReview ? "reviewing" : "not-reviewed";
        if (currentHead is not null && !string.Equals(currentHead, latest.HeadSha,
                StringComparison.OrdinalIgnoreCase)) return "outdated";
        if (latest.Mode == "off") return "not-reviewed";
        if (latest.UnavailableReason is not null) return "unavailable";
        var verdict = latest.Document is { } document
            ? document.GetProperty("verdict").GetString() : null;
        if (verdict is "provider-unavailable" or "timeout" or "auth-failure" or "unparseable")
            return "unavailable";
        if (latest.Mode == "shadow" && verdict is not null) return "shadow";
        return verdict ?? "not-reviewed";
    }

    /// <summary>Git's stable patch identity ignores merge metadata and context lines.</summary>
    public static Task<string?> PatchIdAsync(string repository, string baseSha, string headSha,
        CancellationToken ct = default)
        => Task.Run(() => PatchId(repository, baseSha, headSha, ct), ct);

    private static string? PatchId(string repository, string baseSha, string headSha, CancellationToken ct)
    {
        if (!ReviewSubjectStore.IsValidResultSha(baseSha) || !ReviewSubjectStore.IsValidResultSha(headSha)
            || !Directory.Exists(repository)) return null;
        var diff = Start(repository, "diff", "--no-ext-diff", "--binary", baseSha, headSha);
        var patch = GitProcessBoundary.RunBinary(diff, null, 8 * 1024 * 1024,
            TimeSpan.FromSeconds(15), ct);
        if (!patch.Success || patch.OutputTruncated) return null;
        var identity = GitProcessBoundary.Run(Start(repository, "patch-id", "--stable"),
            Encoding.UTF8.GetString(patch.StandardOutput), TimeSpan.FromSeconds(15), ct);
        return identity.Success
            ? identity.StandardOutput.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
            : null;
    }

    private static ProcessStartInfo Start(string repository, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
}
