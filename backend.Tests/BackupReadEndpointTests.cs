using System.Net;
using AgentStudio.Management;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BackupReadEndpointTests : IClassFixture<BackupReadEndpointTests.Host>
{
    private readonly Host _host;

    public BackupReadEndpointTests(Host host) => _host = host;

    public sealed class Host : ManagementTestHost
    {
        protected override void ConfigureHost(IWebHostBuilder builder) => ManagementApiTests.Configure(builder, Folders);
    }

    [Fact]
    public async Task Backup_reads_audit_actor_target_outcome_and_bound_repeated_requests()
    {
        using var client = _host.CreateClient();
        var audit = _host.Services.GetRequiredService<ControlPlaneAuditLog>();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/management/backups")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        (await client.GetAsync("/api/v1/management/backups")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/v1/management/backups")).EnsureSuccessStatusCode();
        const string missingId = "studio-backup-20261109T000000Z-000000";
        var missing = await client.GetAsync($"/api/v1/management/backups/{missingId}/contents");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var record = await _host.Services.GetRequiredService<StudioBackupService>()
            .CreateAsync(BackupAudit.ManualPrefix + DefaultClientIdentity.Id, CancellationToken.None);
        (await client.GetAsync($"/api/v1/management/backups/{record.Id}/contents")).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/v1/management/backups/{record.Id}/contents")).EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(_host.Folders.Backups, record.FileName), "invalid archive");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.GetAsync($"/api/v1/management/backups/{record.Id}/contents")).StatusCode);
        var entries = audit.ReadNewest(entry => entry.Stream == ControlPlaneAuditStreams.Backup
            && entry.Operation is BackupAudit.InventoryRead or BackupAudit.ContentsRead, 20);
        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, entry => entry.Operation == BackupAudit.InventoryRead && entry.Actor == DefaultClientIdentity.Id
            && entry.Target is null && entry.Outcome == BackupAudit.Succeeded);
        Assert.Contains(entries, entry => entry.Operation == BackupAudit.ContentsRead && entry.Actor == DefaultClientIdentity.Id
            && entry.Target == missingId && entry.Outcome == BackupAudit.Failed);
        Assert.Contains(entries, entry => entry.Operation == BackupAudit.ContentsRead && entry.Actor == DefaultClientIdentity.Id
            && entry.Target == record.Id && entry.Outcome == BackupAudit.Succeeded);
        Assert.Contains(entries, entry => entry.Operation == BackupAudit.ContentsRead && entry.Actor == DefaultClientIdentity.Id
            && entry.Target == record.Id && entry.Outcome == BackupAudit.Failed);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("credentialFiles", entry.Raw);
            Assert.DoesNotContain("files", entry.Raw);
        });
        Assert.True(audit.VerifyChain().Ok);
    }
}
