using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ResultOwnershipRepairTests
{
    [Fact]
    public async Task Denied_repair_names_foreign_owned_result_and_owner()
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-foreign-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "report.html");
        await File.WriteAllTextAsync(file, "container output");
        var calls = new List<string>();
        try
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ResultOwnershipRepair.RepairOrThrowAsync("AGT-2737", root, _ => { }, CancellationToken.None,
                    (command, args, input, _) =>
                    {
                        calls.Add(command);
                        if (command == "sudo")
                        {
                            Assert.Equal(["-n", "/usr/local/sbin/agent-runner-deploy", "chown-results"], args);
                            Assert.Equal("AGT-2737\n", input);
                        }
                        return Task.FromResult(command switch
                        {
                            "find" => new ProcessResult(0, file + "\n", ""),
                            "stat" => new ProcessResult(0, "10001:10001\n", ""),
                            _ => new ProcessResult(1, "", "sudo rule unavailable"),
                        });
                    }));
            Assert.Contains(file, error.Message);
            Assert.Contains("10001:10001", error.Message);
            Assert.Contains("chown results", error.Message);
            Assert.Contains("sudo", calls);
        }
        finally { Directory.Delete(root, true); }
    }
}
