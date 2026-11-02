using System.Diagnostics;

namespace AgentStudio.Tests;

/// <summary>
/// A throw-away Git checkout for the tests of routes that read checkout files:
/// committed links, nested Git metadata and oversize files.
/// </summary>
internal sealed class CheckoutFileFixture : IDisposable
{
    public CheckoutFileFixture(string prefix)
    {
        Parent = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Parent, "repo");
        Outside = Path.Combine(Parent, "outside");
        Directory.CreateDirectory(Outside);
        Directory.CreateDirectory(Parent);
        Git(Parent, "init", "-q", "-b", "main", "repo");
        Git(Root, "config", "user.email", "test@example.com");
        Git(Root, "config", "user.name", "test");
        Git(Root, "config", "commit.gpgsign", "false");
        Git(Root, "config", "core.symlinks", "true");
    }

    public string Parent { get; }

    public string Root { get; }

    /// <summary>A directory next to the checkout, never served.</summary>
    public string Outside { get; }

    public string OutsideFile(string name, string content)
    {
        var path = Path.Combine(Outside, name);
        File.WriteAllText(path, content);
        return path;
    }

    public string Write(string relativePath, string content)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Writes a sparse file one byte above <paramref name="limit"/>.</summary>
    public string WriteOversize(string relativePath, long limit)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write);
        stream.SetLength(limit + 1);
        return full;
    }

    /// <summary>
    /// Creates and commits a file link. False where the platform refuses to
    /// create links for this user (Windows without the privilege).
    /// </summary>
    public bool TryCommitLink(string relativePath, string target)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            File.CreateSymbolicLink(full, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows()
            && ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        Commit();
        return true;
    }

    public void Commit()
    {
        Git(Root, "add", "-A");
        Git(Root, "commit", "-q", "--allow-empty", "-m", "fixture");
    }

    public string Full(string relativePath)
        => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Parent, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(Parent, recursive: true);
        }
        catch { /* best effort */ }
    }
}
