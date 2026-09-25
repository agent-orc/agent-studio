using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

public sealed class GateShellInvocationTests
{
    [Fact]
    public void WindowsPlatformCommand_UsesGitBashWithOneUnchangedCommandArgument()
    {
        const string command = "dotnet test --logger \"console;verbosity=normal\"";

        var (fileName, args) = GateShellInvocation.Build(
            command, VerifyCommandShell.Platform, isWindows: true,
            bashPath: @"C:\Program Files\Git\bin\bash.exe", bashExists: true);

        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", fileName);
        Assert.Equal(new[] { "-lc", command }, args);
    }

    [Fact]
    public void WindowsWithoutBash_StartsDotNetWithLoggerArgumentsIntact()
    {
        var (fileName, args) = GateShellInvocation.Build(
            "dotnet test --filter \"FullyQualifiedName~Example.Test\" " +
            "--logger \"trx;LogFilePrefix=gate\" --logger \"console;verbosity=normal\"",
            VerifyCommandShell.Platform, isWindows: true, bashPath: "bash", bashExists: false);

        Assert.Equal("dotnet", fileName);
        Assert.Equal(new[]
        {
            "test", "--filter", "FullyQualifiedName~Example.Test",
            "--logger", "trx;LogFilePrefix=gate", "--logger", "console;verbosity=normal",
        }, args);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public void WindowsComposedDotNetTest_ProducesTrx()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows shell regression.");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "backend.Tests", "OrchestratorApi.Tests.csproj");
        Skip.IfNot(File.Exists(project), "Repository test project is unavailable.");
        var prefix = "gate-shell-" + Guid.NewGuid().ToString("N");
        var output = Path.Combine(Path.GetTempPath(), prefix);
        Directory.CreateDirectory(output);
        try
        {
            var command = $"dotnet test \"{project}\" --filter \"FullyQualifiedName~BashExecutableTests\" " +
                $"--logger \"trx;LogFilePrefix={prefix}\" --logger \"console;verbosity=normal\" " +
                $"--results-directory \"{output.Replace('\\', '/')}\"";
            var (fileName, args) = GateShellInvocation.Build(
                command, VerifyCommandShell.Platform, isWindows: true,
                BashExecutable.Path, File.Exists(BashExecutable.Path));
            var start = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(180_000), "dotnet test exceeded three minutes.");
            Assert.True(process.ExitCode == 0, $"{stdout.Result}\n{stderr.Result}");
            Assert.NotEmpty(Directory.GetFiles(output, prefix + "*.trx"));
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch { /* best effort */ }
        }
    }
}
