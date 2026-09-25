using AgentStudio.ExecutionPreparation;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-preparation-" + Guid.NewGuid().ToString("N"));

    public ProjectPreparationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Repository_definition_accepts_shell_operators_and_rejects_yaml_aliases()
    {
        var valid = ProjectDefinitionReader.Parse(Definition("test -f package.json && npm ci"));
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Issues.Select(issue => issue.Message)));

        var invalid = ProjectDefinitionReader.Parse(Definition("*shared"));
        Assert.Contains(invalid.Issues, issue => issue.Code == "yaml-feature-unsupported");

        var unknown = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare")
            .Replace("schemaVersion: 1", "schemaVersion: 1\nmystery: true", StringComparison.Ordinal));
        Assert.Contains(unknown.Issues, issue => issue.Code == "unknown-field");
    }

    [Fact]
    public void V1_compatibility_existing_definitions_remain_valid()
    {
        var v1Minimal = Definition(".agent-studio/prepare");
        var parsed = ProjectDefinitionReader.Parse(v1Minimal);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(1, parsed.Definition!.SchemaVersion);
        Assert.Null(parsed.Definition.Project);
        Assert.Null(parsed.Definition.Quality);
    }

    [Fact]
    public void V1_definition_rejects_v2_keys()
    {
        var v1WithV2Keys = Definition(".agent-studio/prepare") + """

            project:
              id: test-project
              components:
                - id: component1
                  path: .
            """;
        var parsed = ProjectDefinitionReader.Parse(v1WithV2Keys);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Issues, issue => issue.Code == "v1-does-not-support-project");
    }

    [Fact]
    public void V2_schema_requires_project_section()
    {
        var v2WithoutProject = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            """;
        var parsed = ProjectDefinitionReader.Parse(v2WithoutProject);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Issues, issue => issue.Code == "v2-project-required");
    }

    [Fact]
    public void V2_schema_with_project_section_is_valid()
    {
        var v2Valid = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            project:
              id: test-project
              components:
                - id: frontend
                  path: frontend
            """;
        var parsed = ProjectDefinitionReader.Parse(v2Valid);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(2, parsed.Definition!.SchemaVersion);
        Assert.NotNull(parsed.Definition.Project);
        Assert.Equal("test-project", parsed.Definition.Project!.Id);
    }

    [Fact]
    public void V2_product_properties_preserve_unknown_state()
    {
        var v2WithProperties = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            project:
              id: test-project
              properties:
                public-facing: true
              components:
                - id: api
                  path: api
                - id: frontend
                  path: frontend
            """;
        var parsed = ProjectDefinitionReader.Parse(v2WithProperties);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(2, parsed.Definition!.SchemaVersion);
        Assert.NotNull(parsed.Definition.Project);
        Assert.True(parsed.Definition.Project!.Properties?.PublicFacing);
        Assert.Null(parsed.Definition.Project.Properties?.HtmlUi);
        var apiComponent = parsed.Definition.Project.Components.First(c => c.Id == "api");
        Assert.Equal("api", apiComponent.Id);
        Assert.Equal("api", apiComponent.Path);
    }

    [Fact]
    public void V2_unsupported_schema_version_is_rejected()
    {
        var v3Definition = Definition(".agent-studio/prepare")
            .Replace("schemaVersion: 1", "schemaVersion: 3", StringComparison.Ordinal);
        var parsed = ProjectDefinitionReader.Parse(v3Definition);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Issues, issue => issue.Code == "unsupported-version");
    }

    [Fact]
    public void V2_quality_applicability_is_parsed_when_present()
    {
        var v2WithQuality = """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [node_modules]
            capabilities: [linux]
            environment:
              CI: "true"
            project:
              id: test-project
              components:
                - id: frontend
                  path: frontend
            quality:
              applicability:
                - id: frontend-seo
                  componentScope: [frontend]
                  selector:
                    allOf: [public-facing, html-ui]
                  domains: [seo]
            """;
        var parsed = ProjectDefinitionReader.Parse(v2WithQuality);

        Assert.True(parsed.IsValid, string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        Assert.NotNull(parsed.Definition!.Quality);
        Assert.Single(parsed.Definition.Quality!.Applicability);
        Assert.Equal("frontend-seo", parsed.Definition.Quality.Applicability[0].Id);
    }

    [Fact]
    public void Repository_definition_validates_both_release_identity_rule_kinds()
    {
        var lockRule = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare") + """

            release:
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  source: backend/packages.lock.json
              restore:
                - dotnet restore backend/OrchestratorApi.csproj --locked-mode
            """);
        Assert.True(lockRule.IsValid, string.Join(Environment.NewLine, lockRule.Issues.Select(issue => issue.Message)));

        var exactRule = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare") + """

            release:
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  version: 0.7.0
                  integrity: sha512-packagehash
              restore:
                - restore exact registry package
            """);
        Assert.True(exactRule.IsValid, string.Join(Environment.NewLine, exactRule.Issues.Select(issue => issue.Message)));

        var ambiguous = ProjectDefinitionReader.Parse(Definition(".agent-studio/prepare") + """

            release:
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  source: backend/packages.lock.json
                  version: 0.7.0
                  integrity: sha512-packagehash
              restore:
                - dotnet restore
            """);
        Assert.Contains(ambiguous.Issues, issue => issue.Code == "release-rule-kind-invalid");
    }

    [Fact]
    public void Agent_studio_project_definition_resolves_its_release_identity_sources()
    {
        var read = ProjectDefinitionReader.ReadWorkspace(FindRepositoryRoot());

        Assert.True(read.IsValid, string.Join(Environment.NewLine, read.Issues.Select(issue => issue.Message)));
        Assert.NotNull(read.Definition?.Release);
        Assert.Contains(read.Definition!.Release!.Identity,
            identity => identity.Package == "CodingAgentRunner"
                        && identity.Source == "backend/packages.lock.json");
    }

    [Fact]
    public async Task Successful_prepare_publishes_immutable_cache_and_second_run_hits_it()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            if [ -f "$NPM_CONFIG_CACHE/marker" ]; then printf hit > observed; else printf miss > observed; fi
            printf cache > "$NPM_CONFIG_CACHE/marker"
            """);
        var cache = Path.Combine(_root, "product-cache");

        var first = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "first.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(first.Succeeded, first.Output);
        Assert.Equal("miss", File.ReadAllText(Path.Combine(_root, "observed")));
        Assert.All(first.Manifest!.Caches, entry => Assert.Equal("published", entry.State));

        var second = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "second.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(second.CacheHit);
        Assert.Equal("hit", File.ReadAllText(Path.Combine(_root, "observed")));
        Assert.All(second.Manifest!.Caches, entry => Assert.Equal("hit", entry.State));
    }

    [Fact]
    public async Task Failed_prepare_discards_cache_staging_and_records_failure_signature()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", "mkdir -p \"$NPM_CONFIG_CACHE\"; printf poison > \"$NPM_CONFIG_CACHE/marker\"; exit 7");
        var cache = Path.Combine(_root, "product-cache");

        var result = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "failed.json"), "subject-2", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PreparationFailureKind.Command, result.FailureKind);
        Assert.StartsWith("command:7:", result.FailureSignature);
        Assert.All(result.Manifest!.Caches, entry => Assert.Equal("discarded", entry.State));
        Assert.Empty(Directory.Exists(Path.Combine(cache, "entries"))
            ? Directory.EnumerateFiles(Path.Combine(cache, "entries"), "manifest.json", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task Incomplete_immutable_entry_is_evicted_and_rebuilt_as_a_miss()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", "printf cache > \"$NPM_CONFIG_CACHE/marker\"");
        var cache = Path.Combine(_root, "product-cache");
        var first = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "first.json"), "subject-1", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(first.Succeeded);
        var content = Directory.EnumerateDirectories(Path.Combine(cache, "entries", "npm"), "content", SearchOption.AllDirectories).Single();
        Directory.Delete(content, recursive: true);
        Directory.CreateDirectory(content);

        var logs = new List<string>();
        var second = await ProjectPreparationExecutor.RunAsync(
            _root, cache, Path.Combine(_root, "second.json"), "subject-1", logs.Add, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(second.Succeeded, second.Output);
        Assert.Equal(PreparationFailureKind.None, second.FailureKind);
        Assert.Contains(logs, line =>
            line.Contains("state=evicted", StringComparison.Ordinal)
            && line.Contains("reason=validation-empty-content", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(
            Assert.Single(second.Manifest!.Caches).EntryPath, "content", "marker")));
    }

    [Fact]
    public async Task Torn_nuget_entry_without_extraction_metadata_is_evicted_and_rebuilt()
    {
        WriteRestoringDotNetRepository();
        var cache = Path.Combine(_root, "product-cache");
        var first = await PrepareAsync(cache, "nuget-first.json");
        var nuget = Assert.Single(first.Manifest!.Caches, item => item.Block == "nuget");
        var metadata = Path.Combine(
            nuget.EntryPath, "content", "xunit.analyzers", "1.4.0", ".nupkg.metadata");
        File.Delete(metadata);
        var logs = new List<string>();

        var second = await ProjectPreparationExecutor.RunAsync(
            _root,
            cache,
            Path.Combine(_root, "nuget-second.json"),
            "subject-1",
            logs.Add,
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        Assert.True(second.Succeeded, second.Output);
        Assert.Equal("published", Assert.Single(second.Manifest!.Caches).State);
        Assert.True(File.Exists(metadata));
        Assert.Contains(logs, line =>
            line.Contains("cache validation block=nuget", StringComparison.Ordinal)
            && line.Contains($"key={nuget.Key}", StringComparison.Ordinal)
            && line.Contains("state=invalid", StringComparison.Ordinal)
            && line.Contains("nuget-metadata-missing", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains($"block=nuget key={nuget.Key} state=evicted", StringComparison.Ordinal));
    }

    [Fact]
    public void Nuget_block_validation_requires_extraction_metadata_for_every_package_version()
    {
        var content = Path.Combine(_root, "nuget-validation");
        var first = Path.Combine(content, "example.package", "1.0.0");
        var second = Path.Combine(content, "other.package", "2.0.0");
        Directory.CreateDirectory(Path.Combine(first, "lib"));
        Directory.CreateDirectory(Path.Combine(second, "analyzers"));
        File.WriteAllText(Path.Combine(first, "lib", "example.dll"), "assembly");
        File.WriteAllText(Path.Combine(second, "analyzers", "other.dll"), "assembly");
        File.WriteAllText(Path.Combine(first, ".nupkg.metadata"), "metadata");

        var torn = ProjectPreparationExecutor.ValidateCacheBlock("nuget", content);
        File.WriteAllText(Path.Combine(second, ".nupkg.metadata"), "metadata");
        var complete = ProjectPreparationExecutor.ValidateCacheBlock("nuget", content);

        Assert.False(torn.Valid);
        Assert.Contains("nuget-metadata-missing", torn.Reason);
        Assert.True(complete.Valid, complete.Reason);
    }

    [Fact]
    public async Task Successful_prepare_does_not_publish_an_empty_cache_block()
    {
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", "#!/bin/sh\nset -eu\n# Intentionally no cache output.\n");
        var cache = Path.Combine(_root, "product-cache");
        var logs = new List<string>();

        var result = await ProjectPreparationExecutor.RunAsync(
            _root,
            cache,
            Path.Combine(_root, "empty.json"),
            "subject-1",
            logs.Add,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Output);
        var block = Assert.Single(result.Manifest!.Caches);
        Assert.Equal("unused", block.State);
        Assert.False(Directory.Exists(block.EntryPath));
        Assert.Contains(logs, line =>
            line.Contains($"block=npm key={block.Key}", StringComparison.Ordinal)
            && line.Contains("state=publish-rejected reason=empty-content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preparation_binds_its_restored_packages_for_later_commands_on_a_miss_and_on_a_hit()
    {
        // TE-52: a green preparation is worthless to the gate when the packages it
        // restored are no longer where the run's own project.assets.json points.
        WriteRestoringDotNetRepository();
        var cache = Path.Combine(_root, "product-cache");

        var miss = await PrepareAsync(cache, "miss.json");
        Assert.True(miss.Succeeded, miss.Output);
        var missPackages = miss.Environment["NUGET_PACKAGES"];
        Assert.True(File.Exists(RestoredPackage(missPackages)),
            "After a cache miss `dotnet build --no-restore` must still find the restored package.");
        Assert.StartsWith(miss.RunRoot!, missPackages, StringComparison.Ordinal);

        var hit = await PrepareAsync(cache, "hit.json");
        Assert.True(hit.CacheHit);
        var hitPackages = hit.Environment["NUGET_PACKAGES"];
        Assert.True(File.Exists(RestoredPackage(hitPackages)),
            "After a cache hit `dotnet build --no-restore` must find the same restored package.");
        Assert.NotEqual(missPackages, hitPackages);
        // The published entry is shared and immutable: a hit works on its own copy.
        var entry = hit.Manifest!.Caches.Single(cacheEntry => cacheEntry.Block == "nuget").EntryPath;
        Assert.False(hitPackages.StartsWith(entry, StringComparison.Ordinal));
        Assert.True(File.Exists(RestoredPackage(Path.Combine(entry, "content"))));
    }

    [Fact]
    public async Task Failed_prepare_on_a_cache_hit_leaves_the_published_entry_byte_identical()
    {
        WriteRestoringDotNetRepository();
        var cache = Path.Combine(_root, "product-cache");
        var published = await PrepareAsync(cache, "published.json");
        Assert.True(published.Succeeded, published.Output);
        var entry = published.Manifest!.Caches.Single(entry => entry.Block == "nuget").EntryPath;
        var before = Fingerprint(entry);

        // A hit whose prepare wrecks its own cache folder and then fails. The
        // shared entry may not be touched by it, and deleting the run root must
        // be enough to roll the whole attempt back.
        Write(".agent-studio/prepare", """
            #!/bin/sh
            rm -rf "$NUGET_PACKAGES"/*
            printf poison > "$NUGET_PACKAGES/poisoned"
            exit 9
            """);
        var failed = await PrepareAsync(cache, "failed.json");

        Assert.False(failed.Succeeded);
        Assert.Empty(failed.Environment);
        Assert.Null(failed.RunRoot);
        Assert.Equal(before, Fingerprint(entry));
        Assert.All(failed.Manifest!.Caches, cacheEntry => Assert.Equal("hit", cacheEntry.State));
    }

    [Fact]
    public async Task Concurrent_prepares_on_one_entry_each_keep_their_own_copy()
    {
        WriteRestoringDotNetRepository();
        var cache = Path.Combine(_root, "product-cache");

        var first = PrepareAsync(cache, "concurrent-a.json");
        var second = PrepareAsync(cache, "concurrent-b.json");
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded, result.Output));
        Assert.All(results, result => Assert.True(
            File.Exists(RestoredPackage(result.Environment["NUGET_PACKAGES"])),
            "Losing the publication race must not take the loser's own packages away."));
        Assert.NotEqual(results[0].Environment["NUGET_PACKAGES"], results[1].Environment["NUGET_PACKAGES"]);
        // Exactly one immutable entry, complete, and no abandoned staging folder.
        var entries = Directory.GetDirectories(Path.Combine(cache, "entries", "nuget"));
        Assert.Single(entries);
        Assert.True(File.Exists(Path.Combine(entries[0], "manifest.json")));
        Assert.True(File.Exists(RestoredPackage(Path.Combine(entries[0], "content"))));

        var third = await PrepareAsync(cache, "concurrent-c.json");
        Assert.True(third.CacheHit);
    }

    [Fact]
    public async Task Releasing_the_run_root_drops_the_per_run_copy_and_keeps_the_published_entry()
    {
        WriteRestoringDotNetRepository();
        var cache = Path.Combine(_root, "product-cache");
        var prepared = await PrepareAsync(cache, "released.json");
        var entry = prepared.Manifest!.Caches.Single(cacheEntry => cacheEntry.Block == "nuget").EntryPath;

        ProjectPreparationExecutor.ReleaseRunRoot(prepared);

        Assert.False(Directory.Exists(prepared.RunRoot));
        Assert.True(File.Exists(RestoredPackage(Path.Combine(entry, "content"))));
    }

    [Fact]
    public async Task Coding_run_launch_environment_carries_the_preparation_cache_binding()
    {
        // The coding run's agent runs build, test and lint itself, so the binding
        // travels as the launch environment overlay (backend CAR + legacy spawn,
        // runner worker specification) instead of as a gate process variable.
        WriteRestoringDotNetRepository();
        var prepared = await PrepareAsync(Path.Combine(_root, "product-cache"), "coding-run.json");
        var launch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["JOB_RESULTS_DIR"] = Path.Combine(_root, "results"),
            ["NUGET_PACKAGES"] = Path.Combine(_root, "host-owned-packages"),
        };

        PreparationCacheEnvironment.Apply(launch, prepared.Environment);

        Assert.Equal(prepared.Environment["NUGET_PACKAGES"], launch["NUGET_PACKAGES"]);
        Assert.Equal(Path.Combine(_root, "results"), launch["JOB_RESULTS_DIR"]);
        Assert.True(File.Exists(RestoredPackage(launch["NUGET_PACKAGES"])));
    }

    [Fact]
    public void Generator_proposes_both_repository_files_for_detected_stack()
    {
        Write("Sample.slnx", "<Solution />");
        Write("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}");
        Write("web/package.json", "{\"scripts\":{\"build\":\"ng build\",\"test\":\"ng test\"}}");
        Write("web/package-lock.json", "{\"lockfileVersion\":3}");

        var proposal = ProjectDefinitionGenerator.Generate(_root);

        Assert.Contains("stack: [dotnet, node]", proposal.Definition);
        Assert.Contains("dotnetSdk: global.json", proposal.Definition);
        Assert.Contains("npm --prefix 'web' ci --prefer-offline", proposal.PrepareScript);
        Assert.True(ProjectDefinitionReader.Parse(proposal.Definition).IsValid);
        var failureCard = ProjectDefinitionProposalService.BuildPrompt(proposal, "tool:node:version-mismatch");
        Assert.Contains("tool:node:version-mismatch", failureCard);
        Assert.Contains("## Reasoning", failureCard);
    }

    [Fact]
    public void Execution_override_requires_justification_and_valid_definition()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);

        Assert.Throws<ArgumentException>(() => settings.SetExecutionDefinitionOverride("Pilot", Definition(".agent-studio/prepare"), ""));
        var saved = settings.SetExecutionDefinitionOverride("Pilot", Definition(".agent-studio/prepare"), "Temporary executor exception");

        Assert.Equal("Temporary executor exception", saved.Justification);
        Assert.NotNull(settings.Get("Pilot").ExecutionDefinitionOverride);
        settings.ClearExecutionDefinitionOverride("Pilot");
        Assert.Null(settings.Get("Pilot").ExecutionDefinitionOverride);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Agent_studio_pilot_runs_twice_and_second_run_is_a_cache_hit()
    {
        var repository = FindRepositoryRoot();
        var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR")
                      ?? Path.Combine(_root, "pilot-results");
        // AGT-2858: the pilot cache is product-owned, not a temp directory. In
        // the shared /tmp it had no owner, no bound and no supported reset, grew
        // to 36 GB, and collected two hand-made "-before-*" operator copies.
        var cache = ProjectPreparationPaths.ProjectCacheRoot("agent-studio-m1-pilot");
        var subjectSha = await RunGitAsync(repository, "rev-parse", "HEAD");

        var first = await ProjectPreparationExecutor.RunAsync(
            repository, cache, Path.Combine(results, "agent-studio-preparation-first.json"),
            subjectSha, null, TimeSpan.FromMinutes(20), CancellationToken.None);
        Assert.True(first.Succeeded, first.Output);
        var second = await ProjectPreparationExecutor.RunAsync(
            repository, cache, Path.Combine(results, "agent-studio-preparation-second.json"),
            subjectSha, null, TimeSpan.FromMinutes(20), CancellationToken.None);

        Assert.True(second.Succeeded, second.Output);
        Assert.True(second.CacheHit, "The unchanged Agent Studio pilot must hit every technology cache.");
    }

    [Theory]
    // POSIX hosts never consult a PowerShell entry point.
    [InlineData(false, true, true, "/bin/sh", null)]
    [InlineData(false, false, false, "/bin/sh", null)]
    // Windows prefers a repository-owned prepare.ps1 when the repository ships one.
    [InlineData(true, true, true, "powershell.exe", null)]
    [InlineData(true, true, false, "powershell.exe", null)]
    // AGT-2822: without it the extensionless POSIX script still runs, through Git
    // Bash, because `powershell -File` refuses a file without a PowerShell extension.
    [InlineData(true, false, true, GitBash, null)]
    // Neither entry point: a named reason instead of an opaque exit code.
    [InlineData(true, false, false, "", PreparationEntryPointPolicy.WindowsEntryMissing)]
    public void Prepare_entry_point_follows_host_and_available_entry(
        bool windows,
        bool powerShellEntry,
        bool gitBashInstalled,
        string expectedFileName,
        string? expectedSignature)
    {
        var script = windows ? @"C:\repo\.agent-studio\prepare" : "/repo/.agent-studio/prepare";

        var entry = PreparationEntryPointPolicy.Resolve(
            script,
            windows,
            candidate => powerShellEntry && candidate.EndsWith(".ps1", StringComparison.Ordinal),
            () => gitBashInstalled ? GitBash : null);

        Assert.Equal(expectedFileName, entry.FileName);
        Assert.Equal(expectedSignature, entry.FailureSignature);
        Assert.Equal(expectedSignature is null, entry.Resolved);
        if (!entry.Resolved)
        {
            Assert.Contains("prepare.ps1", entry.FailureReason);
            Assert.Contains("bash.exe", entry.FailureReason);
            return;
        }
        if (entry.FileName == "powershell.exe")
        {
            Assert.Contains("-File", entry.Arguments);
            Assert.Equal(script + ".ps1", entry.Arguments[^1]);
        }
        else if (entry.FileName == GitBash)
        {
            // Git Bash needs a POSIX-style path, and -e keeps a failing step fatal.
            Assert.Equal(["-e", "C:/repo/.agent-studio/prepare"], entry.Arguments);
        }
        else
        {
            Assert.Equal([script], entry.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Safe_host_environment_adds_the_windows_base_variables_only_on_windows(bool windows)
    {
        var keys = PreparationHostEnvironment.KeysFor(windows);

        Assert.Equal(keys.Distinct(StringComparer.Ordinal).Count(), keys.Count);
        Assert.All(PreparationHostEnvironment.PortableKeys, key => Assert.Contains(key, keys));
        // AGT-2822: NuGet.targets fails with "Value cannot be null. (Parameter path1)"
        // when the Windows base variables are missing from the prepare environment.
        foreach (var windowsKey in PreparationHostEnvironment.WindowsKeys)
            Assert.Equal(windows, keys.Contains(windowsKey, StringComparer.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                                           || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                                           || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("stdout only", "", "stdout only")]
    [InlineData("noise", "the real error", "the real error")]
    [InlineData("", "   ", null)]
    public void Failed_prepare_output_tail_prefers_stderr(string stdout, string stderr, string? expected)
        => Assert.Equal(expected, ProjectPreparationExecutor.OutputTail(stdout, stderr));

    [Fact]
    public void Failed_prepare_output_tail_is_bounded_from_the_end()
    {
        var tail = ProjectPreparationExecutor.OutputTail(string.Empty, new string('a', 50) + "LAST", limit: 4);

        Assert.Equal("LAST", tail);
        Assert.EndsWith("LAST", ProjectPreparationExecutor.ReasonWithOutputTail("Prepare failed.", tail));
        Assert.StartsWith("Prepare failed. Output tail: ", ProjectPreparationExecutor.ReasonWithOutputTail("Prepare failed.", tail));
        Assert.Equal("Prepare failed.", ProjectPreparationExecutor.ReasonWithOutputTail("Prepare failed.", null));
    }

    [Fact]
    public async Task Failing_prepare_records_its_stderr_tail_in_the_manifest_and_reason()
    {
        const string marker = "AGT-2822-restore-refused";
        WritePreparedRepository($"""
            echo 'restoring dependencies'
            echo '{marker}: the dependency source rejected the restore' 1>&2
            exit 9
            """);
        var manifestPath = Path.Combine(_root, "manifests", ProjectPreparationPaths.ManifestFileName);

        var result = await ProjectPreparationExecutor.RunAsync(
            _root, Path.Combine(_root, "cache"), manifestPath, "0f0f0f0", null,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.False(result.Succeeded, result.Output);
        Assert.Equal(PreparationFailureKind.Command, result.FailureKind);
        Assert.Equal(9, result.ExitCode);
        // The manifest keeps the readable tail, the reason repeats a bounded
        // excerpt so the card's integration detail names the real error instead
        // of only "Prepare command failed with exit code 9".
        Assert.Contains(marker, result.Manifest!.FailureOutputTail);
        Assert.Contains(marker, File.ReadAllText(manifestPath));
        Assert.Contains("failureOutputTail", File.ReadAllText(manifestPath));
        Assert.Contains("Output tail:", result.FailureReason);
        Assert.Contains(marker, result.FailureReason);
    }

    [Fact]
    public async Task Green_prepare_keeps_no_output_tail_in_the_manifest()
    {
        WritePreparedRepository("exit 0");
        var manifestPath = Path.Combine(_root, "manifests", ProjectPreparationPaths.ManifestFileName);

        var result = await ProjectPreparationExecutor.RunAsync(
            _root, Path.Combine(_root, "cache"), manifestPath, "0f0f0f0", null,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.True(result.Succeeded, result.Output);
        Assert.Null(result.Manifest!.FailureOutputTail);
        Assert.Null(result.Manifest.FailureReason);
    }

    [Theory]
    // AGT-2833: named Windows signatures instead of an opaque exit code.
    [InlineData("Loading managed Windows PowerShell failed.", PreparationFailureKind.Environment, "prepare:powershell-environment")]
    [InlineData(
        "C:\\repo\\obj\\Sample.csproj.nuget.g.targets(1,1): error MSB4018: "
            + "System.ArgumentNullException: Value cannot be null. (Parameter 'path1')",
        PreparationFailureKind.Environment,
        "prepare:windows-environment")]
    // Already matched before AGT-2833 via the broader "not recognized as an
    // internal" phrase; pinned here so the exact card wording stays covered.
    [InlineData(
        "'dotnet' is not recognized as an internal or external command, operable program or batch file.",
        PreparationFailureKind.ToolMissing,
        "tool:missing")]
    public void Classify_names_known_windows_prepare_signatures(
        string evidence, PreparationFailureKind expectedKind, string expectedSignature)
    {
        var (kind, signature, reason) = ProjectPreparationExecutor.Classify(evidence, exitCode: 1);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedSignature, signature);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task Failing_prepare_with_the_nuget_path1_signature_is_classified_as_windows_environment()
    {
        WritePreparedRepository($"""
            echo 'restoring dependencies'
            echo "NuGet.targets(203,5): error MSB4018: Value cannot be null. (Parameter 'path1')" 1>&2
            exit 1
            """);
        var manifestPath = Path.Combine(_root, "manifests", ProjectPreparationPaths.ManifestFileName);

        var result = await ProjectPreparationExecutor.RunAsync(
            _root, Path.Combine(_root, "cache"), manifestPath, "0f0f0f0", null,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.False(result.Succeeded, result.Output);
        Assert.Equal(PreparationFailureKind.Environment, result.FailureKind);
        Assert.Equal("prepare:windows-environment", result.FailureSignature);
        // The named reason, not just the exit code, reaches the card's
        // integration detail alongside the bounded stderr tail.
        Assert.Contains("Windows base variable NuGet needs", result.FailureReason);
        Assert.Contains("Parameter 'path1'", result.FailureReason);
    }

    // AGT-2822: proves on a real Windows host that a repository without
    // prepare.ps1 runs its POSIX prepare through Git Bash and that the copied
    // Windows base variables let the .NET SDK resolve its user paths.
    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task Windows_runs_a_posix_prepare_without_a_powershell_entry_point()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows preparation entry point.");
        WritePreparedRepository("dotnet --info");
        Assert.False(File.Exists(Path.Combine(_root, ".agent-studio", "prepare.ps1")));
        var manifestPath = Path.Combine(_root, "manifests", ProjectPreparationPaths.ManifestFileName);

        var result = await ProjectPreparationExecutor.RunAsync(
            _root, Path.Combine(_root, "cache"), manifestPath, "0f0f0f0", null,
            TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason + " | " + result.Output);
        Assert.Contains(".NET SDK", result.Output);
    }

    /// <summary>
    /// A fake pilot repository: the v1 definition plus a POSIX prepare script
    /// carrying <paramref name="body"/>. No prepare.ps1 is written, so Windows
    /// exercises the Git Bash entry point.
    /// </summary>
    private void WritePreparedRepository(string body)
    {
        Write(".agent-studio/project.yml", Definition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", "#!/bin/sh\nset -e\n" + body + "\n");
    }

    private const string GitBash = @"C:\Program Files\Git\bin\bash.exe";

    private static string Definition(string prepare) => $$"""
        schemaVersion: 1
        stack: [node]
        toolVersions:
        commands:
          prepare: {{prepare}}
          build:
          test:
          lint:
        testSuites:
        cachePaths: [node_modules]
        capabilities: [linux]
        environment:
          CI: "true"
        """;

    /// <summary>
    /// A .NET repository whose prepare performs the one thing the binding has to
    /// survive: it restores a package into the executor-owned NuGet folder.
    /// </summary>
    private void WriteRestoringDotNetRepository()
    {
        Write("packages.lock.json", "{\"version\":1,\"dependencies\":{}}");
        Write(".agent-studio/project.yml", DotNetDefinition(".agent-studio/prepare"));
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            mkdir -p "$NUGET_PACKAGES/xunit.analyzers/1.4.0"
            printf nupkg > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/xunit.analyzers.nupkg"
            printf metadata > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/.nupkg.metadata"
            """);
    }

    private static string RestoredPackage(string packagesFolder)
        => Path.Combine(packagesFolder, "xunit.analyzers", "1.4.0", "xunit.analyzers.nupkg");

    private Task<ProjectPreparationResult> PrepareAsync(string cacheRoot, string manifestName)
        => ProjectPreparationExecutor.RunAsync(
            _root,
            cacheRoot,
            Path.Combine(_root, "manifests", manifestName),
            "subject-1",
            null,
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

    /// <summary>Content fingerprint of a published cache entry, path by path.</summary>
    private static string Fingerprint(string root)
        => string.Join("\n", Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')
                            + ":"
                            + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                File.ReadAllBytes(path)))));

    private static string DotNetDefinition(string prepare) => $$"""
        schemaVersion: 1
        stack: [dotnet]
        toolVersions:
        commands:
          prepare: {{prepare}}
          build:
          test:
          lint:
        testSuites:
        cachePaths: [bin]
        capabilities: [linux]
        environment:
          CI: "true"
        """;

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agent-taskboard.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Agent Studio repository root was not found.");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Git did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await stderr);
        return output;
    }
}
