using System.Diagnostics;
using System.Text.Json;

if (args is ["--version"])
{
    Console.WriteLine("installation-fake-cli 1.0.0");
    return;
}

var passing = Path.Combine("tests", "known-passing.sh");
var failing = Path.Combine("tests", "known-failing.sh");
if (!File.Exists(passing) || !File.Exists(failing)
    || !File.ReadAllText(passing).Contains("exit 0", StringComparison.Ordinal)
    || !File.ReadAllText(failing).Contains("exit 1", StringComparison.Ordinal))
    throw new InvalidOperationException("The installed scenario fixture checks are missing or changed.");

var log = "known-passing.sh exit=0\nknown-failing.sh exit=1\n";
File.WriteAllText("scenario-run-log.txt", log);
await GitAsync("add", "scenario-run-log.txt");
await GitAsync("-c", "user.name=Scenario Agent", "-c", "user.email=scenario-agent@example.invalid",
    "commit", "-m", "scenario: record fixture check results", "--quiet");
await GitAsync("push", "origin", "HEAD", "--quiet");
var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR")
    ?? throw new InvalidOperationException("JOB_RESULTS_DIR is required.");
Directory.CreateDirectory(results);
File.Copy("scenario-run-log.txt", Path.Combine(results, "scenario-run-log.txt"), overwrite: true);
Console.WriteLine(JsonSerializer.Serialize(new { type = "agent_message", text = "Recorded both fixture checks." }));
Console.WriteLine("[[TASK_DONE]]");

static async Task GitAsync(params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)
        ?? throw new InvalidOperationException("Git did not start.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await Task.WhenAll(output, error);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Git {arguments[0]} failed with code {process.ExitCode}.");
}
