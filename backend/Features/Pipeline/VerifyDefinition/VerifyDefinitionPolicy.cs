using System.Text;
using AgentStudio.Diagnostics;
using AgentStudio.Prompts;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Where the verify commands of a repository come from (AGT-3428).</summary>
public enum VerifyDefinitionState
{
    /// <summary>project.yml, a build profile, or the repository layout yields at least one command.</summary>
    Derived,

    /// <summary>A valid project.yml deliberately declares empty build, test and lint lists.</summary>
    DeclaredEmpty,

    /// <summary>No definition exists and nothing is derivable: no check could verify a delivery.</summary>
    Missing,
}

/// <summary>Minimal stack-specific starting point for a missing definition.</summary>
/// <param name="Stack">Detected stack slug, <c>custom</c> when nothing was recognised.</param>
/// <param name="Definition">Minimal <c>.agent-studio/project.yml</c> text that validates.</param>
/// <param name="PrepareScript">Minimal <c>.agent-studio/prepare</c> text that the definition references.</param>
public sealed record VerifyDefinitionExample(
    string Stack,
    IReadOnlyList<string> Build,
    IReadOnlyList<string> Test,
    string Definition,
    string PrepareScript);

/// <param name="Message">Operator copy naming the missing file with a minimal example; null unless <see cref="VerifyDefinitionState.Missing"/>.</param>
public sealed record VerifyDefinitionReport(
    VerifyDefinitionState State,
    string PlanSource,
    string? Message = null,
    VerifyDefinitionExample? Example = null)
{
    public bool IsMissing => State == VerifyDefinitionState.Missing;
}

/// <summary>
/// The one rule for a repository whose verify plan is empty (AGT-3428): a
/// delivery merges only when a check verified it or the repository declared on
/// purpose that it has none. A valid <c>.agent-studio/project.yml</c> with empty
/// command lists is that declaration and its <see cref="BuildTestGateVerdict.NotApplicable"/>
/// gate admits the merge. Without any definition the empty plan is a setup
/// defect, not a gate verdict: the merge is refused with the typed
/// <see cref="AcceptedIntegrationFailureCodes.VerifyDefinitionMissing"/> code and
/// an actionable message, and the gate failure router never classifies it.
/// The same message is shown at project setup, at the start of the coding run,
/// and in the Remote Review plan, so nobody learns it from a red merge.
/// </summary>
public static class VerifyDefinitionPolicy
{
    /// <summary>Gate reason of an empty plan without any definition (existing wire string).</summary>
    public const string NoVerifyCommandsReason = "no verify commands derivable";

    /// <summary>Gate reason of an empty plan that a valid project.yml declared on purpose.</summary>
    public const string DeclaredEmptyReason = "the project definition declares no verify commands";

    /// <summary>Stable prefix of every missing-definition message; legacy records are recognised by it.</summary>
    public const string MessagePrefix = "Verify definition missing:";

    /// <summary>One-line form for tables and log lines; <see cref="Message"/> carries the example.</summary>
    public static readonly string Summary =
        $"{MessagePrefix} `{ProjectPreparationPaths.Definition}` is missing and no verify commands are derivable; "
        + "the delivery is not merged until the repository defines its checks.";

    /// <summary>Enrichment block id that carries the notice into a coding run.</summary>
    public const string EnrichmentBlockId = "verify-definition-missing";

    public static VerifyDefinitionState Classify(VerifyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsEmpty) return VerifyDefinitionState.Derived;
        return string.Equals(plan.Source, VerifyPlan.SourceProjectDefinition, StringComparison.Ordinal)
            ? VerifyDefinitionState.DeclaredEmpty
            : VerifyDefinitionState.Missing;
    }

    /// <summary>The gate reason recorded for an empty plan.</summary>
    public static string NotApplicableReason(VerifyPlan plan)
        => Classify(plan) == VerifyDefinitionState.DeclaredEmpty ? DeclaredEmptyReason : NoVerifyCommandsReason;

    /// <summary>
    /// True when a gate result may let the delivery merge: a green gate, or a
    /// not-applicable gate whose repository declared that it has no checks.
    /// </summary>
    public static bool AdmitsMerge(BuildTestGateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return gate.Verdict == BuildTestGateVerdict.Ok
               || (gate.Verdict == BuildTestGateVerdict.NotApplicable && !IsMissingDefinition(gate));
    }

    /// <summary>
    /// A not-applicable gate that no declaration explains. Fail closed: only the
    /// declared-empty reason admits a merge without a check.
    /// </summary>
    public static bool IsMissingDefinition(BuildTestGateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return gate.Verdict == BuildTestGateVerdict.NotApplicable
               && !string.Equals(gate.Reason?.Trim(), DeclaredEmptyReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when recorded failure text came from a missing definition, also before AGT-3428.</summary>
    public static bool DescribesMissingDefinition(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && (text.Contains(MessagePrefix, StringComparison.OrdinalIgnoreCase)
               || text.Contains(NoVerifyCommandsReason, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the repository and reports whether its verify commands are defined.</summary>
    public static VerifyDefinitionReport Inspect(string? repositoryPath, BuildProfile? profile)
    {
        var root = repositoryPath ?? string.Empty;
        var plan = VerifyCommandPlanner.Plan(root, profile);
        var state = Classify(plan);
        if (state != VerifyDefinitionState.Missing) return new VerifyDefinitionReport(state, plan.Source);
        var definition = string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            ? null
            : ProjectDefinitionReader.ReadWorkspace(root);
        var invalid = definition?.Issues.FirstOrDefault(issue => issue.Code != "definition-missing");
        var example = ExampleFor(root);
        return new VerifyDefinitionReport(state, plan.Source, Message(example, invalid), example);
    }

    /// <summary>Operator copy: the missing file, why it matters, and a minimal example.</summary>
    public static string Message(VerifyDefinitionExample example, ProjectDefinitionIssue? invalid = null)
    {
        ArgumentNullException.ThrowIfNull(example);
        var state = invalid is null
            ? "is missing"
            : $"is invalid ({invalid.Path}: {invalid.Message.TrimEnd('.')})";
        var text = new StringBuilder();
        text.AppendLf(
            $"{MessagePrefix} `{ProjectPreparationPaths.Definition}` {state}, and no build or test command "
            + "can be derived from the repository root, so no check can verify a delivery. "
            + "Agent Studio does not merge an unverified delivery. "
            + $"Add `{ProjectPreparationPaths.Definition}` and its prepare script `{ProjectPreparationPaths.Script}`. "
            + $"Minimal example for this {example.Stack} repository:");
        text.AppendLf();
        text.AppendLf("```yaml");
        text.Append(example.Definition);
        text.AppendLf("```");
        text.AppendLf();
        text.AppendLf($"`{ProjectPreparationPaths.Script}` (executable):");
        text.AppendLf();
        text.AppendLf("```sh");
        text.Append(example.PrepareScript);
        text.AppendLf("```");
        text.AppendLf();
        text.Append("A repository that has no checks on purpose declares that with a valid project.yml "
                    + "whose build, test and lint lists are empty.");
        return text.ToString();
    }

    /// <summary>
    /// Bounded stack detection (root and one directory level) for the example.
    /// It only shapes the suggestion; it never becomes a gate command.
    /// </summary>
    public static VerifyDefinitionExample ExampleFor(string? repositoryPath)
    {
        var files = CandidateFiles(repositoryPath);
        string? First(params string[] names) => files.FirstOrDefault(file =>
            names.Any(name => name.StartsWith('.')
                ? file.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)));

        if (First(".sln", ".slnx", ".csproj") is { } dotnet)
            return Example("dotnet", [$"dotnet build {dotnet}"], [$"dotnet test {dotnet} --no-build"],
                $"dotnet restore {dotnet}");
        if (First("package.json") is { } package)
        {
            var directory = Path.GetDirectoryName(package)?.Replace('\\', '/') ?? string.Empty;
            var prefix = directory.Length == 0 ? string.Empty : $" --prefix {directory}";
            return Example("node", [], [$"npm{prefix} test"], $"npm{prefix} ci");
        }
        if (First("pyproject.toml", "requirements.txt", "setup.py") is { } python)
            return Example("python", [], ["python -m pytest"],
                Path.GetFileName(python) == "requirements.txt"
                    ? $"python -m pip install -r {python}"
                    : "python -m pip install -e .");
        if (First("go.mod") is not null)
            return Example("go", ["go build ./..."], ["go test ./..."], "go mod download");
        if (First("Cargo.toml") is not null)
            return Example("rust", ["cargo build"], ["cargo test"], "cargo fetch");
        if (First("pom.xml") is not null)
            return Example("java", [], ["mvn -B verify"], "mvn -B dependency:go-offline");
        if (First("Makefile") is not null)
            return Example("make", [], ["make test"], null);
        return Example("custom", [], ["./scripts/test.sh"], null);
    }

    private static VerifyDefinitionExample Example(
        string stack, IReadOnlyList<string> build, IReadOnlyList<string> test, string? prepare)
    {
        var yaml = new StringBuilder();
        yaml.AppendLf("schemaVersion: 1");
        yaml.AppendLf($"stack: [{stack}]");
        yaml.AppendLf("commands:");
        yaml.AppendLf($"  prepare: {ProjectPreparationPaths.Script}");
        if (build.Count > 0)
        {
            yaml.AppendLf("  build:");
            foreach (var command in build) yaml.AppendLf($"    - {command}");
        }
        yaml.AppendLf("  test:");
        foreach (var command in test) yaml.AppendLf($"    - {command}");
        var script = new StringBuilder();
        script.AppendLf("#!/bin/sh");
        script.AppendLf("set -eu");
        if (prepare is not null) script.AppendLf(prepare);
        return new VerifyDefinitionExample(stack, build, test, yaml.ToString(), script.ToString());
    }

    private static IReadOnlyList<string> CandidateFiles(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath)) return [];
        var found = new List<string>();
        try
        {
            found.AddRange(Directory.EnumerateFiles(repositoryPath).Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.Ordinal));
            foreach (var directory in Directory.EnumerateDirectories(repositoryPath).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "dist") continue;
                found.AddRange(Directory.EnumerateFiles(directory)
                    .Select(file => $"{name}/{Path.GetFileName(file)}")
                    .Order(StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "VerifyDefinitionPolicy: stack detection only shapes the example");
        }
        // Root files first, so a root manifest wins over a nested one.
        return found.OrderBy(file => file.Contains('/')).ToArray();
    }
}
