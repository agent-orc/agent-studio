using System.Diagnostics;

namespace AgentStudio.TaskServer;

public sealed record WorkspaceExportGitResult(int Code, string Output, string Error, bool TimedOut = false);

public sealed record WorkspaceExportCommit(bool Committed, string? Sha, string? Error);

public sealed record WorkspaceExportPush(bool Attempted, bool Succeeded, int? AheadCommits, string? Error);

/// <summary>
/// Git steps of the workspace export: stage only export-owned paths
/// (<c>projects/*/tasks/**</c> and the manifest), commit
/// when they changed, and push the branch with a bounded timeout. A failed or
/// slow push leaves commits ahead of the remote; the next export's push
/// publishes the whole backlog. Nothing here runs on a request path.
/// </summary>
public sealed class WorkspaceExportGit(
    string gitCommand,
    Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<WorkspaceExportGitResult>>? run = null)
{
    private const string CommitterName = "agent-studio-export";
    private const string CommitterEmail = "agent-studio-export@local";
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<WorkspaceExportGitResult>> _run =
        run ?? ((root, args, timeout, ct) => RunGitAsync(gitCommand, root, args, timeout, ct));

    public static bool IsRepository(string root) => Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));

    public async Task<WorkspaceExportCommit> CommitAsync(string root, WorkspaceExportResult export, CancellationToken ct)
    {
        // Only export-owned paths, forced: an ignore rule in the clone must not drop exported task folders.
        string[] paths = ["--", $":(glob){WorkspaceExportLayout.ProjectsDirName}/*/{WorkspaceExportLayout.TasksDirName}/**",
            WorkspaceExportLayout.ManifestFileName];
        var add = await _run(root, ["add", "-A", "-f", .. paths], LocalTimeout, ct);
        if (add.Code != 0) return new WorkspaceExportCommit(false, null, "git add: " + add.Error);
        var diff = await _run(root, ["diff", "--cached", "--quiet", .. paths], LocalTimeout, ct);
        if (diff.Code == 0) return new WorkspaceExportCommit(false, null, null);
        if (diff.Code != 1) return new WorkspaceExportCommit(false, null, "git diff: " + diff.Error);
        var message = $"chore(export): workspace export of {export.Tasks} tasks in {export.Projects} projects\n\n"
                      + $"Files written {export.FilesWritten}, removed {export.FilesRemoved}.\n";
        var commit = await _run(root,
            ["-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}", "commit", "-q", "-m", message, .. paths],
            LocalTimeout, ct);
        if (commit.Code != 0) return new WorkspaceExportCommit(false, null, "git commit: " + commit.Error);
        var sha = await _run(root, ["rev-parse", "HEAD"], LocalTimeout, ct);
        return new WorkspaceExportCommit(true, sha.Code == 0 ? sha.Output.Trim() : null, null);
    }

    /// <summary>Counts commits ahead of <c>origin/&lt;branch&gt;</c>, then pushes HEAD to it.</summary>
    public async Task<WorkspaceExportPush> PushAsync(string root, string branch, TimeSpan timeout, CancellationToken ct)
    {
        var ahead = await AheadAsync(root, branch, ct);
        if (ahead == 0) return new WorkspaceExportPush(false, true, 0, null);
        var push = await _run(root, ["push", "origin", $"HEAD:refs/heads/{branch}"], timeout, ct);
        if (push.Code == 0) return new WorkspaceExportPush(true, true, 0, null);
        var error = push.TimedOut ? $"git push timed out after {timeout.TotalSeconds:0} s" : "git push: " + push.Error;
        return new WorkspaceExportPush(true, false, ahead, error);
    }

    /// <summary>Commits the remote does not have yet; every commit of HEAD before the first push.</summary>
    private async Task<int?> AheadAsync(string root, string branch, CancellationToken ct)
    {
        var tracked = await _run(root, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"], LocalTimeout, ct);
        string[] range = tracked.Code == 0 ? ["rev-list", "--count", $"refs/remotes/origin/{branch}..HEAD"] : ["rev-list", "--count", "HEAD"];
        var result = await _run(root, range, LocalTimeout, ct);
        return result.Code == 0 && int.TryParse(result.Output.Trim(), out var ahead) ? ahead : null;
    }

    private static async Task<WorkspaceExportGitResult> RunGitAsync(
        string gitCommand, string root, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(gitCommand) ? "git" : gitCommand.Trim(),
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return new WorkspaceExportGitResult(-1, string.Empty, "git did not start");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
            return new WorkspaceExportGitResult(process.ExitCode, await stdout, (await stderr).Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new WorkspaceExportGitResult(-1, string.Empty, "timed out", TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
