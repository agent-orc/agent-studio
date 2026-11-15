using Xunit;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;
using AgentStudio.Operations.Server.Features.Dispatch;

namespace OperationsServer.Tests;

public sealed class OperationsRecoveryTests
{
    [Fact]
    public void Networked_stack_archives_all_operations_authority_and_spool()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var compose = File.ReadAllText(Path.Combine(root, "deploy/compose/monolith-networked/compose.yaml"));
        Assert.Contains("Backup__Sources__operations__Path: /run/backup-sources/operations", compose);
        Assert.Contains("${STUDIO_DATA_DIR}/operations:/run/backup-sources/operations:ro", compose);
        foreach (var name in new[] { "data", "principals", "agent-secret", "client-secret", "agent-data" })
            Assert.Contains("${STUDIO_DATA_DIR}/operations/" + name + ":", compose);
        Assert.DoesNotContain("- operations-client-secret:", compose);
        var restore = File.ReadAllText(Path.Combine(root, "deploy/compose/monolith-networked/restore-backup.sh"));
        Assert.Contains("--target operations=/data/operations", restore);
    }

    [Fact]
    public async Task Disposable_second_host_keeps_authority_history_and_fences_all_old_credentials()
    {
        var first = Path.Combine(Path.GetTempPath(), "operations-first-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "operations-second-" + Guid.NewGuid().ToString("N"));
        try
        {
            var principalDir = Path.Combine(first, "principals");
            var agentDir = Path.Combine(first, "agent-secret");
            var clientDir = Path.Combine(first, "client-secret");
            OperationsBootstrap.Run(principalDir, agentDir, clientDir);
            var data = Path.Combine(first, "data");
            var firstStore = new OperationsStore(data);
            var oldCommand = Fixture.NewCommand(DateTimeOffset.MaxValue.AddHours(-1));
            await firstStore.TransactionAsync(state =>
            {
                state.Attempts["old-attempt"] = new OperationAttempt("old-attempt", "diagnostics-client", oldCommand,
                    "host-a", null, 1, null, "queued", []);
                return Task.FromResult(0);
            });
            Directory.CreateDirectory(Path.Combine(first, "agent-data", "spool"));
            File.WriteAllText(Path.Combine(first, "agent-data", "spool", "pending.json"), "pending journal");
            File.WriteAllText(Path.Combine(data, OperationsAccess.RevocationsFileName),
                JsonSerializer.Serialize(new OperatorRevocationDocument(["former-operator"], ["former-agent"]), OperationsProtocol.Json));
            File.WriteAllText(Path.Combine(data, "host-revocations.json"), "retained KRL");
            var path = Path.Combine(principalDir, "principals.json");
            var document = JsonSerializer.Deserialize<OperationsAccessDocument>(File.ReadAllText(path), OperationsProtocol.Json)!;
            var oldRemoteToken = "old-remote-" + Guid.NewGuid().ToString("N");
            var oldOperatorToken = "old-operator-" + Guid.NewGuid().ToString("N");
            var oldClientToken = File.ReadAllText(Path.Combine(clientDir, "token"));
            var remote = new OperationsPrincipal("remote-a-old", OperationsProtocol.Audience,
                OperationsProtocol.Digest(oldRemoteToken), "agent", ["agents.connect", "host.inspect"], ["host-a"], []);
            var oldOperator = new OperationsPrincipal("operator-old", OperationsProtocol.Audience,
                OperationsProtocol.Digest(oldOperatorToken), "service", ["host.inspect"], ["host-a"], [],
                OperatorAgentId: "operator-a");
            var retired = remote with { Id = "remote-retired", TokenSha256 = OperationsProtocol.Digest("retired-" + Guid.NewGuid().ToString("N")),
                AgentIds = ["host-retired"], Revoked = true };
            File.WriteAllText(path, JsonSerializer.Serialize(document with { Principals = [.. document.Principals, remote, retired, oldOperator] }, OperationsProtocol.Json));
            Assert.NotNull(new OperationsAccess(path).Authenticate(oldRemoteToken));
            Assert.NotNull(new OperationsAccess(path).Authenticate(oldOperatorToken));

            Copy(first, second);
            var markerDir = Path.Combine(second, "recovery-marker");
            Directory.CreateDirectory(markerDir);
            var marker = Path.Combine(markerDir, "required");
            File.WriteAllText(marker, "restore in progress");
            Assert.True(OperationsRecovery.BlocksOperatorAccess(Path.Combine(second, "data"), marker));
            var recovery = OperationsRecovery.Fence(second, marker, "archive-a");
            Assert.Equal("archive-a", recovery.BackupId);
            Assert.Contains(recovery.Items, item => item.Kind == "reenrol" && item.Id == "host-a" && !item.Complete);
            Assert.Contains(recovery.Items, item => item.Kind == "agent-revocation" && item.Id == "host-a" && !item.Complete);
            Assert.DoesNotContain(recovery.Items, item => item.Id == "host-retired");
            Assert.True(OperationsRecovery.BlocksOperatorAccess(Path.Combine(second, "data"), marker));
            var restoredAccess = new OperationsAccess(Path.Combine(second, "principals", "principals.json"));
            Assert.Null(restoredAccess.Authenticate(oldRemoteToken));
            Assert.Null(restoredAccess.Authenticate(oldOperatorToken));
            Assert.Null(restoredAccess.Authenticate(oldClientToken));
            Assert.Equal("diagnostics-client", restoredAccess.Authenticate(File.ReadAllText(Path.Combine(second, "client-secret", "token")))?.Id);
            Assert.Equal(File.ReadAllText(Path.Combine(data, "host-revocations.json")),
                File.ReadAllText(Path.Combine(second, "data", "host-revocations.json")));
            Assert.Equal(File.ReadAllText(Path.Combine(data, OperationsAccess.RevocationsFileName)),
                File.ReadAllText(Path.Combine(second, "data", OperationsAccess.RevocationsFileName)));
            Assert.Equal("pending journal", File.ReadAllText(Path.Combine(second, "agent-data", "spool", "pending.json")));
            var oldAttempt = await new OperationsStore(Path.Combine(second, "data")).TransactionAsync(state =>
                Task.FromResult(state.Attempts["old-attempt"]));
            Assert.Equal("cancelled", oldAttempt.State);
            Assert.Contains(oldAttempt.Events, entry => entry.Kind == "host-restored");
            Assert.Throws<InvalidDataException>(() => OperationsRecovery.Complete(second, "reenrol", "host-a"));

            var newRemoteToken = "new-remote-" + Guid.NewGuid().ToString("N");
            var fenced = JsonSerializer.Deserialize<OperationsAccessDocument>(File.ReadAllText(Path.Combine(second, "principals", "principals.json")), OperationsProtocol.Json)!;
            var fresh = remote with { Id = "remote-a-new", TokenSha256 = OperationsProtocol.Digest(newRemoteToken) };
            File.WriteAllText(Path.Combine(second, "principals", "principals.json"),
                JsonSerializer.Serialize(fenced with { Principals = [.. fenced.Principals, fresh] }, OperationsProtocol.Json));
            Assert.Throws<InvalidDataException>(() => OperationsRecovery.Complete(second, "reenrol", "host-a"));
            await new OperationsStore(Path.Combine(second, "data")).TransactionAsync(state =>
            {
                state.Agents["host-a"] = new AgentRegistration(
                    new AgentCapability("host-a", "new-boot", "host", [], [], new(), 1, 1, 1),
                    DateTimeOffset.Parse(recovery.FencedAt, System.Globalization.CultureInfo.InvariantCulture).AddSeconds(1));
                return Task.FromResult(0);
            });
            OperationsRecovery.Complete(second, "reenrol", "host-a");
            OperationsRecovery.Complete(second, "agent-revocation", "host-a");
            OperationsRecovery.Complete(second, "host-certificate-revocations", "installation");
            OperationsRecovery.Complete(second, "operator-revocations", "installation");
            Assert.False(OperationsRecovery.BlocksOperatorAccess(Path.Combine(second, "data"), marker));
            Assert.Equal("remote-a-new", restoredAccess.Authenticate(newRemoteToken)?.Id);
            Assert.Null(restoredAccess.Authenticate(oldRemoteToken));
        }
        finally
        {
            if (Directory.Exists(first)) Directory.Delete(first, true);
            if (Directory.Exists(second)) Directory.Delete(second, true);
        }
    }

    [Fact]
    public async Task Recovery_blocks_operator_commands_while_fresh_agents_can_register()
    {
        await using var host = await Fixture.Start();
        var path = Path.Combine(host.Directory, OperationsRecovery.FileName);
        var pending = new OperationsRecoveryDocument("archive-a", DateTimeOffset.MinValue.ToString("O"),
            [new OperationsRecoveryItem("host-certificate-revocations", "installation", false, [])]);
        File.WriteAllText(path, JsonSerializer.Serialize(pending, OperationsProtocol.Json));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await host.Service.PostAsJsonAsync(Fixture.Api + "commands", host.Command())).StatusCode);
        await host.Register();
        File.WriteAllText(path, JsonSerializer.Serialize(pending with
        {
            Items = [pending.Items[0] with { Complete = true }]
        }, OperationsProtocol.Json));
        Assert.Equal(HttpStatusCode.OK,
            (await host.Service.PostAsJsonAsync(Fixture.Api + "commands", host.Command())).StatusCode);
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source)) Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
}
