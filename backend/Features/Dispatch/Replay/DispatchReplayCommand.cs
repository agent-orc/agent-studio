using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Git;

namespace AgentStudio.Dispatch;

/// <summary>Read-only command for an offline test host. Never starts the product or changes the lane.</summary>
public static class DispatchReplayCommand
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length % 2 != 0) throw new ArgumentException("Use --name value pairs.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new[] { "--fixture", "--project", "--recording", "--repo", "--store", "--from", "--to", "--wave", "--slots", "--durations", "--lag", "--check" };
            for (var i = 0; i < args.Length; i += 2)
                if (!allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException($"Unknown or repeated option: {args[i]}.");
            string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException($"Missing {key}.");
            var check = values.GetValueOrDefault("--check", "in-flight") switch
            {
                "running" => DispatchOverlapWindow.Running,
                "in-flight" => DispatchOverlapWindow.InFlight,
                _ => throw new ArgumentException("Use running or in-flight for --check."),
            };
            var options = new DispatchReplayOptions(int.Parse(values.GetValueOrDefault("--slots", "4"), CultureInfo.InvariantCulture),
                values.GetValueOrDefault("--durations", "unit"),
                double.Parse(values.GetValueOrDefault("--lag", "0"), CultureInfo.InvariantCulture), check);
            var project = Required("--project");
            DispatchReplayWave wave;
            if (values.TryGetValue("--fixture", out var path))
            {
                if (new[] { "--wave", "--from", "--to", "--recording", "--store", "--repo" }.Any(values.ContainsKey))
                    throw new ArgumentException("Fixture mode cannot be combined with recorded-wave selectors.");
                wave = ReadFixture(path, project);
            }
            else
            {
                var recording = JsonSerializer.Deserialize<DispatchReplayRecording>(File.ReadAllText(Required("--recording")), Json)
                    ?? throw new ArgumentException("The recording is empty.");
                var store = new DispatchStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Dispatch:StoragePath"] = Required("--store") }).Build());
                var from = values.TryGetValue("--from", out var start) ? DateTimeOffset.Parse(start, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
                var to = values.TryGetValue("--to", out var end) ? DateTimeOffset.Parse(end, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
                wave = Build(recording, project, Required("--repo"), store, values.GetValueOrDefault("--wave"), from, to);
            }
            output.WriteLine(JsonSerializer.Serialize(DispatchReplaySimulator.Run(wave, options), Json));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
    }

    public static DispatchReplayWave Build(DispatchReplayRecording recording, string project, string repository,
        DispatchStore store, string? waveId, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!recording.Project.Equals(project, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The recording belongs to another project.");
        if (waveId is null && (from is null || to is null || from >= to))
            throw new ArgumentException("Select a wave id or a valid half-open time window.");
        if (waveId is not null && (from is not null || to is not null))
            throw new ArgumentException("Choose either a wave id or a time window.");
        var selected = recording.Cards.Where(card => waveId is not null ? card.WaveId == waveId
            : card.Ready.EnteredLaneAt >= from && card.Ready.EnteredLaneAt < to).ToArray();
        if (selected.Length == 0) throw new ArgumentException("No cards match the wave selection.");
        var at = from ?? selected.Min(card => card.Ready.EnteredLaneAt);
        var index = store.IndexAsOf(project, at) ?? throw new InvalidOperationException("No dated co-change index exists at the wave start.");
        var cards = selected.Select(card =>
        {
            if (card.CapturedAt > at) throw new ArgumentException($"{card.Ready.Key}: forecast text was captured after the wave start.");
            ValidateSha(card.BaseSha);
            ValidateSha(card.ResultSha);
            var forecast = TouchForecast.Compute(card.ForecastText, index, recording.HubFiles);
            var delivery = store.Read(project, card.Ready.Key)?.Deliveries.LastOrDefault(delivery => delivery.ResultSha == card.ResultSha);
            var diffBase = delivery?.MergeBase ?? card.BaseSha;
            ValidateSha(diffBase);
            var files = ReadGit(repository, "diff", "--name-only", "-z", diffBase, card.ResultSha, "--")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(TouchForecast.IsCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new DispatchReplayCard(card.Ready with { Forecast = ConvertForecast(forecast) }, files);
        }).ToArray();
        var records = store.ReadConflicts(project, to: at).Where(record => record.At < at).ToArray();
        var ledger = new DispatchLedgerSummary(records.SelectMany(record => TouchForecast.Modules(record.ConflictedFiles, recording.HubFiles)
                .Select(module => new DispatchHotModule(module, record.At))).ToArray(),
            records.SelectMany(record => record.PartnerCards.Select(partner => new DispatchCollidedPair(record.Card, partner))).ToArray(),
            recording.Cards.Where(card => card.Ready.Source is not null && card.Ready.EnteredLaneAt < at)
                .GroupBy(card => card.Ready.Source!.SourceKind, StringComparer.Ordinal)
                .Select(source =>
                {
                    var earlierWave = source.GroupBy(card => card.WaveId).MaxBy(group => group.Max(card => card.Ready.EnteredLaneAt))!;
                    var mean = earlierWave.Average(card => records.Where(record => record.Card == card.Ready.Key)
                        .SelectMany(record => record.PartnerCards).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    return new DispatchSourceHistory(source.Key, mean);
                }).ToArray());
        return new(project, waveId ?? $"{from:O}/{to:O}", at, cards, recording.HubFiles, ledger, index.Revision);
    }

    public static DispatchReplayWave ReadFixture(string path, string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "dispatch-replay-wave/1")
            throw new ArgumentException("Unsupported replay fixture schema.");
        string[] Strings(JsonElement element, string field) => element.GetProperty(field).EnumerateArray().Select(value => value.GetString()!).ToArray();
        var hubs = Strings(root, "hubFiles");
        // The source fixture records no historical lane metadata or size prior. Keep its stated assumptions explicit.
        var at = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        var cards = root.GetProperty("cards").EnumerateArray().Select((card, position) =>
        {
            var files = Strings(card, "forecastFiles");
            var forecast = new DispatchForecast(files, TouchForecast.Modules(files, hubs),
                Strings(card, "citedFiles").Length > 0 ? DispatchForecastConfidence.High : DispatchForecastConfidence.None);
            var group = card.GetProperty("group").GetString()!;
            var ready = new DispatchReadyCard(card.GetProperty("key").GetString()!, 999, at, [], true, forecast, [],
                Source: group == "X" ? null : new(root.GetProperty("id").GetString()! + ":" + group, "playthrough", position, group == "RV" ? 5 : 17));
            return new DispatchReplayCard(ready, Strings(card, "codeFiles"));
        }).ToArray();
        return new(project, root.GetProperty("id").GetString()!, at, cards, hubs, DispatchLedgerSummary.Empty,
            "recorded-fixture:co-change-2026-11-15")
        {
            Assumptions = ["The 17 November fixture supplies recorded forecasts, not a rebuilt dated index; lane entries are tied, pins and dependencies absent, the ledger starts cold, and source size priors are the Dossier stand-ins (17 for DP/IN, 5 for RV)."],
        };
    }

    private static DispatchForecast ConvertForecast(TouchForecastV1 forecast) => new(forecast.ForecastFiles,
        forecast.Modules, forecast.Confidence switch { "cited" => DispatchForecastConfidence.High,
            "module-only" => DispatchForecastConfidence.Low, _ => DispatchForecastConfidence.None });

    private static void ValidateSha(string sha)
    {
        if (sha.Length is not (40 or 64) || sha.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Recorded Git revisions must be full hexadecimal object ids.");
    }

    private static string ReadGit(string repository, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(start, timeout: TimeSpan.FromMinutes(2));
        return result.Success ? result.StandardOutput : throw new IOException($"Replay Git evidence is unavailable: {result.StandardError}");
    }
}
