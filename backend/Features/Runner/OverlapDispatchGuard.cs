using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Operator;

namespace AgentStudio.Runner;

public sealed record OverlapPrediction(string Id, string Project, string Candidate, string Other,
    string CandidateId, string OtherId, string CandidateFolder, string Target, string Path,
    DateTime ObservedAtUtc, string? AttemptId = null, long ReadyGeneration = 0, string Window = "off");
public sealed record OverlapPredictionScore(int Recorded, int Correct, int Incorrect, int Unknown);

/// <summary>
/// Application-owned prediction evidence, separate from card files. Unobserved
/// integrations are censored; an absence from the conflict ledger alone is no success.
/// </summary>
public sealed class OverlapPredictionLedger(string? root = null)
{
    private readonly string _root = root ?? LocalStateRoot.Combine("agentstudio", "overlap-predictions");
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Folder(string project) => Path.Combine(_root, IntegrationConflictLedger.Hash(project.ToUpperInvariant()));

    public bool Record(OverlapPrediction row)
    {
        lock (_gate)
        {
            var folder = Folder(row.Project);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, row.Id + ".json");
            if (File.Exists(path)) return false;
            Write(path, row);
            return true;
        }
    }

    private static void Write(string path, OverlapPrediction row)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(row, Json));
        File.Move(temporary, path, overwrite: true);
    }

    public IReadOnlyList<OverlapPrediction> Read(string project)
    {
        lock (_gate)
            return !Directory.Exists(Folder(project)) ? [] : Directory.EnumerateFiles(Folder(project), "*.json")
                .Select(path => JsonSerializer.Deserialize<OverlapPrediction>(File.ReadAllText(path), Json)!)
                .Where(row => row.Project.Equals(project, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public void BindAttempt(string project, string candidateId, string attemptId, long readyGeneration)
    {
        lock (_gate)
            foreach (var row in Read(project).Where(row => row.CandidateId == candidateId && row.AttemptId is null && row.ReadyGeneration == readyGeneration))
                Write(Path.Combine(Folder(project), row.Id + ".json"), row with { AttemptId = attemptId });
    }

    public static OverlapPredictionScore Score(IReadOnlyList<OverlapPrediction> predictions,
        IReadOnlyList<IntegrationConflictEntry> conflicts,
        Func<OverlapPrediction, (string? Sha, bool Integrated)> outcome)
    {
        var correct = 0;
        var incorrect = 0;
        foreach (var prediction in predictions)
        {
            var delivery = outcome(prediction);
            if (delivery.Sha is null) continue;
            var rows = conflicts.Where(row => row.Project.Equals(prediction.Project, StringComparison.OrdinalIgnoreCase)
                && row.IntegrationTarget == prediction.Target && row.DeliverySha == delivery.Sha
                && (row.CardKey == prediction.Candidate || row.CardKey == prediction.CandidateId)
                && row.DetectedAtUtc.UtcDateTime >= prediction.ObservedAtUtc).ToArray();
            if (rows.Any(row => row.Files.Any(file => file.CardKeys.Concat(file.Commits.SelectMany(commit => commit.CardKeys))
                    .Any(key => key == prediction.Other || key == prediction.OtherId)))) correct++;
            else if (delivery.Integrated && !rows.Any(row => row.Files.Count == 0 || row.Files.Any(file =>
                !file.CardKeys.Concat(file.Commits.SelectMany(commit => commit.CardKeys))
                    .Any(key => key != prediction.Candidate && key != prediction.CandidateId && key != row.CardKey))))
                incorrect++;
        }
        return new(predictions.Count, correct, incorrect, predictions.Count - correct - incorrect);
    }
}

/// <summary>
/// Shared overlap admission cache and wait episodes. Hosts still own capacity,
/// routing and leases. The cache depends on scope and delivery state, never on poll time.
/// </summary>
public sealed class OverlapDispatchGuard(TimeProvider time, ILogger<OverlapDispatchGuard> logger,
    OverlapPredictionLedger evidence)
{
    private sealed record Cached(string Fingerprint, SlotAdmission Admission, DateTime Since, DateTime LoggedAt,
        TaskInfo Card, string Target);
    private readonly object _gate = new();
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _starved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Fingerprint, DateTime Until)> _overrides = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

    public SlotAdmission Decide(TaskInfo card, IReadOnlyList<RunningTask> running,
        IReadOnlyList<InFlightTask> inFlight, int max, string window, string path, string target)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var scope = TaskScopePrediction.Predict(card) with { BypassScope = OverlapComparisonSet.IsDeliveryRecovery(card) };
        var fingerprint = Hash(string.Join('|', window, max, card.State, card.EnteredLaneAt.Ticks,
            card.PendingIntent?.SavedAt.Ticks, card.PendingIntent?.SavedReason, scope.BypassScope,
            string.Join(',', scope.PredictedScope),
            string.Join(';', running.Select(row => $"{row.TaskId}:{row.Version}:{row.Parallelism.Exclusive}:{string.Join(',', row.Parallelism.PredictedScope)}")),
            string.Join(';', inFlight.Select(row => $"{row.TaskId}:{row.Version}:{row.Lane}:{string.Join(',', row.Parallelism.PredictedScope)}"))));
        var key = Key(card);
        lock (_gate)
        {
            var old = _cache.GetValueOrDefault(key);
            var admission = old?.Fingerprint == fingerprint ? old.Admission
                : ParallelSlotPolicy.Decide(card.Id, scope, running, max, inFlight, window);
            var policyAdmission = admission;
            var sameWait = old is not null && !old.Admission.Admitted && !admission.Admitted
                && admission.IsScopeConflict && old.Admission.ConflictTaskId == admission.ConflictTaskId
                && old.Admission.SharedPrefix == admission.SharedPrefix && old.Admission.Window == admission.Window
                && old.Card.EnteredLaneAt == card.EnteredLaneAt;
            var since = sameWait || old?.Fingerprint == fingerprint ? old!.Since : now;
            var logged = old?.Fingerprint == fingerprint ? old.LoggedAt : DateTime.MinValue;
            if (_overrides.TryGetValue(key, out var permit)
                && permit.Fingerprint == fingerprint && permit.Until > now && admission.IsScopeConflict)
                admission = admission with { Decision = SlotDecision.Admit, Reason = "parallel-ok: operator chose Run anyway" };
            else if (_overrides.ContainsKey(key)) _overrides.Remove(key);

            // Record every predicted pair, including advisory pairs and recovery bypasses.
            if (old?.Fingerprint != fingerprint)
            foreach (var other in running.Select(row => new InFlightTask(row.TaskId, row.Parallelism, "running")
                { CardKey = row.CardKey, Version = row.Version })
                .Concat(inFlight).Where(row => !row.TaskId.Equals(card.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var prefix = ParallelSlotPolicy.FirstScopeConflict(scope.PredictedScope, other.Parallelism.PredictedScope);
                if (prefix is null) continue;
                var id = Hash($"{key}|{card.EnteredLaneAt.Ticks}|{other.TaskId}|{target}");
                var recorded = false;
                try
                {
                    recorded = evidence.Record(new(id, card.ProjectName, card.Key ?? card.TaskKey ?? card.Id,
                        other.CardKey ?? other.TaskId, card.Id, other.TaskId, card.FolderPath, target, prefix, now,
                        ReadyGeneration: card.EnteredLaneAt.Ticks, Window: window));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "overlap-prediction-write-failed project={Project} candidate={Candidate}", card.ProjectName, card.Id); }
                if (recorded && (window == OverlapWindows.Off || scope.BypassScope))
                    logger.LogInformation("overlap-advisory path={Path} project={Project} candidate={Candidate} conflict={Conflict} prefix={Prefix}",
                        path, card.ProjectName, card.Id, other.TaskId, prefix);
            }
            if (!admission.Admitted && admission.IsScopeConflict && now - logged >= ReminderInterval)
            {
                logger.LogInformation("[taskboard] overlap-serialize path={Path} window={Window} project={Project} candidate={Candidate} conflict={Conflict} lane={Lane} prefix={Prefix}",
                    path, window, card.ProjectName, card.Id, admission.ConflictTaskId, admission.ConflictLane, admission.SharedPrefix);
                logged = now;
            }
            // Store the policy decision, not the temporary operator permit.
            _cache[key] = new(fingerprint, policyAdmission, since, logged, card, target);
            Prune(now);
            return admission;
        }
    }

    public void FinishSweep(string project, bool scopeOnlyStarved)
    {
        lock (_gate)
            if (scopeOnlyStarved) _starved[project] = time.GetUtcNow().UtcDateTime;
            else _starved.Remove(project);
    }

    public void Claimed(TaskInfo card, string? attemptId = null)
    {
        lock (_gate)
        {
            _overrides.Remove(Key(card));
            _cache.Remove(Key(card));
            _starved.Remove(card.ProjectName);
        }
        if (attemptId is not null)
            try { evidence.BindAttempt(card.ProjectName, card.Id, attemptId, card.EnteredLaneAt.Ticks); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { logger.LogWarning(ex, "overlap-prediction-bind-failed candidate={Candidate}", card.Id); }
    }

    public IReadOnlyList<OperatorDecisionFact> Facts(IEnumerable<TaskInfo> tasks,
        Func<string, ProjectSettings> settings, DateTime now)
    {
        var ready = tasks.Where(card => card.State == TaskStates.Ready).ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            return _cache.Where(pair => ready.ContainsKey(pair.Key) && !pair.Value.Admission.Admitted
                && pair.Value.Admission.IsScopeConflict
                && OverlapWindows.Resolve(settings(pair.Value.Card.ProjectName)) == pair.Value.Admission.Window
                && OverlapComparisonSet.DeliveryVersion(ready[pair.Key]) == OverlapComparisonSet.DeliveryVersion(pair.Value.Card)
                && _starved.TryGetValue(pair.Value.Card.ProjectName, out var checkedAt) && now - checkedAt < TimeSpan.FromMinutes(1)
                && now - pair.Value.Since >= TimeSpan.FromMinutes(Math.Clamp(settings(pair.Value.Card.ProjectName).DispatchOverlapStarvationMinutes, 1, 1440))
                && !_overrides.ContainsKey(pair.Key))
                .Select(pair => new OperatorDecisionFact(OperatorDecisionTypes.OverlapStarved,
                    $"{OperatorDecisionTypes.OverlapStarved}|{pair.Key}|{pair.Value.Fingerprint}",
                    pair.Value.Card.ProjectName, pair.Value.Card.Key ?? pair.Value.Card.Id,
                    $"{pair.Value.Card.Key ?? pair.Value.Card.Id} waits for {pair.Value.Admission.ConflictTaskId} on {pair.Value.Admission.SharedPrefix}. "
                        + "Only scope overlap prevents dispatch while slots are free. Run anyway permits this candidate once for this comparison state (expires in 10 minutes).",
                    pair.Value.Since, [], [pair.Value.Card.Key ?? pair.Value.Card.Id, pair.Value.Admission.ConflictTaskId!],
                    Title: "Scope overlap is starving ready work")).ToArray();
    }

    public bool RunAnyway(string project, string fact)
    {
        lock (_gate)
        {
            var pair = _cache.FirstOrDefault(pair => pair.Value.Card.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)
                && fact == $"{OperatorDecisionTypes.OverlapStarved}|{pair.Key}|{pair.Value.Fingerprint}");
            if (pair.Value is null || !pair.Value.Admission.IsScopeConflict || pair.Value.Admission.Admitted
                || !_starved.ContainsKey(project)) return false;
            _overrides[pair.Key] = (pair.Value.Fingerprint, time.GetUtcNow().UtcDateTime.AddMinutes(10));
            logger.LogWarning("overlap-run-anyway project={Project} candidate={Candidate} conflict={Conflict}",
                project, pair.Value.Card.Id, pair.Value.Admission.ConflictTaskId);
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _cache.Where(pair => now - pair.Value.Since > TimeSpan.FromDays(7)).Select(pair => pair.Key).ToArray())
            _cache.Remove(key);
    }

    private static string Key(TaskInfo card) => card.ProjectName + "/" + card.Id;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
