using System.Globalization;

namespace AgentStudio.Git;

public partial class GitService
{
    /// <summary>
    /// AGT-3965: true when <paramref name="tip"/> has a first-parent commit that
    /// <paramref name="coveredTip"/> does not reach (all of its first-parent
    /// history when null) and that was committed at or before
    /// <paramref name="before"/>. One bounded <c>rev-list -n 1</c>; null when Git
    /// cannot answer, for example because an object is missing.
    /// </summary>
    public bool? HasFirstParentCommitBefore(string repoRoot, string tip, string? coveredTip,
        DateTimeOffset before)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        if (!IsObjectName(tip) || coveredTip is not null && !IsObjectName(coveredTip)) return null;
        var range = coveredTip is null ? tip : coveredTip + ".." + tip;
        var (output, _, code) = RunGitArgs(repoRoot, "rev-list", "-n", "1", "--first-parent",
            "--before=" + before.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), range, RevisionsOnly);
        return code == 0 ? output.Trim().Length > 0 : null;
    }

    private static bool IsObjectName(string value)
        => value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);
}
