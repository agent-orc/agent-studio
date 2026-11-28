using System.Diagnostics;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class AuthorCheckGitTests
{
    [Fact]
    public async Task Dirty_snapshot_uses_working_bytes_and_keeps_the_coding_checkout_intact()
    {
        if (!OperatingSystem.IsLinux()) return;
        var temporary = Directory.CreateTempSubdirectory("author-check-tests-");
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(temporary.FullName, "repo")).FullName;
            var baseRepo = Path.Combine(temporary.FullName, "base.git");
            var capture = Directory.CreateDirectory(Path.Combine(temporary.FullName, "capture")).FullName;
            var import = Directory.CreateDirectory(Path.Combine(temporary.FullName, "import")).FullName;
            await Git(repo, "init", "--template=");
            await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "Tracked.cs"), "original\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "Deleted.cs"), "delete this\n");
            await Git(repo, "add", ".");
            await Git(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "Base");
            var prerequisite = (await Git(repo, "rev-parse", "HEAD")).Trim();
            await Git(repo, "clone", "--bare", repo, baseRepo);
            await File.WriteAllTextAsync(Path.Combine(repo, "Tracked.cs"), "staged\n");
            await Git(repo, "add", "Tracked.cs");
            await File.WriteAllTextAsync(Path.Combine(repo, "Tracked.cs"), "dirty only\n");
            File.Delete(Path.Combine(repo, "Deleted.cs"));
            await File.WriteAllTextAsync(Path.Combine(repo, "Included.cs"), "declared\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "ignored.txt"), "ignored\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "Undeclared.cs"), "not selected\n");

            var status = await Git(repo, "status", "--porcelain");
            var refs = await Git(repo, "show-ref");
            var snapshot = await AuthorCheckGit.CaptureAsync(repo, capture, prerequisite, ["Included.cs"], default);
            Assert.Equal(status, await Git(repo, "status", "--porcelain"));
            Assert.Equal(refs, await Git(repo, "show-ref"));
            Assert.Equal("staged\n", await Git(repo, "show", ":Tracked.cs"));

            File.Copy(Path.Combine(capture, "source.bundle"), Path.Combine(import, "source.bundle"));
            await Assert.ThrowsAsync<InvalidDataException>(() => AuthorCheckGit.VerifyAndMaterializeAsync(baseRepo, import,
                snapshot with { Sha256 = new string('0', 64) }, default));
            Assert.False(Directory.Exists(Path.Combine(import, "source")));
            await AuthorCheckGit.VerifyAndMaterializeAsync(baseRepo, import, snapshot, default);
            var source = Path.Combine(import, "source");
            Assert.Equal("dirty only\n", await File.ReadAllTextAsync(Path.Combine(source, "Tracked.cs")));
            Assert.Equal("declared\n", await File.ReadAllTextAsync(Path.Combine(source, "Included.cs")));
            Assert.False(File.Exists(Path.Combine(source, "Deleted.cs")));
            Assert.False(File.Exists(Path.Combine(source, "ignored.txt")));
            Assert.False(File.Exists(Path.Combine(source, "Undeclared.cs")));
            Assert.False(Directory.Exists(Path.Combine(source, ".git")));
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static async Task<string> Git(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
