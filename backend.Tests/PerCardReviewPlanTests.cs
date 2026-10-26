using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3310: <c>testing.perCard</c> on the Remote Review plan. <c>full</c>
/// (the default) and every T1 fallback keep the plan byte-identical;
/// <c>affected</c> runs build, lint, the touched-area fast checks and the
/// tests the generated project map selects.
/// </summary>
public sealed class PerCardReviewPlanTests : IDisposable
{
    private const string ResultRef = "refs/heads/agent-studio/results/task";
    private const string BackendTest =
        "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound";
    private const string FrontendUnit = "bash scripts/npm-frontend.sh run test:ci";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "per-card-plan-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _folder;

    public PerCardReviewPlanTests()
    {
        _repo = Path.Combine(_root, "repo");
        _folder = Path.Combine(_root, "task");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Change the backend.\n");
        Repository();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("backend/FooService.cs")]
    [InlineData("global.json")]
    public void FullAndEveryFallbackKeepThePlanByteIdentical(string changed)
    {
        var resultSha = Commit(changed);
        var today = Json(Build(new ProjectSettings(), resultSha, changed));

        Assert.Equal(today, Json(Build(Settings(PerCardTestModes.Full), resultSha, changed)));
        Assert.Equal(today, Json(Build(Settings("unknown"), resultSha, changed)));
        if (changed == "global.json")
            Assert.Equal(today, Json(Build(Settings(PerCardTestModes.Affected), resultSha, changed)));
    }

    [Fact]
    public void AffectedBackendDiffRunsTheReferencingClassesBuildAndLint()
    {
        var resultSha = Commit("backend/FooService.cs");

        var plan = Build(Settings(PerCardTestModes.Affected), resultSha, "backend/FooService.cs");

        var shells = plan.Commands.Where(command => command.Arguments.Count == 2)
            .Select(command => command.Arguments[1]).ToList();
        Assert.Contains("echo build", shells);
        Assert.Contains("bash scripts/npm-frontend.sh run lint", shells);
        Assert.Contains("dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~FooServiceTests)\"", shells);
        Assert.DoesNotContain(BackendTest, shells);
        Assert.DoesNotContain(FrontendUnit, shells);
        Assert.DoesNotContain("bash scripts/npm-frontend.sh run typecheck", shells);
        Assert.Equal(TestExecutionLevels.WorkPackage, plan.TestLevel);
        Assert.Matches("^[0-9a-f]{64}$", plan.TestSelectionDigest!);
        Assert.False(plan.BuildTestDeferredToBatch);
    }

    [Fact]
    public void AffectedFrontendDiffAddsTheTypeCheckAndRunsFrontendUnitOnly()
    {
        var resultSha = Commit("frontend/src/app.ts");

        var plan = Build(Settings(PerCardTestModes.Affected), resultSha, "frontend/src/app.ts");

        var typecheck = Assert.Single(plan.Commands, command =>
            command.Arguments.Contains("bash scripts/npm-frontend.sh run typecheck"));
        Assert.Equal("lint", typecheck.Aspect);
        Assert.Equal(Contract.ReviewBaselineModes.ExitStatus, typecheck.BaselineMode);
        Assert.Contains(plan.Commands, command => command.Arguments.Contains(FrontendUnit));
        Assert.DoesNotContain(plan.Commands, command =>
            command.Arguments.Any(argument => argument.StartsWith("dotnet test", StringComparison.Ordinal)));
    }

    [Fact]
    public void FastChecksFollowTheTouchedPackages()
    {
        var verify = VerifyCommandPlanner.Plan(_repo, null);

        var backend = FastCheckPlanner.Plan(_repo, verify, ["backend/FooService.cs"]);
        Assert.Empty(backend.Commands);
        Assert.Contains("fast check type check: the build commands compile the touched .NET projects", backend.Reasons);
        Assert.Contains("fast check format: no .NET format check is declared", backend.Reasons);

        var frontend = FastCheckPlanner.Plan(_repo, verify, ["frontend/src/app.ts"]);
        var command = Assert.Single(frontend.Commands);
        Assert.Equal("bash scripts/npm-frontend.sh run typecheck", command.Command);
        Assert.Equal(VerifyCommandKind.Lint, command.Kind);
        Assert.Equal("", command.WorkingSubdir);
        Assert.Contains("fast check format: frontend/ declares no 'format:check' script", frontend.Reasons);

        // A package without a declared invocation runs its own scripts in place.
        Write("web/package.json",
            "{\"scripts\":{\"lint\":\"eslint .\",\"typecheck\":\"tsc\",\"format:check\":\"prettier --check .\"}}");
        Git("add", "web/package.json");
        var web = FastCheckPlanner.Plan(_repo, verify with { Commands = [] }, ["web/index.ts"]);
        Assert.Equal(["npm run lint", "npm run typecheck", "npm run format:check"],
            web.Commands.Select(item => item.Command));
        Assert.All(web.Commands, item => Assert.Equal("web", item.WorkingSubdir));
    }

    private Contract.ReviewPlanDto Build(ProjectSettings settings, string resultSha, string changed)
        => Builder().Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview,
        }, _repo, settings, "develop", resultSha, [changed], ResultRef);

    private static ProjectSettings Settings(string perCard)
        => new() { Testing = new ProjectTestingSettings { PerCard = perCard } };

    // The deep-review receipt names its own attempt; the rest of the plan is
    // the byte-identity subject.
    private static string Json(Contract.ReviewPlanDto plan)
        => JsonSerializer.Serialize(plan with { DeepReviewDecision = null });

    private static RemoteReviewPlanBuilder Builder()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        return new RemoteReviewPlanBuilder(aspects, config, git, new FakeTimeProvider());
    }

    private void Repository()
    {
        Git("init", "-q", "-b", "develop");
        Git("config", "user.email", "per-card@example.invalid");
        Git("config", "user.name", "Per Card");
        Git("config", "core.autocrlf", "false");
        Write(".agent-studio/prepare", "#!/bin/sh\nexit 0\n");
        Write(".agent-studio/project.yml",
            "schemaVersion: 1\nstack: [custom]\ntoolVersions:\ncommands:\n"
            + "  prepare: .agent-studio/prepare\n  build:\n    - echo build\n  test:\n"
            + $"    - {BackendTest}\n    - {FrontendUnit}\n"
            + "  lint:\n    - bash scripts/npm-frontend.sh run lint\n"
            + "testSuites:\n  - id: frontend-unit\n    category: routine\n    expectedDurationSeconds: 360\n"
            + $"    command: {FrontendUnit}\n"
            + "cachePaths:\ncapabilities: [linux]\nenvironment:\n");
        Write("scripts/npm-frontend.sh", "#!/bin/sh\nexit 0\n");
        Write("frontend/package.json",
            "{\"scripts\":{\"lint\":\"ng lint\",\"typecheck\":\"tsc --noEmit\",\"test:ci\":\"ng test\"}}");
        Write("frontend/src/app.ts", "export const app = 1;\n");
        Write("global.json", "{}\n");
        Write("backend/OrchestratorApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("backend/FooService.cs", "namespace AgentStudio;\npublic sealed class FooService { }\n");
        Write("backend.Tests/OrchestratorApi.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <ItemGroup><ProjectReference Include=\"..\\backend\\OrchestratorApi.csproj\" /></ItemGroup>\n"
            + "</Project>\n");
        Write("backend.Tests/FooServiceTests.cs",
            "namespace AgentStudio.Tests;\npublic sealed class FooServiceTests\n{\n"
            + "    [Fact]\n    public void Runs() => _ = new FooService();\n}\n");
        foreach (var name in new[] { "BarTests", "BazTests", "QuxTests" })
            Write($"backend.Tests/{name}.cs",
                $"namespace AgentStudio.Tests;\npublic sealed class {name}\n{{\n    [Fact]\n    public void Runs() {{ }}\n}}\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
    }

    /// <summary>Commits a change to <paramref name="path"/> on the result branch and returns to develop.</summary>
    private string Commit(string path)
    {
        Git("switch", "-qc", "agent-studio/results/task");
        File.AppendAllText(Path.Combine(_repo, path), path.EndsWith(".json", StringComparison.Ordinal) ? "\n" : "// changed\n");
        Git("add", ".");
        Git("commit", "-qm", "delivery");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        return sha;
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }
}
