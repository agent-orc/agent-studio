using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Repository-declared scope for compile checks. Reviews and release gates keep their plans.</summary>
internal static class IntegrationCompilePlan
{
    internal static StagedVerifyPlan? TryPlan(string repository, BuildTestGateRequest request,
        VerifyPlan plan, IReadOnlyList<string>? paths)
    {
        if (!request.IntegrationPriority || request.RequiredTestLevel != TestExecutionLevels.CompileOnly
            || plan.Source != VerifyPlan.SourceProjectDefinition || paths is not { Count: > 0 }) return null;
        var read = ProjectDefinitionReader.ReadWorkspace(repository);
        if (!read.IsValid || read.Definition is not { } definition) return null;
        var rows = definition.IntegrationBuild.Count > 0
            ? definition.IntegrationBuild : RepositoryDefaults(repository, definition);
        if (rows.Count == 0) return null;
        if (rows.Any(row => row.Paths.Count == 0 || row.Paths.Any(path => string.IsNullOrWhiteSpace(path)
                || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Contains(".."))
            || row.Build.Count == 0 || row.Build.Any(index => index < 0 || index >= definition.Commands.Build.Count)
            || row.Prepare.Count == 0)) return null;
        var selected = new List<ProjectTestSelectionArea>();
        foreach (var path in paths)
        {
            // Every path needs a repository-owned scope; unknown/shared inputs keep the full build.
            var matches = rows.Where(row => row.Paths.Any(prefix => ProjectTestSelectionPolicy.Matches(path, prefix))).ToArray();
            if (matches.Length == 0) return null;
            selected.AddRange(matches);
        }
        var builds = selected.SelectMany(row => row.Build).Distinct()
            .Select(index => definition.Commands.Build[index]).ToHashSet(StringComparer.Ordinal);
        return new(plan.Commands.Where(command => command.Kind == VerifyCommandKind.Build
            && builds.Contains(command.Command)).ToArray(), new TestSelectionAudit
        {
            Level = TestExecutionLevels.CompileOnly, Selector = "integration-build", DiffInput = paths,
            Reasons = ["Repository integration build scope selects build and preparation commands for the exact merge diff."],
        }) { PreparationCommands = selected.SelectMany(row => row.Prepare).Distinct(StringComparer.Ordinal).ToArray() };
    }

    // Until every run host understands integrationBuild, this repository ships without the field.
    // Exact commands and owned entry points fence the default to the supported repository layout.
    private static IReadOnlyList<ProjectTestSelectionArea> RepositoryDefaults(string repository,
        ProjectExecutionDefinition definition)
    {
        string[] commands = ["dotnet build agent-taskboard.sln --no-restore",
            "bash scripts/check-release-build.sh", "bash scripts/npm-frontend.sh run build"];
        string[] files = ["agent-taskboard.sln", "backend/OrchestratorApi.csproj", "frontend/package.json",
            ".nvmrc", "scripts/check-release-build.sh", "scripts/npm-frontend.sh", ".agent-studio/prepare-integration.sh"];
        if (!definition.Commands.Build.SequenceEqual(commands, StringComparer.Ordinal)
            || files.Any(file => !File.Exists(Path.Combine(repository, file)))) return [];
        return
        [
            new(["frontend/", ".nvmrc", "scripts/npm-frontend.sh"], [], [2], [])
            { Prepare = ["bash .agent-studio/prepare-integration.sh frontend"] },
            new(["global.json", "NuGet.config", "Directory.Build.props", "Directory.Build.targets",
                "Directory.Packages.props", "agent-taskboard.sln", "backend/", "backend.Tests/", "runner/",
                "runner.Tests/", "task-server/", "task-server.Tests/", "contracts/", "orchestration-decisions/",
                "orchestrator-engine/", "orchestrator-engine.Tests/", "studio-bff/", "update-service/",
                "cli-hosting/", "testsupport/", "retention/", "retention.Tests/", "setup/", "setup.Tests/",
                "demo-replay-runner/", "demo-replay-runner.Tests/", "operations-server/", "operations-server.Tests/",
                "operations-agent/", "tools/SilentCatchAnalyzer/"], [], [0, 1], [])
            { Prepare = ["bash .agent-studio/prepare-integration.sh backend"] },
        ];
    }
}
