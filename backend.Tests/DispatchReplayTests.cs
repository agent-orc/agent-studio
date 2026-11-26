using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AgentStudio.Dispatch;
using AgentStudio.Tests.Architecture;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

// clock-independent: dates identify historical wave and inventory fixtures; forecast inputs never compare those dates with the wall clock.

public sealed class DispatchReplayTests(ITestOutputHelper output)
{
    private static readonly string Root = CSharpSourceScanner.RepoRoot();
    private static JsonDocument Wave() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,
        "docs/dispatcher-personas/replay/wave-2026-11-17.json")));
    private static JsonDocument Sources()
    {
        var folder = Path.Combine(Root, "backend.Tests/Fixtures/dispatch");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "source-findings.json")));
        using var file = File.OpenRead(Path.Combine(folder, manifest.RootElement.GetProperty("trackedFiles").GetString()!));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var cards = new List<JsonElement>();
        foreach (var name in Strings(manifest.RootElement, "findingFiles"))
        {
            using var findings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, name)));
            cards.AddRange(findings.RootElement.EnumerateArray().Select(card => card.Clone()));
        }
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            integrationTip = manifest.RootElement.GetProperty("integrationTip").GetString(),
            trackedFiles = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries), cards,
        }));
    }
    private static string[] Strings(JsonElement element, string property) => element.GetProperty(property)
        .EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static string FindingText(JsonElement card) => string.Join('\n', card.GetProperty("findings")
        .EnumerateArray().SelectMany(finding => new[] { finding.GetProperty("problem").GetString()!,
            finding.GetProperty("cause").GetString()! }.Concat(Strings(finding, "acceptanceCriteria"))));
    private static CoChangeIndex SourceIndex(JsonElement source)
    {
        var tracked = Strings(source, "trackedFiles");
        Assert.Equal(10_365, tracked.Length);
        return TouchForecast.BuildIndex(source.GetProperty("integrationTip").GetString()!,
            new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), tracked, []);
    }

    [Fact]
    public void StoredWaveForecastsReproduceDossierPairF1()
    {
        using var wave = Wave();
        var hubs = Strings(wave.RootElement, "hubFiles");
        var cards = wave.RootElement.GetProperty("cards").EnumerateArray().ToArray();
        Assert.Equal(57, cards.Length);
        var truth = cards.Select(card => Strings(card, "codeFiles").ToHashSet(StringComparer.Ordinal)).ToArray();
        var files = cards.Select(card => Strings(card, "forecastFiles").Except(hubs).ToHashSet(StringComparer.Ordinal)).ToArray();
        var modules = cards.Select(card => TouchForecast.Modules(Strings(card, "forecastFiles"), hubs)
            .ToHashSet(StringComparer.Ordinal)).ToArray();
        CheckF1("forecast files", files, 0.56);
        CheckF1("modules", modules, 0.52);

        void CheckF1(string label, HashSet<string>[] predicted, double expected)
        {
            var realPairs = 0;
            var predictedPairs = 0;
            var correctPairs = 0;
            for (var a = 0; a < cards.Length; a++)
            for (var b = a + 1; b < cards.Length; b++)
            {
                var real = truth[a].Overlaps(truth[b]);
                var guess = predicted[a].Overlaps(predicted[b]);
                if (real) realPairs++;
                if (guess) predictedPairs++;
                if (real && guess) correctPairs++;
            }
            var f1 = 2.0 * correctPairs / (realPairs + predictedPairs);
            output.WriteLine($"{label}: real pairs={realPairs}, predicted={predictedPairs}, correct={correctPairs}, F1={f1:F4}");
            Assert.InRange(f1, expected - 0.03, expected + 0.03);
        }
    }

    [Fact]
    public void OriginalFindingTextsRecoverDossierCitationsForAtLeastFiftyCards()
    {
        using var wave = Wave();
        using var source = Sources();
        var index = SourceIndex(source.RootElement);
        var expected = wave.RootElement.GetProperty("cards").EnumerateArray()
            .Where(card => card.GetProperty("hasSourceText").GetBoolean())
            .ToDictionary(card => card.GetProperty("key").GetString()!, card => Strings(card, "citedFiles"));
        var cards = source.RootElement.GetProperty("cards").EnumerateArray().ToArray();
        Assert.Equal(55, expected.Count);
        Assert.Equal(expected.Keys.Order(), cards.Select(card => card.GetProperty("key").GetString()!).Order());
        var recovered = 0;
        var exact = 0;
        foreach (var card in cards)
        {
            var key = card.GetProperty("key").GetString()!;
            var forecast = TouchForecast.Compute(FindingText(card), index, TouchForecast.DefaultHubFiles);
            var missing = expected[key].Except(forecast.CitedFiles).ToArray();
            var additional = forecast.CitedFiles.Except(expected[key]).ToArray();
            if (missing.Length == 0) recovered++;
            if (missing.Length == 0 && additional.Length == 0) exact++;
            if (missing.Length > 0 || additional.Length > 0)
                output.WriteLine($"{key}: missing=[{string.Join(", ", missing)}]; additional=[{string.Join(", ", additional)}]");
        }
        // The recorded sets omit some valid citations and expand ambiguous basenames.
        // Keep those historical sets intact and report exact equality independently.
        output.WriteLine($"Recovered all recorded citations: {recovered}/55; exact historical sets: {exact}/55.");
        Assert.InRange(recovered, 50, 55);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void EveryWaveCardComputesUnderFiftyMillisecondsWithWarmIndex()
    {
        using var source = Sources();
        var index = SourceIndex(source.RootElement);
        var texts = source.RootElement.GetProperty("cards").EnumerateArray().Select(FindingText).ToArray();
        foreach (var text in texts) TouchForecast.Compute(text, index, TouchForecast.DefaultHubFiles);
        var times = new List<double>();
        foreach (var text in texts)
        {
            var started = Stopwatch.GetTimestamp();
            var forecast = TouchForecast.Compute(text, index, TouchForecast.DefaultHubFiles);
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(index.Revision, forecast.IndexRevision);
        }
        output.WriteLine($"Warm forecasts: {times.Count}; tracked files: {index.TrackedFiles.Count}; max={times.Max():F3} ms; mean={times.Average():F3} ms.");
        Assert.All(times, milliseconds => Assert.True(milliseconds < 50, $"Forecast took {milliseconds:F3} ms (limit 50 ms)."));
    }
}
