using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Agent;
using AgentStudio.Operations.Agent.Features.Execution;
using AgentStudio.Operations.Server.Features.Dispatch;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Xunit;

namespace OperationsServer.Tests;

public sealed class HostOperationCatalogTests
{
    [Fact]
    public void Routine_host_work_has_typed_capabilities()
    {
        var ids = OperationCatalogue.All.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("host.inspect", ids);
        Assert.Contains("service.restart", ids);
        Assert.Contains("host.drain", ids);
        Assert.Contains("host.resume", ids);
        Assert.Contains("host.logs.collect", ids);
        Assert.Contains("host.configuration.apply", ids);
    }

    [Theory]
    [InlineData("host.drain", "{\"role\":\"root\"}")]
    [InlineData("host.resume", "{\"role\":\"review\",\"command\":\"id\"}")]
    [InlineData("host.logs.collect", "{\"source\":\"../private\"}")]
    [InlineData("host.configuration.apply", "{\"configuration\":\"runtime-config\",\"sha256\":\"bad\"}")]
    public void Host_commands_reject_paths_commands_and_unbounded_inputs(string operationId, string json)
    {
        var input = JsonDocument.Parse(json).RootElement.Clone();
        var command = Fixture.NewCommand(new TestClock().GetUtcNow()) with
        {
            OperationId = operationId, Input = input, InputDigest = OperationsProtocol.Digest(input),
        };
        var principal = Fixture.Edge with { Scopes = [.. Fixture.Edge.Scopes, operationId] };
        Assert.Equal("invalid-input", OperationPolicy.Admission(principal, command, new TestClock().GetUtcNow()));
    }

    [Fact]
    public async Task Host_effects_use_fixed_local_targets_and_leave_matching_append_only_history()
    {
        await using var f = await Fixture.Start();
        var scopes = new[] { "host.drain", "host.resume", "host.logs.collect", "host.configuration.apply" };
        f.SavePrincipals(f.Principals.Select(principal => principal.Id is "edge" or "agent"
            ? principal with { Scopes = [.. principal.Scopes, .. scopes] } : principal).ToArray());
        File.WriteAllText(f.ComposeFile, "reviewed host configuration");
        var target = Path.Combine(f.Directory, "applied.conf");
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.ComposeFile)));
        var settings = new HostRoutineSettings(f.ComposeFile, f.ComposeFile,
            new Dictionary<string, string> { ["runtime-log"] = f.ComposeFile },
            new Dictionary<string, ReviewedConfiguration> { ["runtime-config"] = new(f.ComposeFile, target, digest) });
        var calls = new List<string[]>();
        var agent = new OperationsAgentClient(f.Agent, "host-a", "boot-a", Path.Combine(f.Directory, "spool"), f.Clock,
            run: (start, _, _) =>
            {
                calls.Add(start.ArgumentList.ToArray());
                return Task.FromResult(new ToolRun(0, string.Empty, false));
            }, hostSettings: settings);
        await agent.RegisterAsync(default);

        async Task<OperationAttempt> Run(string id, object input)
        {
            var element = JsonSerializer.SerializeToElement(input);
            var accepted = await f.Submit(f.Command(Guid.NewGuid().ToString("N")) with
            {
                OperationId = id, Input = element, InputDigest = OperationsProtocol.Digest(element),
            });
            await agent.PollOnceAsync(default);
            var completed = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + "attempts/" + accepted.Id);
            Assert.Equal("succeeded", completed!.State);
            return completed;
        }

        var drained = await Run("host.drain", new { role = "review" });
        Assert.True(drained.Result!.Output.GetProperty("drained").GetBoolean());
        Assert.Equal(["host-agent", "drain-role", f.ComposeFile, "review"], calls[0]);
        var resumed = await Run("host.resume", new { role = "review" });
        Assert.False(resumed.Result!.Output.GetProperty("drained").GetBoolean());
        Assert.Equal(["host-agent", "enable-role", f.ComposeFile, "review"], calls[1]);
        var logs = await Run("host.logs.collect", new { source = "runtime-log" });
        Assert.Contains("reviewed host configuration", logs.Result!.Output.GetProperty("text").GetString());
        await Run("host.configuration.apply", new { configuration = "runtime-config", sha256 = digest });
        Assert.Equal("reviewed host configuration", File.ReadAllText(target));

        // A crash after the host append but before the HTTP receipt must be
        // reconciled on the next poll without executing the operation again.
        var pending = new HostJournalEntry(drained.Id, drained.Command.Actor, drained.AgentId,
            drained.Command.OperationId, drained.Command.InputDigest, f.Clock.GetUtcNow().AddSeconds(1), "succeeded");
        File.AppendAllText(Path.Combine(f.Directory, "spool") + ".journal.jsonl",
            JsonSerializer.Serialize(pending, OperationsProtocol.Json) + "\n");
        await agent.PollOnceAsync(default);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Directory, "operations.db") }.ToString());
        db.Open();
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM operation_history WHERE attempt_id = $id";
        count.Parameters.AddWithValue("$id", drained.Id);
        Assert.True((long)count.ExecuteScalar()! >= 3);
        using var receipts = db.CreateCommand();
        receipts.CommandText = "SELECT COUNT(*) FROM host_journal_receipts";
        Assert.Equal(9, (long)receipts.ExecuteScalar()!);
        using var mutation = db.CreateCommand();
        mutation.CommandText = "DELETE FROM operation_history WHERE attempt_id = $id";
        mutation.Parameters.AddWithValue("$id", drained.Id);
        Assert.Throws<SqliteException>(() => mutation.ExecuteNonQuery());

        var unmatched = new HostJournalEntry(Guid.NewGuid().ToString("N"), "operator", "host-a", "host.inspect",
            OperationsProtocol.Digest("unknown"), f.Clock.GetUtcNow(), "execution-started");
        using var rejected = await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/host-a/journal", unmatched);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("host-journal-without-central-audit", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Published_revocations_are_installed_on_the_host_before_polling_for_work()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var f = await Fixture.Start();
        f.SavePrincipals(f.Principals.Select(principal => principal.Id == "edge"
            ? principal with { Scopes = [.. principal.Scopes, "certificates.revoke"] } : principal).ToArray());
        var krl = Path.Combine(f.Directory, "source.krl");
        var start = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-q", "-k", "-f", krl, "-z", "1" }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var bytes = await File.ReadAllBytesAsync(krl);
        var list = new HostRevocationList(1, Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToBase64String(bytes));
        using var published = await f.Service.PutAsJsonAsync(Fixture.Api + "certificates/revocations", list);
        Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        var installed = Path.Combine(f.Directory, "installed.krl");
        var settings = HostRoutineSettings.Empty with { RevokedKeysFile = installed };
        var agent = new OperationsAgentClient(f.Agent, "host-a", "boot-a", Path.Combine(f.Directory, "spool"), f.Clock,
            hostSettings: settings);
        await agent.RegisterAsync(default);
        await agent.PollOnceAsync(default);
        Assert.Equal(bytes, File.ReadAllBytes(installed));
    }
}
