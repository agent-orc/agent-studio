using System.Diagnostics;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3811: the merge-time build gate used to apply only for central
/// build-profile build commands, <c>frontend/</c>, managed sources, or the
/// Compose stack. A root-level Node library whose project.yml declares
/// <c>npm run build</c> and <c>npm test</c> therefore merged into develop
/// without any gate. Commands from project.yml or discovery now count alike.
/// </summary>
public sealed class PreDevelopBuildGateDeclaredCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pre-develop-declared-" + Guid.NewGuid().ToString("N"));

    public PreDevelopBuildGateDeclaredCommandsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void AppliesTo_RootPackageJsonWithProjectYmlBuildCommands()
    {
        NodeLibraryWithProjectDefinition();

        Assert.True(PreDevelopBuildGate.AppliesTo(new BuildProfile(), ["src/tally.js"], _root));
        Assert.True(PreDevelopBuildGate.AppliesTo(null, ["index.js", "package.json"], _root));
    }

    [Fact]
    public void AppliesTo_RootPackageJsonDiscoveryWithoutProjectYml()
    {
        Write("package.json", """
            { "name": "tally", "scripts": { "build": "node build.js", "test": "node --test" } }
            """);
        Write("index.js", "module.exports = 1;\n");
        CommitAll();

        Assert.True(PreDevelopBuildGate.AppliesTo(new BuildProfile(), ["index.js"], _root));
    }

    [Fact]
    public void AppliesTo_DocsOnlyMergeOfADeclaredRepositoryStaysUngated()
    {
        NodeLibraryWithProjectDefinition();

        Assert.False(PreDevelopBuildGate.AppliesTo(
            new BuildProfile(), ["README.md", "docs/usage.md"], _root));
    }

    [Fact]
    public void AppliesTo_RepositoryWithoutBuildOrTestCommandsStaysUngated()
    {
        Write("README.md", "seed\n");
        Write("notes.txt", "plain\n");
        CommitAll();

        Assert.False(PreDevelopBuildGate.AppliesTo(new BuildProfile(), ["notes.txt"], _root));
    }

    [Fact]
    public void AppliesTo_InvalidProjectDefinitionStillGatesSoTheGateReportsIt()
    {
        Write(".agent-studio/project.yml", "schemaVersion: 1\ncommands: &anchor\n");
        Write("index.js", "module.exports = 1;\n");
        CommitAll();

        Assert.True(PreDevelopBuildGate.AppliesTo(new BuildProfile(), ["index.js"], _root));
    }

    [Fact]
    public void ResolveTestLevel_CodeDiffWithoutConventionSliceRunsTheDeclaredTests()
    {
        NodeLibraryWithProjectDefinition();

        Assert.Equal(TestExecutionLevels.WorkPackage,
            PreDevelopBuildGate.ResolveTestLevel(["src/tally.js"], _root));
        Assert.Equal(TestExecutionLevels.BuildOnly,
            PreDevelopBuildGate.ResolveTestLevel(["README.md"], _root));

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);
        var staged = DeterministicTestScope.Plan(_root, plan, ["src/tally.js"], statuses: null,
            policy: null, TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(["src/tally.js"], _root));
        Assert.Contains(staged.Commands, command =>
            command.Kind == VerifyCommandKind.Test && command.Command == "npm test");
        Assert.Contains(staged.Commands, command =>
            command.Kind == VerifyCommandKind.Build && command.Command == "npm run build");
    }

    [Fact]
    public void ResolveTestLevel_CentralProfileCommandsKeepTheBuildOnlyMatrix()
    {
        Write("task.txt", "work\n");
        CommitAll();
        var profile = new BuildProfile { BuildCmds = ["make"], TestCmds = ["make check"] };

        Assert.True(PreDevelopBuildGate.AppliesTo(profile, ["task.txt"], _root));
        Assert.Equal(TestExecutionLevels.BuildOnly,
            PreDevelopBuildGate.ResolveTestLevel(["task.txt"], _root, profile));
    }

    [Fact]
    public void ResolveTestLevel_RepositoryWithAConventionSliceKeepsBuildOnlyForScripts()
    {
        NodeLibraryWithProjectDefinition();
        Write("tools/Tool.csproj", "<Project />\n");
        CommitAll();

        Assert.Equal(TestExecutionLevels.BuildOnly,
            PreDevelopBuildGate.ResolveTestLevel(["scripts/release.sh"], _root));
    }

    [Fact]
    public void FreshMergeReason_NamesTheCommandsAndTheTestedMergedSha()
    {
        var merged = new string('a', 40);
        var gate = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 10, "", "ok", false, false)
        {
            TestedSha = merged,
            Processes =
            [
                new BuildTestGateProcessEvidence { Phase = "preparation", Command = ".agent-studio/prepare" },
                new BuildTestGateProcessEvidence { Command = "npm run build" },
                new BuildTestGateProcessEvidence { Command = "npm test" },
            ],
        };

        var reason = MergeIntoDevelopRunner.FreshMergeReason(
            MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, merged), gate);

        Assert.Equal(
            $"The integration lane created this merge and its gate returned Ok on {merged}; "
            + "commands: npm run build; npm test.",
            reason);
    }

    [Fact]
    public void FreshMergeReason_WithoutAGateNamesWhyNoGateRan()
    {
        var result = MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, new string('b', 40))
            with { GateSkipReason = PreDevelopBuildGate.NotApplicableReason };

        var reason = MergeIntoDevelopRunner.FreshMergeReason(result, gate: null);

        Assert.Contains("no gate ran", reason);
        Assert.Contains(".agent-studio/project.yml", reason);
    }

    private void NodeLibraryWithProjectDefinition()
    {
        Write("package.json", """
            { "name": "tally", "scripts": { "build": "node build.js", "test": "node --test" } }
            """);
        Write("src/tally.js", "module.exports = function tally() { return 1; };\n");
        Write("README.md", "tally\n");
        Write(".agent-studio/prepare", "#!/bin/sh\nset -eu\n");
        Write(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [node]
            commands:
              prepare: .agent-studio/prepare
              build: [npm run build]
              test: [npm test]
              lint: []
            """);
        CommitAll();
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void CommitAll()
    {
        if (!Directory.Exists(Path.Combine(_root, ".git")))
        {
            Git("init -q -b main");
            Git("config user.email test@example.com");
            Git("config user.name test");
            Git("config maintenance.auto false");
        }
        Git("add -A");
        Git("commit -q -m fixture");
    }

    private void Git(string args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {args} failed: {error}");
    }
}
