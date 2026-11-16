using System.Diagnostics;
using AgentStudio.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// PUB-1 - end-to-end derivation tests against real fixture repositories (temp
/// repos with workflows + tags + manifests + commits, same SeedRepo/RunGit style
/// as the other GitService tests). They pin the two reference layouts and the
/// quiet/first-publish special states:
///  - coding-agent-runner analog: a tag-triggered NuGet release workflow + a Pages
///    deploy workflow, tagged v0.3.1, with commits after the tag touching the
///    package source vs the website folder -> a NuGet target at 0.3.1 with a
///    package pending count, and a website target with its own pending count;
///  - coding-agent-chat analog: a tag-triggered npm release + website, never
///    tagged -> npm "first publish pending" (no version, no count) + website;
///  - quiet: no commits since the tag touching scope -> pending 0, no badge;
///  - the per-task publishable chip fold by anchor set-membership.
/// The git operations are real, not mocked.
/// </summary>
public class PublishTargetServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<PublishTargetService> _services = [];

    public PublishTargetServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pub-derive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Derive_NuGetPlusWebsite_TaggedRepo_ReportsVersionAndPendingDeltas()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteWorkflow(repoRoot, "deploy-website.yml", PagesDeployWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Coding.Agent.Runner"));
        WriteFile(repoRoot, "src/Runner/Program.cs", "// v1");
        WriteFile(repoRoot, "website/index.html", "<h1>home</h1>");
        CommitAll(repoRoot, "seed");
        RunGit(repoRoot, "tag", "v0.3.1");

        // Two package-source commits + one docs commit (excluded) + one website commit.
        WriteFile(repoRoot, "src/Runner/Feature.cs", "// f1");
        CommitAll(repoRoot, "feat: one");
        WriteFile(repoRoot, "src/Runner/Feature2.cs", "// f2");
        CommitAll(repoRoot, "feat: two");
        WriteFile(repoRoot, "docs/notes.md", "notes");
        CommitAll(repoRoot, "docs: note");
        WriteFile(repoRoot, "website/about.html", "<h1>about</h1>");
        CommitAll(repoRoot, "site: about");

        var status = BuildService(repoRoot, watchPath).GetProjectPublishStatus("Demo");

        Assert.True(status.IsRepo);
        var pkg = Assert.Single(status.Targets, t => t.Kind == PublishTargetKind.Package);
        Assert.Equal(PublishEcosystems.NuGet, pkg.Ecosystem);
        Assert.Equal("NuGet", pkg.Label);
        Assert.Equal("Coding.Agent.Runner", pkg.PackageName);
        Assert.Equal("0.3.1", pkg.CurrentVersion);
        Assert.False(pkg.FirstPublishPending);
        Assert.Equal(PublishReferenceKinds.Tag, pkg.ReferenceKind);
        Assert.Equal("v0.3.1", pkg.Reference);
        // Two src commits pending; the docs and website commits are out of package scope.
        Assert.Equal(2, pkg.PendingCount);

        var web = Assert.Single(status.Targets, t => t.Kind == PublishTargetKind.Website);
        Assert.Equal("Website", web.Label);
        Assert.Equal(PublishReferenceKinds.ReleaseTag, web.ReferenceKind);
        Assert.Equal(1, web.PendingCount);
    }

    [Fact]
    public void Derive_NpmNeverTagged_ReportsFirstPublishPending()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NpmReleaseWorkflow);
        WriteWorkflow(repoRoot, "deploy-website.yml", PagesDeployWorkflow);
        WriteFile(repoRoot, "package.json", """{ "name": "coding-agent-chat", "version": "0.0.0" }""");
        WriteFile(repoRoot, "src/index.ts", "export const x = 1;");
        WriteFile(repoRoot, "website/index.html", "<h1>home</h1>");
        CommitAll(repoRoot, "seed");
        // Deliberately never tagged: the package has no first publish.

        var status = BuildService(repoRoot, watchPath).GetProjectPublishStatus("Demo");

        var pkg = Assert.Single(status.Targets, t => t.Kind == PublishTargetKind.Package);
        Assert.Equal(PublishEcosystems.Npm, pkg.Ecosystem);
        Assert.Equal("npm", pkg.Label);
        Assert.Equal("coding-agent-chat", pkg.PackageName);
        Assert.Null(pkg.CurrentVersion);
        Assert.True(pkg.FirstPublishPending);
        Assert.Null(pkg.PendingCount);
        Assert.Equal(PublishReferenceKinds.None, pkg.ReferenceKind);

        Assert.Contains(status.Targets, t => t.Kind == PublishTargetKind.Website);
    }

    [Fact]
    public void Derive_NoPendingSinceTag_IsQuiet()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg"));
        WriteFile(repoRoot, "src/Runner/Program.cs", "// v1");
        CommitAll(repoRoot, "seed");
        RunGit(repoRoot, "tag", "v1.0.0");
        // No commits after the tag.

        var status = BuildService(repoRoot, watchPath).GetProjectPublishStatus("Demo");

        var pkg = Assert.Single(status.Targets);
        Assert.Equal("1.0.0", pkg.CurrentVersion);
        Assert.Equal(0, pkg.PendingCount); // quiet: nothing pending -> no badge
    }

    [Fact]
    public void Derive_OnlyCiWorkflow_YieldsNoTargets()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "ci.yml", "on:\n  push:\n    branches: [main]\njobs:\n  test:\n    steps:\n      - run: dotnet test\n");
        WriteFile(repoRoot, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        CommitAll(repoRoot, "seed");

        var status = BuildService(repoRoot, watchPath).GetProjectPublishStatus("Demo");

        Assert.True(status.IsRepo);
        Assert.Empty(status.Targets);
    }

    [Fact]
    public void Derive_UnknownProject_ReturnsEmptyWithError()
    {
        var (repoRoot, watchPath) = SetupRepo();
        var status = BuildService(repoRoot, watchPath).GetProjectPublishStatus("No Such Project");

        Assert.False(status.IsRepo);
        Assert.NotNull(status.Error);
        Assert.Empty(status.Targets);
    }

    [Fact]
    public void Derive_WarmHeartbeatBeyondFormerTtl_DoesNotRecomputeGitProjection()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg"));
        CommitAll(repoRoot, "seed");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z"));
        var service = BuildService(repoRoot, watchPath, time);

        service.GetProjectPublishStatus("Demo");
        Assert.Equal(1, service.ComputationCount);

        time.Advance(TimeSpan.FromMinutes(5));
        service.GetProjectPublishStatus("Demo");

        Assert.Equal(1, service.ComputationCount);
    }

    [Fact]
    public void Derive_UncommittedWorkflowChangeInvalidatesWarmProjectionImmediately()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", "on: [push]\njobs:\n  test:\n    steps:\n      - run: dotnet test\n");
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg"));
        CommitAll(repoRoot, "seed");
        var service = BuildService(repoRoot, watchPath);

        Assert.Empty(service.GetProjectPublishStatus("Demo").Targets);
        Assert.Equal(1, service.ComputationCount);

        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        var status = service.GetProjectPublishStatus("Demo");

        Assert.Contains(status.Targets, target => target.Ecosystem == PublishEcosystems.NuGet);
        Assert.Equal(2, service.ComputationCount);
    }

    [Fact]
    public void Derive_UncommittedNestedManifestChangeInvalidatesViaWatcher()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg.One"));
        CommitAll(repoRoot, "seed");
        var service = BuildService(repoRoot, watchPath);

        var first = Assert.Single(service.GetProjectPublishStatus("Demo").Targets);
        Assert.Equal("Pkg.One", first.PackageName);

        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg.Two"));
        ProjectPublishStatus? refreshed = null;
        // Watcher event delivery has no latency bound; under parallel test
        // load even 30s was observed missed; 60s bounds the worst case. SpinUntil exits early on success.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            refreshed = service.GetProjectPublishStatus("Demo");
            return refreshed.Targets.SingleOrDefault()?.PackageName == "Pkg.Two";
        }, TimeSpan.FromSeconds(60)));

        Assert.NotNull(refreshed);
        Assert.True(service.ComputationCount >= 2);
    }

    [Fact]
    public void Derive_DeletingDirectoryWithNestedManifestInvalidatesWarmProjection()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", ReleaseTriggerOnlyWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg"));
        CommitAll(repoRoot, "seed");
        var service = BuildService(repoRoot, watchPath);

        Assert.Single(service.GetProjectPublishStatus("Demo").Targets);
        Directory.Delete(Path.Combine(repoRoot, "src", "Runner"), recursive: true);

        Assert.True(SpinWait.SpinUntil(
            () => service.GetProjectPublishStatus("Demo").Targets.Count == 0,
            TimeSpan.FromSeconds(60)));
        Assert.True(service.ComputationCount >= 2);
    }

    [Fact]
    public void Derive_RenamingManifestDirectoryIntoIgnoredTreeInvalidatesWarmProjection()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", ReleaseTriggerOnlyWorkflow);
        WriteFile(repoRoot, "src/Runner.v1/Runner.csproj", PackableCsproj("Pkg"));
        CommitAll(repoRoot, "seed");
        var service = BuildService(repoRoot, watchPath);

        Assert.Single(service.GetProjectPublishStatus("Demo").Targets);
        Directory.Move(
            Path.Combine(repoRoot, "src", "Runner.v1"),
            Path.Combine(repoRoot, "src", "obj"));

        Assert.True(SpinWait.SpinUntil(
            () => service.GetProjectPublishStatus("Demo").Targets.Count == 0,
            TimeSpan.FromSeconds(60)));
        Assert.True(service.ComputationCount >= 2);
    }

    [Fact]
    public void Derive_UnrelatedSourceContentChangeDoesNotInvalidatePublishInputs()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg"));
        WriteFile(repoRoot, "src/Runner/Feature.cs", "// committed source");
        CommitAll(repoRoot, "seed");
        var service = BuildService(repoRoot, watchPath);

        service.GetProjectPublishStatus("Demo");
        Assert.Equal(1, service.ComputationCount);
        WriteFile(repoRoot, "src/Runner/Feature.cs", "// uncommitted source edit");

        Assert.False(SpinWait.SpinUntil(() =>
        {
            service.GetProjectPublishStatus("Demo");
            return service.ComputationCount > 1;
        }, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Derive_ErrorValueUsesShortRetryTtl()
    {
        var (repoRoot, watchPath) = SetupRepo();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z"));
        var service = BuildService(repoRoot, watchPath, time);

        Assert.NotNull(service.GetProjectPublishStatus("No Such Project").Error);
        Assert.Equal(1, service.ComputationCount);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(service.GetProjectPublishStatus("No Such Project").Error);
        Assert.Equal(2, service.ComputationCount);
    }

    [Fact]
    public void Derive_RemoteOnlyOriginHeadFallbackUsesRemoteTrackingBranch()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Remote.Pkg"));
        CommitAll(repoRoot, "seed");
        RunGit(repoRoot, "update-ref", "refs/remotes/origin/main", "main");
        RunGit(repoRoot, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        RunGit(repoRoot, "checkout", "-q", "--detach", "main");
        RunGit(repoRoot, "branch", "-D", "main");

        var status = BuildService(repoRoot, watchPath).GetProjectPublishStatus("Demo");

        var package = Assert.Single(status.Targets);
        Assert.Equal("Remote.Pkg", package.PackageName);
    }

    [Fact]
    public void TaskPublishable_MapsCompletedTaskToTheTargetItsCommitTouched()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NuGetReleaseWorkflow);
        WriteWorkflow(repoRoot, "deploy-website.yml", PagesDeployWorkflow);
        WriteFile(repoRoot, "src/Runner/Runner.csproj", PackableCsproj("Pkg"));
        WriteFile(repoRoot, "src/Runner/Program.cs", "// v1");
        WriteFile(repoRoot, "website/index.html", "<h1>home</h1>");
        CommitAll(repoRoot, "seed");
        RunGit(repoRoot, "tag", "v0.1.0");

        WriteFile(repoRoot, "src/Runner/Feature.cs", "// f1");
        CommitAll(repoRoot, "feat: package work");
        var pkgSha = HeadSha(repoRoot);
        WriteFile(repoRoot, "website/about.html", "<h1>about</h1>");
        CommitAll(repoRoot, "site: web work");
        var webSha = HeadSha(repoRoot);

        var svc = BuildService(repoRoot, watchPath);
        var fold = new TaskPublishableService(svc, NullLogger<TaskPublishableService>.Instance);

        var pkgTask = CompletedTask("t-pkg", watchPath, pkgSha);
        var webTask = CompletedTask("t-web", watchPath, webSha);
        var lookup = fold.BuildLookup(new[] { pkgTask, webTask });

        Assert.True(lookup.TryGetValue(pkgTask.TaskKey, out var pkgSignal));
        Assert.Contains("NuGet", pkgSignal!.Labels);
        Assert.DoesNotContain("Website", pkgSignal.Labels);

        Assert.True(lookup.TryGetValue(webTask.TaskKey, out var webSignal));
        Assert.Contains("Website", webSignal!.Labels);
        Assert.DoesNotContain("NuGet", webSignal.Labels);
    }

    [Fact]
    public void TaskPublishable_NoFfMergeAfterTag_MapsAcceptedFeatureAndExcludesEarlierCard()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteWorkflow(repoRoot, "release.yml", NpmReleaseWorkflow);
        WriteFile(repoRoot, "package.json", """{ "name": "fixture", "version": "0.1.0" }""");
        WriteFile(repoRoot, "src/index.ts", "export const base = true;");
        CommitAll(repoRoot, "seed");
        var beforeTag = HeadSha(repoRoot);
        RunGit(repoRoot, "tag", "v0.1.0");

        RunGit(repoRoot, "checkout", "-q", "-b", "task/feature");
        WriteFile(repoRoot, "src/feature.ts", "export const feature = true;");
        CommitAll(repoRoot, "feat: accepted feature");
        var featureSha = HeadSha(repoRoot);
        RunGit(repoRoot, "checkout", "-q", "main");
        RunGit(repoRoot, "merge", "--no-ff", "-q", "-m", "merge(AGT-1): feature", "task/feature");

        RunGit(repoRoot, "checkout", "-q", "-b", "task/docs");
        WriteFile(repoRoot, "docs/notes.md", "release notes");
        CommitAll(repoRoot, "docs: release notes");
        var docsSha = HeadSha(repoRoot);
        RunGit(repoRoot, "checkout", "-q", "main");
        RunGit(repoRoot, "merge", "--no-ff", "-q", "-m", "merge(AGT-2): docs", "task/docs");

        var service = BuildService(repoRoot, watchPath);
        var target = Assert.Single(service.GetProjectPublishStatus("Demo").Targets);
        Assert.Equal(1, target.PendingCount);

        var fold = new TaskPublishableService(service, NullLogger<TaskPublishableService>.Instance);
        var feature = CompletedTask("feature", watchPath, featureSha) with { TaskType = TaskTypes.Feature };
        var docs = CompletedTask("docs", watchPath, docsSha);
        var released = CompletedTask("released", watchPath, beforeTag);
        var lookup = fold.BuildLookup([feature, docs, released]);

        Assert.Contains(target.Id, lookup[feature.TaskKey].TargetIds);
        Assert.DoesNotContain(docs.TaskKey, lookup.Keys);
        Assert.DoesNotContain(released.TaskKey, lookup.Keys);
        var pendingTypes = new[] { feature, docs, released }
            .Where(task => lookup.TryGetValue(task.TaskKey, out var signal)
                && signal.TargetIds.Contains(target.Id))
            .Select(task => task.TaskType);
        Assert.Equal("0.2.0", PublishActionService.SuggestNextVersion(
            target.CurrentVersion, pendingTypes));
    }

    [Fact]
    public void LocateNpm_PrefersRootPackage_SkipsWebsitePackageJson()
    {
        var (repoRoot, _) = SetupRepo();
        WriteFile(repoRoot, "package.json", """{ "name": "root-pkg" }""");
        WriteFile(repoRoot, "website/package.json", """{ "name": "the-website" }""");

        var manifest = PublishManifestLocator.LocateNpm(repoRoot, new[] { "website" });

        Assert.NotNull(manifest);
        Assert.Equal("root-pkg", manifest!.PackageName);
        Assert.Equal(string.Empty, manifest.SourceRootRelDir);
    }

    [Fact]
    public void LocateNuGet_IgnoresTestProjects_PicksPackable()
    {
        var (repoRoot, _) = SetupRepo();
        WriteFile(repoRoot, "test/App.Tests/App.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"1\"/></ItemGroup></Project>");
        WriteFile(repoRoot, "src/App/App.csproj", PackableCsproj("The.App"));

        var manifest = PublishManifestLocator.LocateNuGet(repoRoot, Array.Empty<string>());

        Assert.NotNull(manifest);
        Assert.Equal("The.App", manifest!.PackageName);
        Assert.Equal("src/App", manifest.SourceRootRelDir);
    }

    // ----- workflow fixtures -----

    private const string NuGetReleaseWorkflow = """
        name: release
        on:
          push:
            tags:
              - 'v*'
        jobs:
          publish:
            steps:
              - run: dotnet pack -c Release
              - run: dotnet nuget push out/*.nupkg --api-key ${{ secrets.NUGET }}
        """;

    private const string NpmReleaseWorkflow = """
        name: release
        on:
          push:
            tags: ['v*']
        jobs:
          publish:
            steps:
              - run: npm ci
              - run: npm publish --access public
        """;

    private const string PagesDeployWorkflow = """
        name: deploy-website
        on:
          push:
            branches: [main]
        jobs:
          deploy:
            steps:
              - uses: actions/upload-pages-artifact@v3
                with:
                  path: website
              - uses: actions/deploy-pages@v4
        """;

    private const string ReleaseTriggerOnlyWorkflow = """
        name: release
        on:
          push:
            tags: ['v*']
        jobs:
          release:
            steps:
              - run: echo release
        """;

    private static string PackableCsproj(string packageId) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>{packageId}</PackageId><Version>0.3.1</Version></PropertyGroup></Project>";

    // ----- harness -----

    private (string repoRoot, string watchPath) SetupRepo()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);

        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");
        return (repoRoot, watchPath);
    }

    private PublishTargetService BuildService(
        string repoRoot,
        string watchPath,
        TimeProvider? timeProvider = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var service = new PublishTargetService(
            git,
            settings,
            NullLogger<PublishTargetService>.Instance,
            timeProvider ?? TimeProvider.System);
        _services.Add(service);
        return service;
    }

    private static TaskInfo CompletedTask(string id, string watchPath, string sha) => new()
    {
        Id = id,
        TaskKey = $"{watchPath}::{id}",
        Title = id,
        State = TaskStates.Completed,
        ProjectName = "Demo",
        WatchPath = watchPath,
        FolderPath = Path.Combine(watchPath, TaskStates.Completed, id),
        Commits = [new TaskCommitInfo { Sha = sha }],
    };

    private static void WriteWorkflow(string repoRoot, string name, string content)
        => WriteFile(repoRoot, ".github/workflows/" + name, content);

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void CommitAll(string repoRoot, string message)
    {
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", message);
    }

    private static string HeadSha(string repoRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return outp.Trim();
    }

    private static void RunGit(string cwd, params string[] args)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
