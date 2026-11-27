using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public sealed partial class PostMergeIncidentService
{
    private const string SystemPrincipal = "system:post-merge";
    private const string LiftCondition = "Two consecutive covering passes in the standard envelope on tips containing the repair integration, or an owner lift.";
    private readonly TestQuarantineStore _quarantines;
    private readonly AgentStudio.Personas.LineGuardianStore _guardian;
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _guardianBus;
    private readonly AgentStudio.Git.GitService? _git;
    private readonly ModelRoutingPolicyRegistry _routing;
    private readonly IModelRoutingModeProvider? _routingMode;
    private readonly Func<ProjectRecord, string, string, bool>? _containsRepair;

    public void Apply(ProjectRecord project, PostMergeRunRecord run)
    {
        if (run.Status is not ("red" or "green" or "inconclusive"))
        {
            RecordGuardianReceipt(project, run);
            return;
        }
        lock (_gate)
        {
            if (_quarantines.Read(project.Id).IncidentLog.Any(log => log.RunId == run.Id)
                && (run.TestVerdicts?.Count ?? 0) <= _quarantines.Read(project.Id).IncidentLog.Count(log => log.RunId == run.Id)) return;
            if (run.TestVerdicts is not null || run.Status == "green"
                && (_store.Read(project.Id).Incidents ?? []).Any(item => item.TestId is not null && item.ResolvedAtUtc is null))
            {
                ApplyTests(project, run);
                return;
            }
            RecordGuardianReceipt(project, run);
            ApplyLegacy(project, run);
            foreach (var closed in (_store.Read(project.Id).Incidents ?? []).Where(i => i.ResolvedAtUtc is not null))
                GuardianClosed(project, GuardianIncidentKey(closed.TestId ?? "legacy", closed.Fingerprint, closed.FirstRedAtUtc));
            var reason = run.Status == "red" ? "Legacy red receipt without per-test confirmation evidence." : null;
            _store.SetDevelopDisposition(project.Id, run.Status, reason);
            _quarantines.Log(project.Id, new(run.Id + ":legacy", nameof(RedDevelopRepairPolicy), "compatibility.v1",
                Digest(JsonSerializer.Serialize(run)), "Legacy", null, [run.Status == "red" ? "LineStop" : "Close"],
                [], LiftCondition, reason ?? "Green legacy receipt.", run.Id, run.TipSha, "unknown",
                run.Failure?.Fingerprint ?? FallbackFingerprint(run), SystemPrincipal, _clock.GetUtcNow()));
        }
    }

    public AgentStudio.Personas.GuardianOwnerItem? RequestGuardianOperation(ProjectRecord project,
        string runId, string testId, string operation)
    {
        lock (_gate)
        {
            var state = _store.Read(project.Id);
            var run = state.Runs.FirstOrDefault(r => r.Id == runId)
                ?? throw new InvalidOperationException("A persisted receipt is required.");
            var test = run.TestVerdicts?.FirstOrDefault(t => t.Test == testId)
                ?? throw new InvalidOperationException("The test is absent from the receipt.");
            // Diagnostics can change while triage retains the incident's identity.
            // Read that receipt's journal association, including older receipts.
            var fingerprint = _quarantines.Read(project.Id).IncidentLog
                .LastOrDefault(log => log.RunId == run.Id && log.Test == testId)?.IncidentFingerprint
                ?? state.Incidents?.LastOrDefault(item => item.TestId == testId
                    && item.LatestEvidenceRunId == run.Id)?.Fingerprint
                ?? Fingerprint(run, test);
            var incident = state.Incidents?.LastOrDefault(i => i.TestId == testId && i.Fingerprint == fingerprint);
            var facts = Facts(project, run, test, fingerprint, incident);
            return _guardian.Request(project.Id, GuardianIncidentKey(testId, fingerprint, incident?.FirstRedAtUtc ?? run.FinishedAtUtc ?? run.StartedAtUtc),
                new(operation, facts, incident is not null, facts.ActiveQuarantine?.Kind == RedTestQuarantineKind.Held,
                    Now: _clock.GetUtcNow()));
        }
    }

    private void RecordGuardianReceipt(ProjectRecord project, PostMergeRunRecord run)
    {
        var facts = new RedTestTriageFacts("receipt", "legacy", RedTestCategory.Routine)
        { FirstResult = RedTestResult.Unknown, IncidentFingerprint = run.Failure?.Fingerprint ?? run.Id };
        var system = new RedTestTriageDecision(RedTestClassification.Inconclusive, null, []);
        _guardian.Evaluate(project.Id, run, facts, system, _clock.GetUtcNow(), run.Id + ":receipt:" + run.Status);
    }

    private static string GuardianIncidentKey(string test, string fingerprint, DateTimeOffset firstRed)
        => test + ":" + fingerprint + ":" + firstRed.ToString("O");

    private void GuardianClosed(ProjectRecord project, string incident)
    {
        if (_guardian.Close(project.Id, incident))
            _guardianBus?.EmitLineGuardianAsync(project.Id, incident, "closed", "Red-test incident closed.")
                .GetAwaiter().GetResult();
    }

    public void CloseLifted(ProjectRecord project, ProjectTestQuarantine entry)
    {
        if (entry.LiftedAtUtc is null) return;
        var incident = _store.Read(project.Id).Incidents?.LastOrDefault(i => i.TestId == entry.Test
            && i.Fingerprint == entry.IncidentFingerprint && i.ResolvedAtUtc is null);
        GuardianClosed(project, GuardianIncidentKey(entry.Test, entry.IncidentFingerprint, incident?.FirstRedAtUtc ?? entry.StartedAtUtc));
        _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == entry.IncidentFingerprint
            && item.TestId == entry.Test && item.ResolvedAtUtc is null
                ? item with { ResolvedAtUtc = entry.LiftedAtUtc } : item).ToArray());
    }

    public IReadOnlyList<ProjectTestQuarantine> ActiveQuarantines(ProjectRecord project)
    {
        var repository = Repository(project);
        var active = _quarantines.Active(project.Id, repository, _clock.GetUtcNow());
        if (!active.Any(entry => entry.Kind == "held")) return active;
        var state = _store.Read(project.Id);
        return active.Select(entry => entry.Kind != "held" ? entry : entry with
        {
            TestFile = entry.TestFile ?? state.Runs.SelectMany(run => run.TestVerdicts ?? [])
                .LastOrDefault(test => test.Test == entry.Test)?.TestFile,
            SuiteAreas = entry.SuiteAreas.Count > 0 ? entry.SuiteAreas : [""],
        }).ToArray();
    }

    /// <summary>Collect graph facts on the mutation path; state reads never invoke Git.</summary>
    public ProjectTestQuarantine PrepareQuarantine(ProjectRecord project, ProjectTestQuarantine entry)
    {
        if (entry.Kind != "held") return entry;
        var repository = Repository(project);
        if (repository is null) return entry with { SuiteAreas = [""] };
        var state = _store.Read(project.Id);
        var map = GeneratedTestProjectMap.Generate(repository, VerifyCommandPlanner.Plan(repository, null));
        var command = state.Runs.SelectMany(run => run.Verdicts)
            .LastOrDefault(verdict => entry.SuiteId is not null && verdict.SuiteId == entry.SuiteId)?.Command
            ?? ProjectDefinitionReader.ReadWorkspace(repository).Definition?.TestSuites
                .FirstOrDefault(suite => suite.Id == entry.SuiteId)?.Command;
        var suites = map.SuiteCommands.Where(pair => pair.Key == entry.SuiteId || pair.Value == command)
            .Select(pair => pair.Key).Concat(map.ProjectFiles.Where(path =>
                command?.Replace('\\', '/').Contains(path, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        var areas = map.Rows.Where(row => row.TestProjects.Intersect(suites, StringComparer.Ordinal).Any())
            .Select(row => row.Folder.TrimEnd('/')).ToArray();
        return entry with { SuiteAreas = areas.Length > 0 ? areas : [""] };
    }

    public IReadOnlyList<TestQuarantineHoldFact> ActiveHolds(ProjectRecord project)
        => MonolithTransitionAdapter.ActiveQuarantineHolds(ActiveQuarantines(project), _clock.GetUtcNow());

    private string? Repository(ProjectRecord project)
        => AgentStudio.Docs.ProjectRepoResolver.Resolve(project,
            _scanner.GetWatchPaths().FirstOrDefault(entry => string.Equals(entry.Path, project.StorageLocation,
                StringComparison.OrdinalIgnoreCase)));

    private void ApplyTests(ProjectRecord project, PostMergeRunRecord run, bool overdue = false)
    {
        var state = _store.Read(project.Id);
        if (run.Status == "green" && !overdue) CloseOnGreen(project, state, run);
        var tests = (run.TestVerdicts ?? []).ToList();
        // Green receipts carry only previously failed tests. Include unresolved entries
        // so selected-suite passes can prove a lift without inventing coverage.
        foreach (var incident in (state.Incidents ?? []).Where(item => item.TestId is not null && item.ResolvedAtUtc is null))
            if (tests.All(test => test.Test != incident.TestId))
                tests.Add(new(incident.TestId!, incident.SuiteId ?? "", incident.SuiteCategory ?? "routine", [], "inconclusive"));
        if (tests.Count == 0) RecordGuardianReceipt(project, run);
        var logs = _quarantines.Read(project.Id).IncidentLog;
        var decisions = new List<RedTestTriageDecision>();
        foreach (var test in tests)
        {
            var fingerprint = Fingerprint(run, test);
            var incident = (state.Incidents ?? []).LastOrDefault(item => item.TestId == test.Test
                && item.ResolvedAtUtc is null);
            // RT incidents are per test, not per changing log hash. Keep quarantine identity and clock.
            if (incident is not null) fingerprint = incident.Fingerprint;
            if (test.Attempts.Count == 0)
            {
                incident = (state.Incidents ?? []).LastOrDefault(item => item.TestId == test.Test && item.ResolvedAtUtc is null);
                if (incident is null) continue;
                fingerprint = incident.Fingerprint;
            }
            // Queue recovery must also run when this receipt was already journaled.
            if (incident is not null) ReadyQueuedRepair(project, incident.RepairCardId);
            var key = run.Id + ":" + test.Test + ":" + fingerprint
                + (overdue ? ":ladder:" + (int)(_clock.GetUtcNow() - (incident?.FirstRedAtUtc ?? _clock.GetUtcNow())).TotalHours : "");
            var prior = logs.FirstOrDefault(log => log.DecisionId == key);
            if (prior is not null) continue;
            var facts = Facts(project, run, test, fingerprint, incident);
            var decision = MonolithTransitionAdapter.RedTestTriage(facts);
            var comparison = _guardian.Evaluate(project.Id, run, facts, decision, _clock.GetUtcNow(), key,
                state.Runs.LastOrDefault(previous => previous.Id != run.Id && previous.Status == "green")?.Envelope,
                GuardianIncidentKey(test.Test, fingerprint, incident?.FirstRedAtUtc ?? run.FinishedAtUtc ?? run.StartedAtUtc));
            if (!comparison.Shadow) decision = comparison.Guardian;
            var principal = comparison.Shadow ? SystemPrincipal : AgentStudio.Personas.LineGuardianStore.Principal(project.Id);
            decisions.Add(decision);
            var actions = decision.Decisions;
            var needsCard = actions.Any(action => action.Action is RedTestAction.FixForward or RedTestAction.ProposeRevert
                or RedTestAction.StartHeld or RedTestAction.StartReportOnly or RedTestAction.LineStop or RedTestAction.AskOwner);
            if (needsCard && incident is null)
            {
                var card = RepairCard(project, run, test, fingerprint, facts.SinceLastLift is not null,
                    actions.Any(action => action.Action == RedTestAction.ProposeRevert), principal);
                incident = new(fingerprint, card, actions.Any(action => action.Action == RedTestAction.ProposeRevert)
                    ? "Revert" : "FixForward", run.FinishedAtUtc ?? _clock.GetUtcNow(), null,
                    decision.AttributedMerge, [test.Test], test.CoveringFolders ?? [])
                { TestId = test.Test, SuiteId = test.SuiteId, SuiteCategory = test.SuiteCategory, ModelRung = nameof(RedTestModelRung.Medium), LatestEvidenceRunId = run.Id };
                _store.UpdateIncidents(project.Id, items => [.. items, incident]);
            }
            if (incident is not null && incident.LatestEvidenceRunId != run.Id && test.Attempts.Count > 0)
            {
                if (!_mutations.AppendContinuationNote(incident.RepairCardId,
                        Prompt(project, run, state.LastGreenSha, run.Failure,
                            incident.Action == "Revert" ? RedDevelopRepairAction.Revert : RedDevelopRepairAction.FixForward,
                            fingerprint), project.StorageLocation))
                    throw new InvalidOperationException("The red-test repair could not receive updated evidence.");
                _store.UpdateIncidents(project.Id, items => items.Select(item => item.TestId == incident.TestId
                    && item.Fingerprint == fingerprint && item.ResolvedAtUtc is null
                        ? item with { LatestEvidenceRunId = run.Id } : item).ToArray());
            }
            foreach (var action in actions)
                ApplyAction(project, run, test, fingerprint, incident, facts, action, principal);
            if (incident is not null && facts.RepairCardLane is not null
                && actions.FirstOrDefault(action => action.ModelRung is not null)?.ModelRung is { } modelRung
                && incident.ModelRung != modelRung.ToString())
                SetRepairRung(project, incident.RepairCardId, modelRung);
            var reason = string.Join(" ", actions.Select(action => action.Reason));
            _quarantines.Log(project.Id, new(key, nameof(RedTestTriagePolicy), "rt-4.v1",
                Digest(JsonSerializer.Serialize(new { run.Id, run.TipSha, Facts = facts })),
                decision.Classification.ToString(), null, actions.Select(action => action.Action.ToString()).ToArray(),
                [], LiftCondition, reason.Length <= 280 ? reason : reason[..280], run.Id, run.TipSha, test.Test,
                fingerprint, principal, _clock.GetUtcNow()));
            state = _store.Read(project.Id);
            foreach (var closed in (state.Incidents ?? []).Where(i => i.TestId == test.Test && i.ResolvedAtUtc is not null))
            {
                CloseUnusedRepair(project, closed.RepairCardId);
                GuardianClosed(project, GuardianIncidentKey(closed.TestId!, closed.Fingerprint, closed.FirstRedAtUtc));
            }
        }
        if (overdue) return;
        // A failed prepare/build has no per-test ledger. Keep the compatibility
        // repair path until producers supply an explicit twice-confirmed receipt.
        var current = _store.Read(project.Id);
        var openStops = (current.Incidents ?? []).Where(item => item.ResolvedAtUtc is null && item.LineStopReason is not null).ToArray();
        var lineStop = openStops.FirstOrDefault()?.LineStopReason;
        var inconclusive = decisions.Any(decision => decision.Decisions.Any(action => action.Action == RedTestAction.MarkInconclusive));
        _store.SetDevelopDisposition(project.Id, MonolithTransitionAdapter.QuarantineDevelopState(lineStop), lineStop,
            inconclusive ? run.Id : null);
    }

    private RedTestTriageFacts Facts(ProjectRecord project, PostMergeRunRecord run, PostMergeTestVerdict test,
        string fingerprint, PostMergeIncident? incident)
    {
        var now = _clock.GetUtcNow();
        var state = _store.Read(project.Id);
        var entries = _quarantines.Read(project.Id).Entries;
        var entry = entries.LastOrDefault(item => item.Test == test.Test && item.IncidentFingerprint == fingerprint
            && item.LiftedAtUtc is null);
        var lastLift = entries.Where(item => item.Test == test.Test && item.LiftedAtUtc <= now)
            .MaxBy(item => item.LiftedAtUtc)?.LiftedAtUtc;
        var controls = test.Attempts.Where(attempt => attempt.Phase == PostMergeRunPhases.Control)
            .Select(attempt => Result(attempt.Result)).Take(2).ToArray();
        var points = (run.Failure?.Attribution?.Points ?? []).Where(point => point.ParentResults?.Count == 2)
            .Select(point => new RedTestBisectPoint(point.Sha, Pair(point.Results), Pair(point.ParentResults!))).ToArray();
        var lane = incident is null ? null : _scanner.FindJob(incident.RepairCardId, project.StorageLocation)?.State;
        return new(test.Test, test.SuiteId, test.SuiteCategory == "base" ? RedTestCategory.Base : RedTestCategory.Routine)
        {
            IncidentFingerprint = fingerprint,
            FirstResult = test.Attempts.Count == 0 && run.Status == "green" ? RedTestResult.Pass
                : Result(test.Attempts.FirstOrDefault(attempt => attempt.Phase == PostMergeRunPhases.Initial
                    && attempt.Sha == run.TipSha)?.Result),
            SameShaRepeat = Result(test.Attempts.LastOrDefault(attempt => attempt.Phase == PostMergeRunPhases.Repeat
                && attempt.Sha == run.TipSha)?.Result),
            // RT-6 supplies measured canary/reference facts. Older receipts remain
            // compatible unless an explicit negative fact contradicts their envelope.
            EnvelopeCanary = run.EnvelopeCanaryPassed == false ? RedTestResult.Fail : RedTestResult.Pass,
            FingerprintMatchesReference = EnvelopeMatches(run, state.Runs.FirstOrDefault(previous =>
                previous.Id == entry?.EvidenceRunIds.FirstOrDefault())
                ?? state.Runs.LastOrDefault(previous => previous.Id != run.Id && previous.Status == "green")),
            ReferenceEnvelopeRepeat = Result(test.Attempts.LastOrDefault(attempt => attempt.Phase == PostMergeRunPhases.Reference)?.Result),
            LastGreenControlResults = controls,
            BisectPoints = points,
            ProjectQuarantines = ActiveQuarantines(project).Select(item => new RedTestQuarantine(item.Test,
                item.IncidentFingerprint, item.Kind == "held" ? RedTestQuarantineKind.Held : RedTestQuarantineKind.ReportOnly)).ToArray(),
            ActiveQuarantine = entry is null ? null : new(entry.Kind == "held" ? RedTestQuarantineKind.Held
                : RedTestQuarantineKind.ReportOnly, now - entry.StartedAtUtc, entry.Renewals),
            DistinctFlakyRuns14Days = (state.FlakyHistory ?? []).Where(item => item.Test == test.Test
                && item.AtUtc >= now.AddDays(-14) && item.AtUtc <= now).Select(item => item.RunId)
                .Concat(_quarantines.Read(project.Id).IncidentLog.Where(log => log.Test == test.Test
                    && log.Class == nameof(RedTestClassification.Flaky) && log.AtUtc >= now.AddDays(-14)
                    && log.AtUtc <= now).Select(log => log.RunId)).Append(run.Id).Distinct().Count(),
            EnvironmentOccurrences = _quarantines.Read(project.Id).IncidentLog.Where(log => log.Test == test.Test
                && log.Class == nameof(RedTestClassification.Environment)).Select(log => log.RunId)
                .Append(run.Id).Distinct().Count(),
            SinceLastLift = lastLift is null ? null : now - lastLift,
            RepairCardLane = lane,
            IncidentAge = incident is null ? TimeSpan.Zero : now - incident.FirstRedAtUtc,
            LineStopAge = incident?.LineStopReason is null ? TimeSpan.Zero : now - incident.FirstRedAtUtc,
            LineStopActive = incident?.LineStopReason is not null,
            RevertAppliesCleanly = run.Failure?.RevertAppliesCleanly == true,
            LaterMergeCoupled = run.Failure?.LaterMergeDependsOnSuspect == true,
            AreaHasNewFailure = state.Runs.Where(candidate => candidate.FinishedAtUtc > incident?.FirstRedAtUtc)
                .Any(candidate => (candidate.TestVerdicts ?? []).Any(other =>
                    (other.Test == test.Test || (other.CoveringFolders ?? []).Intersect(test.CoveringFolders ?? [],
                        StringComparer.OrdinalIgnoreCase).Any())
                    && Result(other.Attempts.LastOrDefault(attempt => attempt.Sha == candidate.TipSha
                        && attempt.Phase is PostMergeRunPhases.Initial or PostMergeRunPhases.Repeat)?.Result) == RedTestResult.Fail)),
            LiftRuns = entry is not null ? CoveringRuns(project, entry, run).Select(item => item.Fact).ToArray()
                : incident is null ? [] : CoveringRuns(project, new("line-stop", test.Test, incident.RepairCardId,
                    DateOnly.MaxValue, null, null, "held", SystemPrincipal, incident.FirstRedAtUtc, DateTimeOffset.MaxValue,
                    fingerprint, [], []), run).Select(item => item.Fact).ToArray(),
        };
    }

    private void ApplyAction(ProjectRecord project, PostMergeRunRecord run, PostMergeTestVerdict test,
        string fingerprint, PostMergeIncident? incident, RedTestTriageFacts facts, RedTestDecision action, string principal)
    {
        var now = _clock.GetUtcNow();
        var entry = _quarantines.Read(project.Id).Entries.LastOrDefault(item => item.Test == test.Test
            && item.IncidentFingerprint == fingerprint && item.LiftedAtUtc is null);
        switch (action.Action)
        {
            case RedTestAction.StartHeld:
            case RedTestAction.StartReportOnly:
                var expires = now + action.Duration!.Value;
                _quarantines.Start(project.Id, PrepareQuarantine(project, new(Guid.NewGuid().ToString("N"), test.Test, incident!.RepairCardId,
                    DateOnly.FromDateTime(expires.UtcDateTime), null, action.Reason,
                    action.Action == RedTestAction.StartHeld ? "held" : "report-only", principal, now, expires,
                    fingerprint, [run.Id], test.CoveringFolders ?? [])
                    {
                        SuiteId = test.SuiteId, TestFile = test.TestFile,
                        ConfirmedSuspectPaths = incident.AttributedMerge is not null
                            ? run.Failure?.FailingPaths ?? [] : [],
                        AttributedRevertCardId = incident.Action == "Revert" ? incident.RepairCardId : null,
                    }), ActiveQuarantines(project));
                break;
            case RedTestAction.Renew when entry is not null:
                _quarantines.Renew(project.Id, entry.Id, principal, false, facts.RepairCardLane, now);
                break;
            case RedTestAction.Lift when entry is not null:
                var covering = CoveringRuns(project, entry, run).TakeLast(2).ToArray();
                _quarantines.Lift(project.Id, entry.Id, principal, false,
                    covering.Select(item => item.Fact).ToArray(), covering.Select(item => item.RunId).ToArray(), now);
                _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == fingerprint
                    && item.TestId == test.Test && item.ResolvedAtUtc is null ? item with { ResolvedAtUtc = now } : item).ToArray());
                break;
            case RedTestAction.WithdrawAttribution when incident is not null:
                _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == fingerprint
                    && item.TestId == test.Test && item.ResolvedAtUtc is null
                        ? item with { AttributedMerge = null, FailingPaths = test.CoveringFolders ?? [] } : item).ToArray());
                break;
            case RedTestAction.ResolveLineStop when incident is not null:
                _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == fingerprint
                    && item.TestId == test.Test && item.ResolvedAtUtc is null ? item with { ResolvedAtUtc = now } : item).ToArray());
                break;
            case RedTestAction.LineStop when incident is not null:
                _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == fingerprint
                    && item.TestId == test.Test && item.ResolvedAtUtc is null ? item with { LineStopReason = action.Reason } : item).ToArray());
                break;
            case RedTestAction.AskOwner when incident is not null && action.Question is { } question:
                var asked = new PostMergeOwnerQuestion(question.Options.Select(option => option.ToString()).ToArray(),
                    question.Recommended.ToString(), question.RecommendationReason, run.Id, run.TipSha)
                    { Reason = question.Reason.ToString(), AskedAtUtc = now };
                _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == fingerprint
                    && item.TestId == test.Test && item.ResolvedAtUtc is null
                    && item.OwnerQuestions.All(existing => existing.Reason != asked.Reason)
                        ? item with { OwnerQuestions = [.. item.OwnerQuestions, asked] } : item).ToArray());
                break;
        }
    }

    private string RepairCard(ProjectRecord project, PostMergeRunRecord run, PostMergeTestVerdict test,
        string fingerprint, bool relapse, bool proposeRevert, string principal)
    {
        // Quarantines and incident clocks remain per test; overlapping red sets share the coding repair.
        var related = relapse ? null : (_store.Read(project.Id).Incidents ?? [])
            .Where(item => item.ResolvedAtUtc is null && (item.LatestEvidenceRunId == run.Id
                || MonolithTransitionAdapter.SameRedRepair(item.FailingTests,
                    run.Failure?.FailingTests ?? [test.Test])))
            .Select(item => _scanner.FindJob(item.RepairCardId, project.StorageLocation))
            .FirstOrDefault(card => card is not null
                && MonolithTransitionAdapter.RecurringRepairCard(card.State) != RecurringRepairCardAction.CreateNew);
        if (related is not null)
        {
            ReadyQueuedRepair(project, related.Id);
            return related.Id;
        }
        var title = "Repair red test: " + test.Test;
        var prior = relapse ? null : _scanner.ScanAllJobsWithArchive().FirstOrDefault(card => card.ProjectName == project.DisplayName
            && card.Title == title && card.CreationSource == HumanReviewEscalationCategories.PostMergeRepairCreationSource
            && MonolithTransitionAdapter.RecurringRepairCard(card.State) != RecurringRepairCardAction.CreateNew);
        if (prior is not null)
        {
            ReadyQueuedRepair(project, prior.Id);
            return prior.Id;
        }
        var route = RepairRoute(project, RedTestModelRung.Medium);
        var cardId = _mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Project = project.Id, Title = title, TargetState = MonolithTransitionAdapter.RedRepairCreationLane, TaskType = TaskTypes.Bug,
            Order = 0, CreationSource = HumanReviewEscalationCategories.PostMergeRepairCreationSource, CreatedBy = principal,
            CliType = route.Cli, Agent = route.Cli, Model = route.Model, ThinkingLevel = route.Thinking,
            ModelExplicit = true, ThinkingLevelExplicit = true,
            PromptMarkdown = Prompt(project, run, _store.Read(project.Id).LastGreenSha, run.Failure,
                proposeRevert ? RedDevelopRepairAction.Revert : RedDevelopRepairAction.FixForward, fingerprint) + $"\nTest: {test.Test}\nInitial reasoning: medium\n",
        }) ?? throw new InvalidOperationException("The red-test repair card could not be created.");
        _states.SetOrderInLane(cardId, project.StorageLocation, 0);
        return cardId;
    }

    private ModelRoutingTier RepairTier(RedTestModelRung rung)
    {
        var rank = rung switch { RedTestModelRung.Medium => 2, RedTestModelRung.High => 3, _ => 4 };
        if (_routingMode?.EconomyMode == true) rank = Math.Max(2, rank - _routing.Policy.EconomyMode.DowngradeSteps);
        return _routing.Policy.Tiers.Where(tier => tier.Rank <= rank).MaxBy(tier => tier.Rank)!;
    }

    private (string Cli, string Model, string? Thinking) RepairRoute(ProjectRecord project,
        RedTestModelRung rung, string? existingCli = null)
    {
        var tier = RepairTier(rung);
        var cli = existingCli ?? project.CliDefault ?? CliTypes.Codex;
        if (rung == RedTestModelRung.Medium)
            return (cli, project.ModelDefault ?? ModelMetadataRegistry.DefaultForCli(cli) ?? tier.Model, "medium");
        if (cli == CliTypes.Claude && tier.VendorOverrides.TryGetValue("anthropic", out var vendor))
            return (cli, vendor.Model, vendor.ThinkingLevel);
        return (CliTypes.Codex, tier.Model, tier.ThinkingLevel);
    }

    private void SetRepairRung(ProjectRecord project, string cardId, RedTestModelRung rung)
    {
        var card = _scanner.FindJob(cardId, project.StorageLocation);
        if (card?.State is not (TaskStates.Preparation or TaskStates.Ready)) return;
        rung = MonolithTransitionAdapter.SharedRedRepairRung(rung,
            (_store.Read(project.Id).Incidents ?? []).Where(item => item.RepairCardId == cardId && item.ResolvedAtUtc is null)
                .Select(item => Enum.TryParse<RedTestModelRung>(item.ModelRung, out var active) ? active : RedTestModelRung.Medium));
        var route = RepairRoute(project, rung, card.CliType);
        _store.UpdateIncidents(project.Id, items => items.Select(item => item.RepairCardId == cardId
            && item.ResolvedAtUtc is null ? item with { ModelRung = rung.ToString() } : item).ToArray());
        if (card.Model != route.Model) _mutations.SetJobModel(cardId, route.Model, project.StorageLocation);
        if (card.ThinkingLevel != route.Thinking) _mutations.SetJobThinkingLevel(cardId, route.Thinking, project.StorageLocation);
        _states.SetOrderInLane(cardId, project.StorageLocation, 0);
    }

    public IReadOnlyList<(string RunId, RedTestLiftRun Fact)> CoveringRuns(ProjectRecord project,
        ProjectTestQuarantine entry, PostMergeRunRecord? current = null)
    {
        var state = _store.Read(project.Id);
        var incident = (state.Incidents ?? []).LastOrDefault(item => item.RepairCardId == entry.Card);
        var runs = state.Runs.Concat(current is null ? [] : [current]).DistinctBy(run => run.Id)
            .Where(run => run.FinishedAtUtc >= entry.StartedAtUtc && run.Status is "green" or "red" or "inconclusive")
            .OrderByDescending(run => run.FinishedAtUtc);
        var result = new List<(string, RedTestLiftRun)>();
        foreach (var run in runs)
        {
            var test = run.TestVerdicts?.FirstOrDefault(test => test.Test == entry.Test);
            var selected = run.SuiteSelection;
            var suite = entry.SuiteId ?? incident?.SuiteId;
            var command = suite is not null && selected?.SuiteCommands.TryGetValue(suite, out var suiteCommand) == true
                ? suiteCommand : null;
            var suiteVerdicts = run.Verdicts.Where(verdict => suite is not null && verdict.SuiteId == suite
                || verdict.SuiteId is null && command is not null && (verdict.Command == command
                    || verdict.Command.StartsWith(command + " ", StringComparison.Ordinal))).ToArray();
            var covered = test is not null || suite is not null
                && selected?.SelectedSuites.Contains(suite, StringComparer.Ordinal) == true && suiteVerdicts.Length > 0;
            if (!covered) continue;
            var outcome = test is not null ? Result(test.Attempts.LastOrDefault(attempt => attempt.Sha == run.TipSha
                    && attempt.Phase is PostMergeRunPhases.Initial or PostMergeRunPhases.Repeat)?.Result)
                : suiteVerdicts.All(verdict => verdict.ExitCode == 0) ? RedTestResult.Pass : RedTestResult.Unknown;
            result.Add((run.Id, new(outcome, run.EnvelopeCanaryPassed != false && EnvelopeMatches(run,
                state.Runs.FirstOrDefault(previous => previous.Id == entry.EvidenceRunIds.FirstOrDefault())),
                ContainsRepair(project, entry.Card, run.TipSha))));
            if (result.Count == 2) break;
        }
        result.Reverse();
        return result;
    }

    private bool ContainsRepair(ProjectRecord project, string cardId, string tip)
    {
        if (_containsRepair is not null) return _containsRepair(project, cardId, tip);
        var card = _scanner.FindJob(cardId, project.StorageLocation);
        var integration = card is null ? null : IntegrationVerificationStore.Read(card.FolderPath);
        return integration is { Sha: { } sha } && Repository(project) is { } root
            && _git?.IsAncestor(root, sha, tip) == true;
    }

    private static bool EnvelopeMatches(PostMergeRunRecord run, PostMergeRunRecord? reference)
    {
        if (run.FingerprintMatchesReference is { } measured) return measured;
        if (run.Envelope is not { } actual || reference?.Envelope is not { } expected) return true;
        return TestEnvelope.MatchesReference(actual, expected);
    }

    private static string Fingerprint(PostMergeRunRecord run, PostMergeTestVerdict test)
        => run.Failure?.Fingerprint ?? Digest(test.Test + "\n" + string.Join("\n", run.Verdicts
            .Where(verdict => verdict.ExitCode != 0).Select(verdict => verdict.OutputTail)))[..16];
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static RedTestRunPair Pair(IReadOnlyList<string> results)
        => new(Result(results.ElementAtOrDefault(0)), Result(results.ElementAtOrDefault(1)));
    private static RedTestResult Result(string? result) => result switch
    { "pass" => RedTestResult.Pass, "fail" => RedTestResult.Fail, _ => RedTestResult.Unknown };
}
