using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Persists complete TRX and a small timing index beside review command logs.</summary>
internal static class ReviewTestTimingEvidence
{
    internal static async Task<IReadOnlyList<ReviewArtifactEvidenceDto>> CaptureAsync(
        string standardOutput,
        string attemptRoot,
        string artifactDirectory,
        string artifactPrefix,
        bool retryPerformed,
        Action<string> log,
        CancellationToken ct)
    {
        var artifacts = new List<ReviewArtifactEvidenceDto>();
        var ordinal = 0;
        foreach (var rawPath in ResultPaths(standardOutput))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(rawPath);
                var relative = Path.GetRelativePath(attemptRoot, path);
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    log($"review-test-timing skipped outside attempt path={rawPath}");
                    continue;
                }
                if (!File.Exists(path)) continue;
                var source = await File.ReadAllBytesAsync(path, ct);
                if (source.Length > 64 * 1024 * 1024)
                {
                    log($"review-test-timing skipped oversized TRX bytes={source.Length}");
                    continue;
                }

                var name = $"{artifactPrefix}.{ordinal++}";
                var summary = Summarize(source, retryPerformed, Path.GetFileName(path));
                var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }) + "\n";
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var summaryName = name + ".json";
                await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, summaryName), jsonBytes, ct);
                artifacts.Add(Artifact(summaryName, "application/json", jsonBytes));

                using var buffer = new MemoryStream();
                using (var compressor = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
                    await compressor.WriteAsync(source, ct);
                var compressed = buffer.ToArray();
                var trxName = name + ".trx.gz";
                await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, trxName), compressed, ct);
                artifacts.Add(Artifact(trxName, "application/gzip", compressed));
                log($"review-test-timing captured tests={summary.Total.Tests} " +
                    $"wallSeconds={summary.Total.WallSeconds:0.###} compressedBytes={compressed.Length}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
            {
                log($"review-test-timing unavailable: {ex.Message}");
            }
        }
        return artifacts;
    }

    internal static TimingSummary Summarize(byte[] trx, bool retryPerformed, string source)
    {
        using var stream = new MemoryStream(trx);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 64 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest")
            .Select(element => new
            {
                Id = (string?)element.Attribute("id"),
                Class = (string?)element.Elements().FirstOrDefault(child => child.Name.LocalName == "TestMethod")?.Attribute("className"),
            })
            .Where(item => item.Id is not null)
            .GroupBy(item => item.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Class ?? "unknown", StringComparer.Ordinal);
        var tests = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult")
            .Select(element =>
            {
                var id = (string?)element.Attribute("testId") ?? "";
                var name = (string?)element.Attribute("testName") ?? "unnamed test";
                var className = definitions.GetValueOrDefault(id, "unknown");
                var duration = TimeSpan.TryParse((string?)element.Attribute("duration"),
                    CultureInfo.InvariantCulture, out var parsed) ? parsed.TotalSeconds : 0;
                return new TimingTest(className, name, duration,
                    (string?)element.Attribute("outcome") ?? "Unknown");
            })
            .ToArray();
        var perClass = tests.GroupBy(test => test.Class, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(test => test.Seconds))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Math.Round(group.Sum(test => test.Seconds), 3), StringComparer.Ordinal);
        var times = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Times");
        double? wall = null;
        if (DateTimeOffset.TryParse((string?)times?.Attribute("start"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var started)
            && DateTimeOffset.TryParse((string?)times?.Attribute("finish"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var finished))
            wall = Math.Round(Math.Max(0, (finished - started).TotalSeconds), 3);
        return new TimingSummary(
            1,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            HostLoad1(),
            ConcurrentSuitesAtCapture(),
            retryPerformed ? 1 : 0,
            source,
            new TimingTotal(tests.Length, Math.Round(tests.Sum(test => test.Seconds), 3), wall,
                tests.Count(test => string.Equals(test.Outcome, "Failed", StringComparison.OrdinalIgnoreCase))),
            perClass,
            tests.OrderByDescending(test => test.Seconds).Take(50).ToArray());
    }

    private static IEnumerable<string> ResultPaths(string output)
        => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["Results File:".Length..].Trim())
            .Where(path => path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static ReviewArtifactEvidenceDto Artifact(string name, string mediaType, byte[] bytes)
        => new(name, mediaType, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength, Convert.ToBase64String(bytes));

    private static double? HostLoad1()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var load)
                ? load : null;
        }
        catch (IOException) { return null; }
    }

    private static int? ConcurrentSuitesAtCapture()
    {
        if (!Directory.Exists("/proc")) return null;
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(directory, "comm")).Trim() != "dotnet") continue;
                var args = File.ReadAllBytes(Path.Combine(directory, "cmdline"));
                var values = Encoding.UTF8.GetString(args).Split('\0');
                if (values.Length > 1 && Path.GetFileName(values[0]) == "dotnet" && values[1] == "test") count++;
            }
            catch (IOException) { /* process exited during enumeration */ }
            catch (UnauthorizedAccessException) { /* another user's process */ }
        }
        return count;
    }
}

internal sealed record TimingTest(string Class, string Name, double Seconds, string Outcome);
internal sealed record TimingTotal(int Tests, double SumSeconds, double? WallSeconds, int Failed);
internal sealed record TimingSummary(
    int SchemaVersion,
    DateTimeOffset RecordedAtUtc,
    string Host,
    double? HostLoad1AtCapture,
    int? ConcurrentSuitesAtCapture,
    int Retries,
    string Source,
    TimingTotal Total,
    IReadOnlyDictionary<string, double> PerClassSeconds,
    IReadOnlyList<TimingTest> Slowest50);
