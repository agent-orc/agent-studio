using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentStudio.Git;
using Xunit;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class HostGitInvocationGuardTests
{
    [Fact]
    public void EveryBackendGitLaunchUsesTheSharedRunnerOrDirectHardening()
    {
        var backend = Path.Combine(CSharpSourceScanner.RepoRoot(), "backend");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            var launches = Regex.Matches(source,
                "FileName\\s*=\\s*\"git\"|new\\s+ProcessStartInfo\\(\"git\"\\)").Count;
            if (launches == 0 || source.Contains("GitNetworkProcessRunner.Run(", StringComparison.Ordinal)
                || source.Contains("GitNetworkProcessRunner.RunAsync(", StringComparison.Ordinal))
                continue;
            var hardened = Regex.Matches(source, "HostGitProcessHardening\\.Apply\\(").Count;
            if (hardened < launches)
                violations.Add($"{Path.GetRelativePath(backend, file)}: {launches} Git launches, {hardened} hardening calls");
        }
        Assert.Empty(violations);
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData("core.hooksPath", "hostile-hooks")]
    [InlineData("core.fsmonitor", "true")]
    [InlineData("safe.bareRepository", "all")]
    public void HostGitInvocationOverridesInheritedUnsafeConfiguration(string key, string unsafeValue)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("config");
        start.ArgumentList.Add("--get");
        start.ArgumentList.Add(key);
        start.Environment["GIT_CONFIG_COUNT"] = "1";
        start.Environment["GIT_CONFIG_KEY_0"] = key;
        start.Environment["GIT_CONFIG_VALUE_0"] = unsafeValue;

        var result = GitNetworkProcessRunner.Run(start);

        Assert.True(result.Success, result.StandardError);
        Assert.Equal(key switch
        {
            "core.hooksPath" => OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            "core.fsmonitor" => "false",
            _ => "explicit",
        }, result.StandardOutput.Trim());
        Assert.Equal("4", start.Environment["GIT_CONFIG_COUNT"]);
        HostGitProcessHardening.Apply(start);
        Assert.Equal("4", start.Environment["GIT_CONFIG_COUNT"]);
    }
}
