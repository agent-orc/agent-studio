using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Prompts;

namespace AgentStudio.ExecutionPreparation;

public sealed record ProjectDefinitionProposal(
    string Definition,
    string PrepareScript,
    IReadOnlyList<string> DetectedStacks,
    string Reasoning);

/// <summary>
/// Turns bounded repository stack detection into an ordinary reviewable card.
/// The card owns both repository files, so onboarding never writes directly to
/// a user's checkout and the accepted definition travels with its commit.
/// </summary>
public sealed class ProjectDefinitionProposalService
{
    public const string ProposalTitle = "Add repository execution definition";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<ProjectDefinitionProposalService> _logger;

    public ProjectDefinitionProposalService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        ILogger<ProjectDefinitionProposalService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _logger = logger;
    }

    public ProjectDefinitionProposal Generate(string repositoryPath)
        => ProjectDefinitionGenerator.Generate(repositoryPath);

    public string? CreateCard(string projectName, string? failureReason = null, bool force = false)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        var repositoryPath = string.IsNullOrWhiteSpace(entry.RepositoryPath)
            ? entry.RootPath
            : entry.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return null;
        if (!force && string.IsNullOrWhiteSpace(failureReason)
            && ProjectDefinitionReader.ReadWorkspace(repositoryPath).IsValid)
            return null;

        var suffix = string.IsNullOrWhiteSpace(failureReason) ? string.Empty : " after preparation failure";
        var title = ProposalTitle + suffix;
        var open = _scanner.ScanAllJobs().FirstOrDefault(task =>
            string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(task.Title, title, StringComparison.Ordinal)
            && task.State is not (TaskStates.Completed or TaskStates.Archive));
        if (open is not null) return open.TaskKey ?? open.Id;

        var proposal = Generate(repositoryPath);
        var prompt = BuildPrompt(proposal, failureReason);
        var id = _mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Project = projectName,
            WatchPath = entry.Path,
            Title = title,
            PromptMarkdown = prompt,
            TargetState = TaskStates.Preparation,
            Mode = TaskModes.Coding,
            TaskType = TaskTypes.Feature,
            CliType = CliTypes.Codex,
            CreationSource = TimelineActors.Orchestrator,
            CreatedBy = "Agent Studio Orchestrator",
        });
        _logger.LogInformation(
            "project-definition-proposal-created project={Project} task={Task} trigger={Trigger} stacks={Stacks}",
            projectName,
            id ?? "not-created",
            string.IsNullOrWhiteSpace(failureReason) ? "onboarding" : "preparation-failure",
            string.Join(',', proposal.DetectedStacks));
        return id;
    }

    internal static string BuildPrompt(ProjectDefinitionProposal proposal, string? failureReason)
    {
        var text = new StringBuilder();
        text.AppendLf("# Repository execution definition proposal");
        text.AppendLf();
        text.AppendLf("Add the generated repository-owned preparation contract. Review it against the actual project before applying it. The product owns the technology caches; this card owns only project composition.");
        text.AppendLf();
        text.AppendLf("## Reasoning");
        text.AppendLf();
        text.AppendLf(proposal.Reasoning);
        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            text.AppendLf();
            text.AppendLf("The proposal was triggered by this preparation evidence:");
            text.AppendLf();
            text.AppendLf("> " + failureReason.Trim().Replace("\n", "\n> ", StringComparison.Ordinal));
        }
        text.AppendLf();
        text.AppendLf("## Required files");
        text.AppendLf();
        text.AppendLf($"Write `{ProjectPreparationPaths.Definition}` with:");
        text.AppendLf();
        text.AppendLf("```yaml");
        text.Append(proposal.Definition);
        text.AppendLf("```");
        text.AppendLf();
        text.AppendLf($"Write executable `{ProjectPreparationPaths.Script}` with:");
        text.AppendLf();
        text.AppendLf("```sh");
        text.Append(proposal.PrepareScript);
        text.AppendLf("```");
        text.AppendLf();
        text.AppendLf("## Acceptance criteria");
        text.AppendLf();
        text.AppendLf("- The definition validates against `docs/app/schemas/project-execution.schema.json`.");
        text.AppendLf("- The prepare script uses `npm ci`, `dotnet restore`, and Playwright only when the detected stack needs them.");
        text.AppendLf("- A preparation run writes `preparation-manifest.json`; a second unchanged run reports cache hits.");
        return text.ToString();
    }
}

public static class ProjectDefinitionGenerator
{
    public static ProjectDefinitionProposal Generate(string repositoryPath)
    {
        var root = Path.GetFullPath(repositoryPath);
        var hasDotNet = Directory.EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly).Any()
                        || Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Any();
        var packageRoots = PackageRoots(root);
        var hasNode = packageRoots.Count > 0;
        var hasPlaywright = Files(root, "playwright.config.*").Any();
        var stacks = new List<string>();
        if (hasDotNet) stacks.Add("dotnet");
        if (hasNode) stacks.Add("node");
        if (hasPlaywright) stacks.Add("playwright");
        if (stacks.Count == 0) stacks.Add("custom");

        var build = new List<string>();
        var test = new List<string>();
        var lint = new List<string>();
        var suites = new List<(string Id, string Category, int Duration, string Command)>();
        if (hasDotNet)
        {
            build.Add("dotnet build --no-restore");
            test.Add("dotnet test --no-build --filter Category!=MachineBound");
            suites.Add(("backend", "unit", 600, test[^1]));
        }
        foreach (var packageRoot in packageRoots)
        {
            var scripts = NpmScripts(Path.Combine(root, packageRoot, "package.json"));
            var prefix = string.IsNullOrWhiteSpace(packageRoot) ? string.Empty : $" --prefix {Shell(packageRoot)}";
            if (scripts.Contains("build")) build.Add($"npm{prefix} run build");
            if (scripts.Contains("test"))
            {
                var command = $"npm{prefix} test";
                test.Add(command);
                suites.Add(($"{Id(packageRoot)}-unit", "unit", 300, command));
            }
            if (scripts.Contains("lint")) lint.Add($"npm{prefix} run lint");
        }
        if (hasPlaywright)
        {
            var packageRoot = packageRoots.FirstOrDefault(rootPath =>
                File.Exists(Path.Combine(root, rootPath, "playwright.config.ts"))) ?? packageRoots.FirstOrDefault() ?? string.Empty;
            var prefix = string.IsNullOrWhiteSpace(packageRoot) ? string.Empty : $" --prefix {Shell(packageRoot)}";
            suites.Add(("playwright", "e2e", 900, $"npm{prefix} exec -- playwright test"));
        }

        var yaml = new StringBuilder();
        yaml.AppendLf("schemaVersion: 1");
        yaml.AppendLf($"stack: [{string.Join(", ", stacks)}]");
        yaml.AppendLf("toolVersions:");
        if (File.Exists(Path.Combine(root, ".nvmrc"))) yaml.AppendLf("  node: .nvmrc");
        if (File.Exists(Path.Combine(root, "global.json"))) yaml.AppendLf("  dotnetSdk: global.json");
        yaml.AppendLf("commands:");
        yaml.AppendLf($"  prepare: {ProjectPreparationPaths.Script}");
        AppendList(yaml, "build", build, 2);
        AppendList(yaml, "test", test, 2);
        AppendList(yaml, "lint", lint, 2);
        yaml.AppendLf("testSuites:");
        foreach (var suite in suites)
        {
            yaml.AppendLf($"  - id: {suite.Id}");
            yaml.AppendLf($"    category: {suite.Category}");
            yaml.AppendLf($"    expectedDurationSeconds: {suite.Duration}");
            yaml.AppendLf($"    command: {Quote(suite.Command)}");
        }
        yaml.AppendLf("cachePaths:");
        foreach (var packageRoot in packageRoots)
            yaml.AppendLf($"  - {(string.IsNullOrWhiteSpace(packageRoot) ? "node_modules" : packageRoot + "/node_modules")}");
        yaml.AppendLf("capabilities: [linux]");
        yaml.AppendLf("environment:");
        yaml.AppendLf("  CI: \"true\"");

        var script = new StringBuilder();
        script.AppendLf("#!/bin/sh");
        script.AppendLf("set -eu");
        if (hasDotNet) script.AppendLf("dotnet restore");
        foreach (var packageRoot in packageRoots)
        {
            var prefix = string.IsNullOrWhiteSpace(packageRoot) ? string.Empty : $" --prefix {Shell(packageRoot)}";
            script.AppendLf($"npm{prefix} ci --prefer-offline");
        }
        if (hasPlaywright)
        {
            var packageRoot = packageRoots.FirstOrDefault() ?? string.Empty;
            var prefix = string.IsNullOrWhiteSpace(packageRoot) ? string.Empty : $" --prefix {Shell(packageRoot)}";
            script.AppendLf($"npm{prefix} exec -- playwright install chromium");
        }

        var detected = string.Join(", ", stacks);
        return new(yaml.ToString(), script.ToString(), stacks,
            $"Stack detection found {detected}. Tool versions are referenced only when the repository already carries their manifests. Cache locations remain executor-owned and are keyed from lockfiles, tool manifests, platform, and architecture.");
    }

    private static IReadOnlyList<string> PackageRoots(string root)
    {
        var values = new List<string>();
        if (File.Exists(Path.Combine(root, "package.json"))) values.Add(string.Empty);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Ignored(directory) || !File.Exists(Path.Combine(directory, "package.json"))) continue;
            values.Add(Path.GetRelativePath(root, directory).Replace('\\', '/'));
        }
        return values;
    }

    private static IReadOnlySet<string> NpmScripts(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return scripts.EnumerateObject().Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool Ignored(string path)
        => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is
            ".git" or "node_modules" or "bin" or "obj" or "dist" or "test-results");

    private static IEnumerable<string> Files(string root, string pattern)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var directory))
        {
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray();
                directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var child in directories.Where(child => !Ignored(child))) pending.Enqueue(child);
        }
    }

    private static void AppendList(StringBuilder yaml, string key, IReadOnlyList<string> values, int indent)
    {
        var spaces = new string(' ', indent);
        yaml.AppendLf($"{spaces}{key}:");
        foreach (var value in values) yaml.AppendLf($"{spaces}  - {Quote(value)}");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Id(string value) => string.IsNullOrWhiteSpace(value)
        ? "node"
        : new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray()).Trim('-');
}
