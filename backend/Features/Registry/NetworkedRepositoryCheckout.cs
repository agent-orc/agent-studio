using System.Diagnostics;
using AgentStudio.Git;

namespace AgentStudio.Registry;

/// <summary>Creates the monolith's authoritative checkout before a URL project is registered.</summary>
public static class NetworkedRepositoryCheckout
{
    public static async Task<string> CloneAsync(string repositoryUrl, string storagePath,
        string projectId, CancellationToken ct)
    {
        var checkout = ManagedPath(storagePath, projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        if (Directory.Exists(checkout) || File.Exists(checkout))
            throw new InvalidOperationException("A server checkout already exists for this project id.");

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetDirectoryName(checkout)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var argument in new[] { "clone", "--", repositoryUrl, checkout })
            start.ArgumentList.Add(argument);
        try
        {
            var result = await GitProcessBoundary.RunAsync(start, null,
                GitProcessBoundary.LongOperationTimeout, ct);
            if (result.FailureKind != GitProcessFailureKind.None || result.ExitCode != 0)
                throw new InvalidOperationException("The Studio server could not clone the repository URL. Check its Git access and try again.");
            return ProjectRegistry.ValidateRepositoryPath(checkout)!;
        }
        catch
        {
            if (Directory.Exists(checkout)) Directory.Delete(checkout, recursive: true);
            throw;
        }
    }

    public static void RemoveIfOwned(ProjectRecord project)
    {
        var managed = ManagedPath(project.StorageLocation, project.Id);
        if (string.Equals(project.RepositoryPath, managed, StringComparison.Ordinal)
            && Directory.Exists(managed))
            Directory.Delete(managed, recursive: true);
    }

    private static string ManagedPath(string storagePath, string projectId)
    {
        // Storage lives at <task-repository>/projects/<id>/tasks. Keep Git
        // outside projects, which the task scanner treats as managed storage.
        var root = new DirectoryInfo(storagePath).Parent?.Parent?.Parent?.FullName
            ?? throw new ArgumentException("Project storage path has no task repository root.", nameof(storagePath));
        return Path.Combine(root, "repository-checkouts", projectId);
    }
}
