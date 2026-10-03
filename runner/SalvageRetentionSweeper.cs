using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Runner-owned timer that applies <see cref="SalvageRetentionPolicy"/> to the
/// host salvage directory and this runner's salvage refs (AGT-2999), and keeps
/// the salvage store snapshot the capability advertisement reports.
///
/// <para>
/// Order per sweep: inventory tarballs and refs, resolve card facts from the
/// Task Server, decide, then delete. The active-run set is read again right
/// before deleting, so a run that started while facts were being collected
/// still protects its card. In <c>report</c> mode the sweep logs what it would
/// delete and deletes nothing.
/// </para>
/// </summary>
internal sealed class SalvageRetentionSweeper
{
    public const string ModeApply = "apply";
    public const string ModeReport = "report";
    public const string ModeOff = "off";

    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan InventoryInterval = TimeSpan.FromMinutes(15);
    internal const string StateFileName = "salvage-retention.json";

    private readonly string _salvageDir;
    private readonly string _mode;
    private readonly SalvageRetentionSettings _settings;
    private readonly TimeSpan _sweepInterval;
    private readonly string? _statePath;
    private readonly ISalvageCardDirectory _cards;
    private readonly ISalvageRefStore? _refs;
    private readonly Func<IReadOnlyCollection<string>> _activeCardKeys;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private SalvageSweepDto? _lastSweep;
    private SalvageStoreDto? _current;

    public SalvageRetentionSweeper(
        RunnerOptions options,
        ISalvageCardDirectory cards,
        ISalvageRefStore? refs,
        Func<IReadOnlyCollection<string>> activeCardKeys,
        Action<string> log,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _salvageDir = Path.GetFullPath(options.SalvageDir);
        _mode = options.SalvageRetentionMode;
        _settings = new SalvageRetentionSettings(options.SalvageRetentionDays, options.SalvageMaxPerCard);
        _sweepInterval = TimeSpan.FromHours(Math.Max(1, options.SalvageSweepHours));
        _statePath = string.IsNullOrWhiteSpace(options.StateDir)
            ? null
            : Path.Combine(options.StateDir, StateFileName);
        _cards = cards;
        _refs = refs;
        _activeCardKeys = activeCardKeys;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
        _lastSweep = LoadLastSweep();
    }

    /// <summary>Latest measured store with the last sweep; null before the first inventory.</summary>
    public SalvageStoreDto? Current => Volatile.Read(ref _current);

    public async Task RunAsync(CancellationToken shutdown)
    {
        _log(
            $"salvage-retention started mode={_mode} path={_salvageDir} retentionDays={_settings.RetentionDays} " +
            $"maxPerCard={_settings.MaxPerCard} sweepHours={_sweepInterval.TotalHours:0} refs={(_refs is null ? "off" : "on")}");
        RefreshInventory();
        var nextSweep = _utcNow() + InitialDelay;
        while (!shutdown.IsCancellationRequested)
        {
            var wait = nextSweep - _utcNow();
            if (wait > InventoryInterval) wait = InventoryInterval;
            if (wait > TimeSpan.Zero)
            {
                try { await _delay(wait, shutdown); }
                catch (OperationCanceledException) { return; }
            }
            if (_utcNow() >= nextSweep)
            {
                if (_mode != ModeOff)
                {
                    try
                    {
                        await SweepOnceAsync(shutdown);
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        var now = _utcNow();
                        RecordSweep(new SalvageSweepDto(
                            now, now, _mode, "failed", 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, OneLine(exception.Message)));
                        _log($"salvage-retention sweep failed; retrying next interval: {exception.Message}");
                    }
                }
                nextSweep = _utcNow() + _sweepInterval;
            }
            RefreshInventory();
        }
    }

    public SalvageStoreDto RefreshInventory()
    {
        SalvageStoreDto store;
        try
        {
            store = ToStore(SalvageTarballStore.Inventory(_salvageDir));
        }
        catch (Exception exception)
        {
            // Measurement is diagnostics only; it must never end the daemon's timer.
            _log($"salvage-retention inventory failed path={_salvageDir}: {exception.Message}");
            store = new SalvageStoreDto(_utcNow(), _salvageDir, Directory.Exists(_salvageDir), 0, 0, 0, 0,
                null, null, _mode, _settings.RetentionDays, _settings.MaxPerCard, _lastSweep);
        }
        Volatile.Write(ref _current, store);
        return store;
    }

    public async Task<SalvageSweepDto> SweepOnceAsync(CancellationToken ct)
    {
        var startedAt = _utcNow();
        var inventory = SalvageTarballStore.Inventory(_salvageDir);
        IReadOnlyList<SalvageRefRepository> repositories = _refs is null ? [] : await _refs.CollectAsync(ct);
        var failures = 0;
        foreach (var failed in repositories.Where(repository => repository.Error is not null))
        {
            failures++;
            _log($"salvage-retention refs-skipped repo={failed.RepoPath} error={OneLine(failed.Error!)}");
        }

        var refs = repositories.SelectMany(repository => repository.Refs).ToArray();
        var entries = inventory.Tarballs.Concat(refs).ToArray();
        var references = entries
            .Where(entry => entry.CardKey is not null)
            .Select(entry => new SalvageCardReference(entry.ProjectId, entry.CardKey!))
            .Distinct()
            .ToArray();
        var cards = references.Length == 0
            ? new Dictionary<string, SalvageCardFacts>()
            : await _cards.ResolveAsync(references, ct);
        var decisions = SalvageRetentionPolicy.Decide(
            entries,
            cards,
            ActiveCardKeys(),
            _settings,
            _utcNow());

        // Re-read immediately before deleting: a run that started after the
        // first read must still protect every entry of its card.
        var active = ActiveCardKeys();
        var protectedByActiveRun = decisions.Count(decision => decision.Reason == SalvageRetentionReason.KeepActiveRun);
        var apply = _mode == ModeApply;
        var tarballsEligible = 0;
        long tarballBytesEligible = 0;
        var tarballsDeleted = 0;
        long tarballBytesDeleted = 0;
        var refsEligible = 0;
        var refsDeleted = 0;

        foreach (var decision in decisions.Where(item => item.Delete && item.Entry.Kind == SalvageEntryKind.Tarball))
        {
            var entry = decision.Entry;
            if (active.Contains(entry.CardKey!))
            {
                protectedByActiveRun++;
                continue;
            }
            tarballsEligible++;
            tarballBytesEligible += entry.SizeBytes;
            if (!apply)
            {
                _log($"salvage-retention would-delete kind=tarball entry={entry.Id} card={entry.CardKey} bytes={entry.SizeBytes} reason={SalvageRetentionPolicy.Slug(decision.Reason)}");
                continue;
            }
            if (SalvageTarballStore.TryDelete(_salvageDir, entry, out var error))
            {
                tarballsDeleted++;
                tarballBytesDeleted += entry.SizeBytes;
                _log($"salvage-retention deleted kind=tarball entry={entry.Id} card={entry.CardKey} bytes={entry.SizeBytes} reason={SalvageRetentionPolicy.Slug(decision.Reason)}");
            }
            else
            {
                failures++;
                _log($"salvage-retention delete-failed kind=tarball entry={entry.Id} card={entry.CardKey} error={OneLine(error ?? "unknown")}");
            }
        }

        foreach (var repository in repositories.Where(repository => repository.Error is null))
        {
            var selected = new List<SalvageRetentionDecision>();
            foreach (var decision in decisions.Where(item =>
                         item.Delete
                         && item.Entry.Kind == SalvageEntryKind.GitRef
                         && repository.Refs.Contains(item.Entry)))
            {
                if (active.Contains(decision.Entry.CardKey!))
                {
                    protectedByActiveRun++;
                    continue;
                }
                refsEligible++;
                selected.Add(decision);
                if (!apply)
                    _log($"salvage-retention would-delete kind=ref ref={decision.Entry.Id} card={decision.Entry.CardKey} sha={decision.Entry.Sha} integration={repository.IntegrationBranch} reason={SalvageRetentionPolicy.Slug(decision.Reason)}");
            }
            if (!apply || selected.Count == 0 || _refs is null) continue;

            var results = await _refs.DeleteAsync(repository, selected.Select(item => item.Entry).ToArray(), ct);
            foreach (var decision in selected)
            {
                var entry = decision.Entry;
                if (results.TryGetValue(entry.Id, out var error) && error is null)
                {
                    refsDeleted++;
                    _log($"salvage-retention deleted kind=ref ref={entry.Id} card={entry.CardKey} sha={entry.Sha} integration={repository.IntegrationBranch} reason={SalvageRetentionPolicy.Slug(decision.Reason)}");
                }
                else
                {
                    failures++;
                    _log($"salvage-retention delete-failed kind=ref ref={entry.Id} card={entry.CardKey} error={OneLine(error ?? "no result")}");
                }
            }
        }

        var sweep = new SalvageSweepDto(
            startedAt,
            _utcNow(),
            _mode,
            failures == 0 ? "completed" : "completed-with-failures",
            inventory.Tarballs.Count,
            tarballsEligible,
            tarballBytesEligible,
            tarballsDeleted,
            tarballBytesDeleted,
            refs.Length,
            refsEligible,
            refsDeleted,
            protectedByActiveRun,
            failures);
        var kept = decisions
            .Where(decision => !decision.Delete)
            .GroupBy(decision => SalvageRetentionPolicy.Slug(decision.Reason))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}");
        _log(
            $"salvage-retention sweep mode={sweep.Mode} status={sweep.Status} " +
            $"tarballs={sweep.TarballsInspected} tarballsEligible={sweep.TarballsEligible} " +
            $"tarballsDeleted={sweep.TarballsDeleted} bytesDeleted={sweep.TarballBytesDeleted} " +
            $"bytesEligible={sweep.TarballBytesEligible} refs={sweep.RefsInspected} " +
            $"refsEligible={sweep.RefsEligible} refsDeleted={sweep.RefsDeleted} " +
            $"protectedByActiveRun={sweep.ProtectedByActiveRun} failures={sweep.Failures} " +
            $"kept=[{string.Join(',', kept)}]");
        RecordSweep(sweep);
        return sweep;
    }

    private IReadOnlySet<string> ActiveCardKeys()
        => _activeCardKeys()
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(SalvageRetentionPolicy.NormalizeCardKey)
            .ToHashSet(StringComparer.Ordinal);

    private SalvageStoreDto ToStore(SalvageTarballInventory inventory)
        => new(
            _utcNow(),
            inventory.Root,
            inventory.Exists,
            inventory.SizeBytes,
            inventory.EntryCount,
            inventory.Tarballs.Count,
            inventory.UnrecognizedCount,
            inventory.OldestEntry,
            inventory.OldestEntryAt,
            _mode,
            _settings.RetentionDays,
            _settings.MaxPerCard,
            _lastSweep);

    private void RecordSweep(SalvageSweepDto sweep)
    {
        _lastSweep = sweep;
        if (_statePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(sweep));
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"salvage-retention state write failed path={_statePath}: {exception.Message}");
        }
    }

    private SalvageSweepDto? LoadLastSweep()
    {
        if (_statePath is null || !File.Exists(_statePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<SalvageSweepDto>(File.ReadAllText(_statePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string OneLine(string value)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
