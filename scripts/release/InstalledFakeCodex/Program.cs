using System.Diagnostics;
using System.Text.Json;

if (args is ["--version"])
{
    Console.WriteLine("codex-scenario 1.0");
    return 0;
}
if (args is ["login", "status"])
{
    Console.WriteLine("Scenario login active");
    return 0;
}

var scratchRoot = Environment.GetEnvironmentVariable("SCENARIO_EXPECTED_WORK_ROOT")
    ?? throw new InvalidOperationException("Scenario scratch root is required.");
var workDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
var allowedPrefix = Path.GetFullPath(scratchRoot).TrimEnd(Path.DirectorySeparatorChar)
    + Path.DirectorySeparatorChar;
if (!workDirectory.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Scenario worker started outside its scratch root.");

static int Run(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    process.WaitForExit();
    return process.ExitCode;
}

var shell = Environment.GetEnvironmentVariable("SCENARIO_SHELL") ?? "sh";
var passed = Run(shell, "tests/known-passing.sh");
var failed = Run(shell, "tests/known-failing.sh");
if (passed != 0 || failed == 0)
    throw new InvalidOperationException("Fixture checks did not have the expected outcomes.");
var log = $"known-passing.sh exit={passed}\nknown-failing.sh exit={failed}\n";
File.WriteAllText("scenario-run-log.txt", log);
if (Run("git", "add", "scenario-run-log.txt") != 0
    || Run("git", "-c", "user.name=Scenario Agent", "-c", "user.email=scenario-agent@example.invalid",
        "commit", "-m", "scenario: record fixture check results", "--quiet") != 0
    || Run("git", "push", "origin", "HEAD", "--quiet") != 0)
    throw new InvalidOperationException("Fixture result commit or push failed.");
var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR")
    ?? throw new InvalidOperationException("JOB_RESULTS_DIR is required.");
Directory.CreateDirectory(results);
File.Copy("scenario-run-log.txt", Path.Combine(results, "scenario-run-log.txt"), overwrite: true);
Console.WriteLine(JsonSerializer.Serialize(new
{
    type = "agent_message",
    text = $"ran known-passing.sh (exit {passed}) and known-failing.sh (exit {failed})",
}));
Console.WriteLine("{\"type\":\"tool\",\"name\":\"scenario-fixture-tests\"}");
Console.WriteLine("[[TASK_DONE]]");
return 0;
