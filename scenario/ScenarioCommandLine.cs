namespace AgentStudio.Scenario;

public sealed record ScenarioOptions(
    ScenarioTargetKind Target,
    ScenarioLevel Level,
    string DocumentPath,
    string OutputDirectory,
    string? RemoteTaskServerUrl,
    string? RemoteStudioUrl,
    string? RemoteToken,
    string RepositoryRoot,
    bool KeepEvidence);

public sealed record ScenarioCommandLineResult(
    ScenarioOptions? Options,
    string? Error,
    bool HelpRequested)
{
    public static ScenarioCommandLineResult Help { get; } = new(null, null, true);
    public static ScenarioCommandLineResult Failed(string error) => new(null, error, false);
    public static ScenarioCommandLineResult Parsed(ScenarioOptions options) => new(options, null, false);
}

/// <summary>
/// Boundary validation for the process arguments. Parsing is pure so the
/// contract of every flag, default, and refusal is tested without starting a
/// topology.
/// </summary>
public static class ScenarioCommandLine
{
    public const string DefaultDocumentPath = "testsupport/scenario/deployment-regression.json";

    public const string Usage = """
        agent-studio-scenario - deployment regression scenario runner

        Usage:
          agent-studio-scenario --target inproc|compose|remote [options]

        Options:
          --target <kind>       Required. inproc, compose, or remote.
          --level <level>       smoke (default) or full.
          --scenario <path>     Scenario document. Default:
                                testsupport/scenario/deployment-regression.json
          --out <dir>           Report and evidence directory. Default:
                                JOB_RESULTS_DIR, else artifacts/scenario.
          --repo-root <dir>     Repository root. Default: the current directory.
          --server-url <url>    Task Server base URL. Required for --target remote.
          --studio-url <url>    Studio BFF base URL. Optional for --target remote.
          --token <credential>  Management credential. Required for --target remote.
          --keep-evidence       Keep the temporary run directories after the run.
          -h, --help            Print this help.

        Exit codes:
          0 passed   1 step failed   2 usage error
          3 invalid scenario document   4 target unavailable
        """;

    public static ScenarioCommandLineResult Parse(
        IReadOnlyList<string> args,
        Func<string, string?> environment)
    {
        string? target = null;
        var level = "smoke";
        string? documentPath = null;
        string? outputDirectory = null;
        string? repositoryRoot = null;
        string? serverUrl = null;
        string? studioUrl = null;
        string? token = null;
        var keepEvidence = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "-h" or "--help":
                    return ScenarioCommandLineResult.Help;
                case "--keep-evidence":
                    keepEvidence = true;
                    continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
                return ScenarioCommandLineResult.Failed($"Unexpected argument '{argument}'.");
            if (index + 1 >= args.Count)
                return ScenarioCommandLineResult.Failed($"Option '{argument}' needs a value.");
            var value = args[++index];

            switch (argument)
            {
                case "--target": target = value; break;
                case "--level": level = value; break;
                case "--scenario": documentPath = value; break;
                case "--out": outputDirectory = value; break;
                case "--repo-root": repositoryRoot = value; break;
                case "--server-url": serverUrl = value; break;
                case "--studio-url": studioUrl = value; break;
                case "--token": token = value; break;
                default:
                    return ScenarioCommandLineResult.Failed($"Unknown option '{argument}'.");
            }
        }

        if (target is null)
            return ScenarioCommandLineResult.Failed("Option '--target' is required.");
        if (!ScenarioDocumentLoader.TryParseTarget(target, out var targetKind))
            return ScenarioCommandLineResult.Failed(
                $"Unknown target '{target}'. Use inproc, compose, or remote.");
        if (!ScenarioDocumentLoader.TryParseLevel(level, out var levelKind))
            return ScenarioCommandLineResult.Failed(
                $"Unknown level '{level}'. Use smoke or full.");

        if (targetKind == ScenarioTargetKind.Remote)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return ScenarioCommandLineResult.Failed("Target 'remote' requires '--server-url'.");
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("http" or "https"))
            {
                return ScenarioCommandLineResult.Failed(
                    "Option '--server-url' must be an absolute http or https URL.");
            }
            if (string.IsNullOrWhiteSpace(token))
                return ScenarioCommandLineResult.Failed("Target 'remote' requires '--token'.");
        }
        else if (serverUrl is not null || token is not null || studioUrl is not null)
        {
            return ScenarioCommandLineResult.Failed(
                "Options '--server-url', '--studio-url', and '--token' apply only to '--target remote'.");
        }

        var root = repositoryRoot ?? environment("SCENARIO_REPO_ROOT") ?? ".";
        return ScenarioCommandLineResult.Parsed(new ScenarioOptions(
            targetKind,
            levelKind,
            documentPath ?? DefaultDocumentPath,
            outputDirectory ?? environment("JOB_RESULTS_DIR") ?? "artifacts/scenario",
            serverUrl?.TrimEnd('/'),
            studioUrl?.TrimEnd('/'),
            token,
            root,
            keepEvidence));
    }
}
