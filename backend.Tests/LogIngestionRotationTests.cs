using System.Text;

using Xunit;

namespace AgentStudio.Tests;

public sealed class LogIngestionRotationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "atp-log-rotation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppendBounded_RotatesAtTenMiB_AndRetainsPreviousFileWithMarker()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cli-output.log");
        WriteOversizedLineLog(path);
        const string newest = "[21:45:00.000] [stdout] newest-delivery";

        var appended = LogIngestionEndpoints.AppendBounded(
            path,
            newest,
            deliveryReceipt: null,
            markerTimestamp: new DateTime(2026, 7, 25, 21, 45, 0));

        Assert.True(appended);
        var info = new FileInfo(path);
        Assert.True(
            info.Length <= LogIngestionEndpoints.CliOutputLogCapBytes,
            $"Rotated log remained {info.Length} bytes.");
        var content = File.ReadAllText(path);
        Assert.StartsWith("[21:45:00.000] [system] [cli-output-rotated]", content, StringComparison.Ordinal);
        Assert.EndsWith(newest, content.TrimEnd(), StringComparison.Ordinal);

        var rotation = path + CliOutputLogFile.RotationSuffix;
        Assert.True(File.Exists(rotation));
        Assert.True(new FileInfo(rotation).Length <= LogIngestionEndpoints.CliOutputLogCapBytes);
        Assert.Contains("old-tail-line", File.ReadAllText(rotation), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBounded_DuplicateReceipt_DoesNotAppendTwice()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cli-output.log");
        const string receipt = "[runner-log-delivery:abc123]";
        var payload = $"[21:45:00.000] [stdout] once{Environment.NewLine}"
                      + $"[21:45:00.000] [system] {receipt}";

        Assert.True(LogIngestionEndpoints.AppendBounded(
            path, payload, receipt, new DateTime(2026, 7, 25, 21, 45, 0)));
        Assert.False(LogIngestionEndpoints.AppendBounded(
            path, payload, receipt, new DateTime(2026, 7, 25, 21, 45, 1)));

        Assert.Equal(1, File.ReadLines(path).Count(line => line.Contains("once", StringComparison.Ordinal)));
    }

    [Fact]
    public void MigrateExisting_SplitsLegacyOversizedLogIntoOneBoundedRotationAndActiveTail()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cli-output.log");
        const int cap = 512;
        var lines = Enumerable.Range(0, 80)
            .Select(i => $"[{i:000}] [stdout] legacy-line-{i:000}-abcdefghijklmnopqrstuvwxyz")
            .ToArray();
        File.WriteAllLines(path, lines, new UTF8Encoding(false));

        Assert.True(CliOutputLogFile.MigrateExisting(
            path,
            cap,
            new DateTime(2026, 8, 2, 9, 30, 0)));

        var rotation = path + CliOutputLogFile.RotationSuffix;
        Assert.True(new FileInfo(path).Length <= cap);
        Assert.True(new FileInfo(rotation).Length <= cap);
        Assert.StartsWith("[09:30:00.000] [system] [cli-output-rotated]", File.ReadAllText(path));
        Assert.Contains("legacy-line-079", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("legacy-line-070", File.ReadAllText(rotation), StringComparison.Ordinal);
        Assert.False(CliOutputLogFile.MigrateExisting(
            path,
            cap,
            new DateTime(2026, 8, 2, 9, 31, 0)));
    }

    [Fact]
    public void EnsureRotationIgnored_AppendsRuleOnceAndPreservesExistingEntries()
    {
        Directory.CreateDirectory(_root);
        var ignore = Path.Combine(_root, ".gitignore");
        File.WriteAllText(ignore, "projects/*/tasks/*/*/results/\n");

        Assert.True(CliOutputLogMaintenanceService.EnsureRotationIgnored(_root));
        Assert.False(CliOutputLogMaintenanceService.EnsureRotationIgnored(_root));

        var content = File.ReadAllText(ignore);
        Assert.Contains("projects/*/tasks/*/*/results/", content, StringComparison.Ordinal);
        Assert.Equal(
            1,
            File.ReadLines(ignore).Count(line => line == CliOutputLogFile.RotationIgnorePattern));
        Assert.Contains("/logs/bus/", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRetentionDeletesOldBusAndAuthorityArchivesOnly()
    {
        var bus = Path.Combine(_root, "logs", "bus", "demo");
        var metadata = Path.Combine(_root, ".metadata");
        Directory.CreateDirectory(bus);
        Directory.CreateDirectory(metadata);
        var oldBus = Path.Combine(bus, "old.jsonl");
        var newBus = Path.Combine(bus, "new.jsonl");
        var oldAuthority = Path.Combine(metadata, "attempt-authority.archive-2025-01-01.json");
        var liveAuthority = Path.Combine(metadata, "attempt-authority.json");
        foreach (var path in new[] { oldBus, newBus, oldAuthority, liveAuthority }) File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(oldBus, DateTime.UtcNow.AddDays(-31));
        File.SetLastWriteTimeUtc(oldAuthority, DateTime.UtcNow.AddDays(-91));

        Assert.Equal(2, CliOutputLogMaintenanceService.DeleteExpiredRuntimeFiles(_root, DateTimeOffset.UtcNow));
        Assert.False(File.Exists(oldBus));
        Assert.False(File.Exists(oldAuthority));
        Assert.True(File.Exists(newBus));
        Assert.True(File.Exists(liveAuthority));
    }

    private static void WriteOversizedLineLog(string path)
    {
        var line = Encoding.UTF8.GetBytes(
            "[20:00:00.000] [stdout] old-tail-line abcdefghijklmnopqrstuvwxyz"
            + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        while (stream.Length <= LogIngestionEndpoints.CliOutputLogCapBytes)
            stream.Write(line);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
