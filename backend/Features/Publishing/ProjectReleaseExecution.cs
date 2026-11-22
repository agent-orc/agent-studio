using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Publishing;

/// <summary>Runs repository-owned release commands on a verified release branch checkout.</summary>
internal static class ProjectReleaseExecution
{
    internal static ProjectExecutionDefinition? Read(string root)
    {
        var path = Path.Combine(root, ".agent-studio", "project.yml");
        if (!File.Exists(path)) return null;
        var result = ProjectDefinitionReader.Parse(File.ReadAllText(path));
        if (!result.IsValid)
            throw new InvalidOperationException("Invalid project.yml: " + string.Join("; ", result.Issues.Select(x => x.Message)));
        return result.Definition;
    }

    internal static string Execute(string root, string version, bool dryRun, CancellationToken token)
    {
        var definition = Read(root) ?? throw new InvalidOperationException("A project release definition is required.");
        var release = definition.Release;
        if (release?.Command is null || release.Branch is null)
            throw new InvalidOperationException("Declare release.command and release.branch in project.yml.");
        Git(root, token, "check-ref-format", "--branch", release.Branch);
        var commit = Git(root, token, "rev-parse", "--verify", $"refs/heads/{release.Branch}^{{commit}}").Trim();
        var releaseYaml = Git(root, token, "show", $"{commit}:.agent-studio/project.yml");
        var parsed = ProjectDefinitionReader.Parse(releaseYaml);
        var subject = parsed.Definition ?? throw new InvalidOperationException("Release branch has an invalid project.yml.");
        if (subject.Release?.Command != release.Command || subject.Release.Branch != release.Branch)
            throw new InvalidOperationException("Release definition differs on the release branch. Update that branch before releasing.");
        var verification = new[] { subject.Commands.Prepare }.Concat(subject.Commands.Build)
            .Concat(subject.Commands.Test).Concat(subject.Commands.Lint).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (subject.Commands.Build.Count == 0 || subject.Commands.Test.Count == 0)
            throw new InvalidOperationException("Release verification requires commands.build and commands.test.");
        var plan = $"Branch: {release.Branch}\nCommit: {commit}\nVerification:\n{string.Join('\n', verification)}\nRelease: {release.Command}\nTag: v{version}";
        if (dryRun) return plan;
        if (!string.IsNullOrWhiteSpace(Git(root, token, "tag", "-l", $"v{version}")))
            throw new InvalidOperationException($"Release tag v{version} already exists.");

        var checkout = Path.Combine(Path.GetTempPath(), "studio-release-" + Guid.NewGuid().ToString("N"));
        // A named branch lets repository scripts resolve their release branch correctly.
        // Git refuses this if another checkout already owns that branch.
        Git(root, token, "worktree", "add", checkout, release.Branch);
        try
        {
            if (Git(checkout, token, "rev-parse", "HEAD").Trim() != commit)
                throw new InvalidOperationException("Release branch changed during preparation. Preview and retry.");
            foreach (var command in verification) PublishActionService.RunReleaseCommand(checkout, command, subject, version, token);
            if (Git(checkout, token, "rev-parse", "HEAD").Trim() != commit)
                throw new InvalidOperationException("Verification changed the release commit.");
            if (!string.IsNullOrWhiteSpace(Git(checkout, token, "status", "--porcelain")))
                throw new InvalidOperationException("Verification left changes in the release checkout. Release was not started.");
            PublishActionService.RunReleaseCommand(checkout, release.Command, subject, version, token);
            var tag = $"refs/tags/v{version}";
            var tagCommit = Git(checkout, token, "rev-parse", "--verify", $"{tag}^{{commit}}").Trim();
            if (tagCommit != Git(checkout, token, "rev-parse", "HEAD").Trim())
                throw new InvalidOperationException("Release tag does not point to the release branch tip.");
            if (!string.IsNullOrWhiteSpace(Git(checkout, token, "status", "--porcelain")))
                throw new InvalidOperationException("Release command left uncommitted files. Release was not pushed.");
            Git(checkout, token, "push", "--atomic", "origin", $"HEAD:refs/heads/{release.Branch}", tag);
            return plan;
        }
        finally
        {
            Git(root, CancellationToken.None, "worktree", "remove", "--force", checkout);
        }
    }

    private static string Git(string root, CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(start, timeout: GitNetworkProcessRunner.DefaultTimeout, cancellationToken: token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        return result.StandardOutput;
    }
}
