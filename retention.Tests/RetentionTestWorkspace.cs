using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Retention.Tests;

internal sealed class RetentionTestWorkspace : IDisposable
{
    public RetentionTestWorkspace(bool initializeGit = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "retention-tests-" + Guid.NewGuid().ToString("N"));
        Workspace = Path.Combine(Root, "agent-taskboard-workspace");
        Archive = Path.Combine(Root, "agent-taskboard-archive");
        Backups = Path.Combine(Root, "backups");
        Directory.CreateDirectory(Workspace);
        if (initializeGit)
        {
            Git("init", "-q", "-b", "main");
            Git("config", "user.name", "test");
            Git("config", "user.email", "test@example.com");
            File.WriteAllText(Path.Combine(Workspace, "README.md"), "workspace\n");
            CommitAll("seed");
        }
    }

    public string Root { get; }
    public string Workspace { get; }
    public string Archive { get; }
    public string Backups { get; }

    public string SeedTask(
        string project,
        string lane,
        string key,
        DateTimeOffset enteredLaneAt,
        string? bucket = null)
    {
        var path = Path.Combine(Workspace, "projects", project, "tasks", bucket ?? lane, key);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, state = lane, enteredLaneAt,
        }));
        return path;
    }

    public void CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", message);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Workspace, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }
}
