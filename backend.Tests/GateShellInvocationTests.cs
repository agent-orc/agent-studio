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

    [Fact]
    public void WindowsWithoutBash_StartsNpmCliThroughNodeWithSeparateArguments()
    {
        const string cli = @"C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js";

        var (fileName, args) = GateShellInvocation.Build(
            "npm run build -- --mode \"test value\"", VerifyCommandShell.Platform,
            isWindows: true, bashPath: "bash", bashExists: false, npmCliPath: cli);

        Assert.Equal("node.exe", fileName);
        Assert.Equal(new[] { cli, "run", "build", "--", "--mode", "test value" }, args);
    }

    [Fact]
    public void FindNpmCli_UsesNpmCmdsOwnInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-npm-" + Guid.NewGuid().ToString("N"));
        var cli = Path.Combine(root, "node_modules", "npm", "bin", "npm-cli.js");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
            File.WriteAllText(Path.Combine(root, "npm.cmd"), string.Empty);
            File.WriteAllText(cli, string.Empty);

            Assert.Equal(cli, GateShellInvocation.FindNpmCli(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
