using System.Diagnostics;

namespace AgentStudio.Scenario;

/// <summary>
/// The seeded fixture every target runs against: one tiny Git repository with a
/// known passing and a known failing test, one deterministic coding CLI, and
/// one deterministic review CLI.
///
/// Determinism rules that keep the flake budget at zero:
/// commit identity, author, and timestamps are fixed; the fake CLIs never
/// branch on wall-clock time, host name, or network state; every produced file
/// has fixed content apart from the attempt number the harness controls.
/// </summary>
public sealed class ScenarioFixture : IDisposable
{
    /// <summary>Fixed commit clock. A rerun produces the same seed commit.</summary>
    public const string FixedGitDate = "2026-01-01T00:00:00+00:00";

    private const string AuthorName = "Scenario Fixture";
    private const string AuthorEmail = "scenario@example.invalid";

    private ScenarioFixture(string root, string bareRepository, string codingCli, string reviewCli)
    {
        Root = root;
        BareRepository = bareRepository;
        CodingCli = codingCli;
        ReviewCli = reviewCli;
    }

    public string Root { get; }
    public string BareRepository { get; }
    public string CodingCli { get; }
    public string ReviewCli { get; }
    public string InvocationCounter => Path.Combine(Root, "invocations");

    public static async Task<ScenarioFixture> CreateAsync(string root, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var bare = await CreateSeedRepositoryAsync(root, ct);
        var codingCli = await WriteCodingCliAsync(root);
        var reviewCli = await WriteReviewCliAsync(root);
        return new ScenarioFixture(root, bare, codingCli, reviewCli);
    }

    private static async Task<string> CreateSeedRepositoryAsync(string root, CancellationToken ct)
    {
        var bare = Path.Combine(root, "origin.git");
        var seed = Path.Combine(root, "seed");
        await GitAsync(root, ct, "init", "--bare", bare);
        await GitAsync(root, ct, "init", "-b", "main", seed);
        Directory.CreateDirectory(Path.Combine(seed, "tests"));

        await File.WriteAllTextAsync(
            Path.Combine(seed, "README.md"),
            "# Scenario fixture\n\nSeeded product repository for the deployment regression scenario.\n",
            ct);
        await WriteExecutableAsync(
            Path.Combine(seed, "tests", "passing.sh"),
            "#!/bin/sh\nprintf 'scenario-test passing ok\\n'\nexit 0\n");
        // A permanently red test proves the review evidence contract can carry a
        // pre-existing failure without turning the deployment run red.
        await WriteExecutableAsync(
            Path.Combine(seed, "tests", "failing.sh"),
            "#!/bin/sh\nprintf 'scenario-test failing not-ok\\n'\nexit 1\n");

        await GitAsync(seed, ct, "add", ".");
        await GitAsync(seed, ct, "commit", "-m", "Seed scenario fixture");
        await GitAsync(seed, ct, "remote", "add", "origin", bare);
        await GitAsync(seed, ct, "push", "-u", "origin", "main");
        await GitAsync(bare, ct, "symbolic-ref", "HEAD", "refs/heads/main");
        return bare;
    }

    /// <summary>
    /// Deterministic coding CLI. It mutates the checkout so the runner produces
    /// a real commit and a durable result reference, writes one evidence file
    /// into the collected results directory, emits one agent message and one
    /// tool frame, and finishes with the terminal sentinel.
    ///
    /// The one second pause models a CLI that streams over time instead of
    /// writing everything in one burst before exiting, which is what a real
    /// provider does. It is not a workaround: how much of the narrative reaches
    /// the Task Server, and whether a frame is classified as a tool trace or as
    /// an unknown frame, currently varies between otherwise identical runs. The
    /// scenario therefore asserts a narrative floor rather than exact counts.
    /// That open finding is recorded in
    /// docs/operations/testing/deployment-scenario.md.
    /// </summary>
    private static async Task<string> WriteCodingCliAsync(string root)
    {
        var path = Path.Combine(root, "scenario-coding-cli.sh");
        await WriteExecutableAsync(path, """
            #!/bin/sh
            set -eu
            if [ "${1:-}" = "--version" ]; then
              printf 'scenario-coding-cli 1.0.0\n'
              exit 0
            fi
            attempt=1
            if [ -n "${SCENARIO_INVOCATION_COUNTER:-}" ]; then
              if [ -f "$SCENARIO_INVOCATION_COUNTER" ]; then
                attempt=$(( $(cat "$SCENARIO_INVOCATION_COUNTER") + 1 ))
              fi
              printf '%s' "$attempt" > "$SCENARIO_INVOCATION_COUNTER"
            fi
            mkdir -p src
            printf 'scenario delivery %s\n' "$attempt" > src/delivered.txt
            mkdir -p "$JOB_RESULTS_DIR"
            printf 'scenario coding evidence\n' > "$JOB_RESULTS_DIR/coding-evidence.txt"
            printf '{"type":"agent_message","text":"scenario coding step complete"}\n'
            printf '{"type":"tool","name":"scenario-fixture-tool"}\n'
            sleep 1
            printf '[[TASK_DONE]]\n'
            """);
        return path;
    }

    /// <summary>
    /// Deterministic review CLI. It runs the fixture's two tests and prints one
    /// stable line per outcome, so the review evidence a scenario step submits
    /// is derived from a real child process rather than invented.
    /// </summary>
    private static async Task<string> WriteReviewCliAsync(string root)
    {
        var path = Path.Combine(root, "scenario-review-cli.sh");
        await WriteExecutableAsync(path, """
            #!/bin/sh
            set -eu
            if [ "${1:-}" = "--version" ]; then
              printf 'scenario-review-cli 1.0.0\n'
              exit 0
            fi
            workspace=${1:-.}
            if sh "$workspace/tests/passing.sh" >/dev/null 2>&1; then
              printf 'build-tests passing.sh pass\n'
            else
              printf 'build-tests passing.sh fail\n'
            fi
            if sh "$workspace/tests/failing.sh" >/dev/null 2>&1; then
              printf 'build-tests failing.sh pass\n'
            else
              printf 'build-tests failing.sh known-failure\n'
            fi
            exit 0
            """);
        return path;
    }

    private static async Task WriteExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task GitAsync(
        string workingDirectory,
        CancellationToken ct,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["GIT_AUTHOR_NAME"] = AuthorName;
        start.Environment["GIT_AUTHOR_EMAIL"] = AuthorEmail;
        start.Environment["GIT_COMMITTER_NAME"] = AuthorName;
        start.Environment["GIT_COMMITTER_EMAIL"] = AuthorEmail;
        start.Environment["GIT_AUTHOR_DATE"] = FixedGitDate;
        start.Environment["GIT_COMMITTER_DATE"] = FixedGitDate;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new ScenarioTargetException("Could not start git for the scenario fixture.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new ScenarioTargetException(
                $"git {string.Join(' ', arguments)} exited {process.ExitCode}.{Environment.NewLine}"
                + $"{stdout}{Environment.NewLine}{stderr}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"scenario: fixture cleanup skipped: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"scenario: fixture cleanup skipped: {exception.Message}");
        }
    }
}
